using System.Runtime.InteropServices;
using System.Windows;

namespace PDFWriter;

// Synthetic object model used by --editor-test. No database or patient values.
public sealed class FakeAccessCollection(params object[] items)
{
    public int Count => items.Length;
    public object this[int index] => items[index];
}
public sealed class FakeAccessMetadata(string name, bool loaded)
{
    public string Name => name;
    public bool IsLoaded => loaded;
}
public sealed class FakeAccessProject(FakeAccessCollection allForms) { public FakeAccessCollection AllForms => allForms; }
public sealed class FakeNamedAccessForms(FakeAccessForm target)
{
    public object this[string name] => name == target.MetadataName ? target : throw new InvalidOperationException("Unexpected form name");
    public object this[int index] => throw new InvalidOperationException("Live forms must be looked up by name");
}
public sealed class FakeAccessApp(FakeAccessForm target)
{
    public FakeNamedAccessForms Forms => new(target);
    public FakeAccessProject CurrentProject => new(new FakeAccessCollection(
        new FakeAccessMetadata("未表示フォーム", false), new FakeAccessMetadata(target.MetadataName, true)));
}
public sealed class FakeAccessDatabase { public string Name => @"D:\Clinical\client.mdb"; }
public sealed class FakePatientValue(object? value) { public object? Value => value; }
public sealed class FakePatientControls(bool changePatient = false)
{
    private int idReads;
    public object this[string name] => new FakePatientValue(name switch
    {
        "カルテ番号" => changePatient && ++idReads > 1 ? "002" : "001",
        "住所２" => DBNull.Value,
        "性別コード１" => 1,
        _ => "テスト" + name
    });
}
public sealed class FakeLegacyAccessApp
{
    public object CurrentProject => throw new COMException("Legacy project failure", unchecked((int)0x800A88D2));
    public FakeAccessDatabase CurrentDb() => new();
}
public sealed class FakeAccessForm(string name, FakeAccessCollection controls)
{
    public string MetadataName => name;
    public string Name => throw new COMException("Live form Name fails", unchecked((int)0x800A88D2));
    public FakeAccessCollection Controls => controls;
}
public sealed class FakeAccessControl(string name, int type, string source = "", FakeAccessForm? child = null)
{
    public string Name => name;
    public int ControlType => type;
    public string ControlSource => type == 100 ? throw new COMException("No property") : source;
    public string SourceObject => child?.MetadataName ?? "";
    public FakeAccessForm Form => child ?? throw new COMException("Not loaded");
    public string Value => throw new InvalidOperationException("Patient values must never be read by structure inspection");
    public string Text => throw new InvalidOperationException("Patient text must never be read by structure inspection");
}
public partial class MainWindow
{
    private async Task CheckAccessConnection(string folder, AppSettings config)
    {
        var patientText = AccessSession.ReadPatientControls(new FakePatientControls());
        Check(patientText.Split(Environment.NewLine).Length == 8 && patientText.Contains("性別コード１：1") && patientText.Contains("住所２："), "Patient eight fields and null/code values");
        try
        {
            AccessSession.ReadPatientControls(new FakePatientControls(true));
            throw new InvalidOperationException("Expected patient switch detection");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("切り替わりました")) { }
        // Real Windows COM smoke test, independent of Access installation.
        var dictionaryType = Type.GetTypeFromProgID("Scripting.Dictionary")
            ?? throw new InvalidOperationException("Scripting.Dictionary COM registration missing");
        object dictionary = Activator.CreateInstance(dictionaryType)!;
        try
        {
            dictionaryType.InvokeMember("Add", System.Reflection.BindingFlags.InvokeMethod, null, dictionary, new object[] { "test-key", "test-value" });
            Check(Convert.ToInt32(AccessDispatch.Get(dictionary, "Count")) == 1, "Real COM named property get");
            Check(Convert.ToString(AccessDispatch.Get(dictionary, "Item", "test-key")) == "test-value", "Real COM indexed property get");
        }
        finally { Marshal.ReleaseComObject(dictionary); }
        try
        {
            AccessDispatch.Get(new FakeLegacyAccessApp(), "CurrentProject");
            throw new InvalidOperationException("Expected reflected COM failure");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x800A88D2)) { }
        Check(AccessSession.GetDatabasePath(new FakeLegacyAccessApp()) == @"D:\Clinical\client.mdb", "Legacy Access CurrentDb fallback after 0x800A88D2");
        Check(AccessSession.SameDatabase(@"D:\Clinical\client.mdb", @"d:\clinical\client.mdb"), "Access database path case comparison");
        Check(!AccessSession.SameDatabase(@"D:\Clinical\client.mdb", @"D:\Other\client.mdb"), "Reject another Access database");
        try
        {
            AccessSession.Step<int>("フォーム読み込みテスト", () => throw new COMException("Access failure", unchecked((int)0x800A88D2)));
            throw new InvalidOperationException("Expected staged Access failure");
        }
        catch (AccessOperationException ex)
        {
            Check(ex.HResult == unchecked((int)0x800A88D2) && ex.Message.Contains("フォーム読み込みテスト") && ex.Message.Contains("800A88D2"), "Access failure preserves stage and HRESULT");
        }
        var child = new FakeAccessForm("記載サブフォーム", new FakeAccessCollection(new FakeAccessControl("記載欄", 109, "記載本文")));
        var parent = new FakeAccessForm("患者マスター", new FakeAccessCollection(
            new FakeAccessControl("氏名見出し", 100),
            new FakeAccessControl("患者番号欄", 109, "患者ID"),
            new FakeAccessControl("氏名欄", 109, "氏名"),
            new FakeAccessControl("住所欄", 109, "住所"),
            new FakeAccessControl("生年月日欄", 109, "生年月日"),
            new FakeAccessControl("カルテ記載", 112, child: child),
            new FakeAccessControl("未読込サブフォーム", 112)));
        var app = new FakeAccessApp(parent);
        var result = AccessSession.Inspect(app, "診断テスト用（実接続ではありません）", "患者マスター");
        Check(result.FormFound && result.Controls.Count == 8, "Access controls and nested subform discovery");
        Check(result.Controls.Single(c => c.Name == "記載欄").FormPath == "患者マスター / カルテ記載", "Access nested control path");
        Check(result.Controls.Single(c => c.Name == "氏名欄").ControlSource == "氏名", "Access control binding");
        Check(result.Notes.Count == 1, "Unavailable subform must be reported without failing other fields");
        Check(!AccessSession.Inspect(app, "", "未表示").FormFound, "Missing Access form");
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(new AppSettings { AccessDatabasePath = "test.mdb", AccessFormName = "患者マスター" }))!;
        Check(roundtrip.AccessDatabasePath == "test.mdb" && roundtrip.AccessFormName == "患者マスター", "Access settings roundtrip");
        if (!AccessSession.IsAccessInstalled())
        {
            using var session = new AccessSession();
            try
            {
                await session.ConnectAsync(System.IO.Path.GetFullPath("DYNA_cnt_ons.mdb"), "患者マスター");
                throw new InvalidOperationException("Expected Access-not-installed error");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("COM登録")) { }
        }
        var window = new AccessWindow(config);
        window.ShowInspection(result);
        RenderVisual((FrameworkElement)window.Content, System.IO.Path.Combine(folder, "access.png"), 1080, 710);
        window.Close();
    }
}
