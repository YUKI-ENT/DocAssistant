using System.Windows;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestCertificateAutoLoad(Action<bool, string> verify)
    {
        var store = CertificateDatabase();
        var older = new MedicalCertificate { ChartNumber = "123451", PatientKey = "old.mdb|123451", Name = "OLDER DOCUMENT", IssueDate = new DateTime(2025, 1, 2) };
        var first = store.Save(older); first = store.Save(first);
        var newer = store.Save(new MedicalCertificate { ChartNumber = "123452", PatientKey = "old.mdb|123452", Name = "LATEST DOCUMENT", IssueDate = new DateTime(2025, 2, 3) });
        verify(store.Latest("12345")!.DocumentId == newer.DocumentId && store.Latest("77777") == null, "Latest query selects most recently saved document across branches, not largest revision number");
        certificateDirty = false;
        WorkspaceTabs.SelectedItem = PdfEditorTab;
        const string source = "氏名：架空 花子\nフリガナ：ヤマダ ハナコ\n生年月日：昭和55年11月12日（1980年11月12日）\n性別：女\nカルテ番号：123451";
        var patient = new AccessPatientDisplay(true, "", source, DatabasePath: "new-location.mdb");
        TrackCertificatePatient(patient);
        certificateAutoLoadEnabled = true;
        try
        {
            WorkspaceTabs.SelectedItem = CertificateTab;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            verify(certificate.DocumentId == newer.DocumentId && certificateName.Text == "LATEST DOCUMENT" && certificate.IssueDate == new DateTime(2025, 2, 3), "Opening certificate tab loads current patient's latest saved content without changing issue date");
            verify(certificate.ChartNumber == "123452" && certificate.PatientKey == certificateCurrentKey, "Auto-load preserves stored branch and rebinds same patient after MDB path changes");
            certificateSections[0].Text = "Unsaved edit before returning";
            WorkspaceTabs.SelectedItem = PdfEditorTab; WorkspaceTabs.SelectedItem = CertificateTab;
            verify(!certificateDirty && certificateSections[0].Text == "Unsaved edit before returning" && certificate.Revision == 2, "Returning to tab saves pending edits before loading latest");
            var other = store.Save(new MedicalCertificate { ChartNumber = "654321", Name = "ANOTHER PATIENT" });
            TrackCertificatePatient(patient with { Text = source.Replace("123451", "654321") });
            verify(certificate.DocumentId == other.DocumentId && certificateName.Text == "ANOTHER PATIENT", "Patient change while certificate tab is visible loads that patient's document");
            TrackCertificatePatient(patient with { Text = source.Replace("123451", "777771") });
            verify(certificate.Revision == 0 && certificate.ChartNumber == "777771" && certificateName.Text == "HANAKO YAMADA" && !certificateDirty && store.Latest("77777") == null, "Patient without stored document gets fresh demographics without creating empty database history");
            TrackCertificatePatient(patient);
            string originalDatabase = settings.CertificateDatabasePath;
            certificateSections[2].Text = "Keep this draft if save fails";
            try
            {
                settings.CertificateDatabasePath = @"\\server\share\unsupported.sqlite";
                TrackCertificatePatient(patient with { Text = source.Replace("123451", "654321") });
                verify(certificateSections[2].Text == "Keep this draft if save fails" && certificateDirty && certificate.DocumentId == newer.DocumentId && certificateStatus.Text.StartsWith("処理できませんでした"), "Failed pre-load save keeps original dirty document instead of replacing it");
            }
            finally { settings.CertificateDatabasePath = originalDatabase; }
            certificateDirty = false;
        }
        finally { certificateAutoLoadEnabled = false; certificateSaveTimer.Stop(); }
        // Leave the final visual fixture loaded after the behavioral tests.
        DisplayCertificate(new MedicalCertificate { Name = "HANAKO YAMADA", Sex = "Female", Birthday = new DateTime(1980, 11, 12),
            Clinic = new CertificateClinic { Name = "Example Clinic (Fictional)", Address = "1-2-3 Example, Tokyo, Japan", Contact = "Tel: +81-3-0000-0000", Physician = "Example Physician, MD" },
            Sections = ["Allergic rhinitis", "Hypertension", "The patient is receiving outpatient treatment. Current symptoms are stable.", "Medication details are reviewed by the treating physician."] });
    }
}
