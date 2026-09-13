using System.IO;
using System.Text.Json;
using System.Windows;
using PdfSharp.Pdf;

namespace DocAssistant;

public partial class MainWindow
{
    private async Task TestAudiogramAsync()
    {
        var folder = Path.GetFullPath("tmp/audiogram-test"); Directory.CreateDirectory(folder);
        try
        {
            using var stream = new MemoryStream();
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage(); pdf.AddPage(); pdf.Save(stream, false);
            }
            await LoadPdf(stream.ToArray(), null, null);
            var canvas = views[0].Canvas;
            mode = "AudioRight"; ApplyMode();
            AddAudiogramPoint(canvas, new Point(100, 100));
            AddAudiogramPoint(canvas, new Point(180, 150));
            AddAudiogramPoint(canvas, new Point(260, 100));
            Check(canvas.Children.Count == 5 && Capture().Shapes[0].Count(s => s.Kind == "Line" && !s.Dotted) == 2, "Right circle series uses solid lines");
            Undo(); Check(canvas.Children.Count == 3, "Undo removes one point and its segment");
            Redo(); Check(canvas.Children.Count == 5, "Redo restores point and segment");
            EndAudiogram();
            AddAudiogramPoint(canvas, new Point(340, 100));
            Check(canvas.Children.Count == 6, "Ending series prevents a joining segment");
            mode = "AudioLeft";
            AddAudiogramPoint(canvas, new Point(100, 210));
            AddAudiogramPoint(canvas, new Point(180, 250));
            AddAudiogramPoint(canvas, new Point(260, 210));
            Check(Capture().Shapes[0].Count(s => s.Kind == "AudioCross") == 3 && Capture().Shapes[0].Count(s => s.Dotted) == 2, "Left cross series uses dotted lines and solid symbols");
            AddAudiogramPoint(views[1].Canvas, new Point(100, 210));
            Check(views[1].Canvas.Children.Count == 1, "Pages never join");
            string[] symbols = ["AudioBracketLeft", "AudioBracketRight", "AudioCornerLeft", "AudioCornerRight", "AudioArrowLeft", "AudioArrowRight"];
            for (int i = 0; i < symbols.Length; i++)
            {
                mode = symbols[i]; AddAudiogramPoint(canvas, new Point(100 + i * 50, 330));
            }
            string snapshot = Snapshot();
            RestoreSnapshot(snapshot);
            Check(Snapshot() == snapshot, "All audiogram geometry survives save-format roundtrip");
            var exported = Path.Combine(folder, "audiogram.pdf");
            await WritePdf(exported, true);
            var rendered = await PdfRenderer.RenderAsync(await File.ReadAllBytesAsync(exported));
            Check(rendered.Count == 2, "Audiogram PDF export preserves pages");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered[0].Image));
            using (var imageFile = File.Create(Path.Combine(folder, "symbols.png"))) encoder.Save(imageFile);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            RenderVisual(this, Path.Combine(folder, "window.png"), 1740, 940);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: right/left series, end/restart, undo/redo, page isolation, bone/arrow symbols, save roundtrip, PDF export");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { dirty = false; Close(); }
    }
}
