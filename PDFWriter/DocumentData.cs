using System.Windows;
using System.Windows.Media;

namespace PDFWriter;

public sealed class DocumentData
{
    public int Version { get; set; } = 3;
    public List<List<TextData>> Pages { get; set; } = [];
    public List<List<ShapeData>> Shapes { get; set; } = [];
}
public sealed class TextData
{
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 70;
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Yu Gothic";
    public string ColorHex { get; set; } = "#FF000000";
}
public sealed class ShapeData
{
    public string Kind { get; set; } = "Rectangle";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Thickness { get; set; } = 1.7;
    public bool Dotted { get; set; }
    public bool Reverse { get; set; }
    public string ColorHex { get; set; } = "#FF000000";
}
internal sealed class EditorSnapshot
{
    public DocumentData Document { get; set; } = new();
    public List<string> Ink { get; set; } = [];
}
internal sealed class ShapeElement : FrameworkElement
{
    internal ShapeData Data { get; }
    internal ShapeElement(ShapeData data)
    {
        Data = data;
        Width = Math.Max(1, data.Width);
        Height = Math.Max(1, data.Height);
        IsHitTestVisible = false;
    }
    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(Data.ColorHex)), Math.Clamp(Data.Thickness, .3, 20));
        if (Data.Dotted) { pen.DashStyle = new DashStyle([0, 2.5], 0); pen.DashCap = PenLineCap.Round; }
        var inset = Math.Min(pen.Thickness / 2, Math.Min(ActualWidth, ActualHeight) / 2);
        var rect = new Rect(inset, inset, Math.Max(0, ActualWidth - 2 * inset), Math.Max(0, ActualHeight - 2 * inset));
        switch (Data.Kind)
        {
            case "Ellipse": dc.DrawEllipse(null, pen, new Point(ActualWidth / 2, ActualHeight / 2), rect.Width / 2, rect.Height / 2); break;
            case "Line":
                dc.DrawLine(pen, Data.Reverse ? rect.BottomLeft : rect.TopLeft, Data.Reverse ? rect.TopRight : rect.BottomRight); break;
            default: dc.DrawRectangle(null, pen, rect); break;
        }
    }
}
