using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DocAssistant;
public sealed record EditorColor(string Name, string Hex);
public partial class MainWindow
{
    private string ColorHexValue => (ColorBox.SelectedItem as EditorColor)?.Hex ?? "#FF000000";
    private Color ColorValue => (Color)ColorConverter.ConvertFromString(ColorHexValue);
    private void InitializeColors()
    {
        ColorBox.ItemsSource = new EditorColor[]
        {
            new("黒", "#FF000000"), new("グレー", "#FF64748B"),
            new("赤", "#FFDC2626"), new("オレンジ", "#FFEA580C"),
            new("黄", "#FFEAB308"), new("緑", "#FF15803D"),
            new("青緑", "#FF087F8C"), new("青", "#FF2563EB"),
            new("紫", "#FF9333EA"), new("白", "#FFFFFFFF")
        };
        ColorBox.SelectedIndex = 0;
    }
    private void ColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoring) return;
        CommitHistory();
        foreach (var view in views)
        {
            view.Canvas.DefaultDrawingAttributes.Color = ColorValue;
            if (mode != "Select") continue;
            foreach (var stroke in view.Canvas.GetSelectedStrokes()) stroke.DrawingAttributes.Color = ColorValue;
            foreach (var element in view.Canvas.GetSelectedElements())
            {
                if (element is TextBox box) box.Foreground = new SolidColorBrush(ColorValue);
                if (element is ShapeElement shape) { shape.Data.ColorHex = ColorHexValue; shape.InvalidateVisual(); }
            }
        }
        if (mode == "Text" && activeText != null && views.Any(v => v.Canvas.Children.Contains(activeText)))
            activeText.Foreground = new SolidColorBrush(ColorValue);
        CommitHistory();
    }
}
