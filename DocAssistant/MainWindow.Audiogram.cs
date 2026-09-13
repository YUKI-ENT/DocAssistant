using System.Windows;
using System.Windows.Controls;

namespace DocAssistant;

public partial class MainWindow
{
    private InkCanvas? audiogramCanvas;
    private Point? audiogramPrevious;
    private string? audiogramMode;
    private double audiogramPreviousSize;
    private bool IsAudiogramMode => mode.StartsWith("Audio", StringComparison.Ordinal);
    private void EndAudiogram()
    {
        audiogramCanvas = null; audiogramPrevious = null; audiogramMode = null;
    }
    private void AddAudiogramPoint(InkCanvas canvas, Point point)
    {
        CommitHistory();
        double size = FontSizeValue;
        point = new Point(Math.Clamp(point.X, size / 2, canvas.Width - size / 2),
            Math.Clamp(point.Y, size / 2, canvas.Height - size / 2));
        bool series = mode is "AudioRight" or "AudioLeft";
        if (series && audiogramCanvas == canvas && audiogramMode == mode && audiogramPrevious is Point previous)
        {
            var vector = point - previous;
            if (vector.Length < .5) return;
            // Leave the symbol interiors clear; stop the line at each symbol's edge.
            double startGap = audiogramPreviousSize / 2 + ThicknessValue;
            double endGap = size / 2 + ThicknessValue;
            if (vector.Length > startGap + endGap)
            {
                vector.Normalize();
                var start = previous + vector * startGap;
                var end = point - vector * endGap;
                var bounds = new Rect(start, end);
                AddShape(canvas, new ShapeData { Kind = "Line", X = bounds.X, Y = bounds.Y,
                    Width = Math.Max(1, bounds.Width), Height = Math.Max(1, bounds.Height),
                    Reverse = (end.X - start.X) * (end.Y - start.Y) < 0,
                    ColorHex = ColorHexValue, Thickness = ThicknessValue, Dotted = mode == "AudioLeft" });
            }
        }
        string kind = mode switch { "AudioRight" => "Ellipse", "AudioLeft" => "AudioCross", _ => mode };
        bool narrow = kind.Contains("Bracket") || kind.Contains("Corner");
        double width = narrow ? size * .6 : size;
        AddShape(canvas, new ShapeData { Kind = kind, X = point.X - width / 2, Y = point.Y - size / 2,
            Width = width, Height = size, ColorHex = ColorHexValue, Thickness = ThicknessValue });
        if (series)
        {
            audiogramCanvas = canvas; audiogramPrevious = point; audiogramMode = mode; audiogramPreviousSize = size;
        }
        else EndAudiogram();
        CommitHistory();
    }
}
