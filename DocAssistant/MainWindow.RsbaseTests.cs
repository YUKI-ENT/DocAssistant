using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using PdfSharp.Pdf;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestRsbaseAsync()
    {
        var folder = Path.GetFullPath("tmp/rsbase-test"); Directory.CreateDirectory(folder);
        try
        {
            Check(RsbaseFileName.PatientId("氏名：テスト\r\nカルテ番号：123459\r\nTEL：") == "12345", "Strip only branch digit");
            Check(RsbaseFileName.PatientId("") == "", "Missing patient requires manual ID");
            Check(RsbaseFileName.Build("12345", "0001", new DateTime(2026, 6, 12), "身障意見書") ==
                "12345~0001~2026_06_12~身障意見書~RSB.pdf", "Exact RSBase name and single tilde separators");
            foreach (var title in new[] { "", "不正~区切り", "不正/名前", "改行\n名前" })
            {
                bool rejected = false;
                try { RsbaseFileName.Build("12345", "0001", DateTime.Today, title); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Reject unsafe registration names");
            }
            var templateFolder = Path.Combine(folder, "templates"); Directory.CreateDirectory(templateFolder);
            var templatePath = Path.Combine(templateFolder, "original.pdf");
            using (var pdf = new PdfDocument()) { pdf.AddPage(); pdf.Save(templatePath); }
            var legacy = new AppSettings { TemplateFolder = templateFolder, TemplateFiles = [templatePath] };
            Check(legacy.GetTemplates().Count == 1 && legacy.GetTemplates()[0].Name == "original", "Legacy registration migrates and deduplicates folder entry");
            legacy.Templates = [new("身障意見書", templatePath)]; legacy.RsbasePdfNaming = true;
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
            Check(restored.RsbasePdfNaming && restored.GetTemplates().Single().Name == "身障意見書", "Registered title wins over legacy and folder names");
            await LoadPdf(await File.ReadAllBytesAsync(templatePath), null, null);
            documentTemplateTitle = "身障意見書";
            var saved = JsonSerializer.Deserialize<DocumentData>(JsonSerializer.Serialize(Capture()))!;
            await LoadPdf(await File.ReadAllBytesAsync(templatePath), saved, null);
            Check(documentTemplateTitle == "身障意見書", "Reopened edit preserves template title");
            await WritePdf(Path.Combine(folder, RsbaseFileName.Build("12345", "0001", new DateTime(2026, 6, 12), documentTemplateTitle)), true);
            var settingsView = new SettingsWindow(restored);
            try
            {
                settingsView.Show();
                Check(((CheckBox)settingsView.FindName("RsbaseNaming")).IsChecked == true, "RSBase checkbox restores");
                RenderVisual(settingsView, Path.Combine(folder, "settings.png"), 780, 800);
            }
            finally { settingsView.Close(); }
            var namingView = new RsbaseExportWindow("12345", "身障意見書");
            try
            {
                namingView.Show();
                RenderVisual(namingView, Path.Combine(folder, "naming.png"), 610, 490);
            }
            finally { namingView.Close(); }
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: RSBase filename, branch removal, validation, legacy settings, titles, edit reload, PDF export, settings/naming UI");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { dirty = false; Close(); }
    }
}
