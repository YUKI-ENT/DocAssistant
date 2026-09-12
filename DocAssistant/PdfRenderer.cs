using System.IO;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace DocAssistant;

internal sealed record RenderedPdfPage(BitmapSource Image, double Width, double Height);

internal static class PdfRenderer
{
    // PDFium renders to a CPU bitmap; do not reintroduce Windows.Data.Pdf here.
    // That renderer left this machine's Intel D3D11 driver stuck during DLL shutdown.
    internal static Task<List<RenderedPdfPage>> RenderAsync(byte[] bytes) => Task.Run(() =>
    {
        var sizes = Conversion.GetPageSizes(bytes).ToArray();
        var pages = new List<RenderedPdfPage>();
        var index = 0;
        foreach (var rendered in Conversion.ToImages(bytes, options: new RenderOptions(Dpi: 240)))
        {
            using var bitmap = rendered;
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            // Existing .pdfwrite coordinates use WPF DIPs (96/inch), not PDF points (72/inch).
            pages.Add(new RenderedPdfPage(image, sizes[index].Width * 96.0 / 72, sizes[index].Height * 96.0 / 72));
            index++;
        }
        return pages;
    });
}
