using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DocAssistant;

internal static class CertificateRenderer
{
    internal const double Width = 793.7008, Height = 1122.5197;
    private const double Margin = 60;
    private static FormattedText Text(string value, double size, double width, bool bold = false)
    {
        return new FormattedText(value, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Arial"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1)
            { MaxTextWidth = width };
    }

    internal static List<BitmapSource> Render(MedicalCertificate data)
    {
        double width = Width - Margin * 2;
        var title = Text("MEDICAL CERTIFICATE", 23, width, true);
        var header = Text($"Patient name: {data.Name}\nDate of birth: {data.Birthday?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "[not entered]"}\nSex: {(data.Sex == "" ? "[not entered]" : data.Sex)}    Age: {(data.Age is int age ? age.ToString(CultureInfo.InvariantCulture) + " years" : "[not entered]")}\nDate of issue: {data.IssueDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}", 14, width);
        var footer = Text(string.Join("\n", new[] { data.Clinic.Name, data.Clinic.Address, data.Clinic.Contact }.Where(s => !string.IsNullOrWhiteSpace(s))), 12, width);
        var signature = Text("Physician: " + data.Clinic.Physician + "\n\nSignature: __________________________________", 14, width);
        double bodyTop = Margin + title.Height + 18 + header.Height + 25;
        double footerTop = Height - Margin - Math.Max(footer.Height, 14) - 22;
        double signatureTop = footerTop - 18 - signature.Height;
        // Reserve the same body area on every page so the final signature stays
        // with the clinic details and can never be pushed onto a page by itself.
        double bodyHeight = signatureTop - 25 - bodyTop;
        if (bodyHeight < 250) throw new InvalidOperationException("患者氏名または医院情報が長すぎます。改行や記載量を調整してください。");
        var flow = new FlowDocument { FontFamily = new FontFamily("Arial"), FontSize = 14.667, Foreground = Brushes.Black,
            PageWidth = width, PageHeight = bodyHeight, PagePadding = new Thickness(0), ColumnWidth = width,
            IsColumnWidthFlexible = false, ColumnGap = 0, LineHeight = 21, TextAlignment = TextAlignment.Left };
        for (int i = 0; i < MedicalCertificate.Headings.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(data.Sections[i])) continue;
            flow.Blocks.Add(new Paragraph(new Run(MedicalCertificate.Headings[i])) { FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4), KeepWithNext = true });
            foreach (var paragraph in Regex.Split(data.Sections[i].Trim().ReplaceLineEndings("\n"), @"\n\s*\n"))
                flow.Blocks.Add(new Paragraph(new Run(paragraph)) { Margin = new Thickness(0, 0, 0, 12), MinOrphanLines = 2, MinWidowLines = 2 });
        }
        if (flow.Blocks.Count == 0) flow.Blocks.Add(new Paragraph());
        var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.ComputePageCount();
        if (paginator.PageCount > 100) throw new InvalidOperationException("100ページを超えています。本文の記載量を確認してください。");
        var pages = new List<BitmapSource>();
        for (int i = 0; i < paginator.PageCount; i++)
        {
            using var page = paginator.GetPage(i);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
                dc.DrawText(title, new Point(Margin, Margin));
                dc.DrawText(header, new Point(Margin, Margin + title.Height + 18));
                dc.DrawLine(new Pen(Brushes.Gray, .6), new Point(Margin, bodyTop - 12), new Point(Width - Margin, bodyTop - 12));
                dc.PushTransform(new TranslateTransform(Margin, bodyTop));
                dc.DrawRectangle(new VisualBrush(page.Visual) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, width, bodyHeight), ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, width, bodyHeight), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, bodyHeight));
                dc.Pop();
                if (i == paginator.PageCount - 1) dc.DrawText(signature, new Point(Margin, signatureTop));
                double separatorTop = i == paginator.PageCount - 1 ? signatureTop - 12 : footerTop - 10;
                dc.DrawLine(new Pen(Brushes.Gray, .6), new Point(Margin, separatorTop), new Point(Width - Margin, separatorTop));
                dc.DrawText(footer, new Point(Margin, footerTop));
                if (paginator.PageCount > 1)
                {
                    var number = Text($"Page {i + 1} of {paginator.PageCount}", 11, width);
                    number.TextAlignment = TextAlignment.Right;
                    dc.DrawText(number, new Point(Margin, Height - Margin + 4));
                }
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width * 2), (int)Math.Ceiling(Height * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze(); pages.Add(bitmap);
        }
        return pages;
    }

    internal static void Export(string path, IReadOnlyList<BitmapSource> pages)
        => WriteBytes(path, CreatePdf(pages));

    internal static byte[] CreatePdf(IReadOnlyList<BitmapSource> pages)
    {
        using var pdf = new PdfDocument();
        foreach (var bitmap in pages)
        {
            var page = pdf.AddPage(); page.Width = XUnit.FromPoint(Width * .75); page.Height = XUnit.FromPoint(Height * .75);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
            using var image = XImage.FromStream(stream); using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }
        using var output = new MemoryStream(); pdf.Save(output, false); return output.ToArray();
    }
    internal static void WriteBytes(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

