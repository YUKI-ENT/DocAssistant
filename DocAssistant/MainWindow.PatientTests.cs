using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;

namespace DocAssistant;

public sealed class FakeMemoForm(object? value, bool missing = false)
{
    public FakeMemoRecord Recordset => new(value, missing);
}
public sealed class FakeMemoRecord(object? value, bool missing)
{
    public bool BOF => false;
    public bool EOF => false;
    public FakeMemoFields Fields => new(value, missing);
    public void Close() => throw new InvalidOperationException("Do not close the form recordset");
}
public sealed class FakeMemoFields(object? value, bool missing)
{
    public object this[string name] => name == "メモ" && !missing ? new FakePatientValue(value) : throw new COMException("Missing field");
}

public partial class MainWindow
{
    private async Task TestPatientMemoAsync()
    {
        var folder = Path.GetFullPath("tmp/patient-test"); Directory.CreateDirectory(folder);
        try
        {
            const string memo = "薬剤：テスト注意\r\n連絡時の注意事項";
            Check(AccessSession.ReadPatientMemo(new FakeMemoForm(memo)) == memo, "Read full multiline memo from current record");
            Check(AccessSession.ReadPatientMemo(new FakeMemoForm(DBNull.Value)) == "", "Null memo");
            Check(AccessSession.ReadPatientMemo(new FakeMemoForm(null, true)) == "取得できません", "Missing memo is not reported as empty");
            var patient = AccessSession.ReadAutomaticPatient(new FakeMonitorApp());
            Check(patient.PatientMemo == memo && patient.Text.Contains("カルテ番号：001"), "Automatic monitor includes memo");
            ShowChart(patient);
            TextBox MemoBox() => PatientFieldsGrid.Children.OfType<TextBox>().Last();
            Check(MemoBox().Text == memo, "Multiline memo and colon remain in one display field");
            var originalBox = MemoBox(); originalBox.Select(0, 2);
            ShowChart(patient);
            Check(ReferenceEquals(originalBox, MemoBox()) && MemoBox().SelectionLength == 2, "Unchanged polling preserves selection");
            ShowChart(patient with { PatientMemo = "変更後" });
            Check(MemoBox().Text == "変更後", "Memo updates for same patient");
            ShowChart(patient with { Text = patient.Text.Replace("001", "002"), PatientMemo = "" });
            Check(MemoBox().Text == "未登録", "Next patient's empty memo clears prior warning");
            ShowChart(new(false, "未接続"));
            Check(PatientFieldsGrid.Children.Count == 0, "Disconnect clears memo");
            bool changed = false;
            try { AccessSession.ReadAutomaticPatient(new FakeMonitorApp(changePatient: true)); }
            catch (InvalidOperationException ex) { changed = ex.Message.Contains("切り替わりました"); }
            Check(changed, "Patient switch during read rejects data");
            ShowChart(patient);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            RenderVisual(this, Path.Combine(folder, "window.png"), 1740, 940);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: current record memo, null/missing, multiline display, refresh, selection, patient switch, disconnect");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
