using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestPdfExport()
    {
        var folder = Path.GetFullPath("tmp/pdf-export"); Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "source.pdf");
            using (var pdf = new PdfDocument())
            {
                for (int i = 0; i < 9; i++)
                {
                    var page = pdf.AddPage();
                    page.MediaBox = i < 4 ? new PdfRectangle(new XPoint(0, 0), new XPoint(240, 320)) : new PdfRectangle(new XPoint(40, 60), new XPoint(280, 380));
                    if (i >= 4) page.CropBox = new PdfRectangle(new XPoint(55, 85), new XPoint(265, 355));
                    page.Rotate = i % 4 * 90;
                    var font = new PdfDictionary(pdf);
                    font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1");
                    font.Elements.SetName("/BaseFont", "/Helvetica");
                    var fonts = new PdfDictionary(pdf); fonts.Elements["/F1"] = font;
                    page.Resources.Elements["/Font"] = fonts;
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
                        "q 0.9 0.95 1 rg 70 100 120 140 re f Q BT /F1 14 Tf 70 180 Td (Original searchable text) Tj ET"));
                }
                pdf.Save(source);
            }
            var sourceBytes = await File.ReadAllBytesAsync(source);
            await LoadPdf(sourceBytes, null, null);
            for (int i = 0; i < views.Count - 1; i++)
            {
                var view = views[i];
                AddText(view.Canvas, new TextData { Text = "日本語の記入\n折り返しテスト ABC", X = 20, Y = 25,
                    Width = 200, Height = 75, FontFamily = i % 2 == 0 ? "Yu Gothic" : "Yu Mincho", FontSize = 16 });
                AddShape(view.Canvas, new ShapeData { Kind = "Rectangle", X = 30, Y = 130,
                    Width = 80, Height = 50, Thickness = 4, ColorHex = "#FFFF0000" });
                AddShape(view.Canvas, new ShapeData { Kind = "Line", X = 135, Y = 145,
                    Width = 70, Height = 40, Thickness = 3, Dotted = true, Reverse = true, ColorHex = "#FF008000" });
                view.Canvas.Strokes.Add(new Stroke(new StylusPointCollection {
                    new StylusPoint(40, 210), new StylusPoint(80, 240), new StylusPoint(120, 215) },
                    new DrawingAttributes { Width = 5, Height = 5, Color = Colors.Blue }));
            }
            CommitHistory();
            var snapshot = Snapshot(); var wasDirty = dirty;
            var full = Path.Combine(folder, "export.pdf");
            await WritePdf(full, true);
            await WritePdf(Path.Combine(folder, "text-only.pdf"), false);
            var lockedPath = Path.Combine(folder, "locked-destination.tmp");
            File.WriteAllText(lockedPath, "Existing destination");
            using (var locked = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = false;
                try { await WritePdf(lockedPath, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed, "Locked destination reports a failure");
            }
            Check(File.ReadAllText(lockedPath) == "Existing destination" &&
                !Directory.GetFiles(folder, "locked-destination.tmp.*.tmp").Any(),
                "Failed export preserves destination and removes temporary output");
            File.Delete(lockedPath);
            Check(snapshot == Snapshot() && dirty == wasDirty, "Export preserves editable state and unsaved flag");
            Check(sourceBytes.SequenceEqual(File.ReadAllBytes(source)), "Original file unchanged");
            using (var before = PdfReader.Open(source, PdfDocumentOpenMode.Import))
            using (var after = PdfReader.Open(full, PdfDocumentOpenMode.Import))
            {
                Check(before.PageCount == after.PageCount, "Page count preserved");
                for (int i = 0; i < before.PageCount; i++)
                    Check(before.Pages[i].MediaBox == after.Pages[i].MediaBox &&
                        before.Pages[i].CropBox == after.Pages[i].CropBox && before.Pages[i].Rotate == after.Pages[i].Rotate,
                        $"Page {i}: media/crop/rotation preserved");
            }
            var rendered = await PdfRenderer.RenderAsync(await File.ReadAllBytesAsync(full));
            var textOnly = await PdfRenderer.RenderAsync(await File.ReadAllBytesAsync(Path.Combine(folder, "text-only.pdf")));
            for (int i = 0; i < rendered.Count; i++)
            {
                var result = rendered[i]; var view = views[i];
                Check(Math.Abs(result.Width - view.Width) < .01 && Math.Abs(result.Height - view.Height) < .01,
                    $"Page {i}: visible dimensions preserved");
                if (i < views.Count - 1)
                {
                    Check(PixelNear(result, 32, 150, Colors.Red), $"Page {i}: rotated/cropped red rectangle alignment");
                    Check(PixelNear(result, 80, 240, Colors.Blue), $"Page {i}: ink alignment");
                    Check(!PixelNear(textOnly[i], 32, 150, Colors.Red) && !PixelNear(textOnly[i], 80, 240, Colors.Blue),
                        $"Page {i}: text-only excludes shapes and ink");
                }
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(result.Image));
                using var output = File.Create(Path.Combine(folder, $"page-{i + 1}.png")); encoder.Save(output);
            }
            WorkspaceTabs.SelectedIndex = 0;
            var print = BuildPrintDocument(views[0].Width, views[0].Height);
            RenderVisual((FrameworkElement)print.DocumentPaginator.GetPage(0).Visual,
                Path.Combine(folder, "print.png"), views[0].Width, views[0].Height);
            RenderVisual((FrameworkElement)Content, Path.Combine(folder, "ui.png"), 1740, 940);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: direct PDF export; 9 mixed-size/rotated/cropped pages; Japanese fonts; shapes and ink; text-only; source and editing state unchanged; failed overwrite preserves destination; print rendering.");
            MarkSaved(); Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString());
            if (original != null) MarkSaved();
            Application.Current.Shutdown(1);
        }
    }

    private static bool PixelNear(RenderedPdfPage page, double x, double y, Color expected)
    {
        var bitmap = new FormatConvertedBitmap(page.Image, PixelFormats.Bgra32, null, 0);
        var px = (int)(x / page.Width * bitmap.PixelWidth);
        var py = (int)(y / page.Height * bitmap.PixelHeight);
        var pixels = new byte[4 * 5 * 5];
        bitmap.CopyPixels(new Int32Rect(px - 2, py - 2, 5, 5), pixels, 20, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - expected.B) < 30 && Math.Abs(pixels[i + 1] - expected.G) < 30 &&
                Math.Abs(pixels[i + 2] - expected.R) < 30) return true;
        return false;
    }
}
