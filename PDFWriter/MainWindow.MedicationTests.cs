using System.Windows.Controls;

namespace PDFWriter;

public sealed class FakeMedicationDatabase(FakeNoteRecords records)
{
    public bool Opened { get; private set; }
    public object OpenRecordset(string sql, int type, int options)
    {
        if (type != 4 || options != 4 || sql != AccessSession.MedicationSql("1234"))
            throw new InvalidOperationException("Expected the all-branch read-only medication query");
        Opened = true;
        return records;
    }
    public void Close() => throw new InvalidOperationException("Do not close the user's database");
}
public sealed class FakeMedicationApp(FakeMedicationDatabase database)
{
    public object CurrentDb() => database;
}

public partial class MainWindow
{
    private MedicationHistory CheckMedicationHistory()
    {
        static Dictionary<string, object?> Row(DateTime? date, long number, decimal order, string name, object? quantity) => new()
        {
            ["受診日"] = date, ["受診コード"] = "200", ["カルテ番号"] = number,
            ["順番"] = order, ["薬名"] = name, ["数量"] = quantity, ["受診患者番号"] = number
        };
        var today = new DateTime(2026, 9, 10);
        var records = new FakeNoteRecords(
            Row(today, 1230, 2, "確認薬B（架空）", 1.5m),
            Row(today, 1230, 1, "確認薬A（架空）", 0),
            Row(today, 1239, 1, "別枝の薬（架空）", 2),
            Row(today.AddDays(-7), 1232, 1, "以前の薬（架空）", DBNull.Value),
            Row(null, 1232, 1, "日付不明の薬（架空）", 1));
        var database = new FakeMedicationDatabase(records);
        var history = AccessSession.ReadMedicationHistory(new FakeMedicationApp(database), "1234");
        Check(database.Opened && records.Closed, "Open a read-only snapshot and close it");
        Check(history.PatientKey == "123" && history.Days.Count == 3 && history.Days[0].Key == "2026/09/10", "Group all dates newest first");
        var text = history.Days[0].Text;
        Check(text.Contains("カルテ番号 123-0") && text.Contains("カルテ番号 123-9") && text.IndexOf("確認薬A") < text.IndexOf("確認薬B"), "Keep all sibling branches and row order");
        Check(text.Contains("数量：0") && text.Contains("数量：1.5") && history.Days[1].Text.Contains("数量：未登録"), "Preserve zero and fractional quantities; do not guess null values");
        Check(history.Days.Last().Key == "日付不明" && !text.Replace("\r\n", "").Contains('\n'), "Unknown dates and CRLF");
        string sql = AccessSession.MedicationSql("1239");
        Check(sql.Contains("M.[カルテ番号] >= 1230") && sql.Contains("M.[カルテ番号] < 1240") && !sql.Contains("TOP") && !sql.Contains("Date()"), "Query covers the whole family and all dates without truncation");
        try { AccessSession.MedicationSql("1234 OR 1=1"); throw new Exception("Expected invalid patient number rejection"); }
        catch (InvalidOperationException) { }
        var foreign = new FakeNoteRecords(Row(today, 1240, 1, "別患者", 1));
        try { AccessSession.ReadMedicationHistory(new FakeMedicationApp(new FakeMedicationDatabase(foreign)), "1234"); throw new Exception("Expected foreign patient rejection"); }
        catch (InvalidOperationException) { Check(foreign.Closed, "Close snapshot when rejecting mismatched patient"); }
        Check(AccessSession.ReadMedicationRecords(new FakeNoteRecords(), 1234).Days.Count == 0, "Empty medication history");
        var longHistory = AccessSession.ReadMedicationRecords(new FakeNoteRecords(Enumerable.Range(0, 5001)
            .Select(i => Row(today, 1239, i, $"薬{i:D5}", 1)).ToArray()), 1234);
        Check(longHistory.Days.Single().Text.Contains("薬05000") && longHistory.Status.Contains("5001件"), "All medication rows beyond the notes' 5000-row cap remain available");
        ShowMedicationHistory(history);
        var expander = (Expander)MedicationDays.Children[0];
        Check(!expander.IsExpanded && expander.Content == null, "Dates start collapsed and create detail only when expanded");
        expander.IsExpanded = true;
        var groups = expander.Content as StackPanel ?? throw new InvalidOperationException("Missing date groups");
        Check(groups.Children.Count == 2, "Each date has two clinical groups");
        var box = (RichTextBox)((StackPanel)((System.Windows.Controls.Border)groups.Children[1]).Child).Children[1];
        box.SelectAll();
        var selected = box.Selection.Text;
        ShowMedicationHistory(history with { Days = history.Days.ToArray() });
        Check(ReferenceEquals(expander, MedicationDays.Children[0]) && expander.IsExpanded && box.Selection.Text == selected, "Refresh preserves expansion and selection");
        expander.IsExpanded = false;
        Check(!expander.IsExpanded, "Date can be collapsed again");
        ShowMedicationHistory(history with { PatientKey = "999" });
        Check(!ReferenceEquals(expander, MedicationDays.Children[0]) && !((Expander)MedicationDays.Children[0]).IsExpanded, "Patient change resets expanded state");
        ShowMedicationHistory(null);
        Check(MedicationDays.Children.Count == 0, "Disconnect clears medication history");
        return history;
    }
}

