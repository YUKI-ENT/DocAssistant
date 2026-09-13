using System.IO;
using System.Windows;
using System.Runtime.InteropServices;

namespace DocAssistant;

// Fabricated in-memory DAO objects. These tests never attach to Access or write a database.
public sealed class FakeReferralField(FakeReferralRecord record, string name)
{
    public string Name => name;
    public int Attributes => name == "紹介番号" && record.AutoNumber ? 16 : 0;
    public int Type => name is "紹介番号" or "カルテ番号" ? 4 : name == "日付" ? 8 : name.StartsWith("紹介先") ? 10 : 12;
    public int Size => 255;
    public object? Value { get => record.Values[name]; set => record.Values[name] = value; }
}
public sealed class FakeReferralFields(FakeReferralRecord record)
{
    public FakeReferralField this[string name] => new(record, name);
}
public sealed class FakeReferralRecord(Dictionary<string, object?> row)
{
    public Dictionary<string, object?> Saved = new(row);
    private Dictionary<string, object?>? staged;
    public Dictionary<string, object?> Values => staged ?? Saved;
    public bool AutoNumber, Missing, FailUpdate, Closed;
    public int Updates, Cancels, DuplicateFailures;
    public Action? OnEdit, BeforeUpdate;
    public bool LockEdits { get; set; }
    public bool EOF => Missing;
    public FakeReferralFields Fields => new(this);
    public object LastModified => 1;
    public object Bookmark { get; set; } = 0;
    public void Edit() { staged = new(Saved); OnEdit?.Invoke(); }
    public void AddNew() { staged = Saved.Keys.ToDictionary(k => k, _ => (object?)null); OnEdit?.Invoke(); }
    public void Update()
    {
        if (FailUpdate) throw new IOException("Simulated update failure");
        BeforeUpdate?.Invoke();
        if (DuplicateFailures > 0) { DuplicateFailures--; throw new COMException("Duplicate key", unchecked((int)0x800A0BCE)); }
        if (staged!["紹介番号"] == null) throw new InvalidOperationException("Manual number must be assigned");
        Saved = new(staged); staged = null; Updates++;
    }
    public void CancelUpdate() { staged = null; Cancels++; }
    public void Close() => Closed = true;
}
public sealed class FakeReferralDatabase(FakeReferralRecord record)
{
    public string Sql = "";
    public object? Maximum = 900;
    public bool UniqueIndex = true, CompositeIndex;
    public int MaximumReads;
    public FakeReferralTableDefs TableDefs => new(this);
    public object OpenRecordset(string sql, int type, int options = 0)
    {
        Sql = sql;
        if (sql == "SELECT Max([紹介番号]) AS [最大紹介番号] FROM [紹介状];")
        {
            if (type != 4 || options != 4) throw new InvalidOperationException("MAX must be read-only");
            MaximumReads++;
            return new FakeNoteRecords(new Dictionary<string, object?> { ["最大紹介番号"] = Maximum });
        }
        return record;
    }
}
public sealed class FakeReferralTableDefs(FakeReferralDatabase db)
{
    public FakeReferralTable this[string name] => name == "紹介状" ? new(db) : throw new InvalidOperationException();
}
public sealed class FakeReferralTable(FakeReferralDatabase db)
{
    public FakeAccessCollection Indexes => new(new FakeReferralIndex(db));
}
public sealed class FakeReferralIndex(FakeReferralDatabase db)
{
    public bool Unique => db.UniqueIndex;
    public FakeAccessCollection Fields => db.CompositeIndex
        ? new(new FakeNoteField("紹介番号"), new FakeNoteField("カルテ番号")) : new(new FakeNoteField("紹介番号"));
}
public sealed class FakeReferralPatientControl(FakeReferralApp app)
{
    public object Value => app.PatientNumber;
}
public sealed class FakeReferralPatientControls(FakeReferralApp app)
{
    public FakeReferralPatientControl this[string name] => name == "カルテ番号" ? new(app) : throw new InvalidOperationException();
}
public sealed class FakeReferralPatientForm(FakeReferralApp app)
{
    public bool NewRecord => false;
    public FakeReferralPatientControls Controls => new(app);
}
public sealed class FakeReferralForms(FakeReferralApp app)
{
    public object this[string name] => name == "患者マスター" ? new FakeReferralPatientForm(app) : name == "紹介状" ? new FakeOpenedReferralForm(app) : throw new InvalidOperationException();
}
public sealed class FakeReferralProject(FakeReferralApp app)
{
    public string FullName => app.Path;
    public FakeAccessCollection AllForms => new(new FakeAccessMetadata("紹介状", app.ReferralFormOpen));
}
public sealed class FakeReferralApp(FakeReferralRecord record)
{
    public string Path = @"D:\Clinical\client.mdb";
    public long PatientNumber = 12345;
    public bool ReferralFormOpen;
    public long OpenedNumber = 80, OpenedChart = 12341;
    public FakeReferralCommands DoCmd => new(this);
    public int OpenCalls, SelectCalls;
    public string OpenWhere = "";
    public FakeReferralProject CurrentProject => new(this);
    public FakeReferralForms Forms => new(this);
    public FakeReferralDatabase Database { get; } = new(record);
    public FakeReferralDatabase CurrentDb() => Database;
}

public sealed class FakeOpenedReferralForm(FakeReferralApp app)
{
    public bool NewRecord => false;
    public FakeOpenedReferralControls Controls => new(app);
}
public sealed class FakeOpenedReferralControls(FakeReferralApp app)
{
    public FakePatientValue this[string name] => new(name == "紹介番号" ? app.OpenedNumber : app.OpenedChart);
}
public sealed class FakeReferralCommands(FakeReferralApp app)
{
    public void OpenForm(string name, int view, object? filter = null, string? where = null, object? mode = null, int window = 0, object? args = null)
    {
        if (name != "紹介状" || view != 0) throw new InvalidOperationException("Unexpected form");
        app.OpenCalls++; app.OpenWhere = where ?? ""; app.ReferralFormOpen = true;
    }
    public void SelectObject(int type, string name, bool navigation)
    {
        if (type != 2 || name != "紹介状" || navigation) throw new InvalidOperationException("Unexpected selection");
        app.SelectCalls++;
    }
    public void Restore() { }
}

public partial class MainWindow
{
    private async Task TestReferrals()
    {
        var folder = Path.GetFullPath("tmp/referral-test"); Directory.CreateDirectory(folder);
        try
        {
            var baseline = new ReferralLetter(80, 12341, new DateTime(2026, 8, 1), "架空総合病院", "内科", "テスト医師",
                "動作確認用の傷病名", "入力補助機能の確認", "架空の経過\r\n複数行の確認", "検査情報の入力確認", "実在の患者情報ではありません。", "検査結果の入力確認");
            Dictionary<string, object?> Row(ReferralLetter letter)
            {
                var row = letter.Values(); row["紹介番号"] = letter.Number; row["カルテ番号"] = letter.ChartNumber; return row;
            }
            var patient = new ReferralPatient(@"D:\Clinical\client.mdb", 12345, "動作確認 太郎（架空）");
            Check(AccessSession.ReferralSql(12345).Contains(">= 12340") && AccessSession.ReferralSql(12345).Contains("< 12350"), "All branches use a numeric range");
            Check(AccessSession.ReferralSql(long.MaxValue).Contains("9223372036854775810"), "Range upper bound does not overflow");
            foreach (var field in new[] { "紹介先1", "紹介先2", "紹介先先生" })
                Check(AccessSession.ReferralChoicesSql(field).Contains("GROUP BY Trim") && AccessSession.ReferralChoicesSql(field).Contains("Count(*) DESC") &&
                    !AccessSession.ReferralChoicesSql(field).Contains("カルテ番号"), "Global unique frequency choices: " + field);
            var records = new FakeNoteRecords(Enumerable.Range(0, 300).Select(i => Row(baseline with { Number = i, ChartNumber = 12340 + i % 10 })).ToArray());
            var letters = AccessSession.ParseReferralRows(AccessSession.ReadReferralRows(records), patient.ChartNumber);
            Check(letters.Count == 300 && letters[0].Number == 299 && letters[^1].Number == 0 && letters[0].Treatment.Contains("\r\n"), "Batched history preserves all rows and line endings");
            bool mismatch = false;
            try { AccessSession.ParseReferralRows([Row(baseline with { ChartNumber = 12350 })], patient.ChartNumber); }
            catch (InvalidOperationException) { mismatch = true; }
            Check(mismatch, "Reject another patient group");
            var copy = baseline.CopyFor(patient.ChartNumber, DateTime.Today);
            Check(copy.Number == null && copy.ChartNumber == 12345 && copy.Date == DateTime.Today && copy.Treatment == baseline.Treatment && baseline.Number == 80,
                "Copy uses current branch and today's date without altering original");
            var draft = baseline with { Purpose = "変更後の紹介目的", Remarks = "" };
            var writeRecords = new FakeReferralRecord(Row(baseline)); var app = new FakeReferralApp(writeRecords);
            var saved = AccessSession.SaveReferralRecord(app, patient, baseline, draft);
            Check(saved == draft && writeRecords.Updates == 1 && writeRecords.Closed && writeRecords.LockEdits, "One atomic update retains exact original branch");
            Check(app.Database.Sql.Contains("[紹介番号] = 80 AND [カルテ番号] = 12341"), "Update targets one exact ID and branch");
            var addedRecords = new FakeReferralRecord(Row(baseline));
            var added = AccessSession.SaveReferralRecord(new FakeReferralApp(addedRecords), patient, copy, copy);
            Check(added.Number == 901 && added.ChartNumber == 12345 && addedRecords.Updates == 1, "New record receives global MAX + 1");
            var emptyRecords = new FakeReferralRecord(Row(baseline)); var emptyApp = new FakeReferralApp(emptyRecords);
            emptyApp.Database.Maximum = DBNull.Value;
            Check(AccessSession.SaveReferralRecord(emptyApp, patient, copy, copy).Number == 1, "Empty table starts at one");
            Check(AccessSession.NextReferralNumber(int.MaxValue - 1) == int.MaxValue, "Long integer upper boundary");
            var collisionRecords = new FakeReferralRecord(Row(baseline)) { DuplicateFailures = 1 };
            var collisionApp = new FakeReferralApp(collisionRecords);
            collisionRecords.BeforeUpdate = () => { if (collisionRecords.DuplicateFailures > 0) collisionApp.Database.Maximum = 901; };
            var retried = AccessSession.SaveReferralRecord(collisionApp, patient, copy, copy);
            Check(retried.Number == 902 && collisionRecords.Cancels == 1 && collisionRecords.Updates == 1 && collisionApp.Database.MaximumReads == 2,
                "Known duplicate cancels and rereads global maximum without overwriting another insert");
            var longRecords = new FakeReferralRecord(Row(baseline));
            var longDraft = draft with { TestResults = new string('検', 12000) + "\r\n結果" };
            Check(AccessSession.SaveReferralRecord(new FakeReferralApp(longRecords), patient, baseline, longDraft).TestResults == longDraft.TestResults, "Long Text is not truncated to 255 characters");
            void Rejected(FakeReferralRecord rec, FakeReferralApp fake, ReferralLetter before, ReferralLetter after, string message)
            {
                bool failed = false;
                try { AccessSession.SaveReferralRecord(fake, patient, before, after); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { failed = true; }
                Check(failed && rec.Updates == 0, message);
            }
            var auto = new FakeReferralRecord(Row(baseline)) { AutoNumber = true };
            Rejected(auto, new(auto), copy, copy, "Reject schema mismatch with confirmed manual long-integer numbering");
            var overflow = new FakeReferralRecord(Row(baseline)); var overflowApp = new FakeReferralApp(overflow);
            overflowApp.Database.Maximum = int.MaxValue;
            Rejected(overflow, overflowApp, copy, copy, "Long-integer overflow blocked before insertion");
            var unindexed = new FakeReferralRecord(Row(baseline)); var unindexedApp = new FakeReferralApp(unindexed);
            unindexedApp.Database.UniqueIndex = false;
            Rejected(unindexed, unindexedApp, copy, copy, "Unindexed numbering is blocked without altering the schema");
            unindexedApp.Database.UniqueIndex = true; unindexedApp.Database.CompositeIndex = true;
            Rejected(unindexed, unindexedApp, copy, copy, "Composite key cannot guarantee globally unique referral numbers");
            var repeated = new FakeReferralRecord(Row(baseline)) { DuplicateFailures = 5 };
            var repeatedApp = new FakeReferralApp(repeated);
            Rejected(repeated, repeatedApp, copy, copy, "Repeated collisions stop after three attempts");
            Check(repeatedApp.Database.MaximumReads == 3 && repeated.Cancels == 3, "Number retry count is bounded");
            var conflict = new FakeReferralRecord(Row(baseline with { Purpose = "別端末で変更" }));
            Rejected(conflict, new(conflict), baseline, draft, "Concurrent edits are not overwritten");
            Check(conflict.Cancels == 1 && conflict.Closed, "Conflict cancels edit and closes recordset");
            var switched = new FakeReferralRecord(Row(baseline)); var switchedApp = new FakeReferralApp(switched);
            switched.OnEdit = () => switchedApp.PatientNumber = 99999;
            Rejected(switched, switchedApp, baseline, draft, "Patient change before Update aborts save");
            Check(switched.Cancels == 1, "Patient switch cancels staged fields");
            var openForm = new FakeReferralRecord(Row(baseline));
            Rejected(openForm, new(openForm) { ReferralFormOpen = true }, baseline, draft, "Open referral form prevents lifecycle interference");
            var wrongDb = new FakeReferralRecord(Row(baseline));
            Rejected(wrongDb, new(wrongDb) { Path = @"D:\Other\client.mdb" }, baseline, draft, "Wrong database is rejected");
            var oversized = new FakeReferralRecord(Row(baseline));
            Rejected(oversized, new(oversized), baseline, draft with { Destination1 = new string('A', 256) }, "Short text field length validated before edit");
            var failure = new FakeReferralRecord(Row(baseline)) { FailUpdate = true };
            Rejected(failure, new(failure), baseline, draft, "Update failure preserves original");
            Check(Equals(failure.Saved["紹介目的"], baseline.Purpose) && failure.Cancels == 1 && failure.Closed, "Failed write leaves no staged edit");

            var opener = new FakeReferralApp(new FakeReferralRecord(Row(baseline)));
            AccessSession.OpenReferralForm(opener, patient, baseline);
            Check(opener.OpenCalls == 1 && opener.SelectCalls == 1 && opener.OpenWhere == "[紹介番号] = 80 AND [カルテ番号] = 12341", "Open form uses exact saved number and branch");
            AccessSession.OpenReferralForm(opener, patient, baseline);
            Check(opener.OpenCalls == 1 && opener.SelectCalls == 2, "Existing matching form is brought forward without reopening");
            opener.OpenedNumber = 999;
            bool wrongForm = false;
            try { AccessSession.OpenReferralForm(opener, patient, baseline); } catch (InvalidOperationException) { wrongForm = true; }
            Check(wrongForm && opener.OpenCalls == 1 && opener.SelectCalls == 2, "Existing other referral is not moved or closed");
            var wrongOpen = new FakeReferralApp(new FakeReferralRecord(Row(baseline))) { PatientNumber = 99999 };
            bool wrongOpenPatient = false;
            try { AccessSession.OpenReferralForm(wrongOpen, patient, baseline); } catch (InvalidOperationException) { wrongOpenPatient = true; }
            Check(wrongOpenPatient && wrongOpen.OpenCalls == 0, "Patient mismatch blocks opening");
            var overridden = new FakeReferralApp(new FakeReferralRecord(Row(baseline))) { OpenedNumber = 999 };
            bool wrongEvent = false;
            try { AccessSession.OpenReferralForm(overridden, patient, baseline); } catch (InvalidOperationException) { wrongEvent = true; }
            Check(wrongEvent && overridden.OpenCalls == 1 && overridden.SelectCalls == 0, "Open event changing record is detected");
            currentReferralPatient = editingReferralPatient = patient; referralLoaded = true;
            referralLetters = [baseline, baseline with { Number = 70, Date = new DateTime(2026, 7, 1) }];
            ReferralDestination1.ItemsSource = new[] { new ReferralSuggestion("架空総合病院", 30), new ReferralSuggestion("サンプル診療所", 12) };
            ReferralDestination2.ItemsSource = new[] { new ReferralSuggestion("内科", 24), new ReferralSuggestion("外科", 10) };
            ReferralDoctor.ItemsSource = new[] { new ReferralSuggestion("テスト医師", 20) };
            DisplayReferral(baseline, 0); WorkspaceTabs.SelectedItem = ReferralTab;
            MoveReferral(1); Check(referralBaseline?.Number == 70 && ReferralNewer.IsEnabled, "Previous/next history navigation");
            MoveReferral(-1); Check(referralBaseline == baseline && !referralDirty, "Navigation returns clean baseline");
            var medicationDate = new DateTime(2026, 8, 1);
            var longDrug = "架空薬剤" + new string('長', 90);
            var prescriptions = ReferralPrescription.FromHistory(new("1234", "", [
                new("2026/07/01", "", "", Rows: [new(medicationDate.AddMonths(-1), "old", 12345, 1, "以前の薬", "1")]),
                new("2026/08/01", "", "", Rows: [new(medicationDate, "latest", 12345, 2, longDrug, "2"), new(medicationDate, "latest", 12345, 1, "架空薬A", "1")]) ]));
            Check(prescriptions.Count == 2 && prescriptions[0].DateLabel == "2026/08/01" && prescriptions[0].Label.EndsWith("…"), "Prescription choices are newest first with abbreviated preview");
            Check(prescriptions[0].Content.StartsWith("架空薬A") && prescriptions[0].Text.Contains(longDrug), "Prescription preserves complete medication content and row order");
            ReferralMedication.ItemsSource = prescriptions;
            Check(!ReferralAddMedication.IsEnabled, "Prescription append needs a selection");
            ReferralMedication.SelectedIndex = 0;
            Check(!referralDirty && ReferralAddMedication.IsEnabled, "Selecting a prescription does not alter draft");
            ReferralTests.Text = "既存の検査本文";
            AddReferralMedication(this, new RoutedEventArgs());
            Check(ReferralTests.Text == "既存の検査本文\r\n" + prescriptions[0].Text + "\r\n" && referralDirty, "Append full dated prescription without overwriting existing text");
            ReferralTests.Text = "";
            AddReferralMedication(this, new RoutedEventArgs());
            Check(ReferralTests.Text == prescriptions[0].Text + "\r\n", "Empty test field has no leading blank line");
            Check(ReferralPrescription.FromHistory(null).Count == 0, "Failed history cannot retain old prescriptions");
            ReferralPurpose.ItemsSource = new[] { "精査", "加療" };
            ReferralPurpose.SelectedIndex = 0;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(CaptureReferral().Purpose == "精査" && referralDirty, "Purpose selection updates draft");
            ReferralTemplate.ItemsSource = new[] { "追加文" }; ReferralTemplate.SelectedIndex = 0;
            ReferralTests.Text = "前後"; ReferralTests.CaretIndex = 1;
            InsertReferralTemplate(new System.Windows.Controls.Button { Tag = "Cursor" }, new RoutedEventArgs());
            Check(ReferralTests.Text == "前追加文後", "Template inserts at retained caret without replacing text");
            InsertReferralTemplate(new System.Windows.Controls.Button { Tag = "Start" }, new RoutedEventArgs());
            InsertReferralTemplate(new System.Windows.Controls.Button { Tag = "End" }, new RoutedEventArgs());
            Check(ReferralTests.Text == "追加文前追加文後追加文", "Template supports start and end");
            TrackReferralPatient(new(true, "", "氏名：動作確認 太郎（架空）\r\nカルテ番号：12345", DatabasePath: patient.DatabasePath, PatientMemo: "注意A\r\n注意B"));
            ReferralRemarks.Text = "既存備考";
            AddReferralAttention(this, new RoutedEventArgs());
            Check(ReferralRemarks.Text == "既存備考\r\n注意A\r\n注意B", "Attention appends intact multiline memo");
            ShowPatientFields("氏名：架空", "注意A");
            Check(PatientFieldsGrid.Children.OfType<System.Windows.Controls.TextBox>().Last().Foreground == System.Windows.Media.Brushes.Red, "Attention is red");
            ReferralPurpose.Text = "編集途中の下書き";
            Check(referralDirty && ReferralSave.IsEnabled, "Editing enables save");
            TrackReferralPatient(new(true, "", "氏名：別の患者\r\nカルテ番号：99999", DatabasePath: patient.DatabasePath));
            Check(referralDirty && ReferralPurpose.Text == "編集途中の下書き" && !ReferralSave.IsEnabled && ReferralPatientWarning.Visibility == Visibility.Visible,
                "Patient switch preserves draft and blocks wrong-patient save");
            var testsBefore = ReferralTests.Text;
            AddReferralMedication(this, new RoutedEventArgs());
            Check(ReferralTests.Text == testsBefore && !ReferralAddMedication.IsEnabled && !ReferralMedication.IsEnabled, "Patient switch blocks prescription append");
            var remarksBefore = ReferralRemarks.Text;
            AddReferralAttention(this, new RoutedEventArgs());
            Check(ReferralRemarks.Text == remarksBefore && !ReferralAddAttention.IsEnabled, "Different patient attention cannot enter draft");
            currentReferralPatient = patient; DisplayReferral(baseline, 0);
            CopyReferral(this, new RoutedEventArgs()); Check(referralDirty && referralBaseline is { Number: null } && referralBaseline.ChartNumber == patient.ChartNumber, "Copy button creates local draft");
            DisplayReferral(baseline, 0); ReferralStatus.Text = "動作確認用の架空データです。Accessへの接続・保存は行っていません。";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ReferralScroll.ScrollToTop(); UpdateLayout();
            RenderVisual((FrameworkElement)Content, Path.Combine(folder, "referral.png"), 1740, 940);
            ReferralScroll.ScrollToVerticalOffset(350); UpdateLayout();
            RenderVisual((FrameworkElement)Content, Path.Combine(folder, "prescription.png"), 1740, 940);
            ReferralScroll.ScrollToTop(); UpdateLayout();
            ReferralDestination1.IsDropDownOpen = true;
            ReferralDestination1.IsDropDownOpen = false;
            ToggleLlmPane(this, new RoutedEventArgs()); ToggleChartPane(this, new RoutedEventArgs());
            Width = 1380; UpdateLayout();
            RenderVisual(this, Path.Combine(folder, "wide.png"), 1380, 940);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: 300-row branch history, frequency SQL, navigation, copy, draft isolation, exact-row writes, manual MAX+1, empty table, duplicate retry, unique index, overflow, Long Text, conflict and failure handling, form isolation, field limits, UI rendering. No real database access.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { referralDirty = false; Close(); }
    }
}
