using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf.IO;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestCertificateAsync()
    {
        string folder = Path.GetFullPath("tmp/certificate-test"); Directory.CreateDirectory(folder);
        settings.CertificateDatabasePath = Path.Combine(folder, "ui-" + Guid.NewGuid().ToString("N") + ".sqlite");
        certificateSaveTimer.Interval = TimeSpan.FromHours(1);
        var checks = new List<string>();
        void Verify(bool condition, string message) { Check(condition, message); checks.Add(message); }
        void SaveImage(BitmapSource image, string file)
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(folder, file)); png.Save(stream);
        }
        int SignatureInk(BitmapSource bitmap)
        {
            int stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
            int count = 0;
            for (int y = (int)((CertificateRenderer.Height - 200) * 2); y < (int)((CertificateRenderer.Height - 135) * 2); y++)
                for (int x = 120; x < bitmap.PixelWidth - 120; x++)
                { int n = y * stride + x * 4; if (pixels[n] < 80 && pixels[n + 1] < 80 && pixels[n + 2] < 80 && pixels[n + 3] > 0) count++; }
            return count;
        }
        try
        {
            Verify(MedicalCertificate.Romanize("ﾔﾏﾀﾞ ﾀﾛｳ") == "YAMADA TAROU", "Half-width kana normalize and deterministic name candidate");
            Verify(MedicalCertificate.Romanize("はっとり しんいち") == "HATTORI SHIN'ICHI", "Geminate consonant and N before vowel");
            Verify(MedicalCertificate.Romanize("オーエン") == "" && MedicalCertificate.Romanize("山田") == "", "Unsupported spelling requires manual input");
            Verify(MedicalCertificate.CertificateNameCandidate("ﾔﾏﾀﾞ　ﾊﾅｺ") == "HANAKO YAMADA", "Certificate candidate uses given-name then surname with normalized separator");
            Verify(MedicalCertificate.CertificateNameCandidate("ヤマダハナコ") == "" && MedicalCertificate.CertificateNameCandidate("ヤマダ アンナ マリア") == "", "Ambiguous name boundaries require manual input instead of guessed reversal");
            Verify(MedicalCertificate.ReadBirthday("生年月日：昭和55年11月12日（1980年11月12日）") == new DateTime(1980, 11, 12), "Gregorian birthday extracted from existing validated date");
            Verify(MedicalCertificate.ReadBirthday("生年月日：令和1年2月30日") == null, "Invalid birthday remains missing");
            var data = new MedicalCertificate { Name = "YAMADA TARO", Sex = "Male", Birthday = new DateTime(1980, 11, 12), IssueDate = new DateTime(2026, 11, 12),
                Clinic = new CertificateClinic { Name = "Example Clinic (Fictional)", Address = "1-2-3 Example, Tokyo, Japan", Contact = "Tel: +81-3-0000-0000", Physician = "Example Physician, MD" },
                Sections = ["Allergic rhinitis", "", "The patient is receiving outpatient treatment. Current symptoms are stable.", "Medication details to be entered by the treating physician."] };
            DisplayCertificate(data);
            var single = PreviewCertificate(); Verify(single.Count == 1, "Short certificate fits one A4 page");
            Verify(SignatureInk(single[0]) > 100, "Single-page signature is grouped in the lower clinic region");
            SaveImage(single[0], "single-preview.png");
            CertificateRenderer.Export(Path.Combine(folder, "single.pdf"), single);
            using (var pdf = PdfReader.Open(Path.Combine(folder, "single.pdf"), PdfDocumentOpenMode.Import))
                Verify(pdf.PageCount == 1 && Math.Abs(pdf.Pages[0].Width.Point - 595.276) < .1 && Math.Abs(pdf.Pages[0].Height.Point - 841.89) < .1, "PDF has A4 dimensions");
            certificateSections[2].Text = string.Join("\n\n", Enumerable.Range(1, 30).Select(n => $"Clinical record {n:D2}: The patient has been followed at this clinic. This is fictional data used to verify automatic pagination and preservation of all paragraphs. Symptoms, medications and the need for assistance must be reviewed by the treating physician."));
            var longData = CaptureCertificate();
            var pages = PreviewCertificate(); Verify(pages.Count >= 3, "Long section paginates across multiple pages");
            Verify(pages.Take(pages.Count - 1).All(p => SignatureInk(p) == 0) && SignatureInk(pages[^1]) > 100, "Only the final page contains a signature in the reserved lower region");
            CertificateRenderer.Export(Path.Combine(folder, "multiple.pdf"), pages);
            var rendered = await PdfRenderer.RenderAsync(await File.ReadAllBytesAsync(Path.Combine(folder, "multiple.pdf")));
            Verify(rendered.Count == pages.Count, "PDF round-trip preserves every preview page");
            for (int i = 0; i < rendered.Count; i++) SaveImage(rendered[i].Image, $"pdf-page-{i + 1}.png");
            var restored = JsonSerializer.Deserialize<MedicalCertificate>(JsonSerializer.Serialize(longData))!;
            Verify(restored.Sections[2] == longData.Sections[2] && restored.Clinic.Physician == data.Clinic.Physician && restored.Birthday == data.Birthday, "Draft round-trip preserves sections, clinic snapshot and dates");
            var oldSettings = JsonSerializer.Deserialize<AppSettings>("{}")!;
            Verify(oldSettings.CertificateClinic.Name == "", "Old settings load without migration");
            Verify(oldSettings.CertificateRsbaseNaming && oldSettings.CertificateRegistrationTitle == "英文診断書", "Old settings default to RSBase certificate output");
            var legacyDraft = new MedicalCertificate { PatientKey = "fake.mdb|123459" };
            Verify(legacyDraft.RsbasePatientId() == "12345", "Legacy draft uses its own stored chart number for RSBase");
            legacyDraft.ChartNumber = "987651";
            Verify(legacyDraft.RsbasePatientId() == "98765", "Structured chart number takes precedence over legacy key");
            Verify(new MedicalCertificate().RsbasePatientId() == "", "Manual draft requires manual RSBase patient ID");
            var namingSettings = new AppSettings { CertificateRegistrationTitle = "渡航用英文診断書", CertificateRsbaseNaming = true };
            var roundTripSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(namingSettings))!;
            Verify(roundTripSettings.CertificateRegistrationTitle == "渡航用英文診断書" && roundTripSettings.CertificateRsbaseNaming, "Certificate RSBase preferences persist independently");
            var excessive = new MedicalCertificate { Name = data.Name, Sections = data.Sections, Clinic = new CertificateClinic { Address = string.Join("\n", Enumerable.Repeat("Long clinic address", 100)) } };
            bool rejected = false; try { CertificateRenderer.Render(excessive); } catch (InvalidOperationException) { rejected = true; }
            Verify(rejected, "Oversized fixed regions rejected instead of clipping");

            const string source = "氏名：架空 太郎\nフリガナ：ヤマダ タロウ\n生年月日：昭和55年11月12日（1980年11月12日）\n性別：男\nカルテ番号：101";
            var patient = new AccessPatientDisplay(true, "", source, DatabasePath: "fake.mdb");
            TrackCertificatePatient(patient);
            data.PatientKey = certificateCurrentKey; data.PatientSource = "架空 太郎 / 101"; DisplayCertificate(data);
            certificateSections[0].Text = "Hand-edited diagnosis";
            certificateSections[2].Clear();
            SetClinicalText(LlmResult, "Generated clinical summary"); LlmCertificateTarget.SelectedIndex = 2;
            ImportLlmResultToCertificate(this, new RoutedEventArgs());
            Verify(certificateSections[2].Text == "Generated clinical summary" && certificateSections[0].Text == "Hand-edited diagnosis", "Central AI result transfers only to the selected empty certificate section");
            Verify(!ImportCertificateText(0, "Replacement", () => false) && certificateSections[0].Text == "Hand-edited diagnosis", "Declining AI overwrite preserves manually edited text");
            Verify(ImportCertificateText(0, "Approved replacement", () => true) && certificateSections[0].Text == "Approved replacement", "Confirmed AI overwrite is explicit and marks the draft changed");
            var run = new System.Windows.Documents.Run("First sentence. Second sentence.");
            LlmResult.Document.Blocks.Clear(); LlmResult.Document.Blocks.Add(new System.Windows.Documents.Paragraph(run));
            LlmResult.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(15)!);
            certificateSections[1].Clear(); LlmCertificateTarget.SelectedIndex = 1;
            ImportLlmResultToCertificate(this, new RoutedEventArgs());
            Verify(certificateSections[1].Text == "First sentence.", "Central AI transfers selected text instead of unrelated result paragraphs");
            TrackCertificatePatient(patient with { Text = source.Replace("101", "202") });
            bool mismatch = false; try { ImportCertificateText(0, "Wrong patient", () => true); } catch (InvalidOperationException) { mismatch = true; }
            Verify(mismatch && certificateSections[0].Text == "Approved replacement", "Central AI import rejects mismatched chart patient");
            TrackCertificatePatient(patient); certificateDirty = false;

            await TestCertificateStore(folder, Verify, single);
            await TestCertificateAutoLoad(Verify);
            WorkspaceTabs.SelectedItem = CertificateTab; PreviewCertificate();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            RenderVisual(this, Path.Combine(folder, "window.png"), 1740, 940);
            File.WriteAllLines(Path.Combine(folder, "result.txt"), new[] { "PASS" }.Concat(checks));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), string.Join("\n", checks) + "\nFAIL: " + ex); Environment.ExitCode = 1; }
        finally { certificateDirty = false; Close(); }
    }
}
