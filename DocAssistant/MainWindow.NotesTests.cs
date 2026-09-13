using System.Windows;

namespace DocAssistant;

public sealed class FakeNoteRecords(params Dictionary<string, object?>[] rows)
{
    private int index;
    public Action? OnGetRows;
    public bool Closed { get; private set; }
    public bool BOF => rows.Length == 0 || index < 0;
    public bool EOF => rows.Length == 0 || index >= rows.Length;
    public int RecordCount => rows.Length;
    public FakeNoteFields Fields => new(rows[0].Keys.ToArray());
    public void MoveLast() => index = rows.Length - 1;
    public void MoveFirst() => index = 0;
    public void Move(int offset) => index += offset;
    public Array GetRows(int count)
    {
        OnGetRows?.Invoke();
        count = Math.Min(count, rows.Length - index);
        var names = rows[0].Keys.ToArray();
        var result = new object?[names.Length, count];
        for (int row = 0; row < count; row++)
            for (int field = 0; field < names.Length; field++) result[field, row] = rows[index + row][names[field]];
        index += count;
        return result;
    }
    public void Close() => Closed = true;
}
public sealed class FakeNoteFields(string[] names)
{
    public int Count => names.Length;
    public FakeNoteField this[int index] => new(names[index]);
}
public sealed class FakeNoteField(string name) { public string Name => name; }
public sealed class FakeNoteSub(FakeNoteRecords records)
{
    public FakeNoteForm Form => new(records);
}
public sealed class FakeNoteForm(FakeNoteRecords records)
{
    public FakeNoteRecords RecordsetClone => records;
    public object Bookmark { get => throw new InvalidOperationException("Must not move the live form"); set => throw new InvalidOperationException("Must not move the live form"); }
}

public partial class MainWindow
{
    private async Task TestNotesAsync()
    {
        var folder = System.IO.Path.GetFullPath("tmp/notes-test"); System.IO.Directory.CreateDirectory(folder);
        try
        {
            CheckClinicalNotes();
            CheckLiveDraftNotes();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "result.txt"), "PASS: clinical notes, live draft controls, unsaved and focused text, blank clone, multiline rows, clearing, patient/visit isolation, mid-read changes. No database access.");
        }
        catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }

    private void CheckLiveDraftNotes()
    {
        static Dictionary<string, object?> Row(int order, string text, string visit = "103") => new()
        { ["カルテ番号"] = 1L, ["受診コード"] = visit, ["順番"] = order, ["症状"] = text };
        var body = new FakeLiveNoteControl("症状", "画面にある所見\n続き");
        var patient = new FakeLiveNoteControl("カルテ番号", "001");
        var order = new FakeLiveNoteControl("順番", "2");
        var visit = new FakeLiveNoteControl("受診コード", "103");
        var controls = new FakeAccessCollection(patient, visit, order, body);
        AccessNotes Read(FakeNoteRecords records) => AccessSession.ReadDraftNotes(
            new FakePatientControls(draft: new FakeLiveNoteSub(new FakeLiveNoteForm(records, controls))), "001");
        var empty = new FakeNoteRecords();
        Check(Read(empty).Text == "画面にある所見\r\n続き" && empty.Closed, "Blank clone still displays live control value without Update");
        body.FocusedText = "入力中の最新文字";
        var rows = new FakeNoteRecords(Row(1, "第1行"), Row(2, "古い本文"), Row(3, "第3行"), Row(1, "前の受診", "102"));
        var result = Read(rows);
        Check(result.Notes!.Count == 3 && result.Text.Contains("入力中の最新文字") && !result.Text.Contains("古い本文") && !result.Text.Contains("前の受診"), "Focused Text replaces exactly one row and excludes old visit");
        Check(result.Text.IndexOf("第1行") < result.Text.IndexOf("入力中") && result.Text.IndexOf("入力中") < result.Text.IndexOf("第3行"), "Live row preserves other rows and ordering");
        body.FocusedText = "";
        Check(Read(new FakeNoteRecords(Row(2, "削除した本文"))).Text == "", "Intentional deletion never restores the saved text");
        body.FocusedText = null; body.Value = "更新後の値";
        Check(Read(new FakeNoteRecords(Row(2, "古い値"))).Text == "更新後の値", "Value updates on subsequent polls");
        patient.Value = "002";
        bool wrongPatient = false;
        try { Read(new FakeNoteRecords()); } catch (InvalidOperationException) { wrongPatient = true; }
        Check(wrongPatient, "Wrong branch is rejected");
        patient.Value = "001";
        var changing = new FakeNoteRecords(Row(2, "保存値")) { OnGetRows = () => visit.Value = "104" };
        bool switched = false;
        try { Read(changing); } catch (InvalidOperationException ex) { switched = ex.Message.Contains("変更されました"); }
        Check(switched && changing.Closed, "Changing visit during clone read rejects mixed data and closes clone");
    }

    private AccessPatientDisplay CheckClinicalNotes()
    {
        static Dictionary<string, object?> Row(string date, string visit, long number, decimal order, string? text, string? instruction = null) => new()
        {
            ["受診日"] = DateTime.Parse(date), ["受診コード"] = visit, ["カルテ番号"] = number,
            ["順番"] = order, ["症状"] = (object?)text ?? DBNull.Value, ["指示欄"] = (object?)instruction ?? DBNull.Value
        };
        var records = new FakeNoteRecords(
            Row("2026-09-01", "100", 1, 1, "初回の記載（架空）"),
            Row("2026-09-10", "101", 1, 1, "問診（架空）\n経過は良好", "内服の指示（架空）\n追加説明"),
            Row("2026-09-10", "101", 1, 2, "所見（架空）\\_\n注意事項（架空）$"),
            Row("2026-09-10", "102", 2, 1, "別枝の記載（架空）"),
            Row("2026-09-10", "102", 2, 2, null, "処置の指示（架空）"));
        static Dictionary<string, object?> Draft(long number, int order, string body) => new()
        {
            ["カルテ番号"] = number, ["受診コード"] = "103", ["順番"] = order, ["症状"] = body
        };
        var draftRecords = new FakeNoteRecords(Draft(1, 2, "未確定の所見（架空）"), Draft(1, 1, "当日の経過（架空）\n追記"), Draft(2, 1, "別枝の未確定記録"));
        var live = AccessSession.ReadAutomaticPatient(new FakeMonitorApp(clinical: new FakeNoteSub(records), draft: new FakeNoteSub(draftRecords)));
        Check(draftRecords.Closed && live.DraftText.Contains("当日の経過（架空）\r\n追記") && !live.DraftText.Contains("別枝"), "Draft records use exact patient and CRLF without a date field");
        Check(live.DraftText.IndexOf("当日の経過") < live.DraftText.IndexOf("未確定の所見") && !live.DraftText.Contains("日付なし"), "Drafts sort by row and do not invent dates");
        Check(records.Closed, "Close the recordset clone after successful traversal");
        Check(live.NotesText.Contains("カルテ番号 0-1") && live.NotesText.Contains("カルテ番号 0-2"), "Include sibling branches");
        Check(live.NotesText.IndexOf("2026/09/10") < live.NotesText.IndexOf("2026/09/01"), "Newest date first");
        Check(live.NotesText.IndexOf("問診") < live.NotesText.IndexOf("所見"), "Ascending row order within a visit");
        Check(live.NotesText.Contains("問診（架空）\r\n経過は良好") && !live.NotesText.Replace("\r\n", "").Contains('\n'), "Clinical text uses CRLF");
        Check(live.NotesStatus.StartsWith("4件"), "Ignore empty symptoms without counting them as notes");
        Check(live.InstructionsText.Contains("内服の指示（架空）\r\n追加説明") && live.InstructionsText.Contains("処置の指示（架空）"), "Include instruction-only rows and normalize CRLF");
        Check(!live.NotesText.Contains("処置の指示") && !live.InstructionsText.Contains("問診"), "Keep symptoms and instructions in separate sections");
        ShowChart(live);
        ChartDraft.SelectAll();
        var draftSelection = ChartDraft.Selection.Text;
        ShowChart(live);
        Check(ChartDraft.Selection.Text == draftSelection, "Unchanged draft preserves selection");
        var visits = BuildVisitHistory(live with { Medication = new("0", "", [new("2026/09/10", "", "薬A"), new("日付不明", "", "薬B")]) });
        Check(visits.Days.Count == 2 && visits.Days[0].Key == "2026/09/10", "Only notes and instructions contribute visit dates");
        Check(visits.Days[0].Notes.Contains("問診") && visits.Days[0].Text == "" && !visits.Days[0].Notes.Contains("カルテ番号") && !visits.Days[0].Instructions.Contains("受診 102") && visits.Days[0].Instructions.Contains("処置の指示"), "Keep two groups without identity headers");
        ShowChart(new(false, "未接続"));
        Check((string?)ChartDraft.Tag == "" && PatientFieldsGrid.Children.Count == 0 && MedicationDays.Children.Count == 0, "Disconnect clears all chart cards");
        var styled = ClinicalValue("通常\n赤字\\_\n黄色$\n併用\\_$\n通常に戻る");
        var paragraphs = styled.Document.Blocks.Cast<System.Windows.Documents.Paragraph>().ToArray();
        var red = (System.Windows.Documents.Run)paragraphs[1].Inlines.FirstInline;
        var both = (System.Windows.Documents.Run)paragraphs[3].Inlines.FirstInline;
        Check(red.Foreground == System.Windows.Media.Brushes.Red && red.TextDecorations == TextDecorations.Underline, "Red underline applies to marker line");
        Check(paragraphs[2].Background == System.Windows.Media.Brushes.Yellow && paragraphs[3].Background == System.Windows.Media.Brushes.Yellow && both.Foreground == System.Windows.Media.Brushes.Red, "Yellow background and combined markers");
        Check(paragraphs[4].Background != System.Windows.Media.Brushes.Yellow && ((System.Windows.Documents.Run)paragraphs[4].Inlines.FirstInline).Foreground != System.Windows.Media.Brushes.Red, "Formatting does not leak to next line");
        foreach (var marker in new[] { "_", @"\_", "¥_", "＿" })
        {
            var middle = ClinicalValue("前半" + marker + "後半\n通常行");
            var first = (System.Windows.Documents.Paragraph)middle.Document.Blocks.FirstBlock;
            var marked = (System.Windows.Documents.Run)first.Inlines.FirstInline;
            Check(marked.Foreground == System.Windows.Media.Brushes.Red && marked.TextDecorations == TextDecorations.Underline,
                "Mid-line underscore marker styles the entire line: " + marker);
        }
        styled.SelectAll();
        string selected = styled.Selection.Text;
        SetClinicalText(styled, (string)styled.Tag);
        Check(styled.Selection.Text == selected && selected.Contains("\\_"), "Unchanged rich text preserves selection and original markers");
        var repeatName = ChartValue("山田 太郎");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            repeatName.SelectAll();
            Check(repeatName.SelectedText == "山田 太郎", "Same demographic field can be selected again");
            ClearChartDragSelection(repeatName);
            Check(repeatName.SelectionLength == 0 && repeatName.Text == "山田 太郎", "Drag cleanup clears selection without removing source text");
            styled.SelectAll();
            Check(!styled.Selection.IsEmpty, "Same clinical text can be selected again");
            ClearChartDragSelection(styled);
            Check(styled.Selection.IsEmpty, "Rich text drag cleanup clears stale selection");
        }
        var empty = new FakeNoteRecords();
        Check(AccessSession.ReadClinicalNotes(new FakePatientControls(clinical: new FakeNoteSub(empty)), "001").Text == "" && empty.Closed, "Empty clone closes cleanly");
        var foreign = new FakeNoteRecords(Row("2026-09-10", "103", 11, 1, "他の患者"));
        var rejected = AccessSession.ReadAutomaticPatient(new FakeMonitorApp(clinical: new FakeNoteSub(foreign)));
        Check(rejected.Text.Contains("カルテ番号：001") && rejected.NotesText == "" && foreign.Closed, "Reject another patient family but keep verified demographics");
        var missing = AccessSession.ReadAutomaticPatient(new FakeMonitorApp());
        Check(missing.Text.Contains("カルテ番号：001") && missing.NotesText == "" && missing.NotesStatus.Contains("取得できません"), "Missing subform does not break demographics");
        try
        {
            // ReadAutomaticPatient must recheck identity after the whole notes read.
            AccessSession.ReadAutomaticPatient(new FakeChangingNoteApp(), (_, _) => new AccessNotes("", "前患者の本文"));
            throw new InvalidOperationException("Expected patient switch rejection after notes read");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("切り替わりました")) { }
        var many = new FakeNoteRecords(Enumerable.Range(0, 5001)
            .Select(i => Row("2026-09-10", "200", 1, i, $"記載{i:D5}")).ToArray());
        var capped = AccessSession.ReadClinicalNotes(new FakePatientControls(clinical: new FakeNoteSub(many)), "001");
        Check(capped.Status.Contains("上限") && !capped.Text.Contains("記載00000") && capped.Text.Contains("記載05000") && many.Closed,
            "Batch reads retain the final 5000 rows and report truncation");
        return live;
    }
}
public sealed class FakeChangingNoteApp
{
    public FakeAccessProject CurrentProject => new(new FakeAccessCollection(new FakeAccessMetadata("患者マスター", true)));
    public FakeChangingNoteForms Forms => new();
}
public sealed class FakeChangingNoteForms
{
    public FakeChangingNoteForm this[string name] => new();
}
public sealed class FakeChangingNoteForm
{
    public bool NewRecord => false;
    public FakeChangingNoteControls Controls { get; } = new();
}
public sealed class FakeChangingNoteControls
{
    private int reads;
    public object this[string name] => new FakePatientValue(name == "カルテ番号" ? (++reads >= 5 ? "011" : "001") : "架空");
}

public sealed class FakeLiveNoteControl(string source, string value)
{
    public string ControlSource => source;
    public string Value { get; set; } = value;
    public string? FocusedText;
    public string Text => FocusedText ?? throw new System.Runtime.InteropServices.COMException("No focus", unchecked((int)0x800A0889));
}
public sealed class FakeLiveNoteForm(FakeNoteRecords records, FakeAccessCollection controls)
{
    public FakeNoteRecords RecordsetClone => records;
    public FakeAccessCollection Controls => controls;
    public void Update() => throw new InvalidOperationException("Never save Access from a monitor");
    public void Requery() => throw new InvalidOperationException("Never requery the live form");
    public object Recordset => throw new InvalidOperationException("Do not access the live recordset");
}
public sealed class FakeLiveNoteSub(FakeLiveNoteForm form)
{
    public FakeLiveNoteForm Form => form;
}
