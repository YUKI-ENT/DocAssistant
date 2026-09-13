using System.Runtime.InteropServices;
using System.Windows;

namespace DocAssistant;

// Synthetic object model used by --editor-test. Only fabricated patient values; no database access.
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
public sealed class FakePatientControls(bool changePatient = false, object? clinical = null, object? draft = null)
{
    private int idReads;
    public object this[string name] => name == "受診症状サブフォーム" && draft != null ? draft : name == "受診カルテサブ" && clinical != null ? clinical : new FakePatientValue(name switch
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
public sealed class FakeMonitorForm(bool newRecord, bool changePatient, object? clinical, object? draft = null)
{
    public FakeBirthdayRecord Recordset => new();
    public bool NewRecord => newRecord;
    public FakePatientControls Controls { get; } = new(changePatient, clinical, draft);
}
public sealed class FakeMonitorForms(bool newRecord, bool changePatient, object? clinical, object? draft = null)
{
    public object this[string name] => name == "患者マスター" ? new FakeMonitorForm(newRecord, changePatient, clinical, draft) : throw new InvalidOperationException();
}
public sealed class FakeMonitorApp(bool exists = true, bool loaded = true, bool newRecord = false, bool changePatient = false, object? clinical = null, object? draft = null)
{
    public FakeAccessProject CurrentProject => new(new FakeAccessCollection(new FakeAccessMetadata(exists ? "患者マスター" : "別フォーム", loaded)));
    public FakeMonitorForms Forms => loaded ? new(newRecord, changePatient, clinical, draft) : throw new InvalidOperationException("Closed forms must not be read");
}
public partial class MainWindow
{
    private async Task CheckAccessConnection(string folder, AppSettings config)
    {
        Check(!AccessSession.ReadAutomaticPatient(new FakeMonitorApp(exists: false)).Detected, "Unrelated Access database is not a chart");
        var waiting = AccessSession.ReadAutomaticPatient(new FakeMonitorApp(loaded: false));
        Check(waiting.Detected && waiting.Text == "", "Existing but closed patient form waits without opening it");
        Check(AccessSession.ReadAutomaticPatient(new FakeMonitorApp(newRecord: true)).Text == "", "New record must not retain previous patient");
        var live = CheckClinicalNotes() with { Medication = CheckMedicationHistory() };
        Check(live.Detected && live.Text.Contains("カルテ番号：001"), "Auto display reads current patient");
        ShowChart(live);
        var patientValue = (System.Windows.Controls.TextBox)PatientFieldsGrid.Children[1];
        patientValue.Select(0, 1);
        ShowChart(live);
        Check(patientValue.SelectionLength == 1, "Unchanged polling preserves text selection for copy");
        ShowChart(waiting);
        Check(PatientFieldsGrid.Children.Count == 0 && ChartPlaceholder.Visibility == Visibility.Visible, "Lost source clears previous patient");
        try
        {
            AccessSession.ReadAutomaticPatient(new FakeMonitorApp(changePatient: true));
            throw new InvalidOperationException("Expected auto monitor patient switch detection");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("切り替わりました")) { }
        Check(AccessSession.FormatPatientSex("2") == "女" && AccessSession.FormatPatientSex("") == "未登録" && AccessSession.FormatPatientSex("9").Contains("不明"), "Sex labels and unknown codes");
        Check(AccessSession.ReadPatientBirthday(new FakeMonitorForm(false, false, null)) == "昭和55年4月15日（1980年4月15日）", "Birthday comes from current record fields");
        Check(live.Text.Contains("生年月日：昭和55年4月15日"), "Birthday appears in automatic patient panel");
        Check(AccessSession.FormatPatientBirthday("", "", "", "") == "未登録", "Empty birthday");
        Check(AccessSession.FormatPatientBirthday("平成", "元", "1", "8") == "平成元年1月8日（1989年1月8日）", "Preserve Japanese era year");
        Check(AccessSession.FormatPatientBirthday("令和", "1", "5", "").Contains("不明日"), "Partial birthday does not invent a day");
        Check(AccessSession.FormatPatientBirthday("令和", "元", "5", "1").EndsWith("（2019年5月1日）"), "Reiwa conversion");
        Check(!AccessSession.FormatPatientBirthday("平成", "31", "5", "1").Contains('（'), "Reject a date outside the stated era");
        Check(!AccessSession.FormatPatientBirthday("昭和", "55", "2", "30").Contains('（'), "Reject invalid calendar dates");
        var patientText = AccessSession.ReadPatientControls(new FakePatientControls());
        Check(patientText.Split(Environment.NewLine).Length == 8 && patientText.Contains("性別：男") && patientText.Contains("住所２："), "Patient eight fields and null/code values");
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
            Check(Convert.ToBoolean(AccessDispatch.Call(dictionary, "Exists", "test-key")), "Real COM method invocation");
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
        ShowChart(live);
        var medicationDay = (System.Windows.Controls.Expander)MedicationDays.Children[0];
        medicationDay.IsExpanded = true;
        UpdateLayout();
        MedicationScroll.ScrollToBottom();
        UpdateLayout();
        RenderVisual((FrameworkElement)Content, System.IO.Path.Combine(folder, "medication-history.png"), 1300, 880);
        medicationDay.IsExpanded = false;
        var window = new AccessWindow(config);
        window.ShowInspection(result);
        RenderVisual((FrameworkElement)window.Content, System.IO.Path.Combine(folder, "access.png"), 1080, 710);
        window.Close();
    }
}

public sealed class FakeBirthdayRecord
{
    public bool BOF => false;
    public bool EOF => false;
    public FakeBirthdayFields Fields => new();
    public void Close() => throw new InvalidOperationException("Do not close the live form recordset");
}
public sealed class FakeBirthdayFields
{
    public object this[string name] => new FakePatientValue(name switch
    {
        "年号" => "昭和", "生年" => 55, "月" => 4, "日" => 15,
        "メモ" => "薬剤：テスト注意\r\n連絡時の注意事項",
        _ => throw new InvalidOperationException("Unexpected birthday field")
    });
}
