using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestCertificateStore(string folder, Action<bool, string> verify, List<BitmapSource> samplePages)
    {
        verify(MedicalCertificate.Headings.Length == 4 && MedicalCertificate.Labels.Length == 4 && certificateSections.Length == 4, "Exactly four clinical fields in model and UI");
        verify(MedicalCertificate.ReadSex("性別：男") == "Male" && MedicalCertificate.ReadSex("性別：女") == "Female" && MedicalCertificate.ReadSex("性別：不明（コード：9）") == "", "Sex is deterministic; unknown codes require manual selection");
        verify(MedicalCertificate.AgeOn(new DateTime(1980, 11, 12), new DateTime(2026, 11, 11)) == 45 &&
            MedicalCertificate.AgeOn(new DateTime(1980, 11, 12), new DateTime(2026, 11, 12)) == 46 &&
            MedicalCertificate.AgeOn(new DateTime(2027, 1, 1), new DateTime(2026, 1, 1)) == null &&
            MedicalCertificate.AgeOn(new DateTime(2000, 2, 29), new DateTime(2025, 2, 28)) == 25, "Age at issue date handles birthday, future date and leap day");
        var legacy = MedicalCertificate.Parse("""
            {"Version":1,"PatientKey":"old-workstation.mdb|123459","Purpose":"old purpose","Sections":["diagnosis","comorbidity","summary","treatment","old allergy","old travel","old extra"],"IssueDate":"2026-11-12T00:00:00","Clinic":{}}
            """);
        verify(legacy.Version == 2 && legacy.Sections.SequenceEqual(new[] { "diagnosis", "comorbidity", "summary", "treatment" }) && legacy.ChartNumber == "123459" && !JsonSerializer.Serialize(legacy).Contains("Purpose"), "Legacy JSON imports first four sections without removed fields");
        string testPath = Path.Combine(folder, "repository-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new SqliteCertificateStore(testPath);
        verify(store.History().Count == 0 && store.EnvironmentId.Length == 32, "SQLite schema and persistent environment created");
        legacy.Name = "PATIENT ONE"; legacy.Sex = "Male"; legacy.Material = "生成用の材料";
        var first = store.Save(legacy);
        verify(first.Revision == 1 && legacy.Revision == 0 && first.EnvironmentId == store.EnvironmentId, "Save returns committed metadata without mutating unsaved source");
        first.Sections[0] = "Revised diagnosis";
        var second = store.Save(first);
        verify(second.Revision == 2 && store.Load(second.DocumentId, 1).Sections[0] == "diagnosis" && store.Load(second.DocumentId, 2).Material == "生成用の材料", "Immutable revisions preserve old clinical content and editing material");
        int count = store.History().Count;
        bool conflict = false; try { store.Save(first); } catch (InvalidOperationException) { conflict = true; }
        verify(conflict && store.History().Count == count, "Stale writer rejected with no partial revision");
        var other = new MedicalCertificate { Name = "PATIENT TWO", ChartNumber = "999990" }; store.Save(other);
        verify(store.History("12345").Count == 2 && store.History("99999").Count == 1, "Patient history groups branch numbers without mixing other patients");
        var foreign = new MedicalCertificate { EnvironmentId = Guid.NewGuid().ToString("N") };
        conflict = false; try { store.Save(foreign); } catch (InvalidOperationException) { conflict = true; }
        verify(conflict, "Foreign database document requires explicit copy");
        var pdf = CertificateRenderer.CreatePdf(samplePages);
        var issue = store.PrepareIssue(second, pdf, "12345~0001~2026_11_12~英文診断書~RSB.pdf");
        verify(issue.Data.Revision == 3 && store.History("12345")[0].IssueId == issue.Id && !store.History("12345")[0].Exported && store.LoadPdf(issue.Id).SequenceEqual(pdf), "Prepared issue atomically stores revision and exact PDF before file output");
        bool ioFailed = false;
        try { CertificateRenderer.WriteBytes(Path.Combine(folder, Guid.NewGuid().ToString("N"), "missing", "output.pdf"), pdf); }
        catch (DirectoryNotFoundException) { ioFailed = true; }
        verify(ioFailed && store.LoadPdf(issue.Id).SequenceEqual(pdf), "Output failure leaves exact PDF available for recovery");
        string export = Path.Combine(folder, "stored-reissue.pdf"); CertificateRenderer.WriteBytes(export, store.LoadPdf(issue.Id)); store.RecordExport(issue.Id, export);
        verify(File.ReadAllBytes(export).SequenceEqual(pdf) && store.History("12345")[0].Exported, "Reissue preserves original bytes and records completed export");
        var reopened = new SqliteCertificateStore(testPath);
        verify(reopened.EnvironmentId == store.EnvironmentId && reopened.LoadPdf(issue.Id).SequenceEqual(pdf), "Reopened database preserves environment and issued PDF");
        string backup = Path.Combine(folder, "backup-" + Guid.NewGuid().ToString("N") + ".sqlite");
        store.Backup(backup); var backedUp = new SqliteCertificateStore(backup);
        verify(backedUp.History().Count == store.History().Count && backedUp.LoadPdf(issue.Id).SequenceEqual(pdf), "Online backup includes every revision and original PDF");
        store.Save(issue.Data); int changedCount = store.History().Count; store.Restore(backup);
        store = new SqliteCertificateStore(testPath);
        verify(store.History().Count == changedCount - 1 && Directory.GetFiles(folder, Path.GetFileName(testPath) + ".before-restore-*.sqlite").Length == 1, "Restore replaces database only after preserving a pre-restore backup");
        bool selfRejected = false; try { store.Backup(testPath); } catch (ArgumentException) { selfRejected = true; }
        verify(selfRejected && store.LoadPdf(issue.Id).SequenceEqual(pdf), "Self backup cannot overwrite active database");
        var invalidFile = Path.Combine(folder, "invalid-" + Guid.NewGuid().ToString("N") + ".sqlite"); File.WriteAllText(invalidFile, "not a database");
        bool invalidRejected = false; try { store.Restore(invalidFile); } catch (Exception ex) when (ex is SqliteException or InvalidDataException) { invalidRejected = true; }
        verify(invalidRejected && store.LoadPdf(issue.Id).SequenceEqual(pdf), "Invalid backup leaves live database intact");
        bool networkRejected = false; try { SqliteCertificateStore.ValidatePath(@"\\server\share\certificates.sqlite"); } catch (ArgumentException) { networkRejected = true; }
        verify(networkRejected, "Network database paths rejected");

        // Exercise the same save path used by the UI and the autosave timer against an isolated database.
        var ui = new MedicalCertificate { Material = "Saved material", Name = "UI PATIENT", Sex = "Female", Birthday = new DateTime(1980, 11, 12), IssueDate = new DateTime(2026, 11, 11) };
        DisplayCertificate(ui); certificateSections[0].Text = "UI diagnosis";
        SaveCertificateToDatabase(true);
        verify(!certificateDirty && certificate.Revision == 1 && certificateAge.Text.Contains("45"), "UI save commits full draft and displays issue-date age");
        var loaded = CertificateDatabase().Load(certificate.DocumentId, 1);
        verify(loaded.Sex == "Female" && loaded.Material == "Saved material" && loaded.Sections[0] == "UI diagnosis", "UI database draft restores demographics and all input");
        certificateSections[1].Text = "Updated comorbidity";
        certificateSaveTimer.Interval = TimeSpan.FromMilliseconds(30); certificateSaveTimer.Start();
        await Task.Delay(120);
        verify(!certificateDirty && certificate.Revision == 2, "Debounced timer autosaves input changes");
        certificateSaveTimer.Interval = TimeSpan.FromHours(1);
        var history = new CertificateHistoryWindow(CertificateDatabase(), "", folder) { Owner = this };
        try { history.Show(); RenderVisual(history, Path.Combine(folder, "history.png"), 1000, 660); }
        finally { history.Close(); }
        // Preserve the completed synthetic preview for visual review after repository tests.
        ui.Clinic = new CertificateClinic { Name = "Example Clinic (Fictional)", Address = "1-2-3 Example, Tokyo, Japan", Contact = "Tel: +81-3-0000-0000", Physician = "Example Physician, MD" };
        ui.Sections = ["Allergic rhinitis", "Hypertension", "The patient is receiving outpatient treatment. Current symptoms are stable.", "Medication details are reviewed by the treating physician."];
        DisplayCertificate(ui);
        var updated = PreviewCertificate(); CertificateRenderer.Export(Path.Combine(folder, "four-fields.pdf"), updated);
        var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(updated[0]));
        using var imageFile = File.Create(Path.Combine(folder, "four-fields.png")); image.Save(imageFile);
    }
}
