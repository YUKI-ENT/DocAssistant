using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PDFWriter;
public partial class MainWindow
{
    private TextBox? activeText;
    private InkCanvas? gestureCanvas;
    private Point gestureStart;
    private string? gestureKind;
    private ShapeElement? shapePreview;
    private Rectangle? selectionPreview;
    private double FontSizeValue => FontSizeBox.SelectedItem is double value ? value : 14;
    private string FontFamilyValue => FontFamilyBox.SelectedItem as string ?? "Yu Gothic";
    private double ThicknessValue => ThicknessBox.SelectedItem is double value ? value : 1.7;
    private bool IsDotted => DashBox.SelectedIndex == 1;

    private void AttachEditor(InkCanvas canvas)
    {
        // Focusing the page (including InkCanvas's native pen/selection handling)
        // requests BringIntoView for the whole page. That would scroll under the
        // pointer after mouse coordinates have already been captured.
        canvas.RequestBringIntoView += (_, e) => e.Handled = true;
        canvas.Strokes.StrokesChanged += (_, _) => PendingEdit();
        canvas.StrokeCollected += (_, _) => CommitHistory();
        canvas.SelectionMoved += (_, _) => CommitHistory();
        canvas.SelectionResized += (_, _) => CommitHistory();
        canvas.PreviewMouseLeftButtonDown += CanvasMouseDown;
        canvas.PreviewMouseMove += CanvasMouseMove;
        canvas.PreviewMouseLeftButtonUp += CanvasMouseUp;
        canvas.LostMouseCapture += (_, _) => { if (gestureCanvas == canvas) CancelGesture(); };
    }
    private void CleanupEmptyText()
    {
        foreach (var view in views)
            foreach (var box in view.Canvas.Children.OfType<TextBox>().Where(t => string.IsNullOrWhiteSpace(t.Text)).ToArray())
            {
                if (activeText == box) activeText = null;
                view.Canvas.Children.Remove(box);
            }
    }
    private TextBox AddText(InkCanvas canvas, TextData data)
    {
        var box = new TextBox
        {
            Text = data.Text, Width = Math.Clamp(data.Width, 30, canvas.Width), Height = Math.Clamp(data.Height, 24, canvas.Height),
            FontSize = Math.Clamp(data.FontSize, 6, 100), FontFamily = new FontFamily(data.FontFamily), Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(data.ColorHex)),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsUndoEnabled = false,
            Background = Brushes.Transparent, BorderBrush = Brushes.LightSteelBlue, BorderThickness = new Thickness(1),
            Padding = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        InkCanvas.SetLeft(box, Math.Clamp(data.X, 0, canvas.Width - box.Width));
        InkCanvas.SetTop(box, Math.Clamp(data.Y, 0, canvas.Height - box.Height));
        box.TextChanged += (_, _) => PendingEdit();
        box.GotKeyboardFocus += (_, _) => activeText = box;
        box.LostKeyboardFocus += (_, _) =>
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (restoring || box.IsKeyboardFocusWithin || !canvas.Children.Contains(box)) return;
                if (string.IsNullOrWhiteSpace(box.Text))
                {
                    canvas.Children.Remove(box);
                    if (activeText == box) activeText = null;
                }
                CommitHistory();
            }));
        };
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "文字枠を削除" };
        delete.Click += (_, _) => { CommitHistory(); canvas.Children.Remove(box); if (activeText == box) activeText = null; CommitHistory(); };
        menu.Items.Add(delete); box.ContextMenu = menu;
        canvas.Children.Add(box);
        return box;
    }
    private ShapeElement AddShape(InkCanvas canvas, ShapeData data)
    {
        var element = new ShapeElement(data);
        InkCanvas.SetLeft(element, data.X); InkCanvas.SetTop(element, data.Y);
        canvas.Children.Add(element);
        return element;
    }
    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        FinishEditing();
        CancelGesture();
        mode = ((RadioButton)sender).Tag?.ToString() ?? "Text";
        // Keep keyboard events routed through this window after changing tools.
        // Custom drawing handles PreviewMouseDown, so WPF will not focus the canvas for us.
        Focus();
        activeText = null;
        ApplyMode();
        Status.Text = mode switch
        {
            "Select" => "長方形で囲んで選択。枠の内側で移動、端のハンドルで拡縮できます。",
            "Text" => "クリックして文字入力。空の枠は別の場所を選ぶと消えます。",
            "Ink" => "ドラッグして手書き。太さは上のツールバーで変更できます。",
            "Erase" => "線をなぞると手書きを削除します。図形・文字は選択してDelete。",
            _ => "ドラッグして図形を描画。線種で実線／点線を選べます。"
        };
    }
    private void ApplyMode()
    {
        foreach (var view in views)
        {
            var canvas = view.Canvas;
            canvas.EditingMode = mode switch { "Ink" => InkCanvasEditingMode.Ink, "Erase" => InkCanvasEditingMode.EraseByStroke, "Select" => InkCanvasEditingMode.Select, _ => InkCanvasEditingMode.None };
            canvas.DefaultDrawingAttributes = new DrawingAttributes { Color = ColorValue, Width = ThicknessValue, Height = ThicknessValue, FitToCurve = true };
            canvas.Cursor = mode is "Rectangle" or "Ellipse" or "Line" ? Cursors.Cross : Cursors.Arrow;
            foreach (var box in canvas.Children.OfType<TextBox>()) { box.IsHitTestVisible = mode == "Text"; box.IsReadOnly = mode != "Text"; box.BorderBrush = mode is "Text" or "Select" ? Brushes.LightSteelBlue : Brushes.Transparent; }
        }
    }
    private void TextStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoring) return;
        CommitHistory();
        var boxes = views.SelectMany(v => v.Canvas.GetSelectedElements().OfType<TextBox>()).ToList();
        if (mode == "Text" && activeText != null && views.Any(v => v.Canvas.Children.Contains(activeText))) boxes.Add(activeText);
        foreach (var box in boxes.Distinct())
        {
            if (sender == FontFamilyBox) box.FontFamily = new FontFamily(FontFamilyValue);
            if (sender == FontSizeBox) box.FontSize = FontSizeValue;
        }
        CommitHistory();
    }
    private void LineStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoring) return;
        CommitHistory();
        foreach (var view in views)
        {
            view.Canvas.DefaultDrawingAttributes.Width = view.Canvas.DefaultDrawingAttributes.Height = ThicknessValue;
            if (sender == ThicknessBox)
                foreach (var stroke in view.Canvas.GetSelectedStrokes()) stroke.DrawingAttributes.Width = stroke.DrawingAttributes.Height = ThicknessValue;
            foreach (var shape in view.Canvas.GetSelectedElements().OfType<ShapeElement>())
            {
                if (sender == ThicknessBox) shape.Data.Thickness = ThicknessValue;
                if (sender == DashBox) shape.Data.Dotted = IsDotted;
                shape.InvalidateVisual();
            }
        }
        CommitHistory();
    }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PageScale != null) PageScale.ScaleX = PageScale.ScaleY = e.NewValue;
        if (ZoomLabel != null) ZoomLabel.Text = $"{e.NewValue:P0}";
    }
    private void DocumentScrolled(object sender, ScrollChangedEventArgs e) => UpdatePageIndicator();
    private void UpdatePageIndicator()
    {
        if (PageIndicator == null) return;
        if (views.Count == 0) { PageIndicator.Text = "PAGE  — / —"; return; }
        var best = 0;
        var largestVisibleHeight = -1d;
        for (var i = 0; i < views.Count; i++)
        {
            var canvas = views[i].Canvas;
            if (!canvas.IsDescendantOf(DocumentScroll)) continue;
            var bounds = canvas.TransformToAncestor(DocumentScroll).TransformBounds(new Rect(canvas.RenderSize));
            var visibleHeight = Math.Max(0, Math.Min(DocumentScroll.ViewportHeight, bounds.Bottom) - Math.Max(0, bounds.Top));
            if (visibleHeight > largestVisibleHeight) { largestVisibleHeight = visibleHeight; best = i; }
        }
        PageIndicator.Text = $"PAGE  {best + 1:00} / {views.Count:00}";
    }
    private static bool InsideText(DependencyObject? source, InkCanvas canvas)
    {
        for (var current = source; current != null && current != canvas; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBox) return true;
        return false;
    }
    private static Point LimitPoint(InkCanvas canvas, Point point) => new(Math.Clamp(point.X, 0, canvas.Width), Math.Clamp(point.Y, 0, canvas.Height));
    private void CanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var canvas = (InkCanvas)sender;
        if (mode == "Text" && InsideText(e.OriginalSource as DependencyObject, canvas)) return;
        FinishEditing();
        var point = LimitPoint(canvas, e.GetPosition(canvas));
        if (mode == "Text")
        {
            foreach (var view in views) view.Canvas.Select(new StrokeCollection(), Array.Empty<UIElement>());
            activeText = AddText(canvas, new TextData { X = point.X, Y = point.Y, Width = Math.Min(240, canvas.Width - point.X), FontSize = FontSizeValue, FontFamily = FontFamilyValue, ColorHex = ColorHexValue });
            activeText.Focus(); e.Handled = true; return;
        }
        if (mode is not ("Select" or "Rectangle" or "Ellipse" or "Line")) return;
        if (mode == "Select")
        {
            var bounds = canvas.GetSelectionBounds();
            if (!bounds.IsEmpty) { bounds.Inflate(10, 10); if (bounds.Contains(point)) return; }
        }
        BeginGesture(canvas, point);
        e.Handled = true;
    }
    private void BeginGesture(InkCanvas canvas, Point point)
    {
        CancelGesture();
        canvas.Focus();
        foreach (var view in views) view.Canvas.Select(new StrokeCollection(), Array.Empty<UIElement>());
        gestureCanvas = canvas; gestureStart = point; gestureKind = mode;
        var overlay = views.First(v => v.Canvas == canvas).Adorner;
        if (mode == "Select")
        {
            selectionPreview = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(8, 127, 140)), Fill = new SolidColorBrush(Color.FromArgb(28, 8, 127, 140)), StrokeThickness = 1, StrokeDashArray = [4, 3] };
            overlay.Children.Add(selectionPreview);
        }
        else
        {
            shapePreview = new ShapeElement(new ShapeData { Kind = mode, ColorHex = ColorHexValue, Thickness = ThicknessValue, Dotted = IsDotted, Width = 1, Height = 1 });
            overlay.Children.Add(shapePreview);
        }
        canvas.CaptureMouse();
    }
    private void UpdateGesture(Point point)
    {
        if (gestureCanvas == null) return;
        point = LimitPoint(gestureCanvas, point);
        var rect = new Rect(gestureStart, point);
        FrameworkElement? preview = selectionPreview ?? (FrameworkElement?)shapePreview;
        if (preview == null) return;
        Canvas.SetLeft(preview, rect.Left); Canvas.SetTop(preview, rect.Top);
        preview.Width = Math.Max(1, rect.Width); preview.Height = Math.Max(1, rect.Height);
        if (shapePreview != null)
        {
            shapePreview.Data.Reverse = (point.X - gestureStart.X) * (point.Y - gestureStart.Y) < 0;
            shapePreview.InvalidateVisual();
        }
    }
    private void CanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (gestureCanvas != sender) return;
        UpdateGesture(e.GetPosition(gestureCanvas)); e.Handled = true;
    }
    private void CanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (gestureCanvas == sender)
        {
            EndGesture(e.GetPosition(gestureCanvas)); e.Handled = true;
        }
        else if (mode == "Erase") CommitHistory();
    }
    private void EndGesture(Point point)
    {
        if (gestureCanvas == null) return;
        var canvas = gestureCanvas;
        point = LimitPoint(canvas, point);
        var rect = new Rect(gestureStart, point);
        ShapeElement? created = null;
        if (gestureKind == "Select") SelectRectangle(canvas, rect);
        else if ((point - gestureStart).Length >= 3)
        {
            created = AddShape(canvas, new ShapeData { Kind = gestureKind!, ColorHex = ColorHexValue, X = rect.Left, Y = rect.Top, Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height), Thickness = ThicknessValue, Dotted = IsDotted, Reverse = (point.X - gestureStart.X) * (point.Y - gestureStart.Y) < 0 });
        }
        CancelGesture();
        CommitHistory();
        if (created != null)
        {
            SelectTool.IsChecked = true;
            mode = "Select";
            ApplyMode();
            // Selection handles need the arranged size of the newly added element.
            canvas.UpdateLayout();
            canvas.Select(new StrokeCollection(), new UIElement[] { created });
            canvas.Focus();
            Status.Text = "図形を選択しました。枠の内側で移動、ハンドルでサイズ変更。Ctrl+Zで取り消せます。";
        }
    }
    private static void SelectRectangle(InkCanvas canvas, Rect rect)
    {
        var strokes = new StrokeCollection(canvas.Strokes.Where(stroke => rect.IntersectsWith(stroke.GetBounds())));
        var elements = canvas.Children.OfType<FrameworkElement>().Where(element => rect.IntersectsWith(new Rect(InkCanvas.GetLeft(element), InkCanvas.GetTop(element), element.Width, element.Height))).Cast<UIElement>().ToArray();
        canvas.Select(strokes, elements);
    }
    private void CancelGesture()
    {
        var canvas = gestureCanvas;
        gestureCanvas = null; gestureKind = null; shapePreview = null; selectionPreview = null;
        foreach (var view in views) view.Adorner.Children.Clear();
        if (canvas?.IsMouseCaptured == true) canvas.ReleaseMouseCapture();
    }
}
