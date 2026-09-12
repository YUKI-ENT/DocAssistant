using System.Windows.Controls;

namespace DocAssistant;

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
        var currentRecords = new FakeNoteRecords(
            Row(null, 1234, 2, "当日薬B", 0), Row(null, 1234, 1, "当日薬A", 1.5m),
            new Dictionary<string, object?>(Row(null, 1234, 0, "過去薬", 1)) { ["受診コード"] = "199" });
        var currentControls = new FakeCurrentMedicationControls(currentRecords);
        var current = AccessSession.ReadCurrentMedication(currentControls, "1234");
        Check(currentRecords.Closed && !current.Text.Contains("過去薬") && current.Text.IndexOf("当日薬A") < current.Text.IndexOf("当日薬B"),
            "Current subform visit only, sorted, clone closed");
        Check(current.Text.Contains("当日薬B　0") && current.Text.Contains("当日薬A　1.5") && !current.Text.Contains("数量：") && !current.Text.Contains("受診 200"), "Current medication preserves quantities without identity headers");
        Check(AccessSession.ReadCurrentMedicationRecords(new FakeNoteRecords(), 1234, "200").Text == "", "Empty current visit clears medications");
        try
        {
            AccessSession.ReadCurrentMedicationRecords(new FakeNoteRecords(Row(null, 1235, 1, "別枝", 1)), 1234, "200");
            throw new Exception("Expected exact patient rejection");
        }
        catch (InvalidOperationException) { }
        var switchingRecords = new FakeNoteRecords(Row(null, 1234, 1, "当日薬", 1));
        try
        {
            AccessSession.ReadCurrentMedication(new FakeCurrentMedicationControls(switchingRecords, true), "1234");
            throw new Exception("Expected visit switch rejection");
        }
        catch (InvalidOperationException) { Check(switchingRecords.Closed, "Close clone after visit switch"); }
        ShowChart(new(true, "", TodayMedication: current));
        Check((string?)ChartTodayMedication.Tag == current.Text, "Today's medication shown in its own panel");
        ShowChart(new(false, ""));
        Check((string?)ChartTodayMedication.Tag == "", "Disconnect clears today's medication");
        foreach (var nameField in new[] { "薬名", "検査項目名", "行為名" })
        {
            Dictionary<string, object?> ClinicalRow(string visit, int order, string name) => new()
            {
                ["受診コード"] = visit, ["カルテ番号"] = 1234, ["順番"] = order,
                [nameField] = name, ["数量"] = 1
            };
            var rows = new FakeNoteRecords(ClinicalRow("199", 1, "過去の項目"), ClinicalRow("200", 2, "項目B"), ClinicalRow("200", 1, "項目A"));
            var section = AccessSession.ReadCurrentMedicationRecords(rows, 1234, "200", nameField);
            Check(!section.Text.Contains("過去") && section.Text.IndexOf("項目A") < section.Text.IndexOf("項目B"), "Filter and sort each clinical name field: " + nameField);
        }
        var todayDisplay = new AccessPatientDisplay(true, "", Text: "患者A", TodayMedication: current with { Visit = "200" },
            TodayTests: new("検査A　1", Visit: "200"), TodayProcedures: new("", "データなし", "200"),
            TodayInjections: new("注射A　1", Visit: "200"));
        ShowChart(todayDisplay);
        Check(TodayMedicationExpander.IsEnabled && TodayTestsExpander.IsEnabled && TodayInjectionsExpander.IsEnabled && !TodayProceduresExpander.IsEnabled,
            "Only sections containing data can expand");
        Check(!TodayMedicationExpander.IsExpanded && !TodayTestsExpander.IsExpanded, "Sections initially collapse");
        TodayTestsExpander.IsExpanded = true;
        ShowChart(todayDisplay);
        Check(TodayTestsExpander.IsExpanded, "Polling preserves expansion");
        ShowChart(todayDisplay with { TodayTests = new("別受診の検査", Visit: "201") });
        Check(!TodayTestsExpander.IsExpanded, "Visit changes collapse section");
        ShowChart(new(false, ""));
        Check(!TodayTestsExpander.IsEnabled && !TodayInjectionsExpander.IsEnabled && (string?)ChartTodayInjections.Tag == "", "Disconnect clears all sections");
        var basicRows = new FakeNoteRecords(
            new() { ["受診コード"] = "199", ["カルテ番号"] = 1234, ["基本診療項目"] = "過去診療", ["数量"] = 1 },
            new() { ["受診コード"] = "200", ["カルテ番号"] = 1234, ["基本診療項目"] = "基本診療A", ["数量"] = 1 });
        var basic = AccessSession.ReadCurrentMedicationRecords(basicRows, 1234, "200", "基本診療項目", false);
        Check(basic.Text == "基本診療A　1", "Basic care supports records without order field and excludes other visits");
        ShowChart(new(true, "", TodayBasic: basic));
        Check(TodayBasicExpander.IsEnabled && !TodayBasicExpander.IsExpanded, "Basic care starts collapsed with data");
        ShowChart(new(false, ""));
        Check(!TodayBasicExpander.IsEnabled && (string?)ChartTodayBasic.Tag == "", "Basic care clears on disconnect");
        var unitRows = new FakeNoteRecords(
            new Dictionary<string, object?>(Row(null, 1234, 1, "単位確認薬", 1.5m)) { ["区分"] = "錠" },
            new Dictionary<string, object?>(Row(null, 1234, 2, "単位未登録薬", 0)) { ["区分"] = DBNull.Value });
        var withUnits = AccessSession.ReadCurrentMedication(new FakeCurrentMedicationControls(unitRows), "1234");
        Check(withUnits.Text.Contains("単位確認薬　1.5錠") && withUnits.Text.Contains("単位未登録薬　0") && !withUnits.Text.Contains("数量：") && unitRows.Closed,
            "Medication reads unit from kubun and preserves null units and quantities");
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


public sealed class FakeCurrentMedicationControls(FakeNoteRecords records, bool changing = false)
{
    public FakeCurrentMedicationSub this[string name] => name == "受診投薬サブフォーム" ? new(records, changing) : throw new MissingMemberException(name);
}
public sealed class FakeCurrentMedicationSub(FakeNoteRecords records, bool changing)
{
    public FakeCurrentMedicationForm Form { get; } = new(records, changing);
}
public sealed class FakeCurrentMedicationForm(FakeNoteRecords records, bool changing)
{
    public FakeCurrentVisitControls Controls { get; } = new(changing);
    public FakeNoteRecords RecordsetClone => records;
}
public sealed class FakeCurrentVisitControls(bool changing)
{
    private int reads;
    public FakePatientValue this[string name] => new(changing && ++reads > 1 ? "201" : "200");
}
