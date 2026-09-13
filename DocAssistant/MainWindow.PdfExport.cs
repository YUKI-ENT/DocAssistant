using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace DocAssistant;

public partial class MainWindow
{
    private async void ExportPdf(object sender, RoutedEventArgs e)
    {
        if (original == null) return;
        FinishEditing();
        var dialog = new SaveFileDialog
        {
            Title = "PDF出力", Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            InitialDirectory = settings.SaveFolder,
            FileName = Path.GetFileNameWithoutExtension(DocumentTitle.Text) + "_記入済み.pdf"
        };
        if (dialog.ShowDialog(this) != true) return;
        await Guard(async () =>
        {
            await WritePdf(dialog.FileName, PrintInk.IsChecked == true);
            Status.Text = "PDFを出力しました。再編集用のデータは「保存」で保存できます。";
        });
    }

    private async Task WritePdf(string path, bool includeInk)
    {
        using var input = new MemoryStream(original ?? throw new InvalidOperationException("PDFを開いてください。"));
        using var document = await Task.Run(() => PdfReader.Open(input, PdfDocumentOpenMode.Modify));
        if (document.PageCount != views.Count) throw new InvalidDataException("元PDFと編集画面のページ数が一致しません。");
        for (int i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (!view.Canvas.Children.OfType<TextBox>().Any(b => !string.IsNullOrEmpty(b.Text)) &&
                (!includeInk || (!view.Canvas.Children.OfType<ShapeElement>().Any() && view.Canvas.Strokes.Count == 0))) continue;
            Status.Text = $"PDFを出力中… {i + 1} / {views.Count}ページ";
            var overlay = RenderPdfOverlay(view, includeInk);
            var page = document.Pages[i];
            await Task.Run(() => AppendPdfOverlay(page, overlay));
        }
        // Only replace the destination after the complete PDF has been written successfully.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(() => document.Save(temp));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Grid BuildAnnotationVisual(PageView view, bool includeInk)
    {
        var content = new Grid { Width = view.Width, Height = view.Height, ClipToBounds = true };
        var overlay = new Canvas { Width = view.Width, Height = view.Height };
        foreach (TextBox box in view.Canvas.Children.OfType<TextBox>())
        {
            var text = new TextBlock { Text = box.Text, Width = box.Width, Height = box.Height,
                FontFamily = box.FontFamily, FontSize = box.FontSize, Foreground = box.Foreground,
                FontWeight = box.FontWeight, FontStyle = box.FontStyle, FontStretch = box.FontStretch,
                TextAlignment = box.TextAlignment, FlowDirection = box.FlowDirection,
                TextWrapping = TextWrapping.Wrap, ClipToBounds = true, Padding = new Thickness(2) };
            Canvas.SetLeft(text, InkCanvas.GetLeft(box)); Canvas.SetTop(text, InkCanvas.GetTop(box));
            overlay.Children.Add(text);
        }
        if (includeInk)
        {
            foreach (var shape in view.Canvas.Children.OfType<ShapeElement>())
            {
                var copy = new ShapeElement(shape.Data) { Width = shape.Width, Height = shape.Height };
                Canvas.SetLeft(copy, InkCanvas.GetLeft(shape)); Canvas.SetTop(copy, InkCanvas.GetTop(shape));
                overlay.Children.Add(copy);
            }
        }
        content.Children.Add(overlay);
        if (includeInk) content.Children.Add(new InkPresenter { Width = view.Width, Height = view.Height,
            Strokes = view.Canvas.Strokes.Clone(), IsHitTestVisible = false });
        return content;
    }

    private static byte[] RenderPdfOverlay(PageView view, bool includeInk)
    {
        var visual = BuildAnnotationVisual(view, includeInk);
        visual.Measure(new Size(view.Width, view.Height));
        visual.Arrange(new Rect(0, 0, view.Width, view.Height));
        visual.UpdateLayout();
        // Render only annotations with alpha, retaining the original PDF's text and vectors.
        // Bound the bitmap allocation for the x86 application on unusually large pages.
        var scale = Math.Min(300.0 / 96, Math.Sqrt(16_000_000.0 / (view.Width * view.Height)));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.Width * scale),
            (int)Math.Ceiling(view.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        return output.ToArray();
    }

    private static void AppendPdfOverlay(PdfPage page, byte[] overlay)
    {
        var media = page.MediaBox;
        var crop = page.Elements.ContainsKey("/CropBox") ? page.CropBox : media;
        var left = Math.Max(media.X1, crop.X1); var bottom = Math.Max(media.Y1, crop.Y1);
        var right = Math.Min(media.X2, crop.X2); var top = Math.Min(media.Y2, crop.Y2);
        var width = right - left; var height = top - bottom;
        if (width <= 0 || height <= 0) throw new InvalidDataException("PDFのページ領域が不正です。");
        using var stream = new MemoryStream(overlay, 0, overlay.Length, false, true);
        using var image = XImage.FromStream(stream);
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        // PDFium displays the visible crop after /Rotate. Map that view back into the
        // original page coordinates; retain MediaBox, CropBox and Rotate unchanged.
        var pageHeight = graphics.PageSize.Height;
        var rotation = ((page.Rotate % 360) + 360) % 360;
        var matrix = rotation switch
        {
            0 => new XMatrix(1, 0, 0, 1, left, pageHeight - top),
            90 => new XMatrix(0, -1, 1, 0, left, pageHeight - bottom),
            180 => new XMatrix(-1, 0, 0, -1, right, pageHeight - bottom),
            270 => new XMatrix(0, 1, -1, 0, right, pageHeight - top),
            _ => throw new InvalidDataException("PDFのページ回転角度が不正です。")
        };
        graphics.MultiplyTransform(matrix);
        graphics.DrawImage(image, 0, 0, rotation is 90 or 270 ? height : width,
            rotation is 90 or 270 ? width : height);
    }
}
