using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDFWriter;
public partial class MainWindow
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void CheckShapeSelection(InkCanvas canvas, string kind)
    {
        Check(mode == "Select" && SelectTool.IsChecked == true && canvas.EditingMode == InkCanvasEditingMode.Select, $"{kind}: automatic selection mode");
        Check(canvas.GetSelectedElements().SingleOrDefault() is ShapeElement shape && shape.Data.Kind == kind, $"{kind}: new shape selected");
        Check(!canvas.GetSelectionBounds().IsEmpty, $"{kind}: selection handles have bounds");
        Check(canvas.IsKeyboardFocusWithin, $"{kind}: keyboard shortcuts must reach the editor after drawing");
    }
    private static void RenderVisual(FrameworkElement visual, string path, double width, double height)
    {
        visual.Measure(new Size(width, height)); visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private async Task Smoke()
    {
        var folder = Path.GetFullPath("tmp/smoke"); Directory.CreateDirectory(folder);
        try
        {
            await LoadPdf(await File.ReadAllBytesAsync("cyokakuheikoikensyo.pdf"), null, null);
            Check(views.Count == 5, "Expected five pages");
            await CheckStablePageViewport();
            CheckTextPlacement();
            var first = views[0].Canvas;
            foreach (var format in new[] { ChartDragFormat, DataFormats.UnicodeText, DataFormats.Text })
            {
                var dragData = new DataObject();
                dragData.SetData(format, "ドラッグ受付確認");
                foreach (var dragEvent in new[] { DragDrop.PreviewDragEnterEvent, DragDrop.PreviewDragOverEvent })
                {
                    var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                        new object[] { dragData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, first, new Point(100, 200) }, null)!;
                    args.RoutedEvent = dragEvent;
                    first.RaiseEvent(args);
                    Check(args.Handled && args.Effects == DragDropEffects.Copy, "Page accepts routed text drag before InkCanvas: " + format);
                }
            }
            var fileData = new DataObject(DataFormats.FileDrop, new[] { "example.pdf" });
            Check(GetDraggedChartText(fileData) == null, "Non-text drag is rejected");
            var beforeDrop = Snapshot();
            InsertChartText(first, new Point(100, 200), "山田 太郎\nドラッグ挿入の確認");
            var dropped = first.Children.OfType<TextBox>().Single();
            Check(dropped.Text == "山田 太郎\r\nドラッグ挿入の確認" && InkCanvas.GetLeft(dropped) == 100 &&
                InkCanvas.GetTop(dropped) > 150 && InkCanvas.GetTop(dropped) <= 200, "Chart drop inserts multiline text at page coordinates");
            Check(Capture().Pages[0].Single().Text == dropped.Text && dirty, "Dropped text is included in saved annotations");
            Undo();
            Check(Snapshot() == beforeDrop, "One undo removes chart insertion");
            Redo();
            Check(first.Children.OfType<TextBox>().Single().Text.Contains("ドラッグ挿入"), "Redo restores chart insertion");
            Undo();
            InsertChartText(first, new Point(first.Width, first.Height), "端の文字");
            var edge = first.Children.OfType<TextBox>().Single();
            Check(InkCanvas.GetLeft(edge) + edge.Width <= first.Width && InkCanvas.GetTop(edge) + edge.Height <= first.Height,
                "Drop at page edge stays within the page");
            Undo();
            ResetHistory();
            AddText(first, new TextData { X = 120, Y = 400 });
            CleanupEmptyText(); CommitHistory();
            Check(first.Children.Count == 0 && historyIndex == 0 && !dirty, "Empty frame must disappear without adding history or dirty state");

            var text = AddText(first, new TextData { X = 125, Y = 440, Width = 360 });
            text.Text = "動作確認用 架空の記入\n日本語・複数行の確認";
            CommitHistory();
            Check(historyIndex == 1 && dirty, "Text must create one undo entry");
            Undo(); Check(first.Children.Count == 0 && !dirty, "Undo text should return to saved state");
            Redo(); Check(first.Children.OfType<TextBox>().Single().Text.Contains("日本語"), "Redo text");
            activeText = first.Children.OfType<TextBox>().Single();
            FontFamilyBox.SelectedItem = "Yu Mincho";
            Check(activeText.FontFamily.Source == "Yu Mincho", "Change font");
            Undo(); Check(first.Children.OfType<TextBox>().Single().FontFamily.Source == "Yu Gothic", "Undo font");
            Redo(); Check(first.Children.OfType<TextBox>().Single().FontFamily.Source == "Yu Mincho", "Redo font");

            // Use the same rectangle gesture handlers as pointer input.
            mode = "Rectangle"; ApplyMode();
            BeginGesture(first, new Point(125, 550)); UpdateGesture(new Point(260, 600)); EndGesture(new Point(260, 600));
            Check(first.Children.OfType<ShapeElement>().Single().Data.Kind == "Rectangle", "Rectangle gesture");
            CheckShapeSelection(first, "Rectangle");
            Undo(); Check(!first.Children.OfType<ShapeElement>().Any(), "Undo rectangle");
            Redo(); Check(first.Children.OfType<ShapeElement>().Count() == 1, "Redo rectangle");

            mode = "Ellipse"; ApplyMode();
            BeginGesture(first, new Point(290, 550)); EndGesture(new Point(380, 605));
            CheckShapeSelection(first, "Ellipse");
            Undo(); Check(first.Children.OfType<ShapeElement>().Count() == 1, "Undo ellipse");
            Redo(); Check(first.Children.OfType<ShapeElement>().Count() == 2, "Redo ellipse");
            mode = "Line"; DashBox.SelectedIndex = 1; ThicknessBox.SelectedItem = 3d; ApplyMode();
            BeginGesture(first, new Point(420, 600)); EndGesture(new Point(560, 550));
            var line = first.Children.OfType<ShapeElement>().Last();
            Check(line.Data.Kind == "Line" && line.Data.Reverse && line.Data.Dotted && line.Data.Thickness == 3, "Dotted diagonal line and thickness");
            CheckShapeSelection(first, "Line");
            Undo(); Check(first.Children.OfType<ShapeElement>().Count() == 2, "Undo line");
            Redo(); Check(first.Children.OfType<ShapeElement>().Count() == 3, "Redo line");

            mode = "Select"; ApplyMode();
            SelectRectangle(first, new Rect(120, 540, 150, 70));
            Check(first.GetSelectedElements().Count == 1, "Rectangular selection must select only intersecting rectangle");
            DeleteSelection(); Check(first.Children.OfType<ShapeElement>().Count() == 2, "Delete selected shape");
            Undo(); Check(first.Children.OfType<ShapeElement>().Count() == 3, "Undo deletion");
            SelectRectangle(first, new Rect(120, 540, 150, 70));
            var selected = (FrameworkElement)first.GetSelectedElements().Single();
            InkCanvas.SetLeft(selected, 145); CommitHistory();
            Undo(); Check(Math.Abs(InkCanvas.GetLeft(first.Children.OfType<ShapeElement>().First()) - 125) < .01, "Undo movement");
            Redo(); Check(Math.Abs(InkCanvas.GetLeft(first.Children.OfType<ShapeElement>().First()) - 145) < .01, "Redo movement");

            var stroke = new Stroke(new StylusPointCollection { new StylusPoint(570, 570), new StylusPoint(610, 620), new StylusPoint(650, 590) }, new DrawingAttributes { Width = 3, Height = 3 });
            views[1].Canvas.Strokes.Add(stroke); CommitHistory();
            Undo(); Check(views[1].Canvas.Strokes.Count == 0, "Undo pen");
            Redo(); Check(views[1].Canvas.Strokes.Count == 1, "Redo pen");
            views[1].Canvas.Strokes.Clear(); CommitHistory();
            Undo(); Check(views[1].Canvas.Strokes.Count == 1, "Undo eraser");

            CheckColors();
            var expected = JsonSerializer.Serialize(Capture());
            await WriteProject(Path.Combine(folder, "roundtrip.pdfwrite"));
            await ReadProject(Path.Combine(folder, "roundtrip.pdfwrite"));
            Check(JsonSerializer.Serialize(Capture()) == expected, "Text/font/shapes/line-style coordinates must survive save/reload");
            Check(views[1].Canvas.Strokes.Count == 1 && views[1].Canvas.Strokes[0].DrawingAttributes.Width == 3, "Ink thickness must survive save/reload");
            Check(views[1].Canvas.Strokes[0].DrawingAttributes.Color.ToString() == "#FF2563EB", "Ink color must survive save/reload");

            var document = BuildPrintDocument(794, 1123);
            for (int i = 0; i < 2; i++)
                RenderVisual((FrameworkElement)document.DocumentPaginator.GetPage(i).Visual, Path.Combine(folder, $"print-{i + 1}.png"), 794, 1123);
            PrintInk.IsChecked = false;
            var textOnly = BuildPrintDocument(794, 1123);
            RenderVisual((FrameworkElement)textOnly.DocumentPaginator.GetPage(0).Visual, Path.Combine(folder, "text-only.png"), 794, 1123);
            PrintInk.IsChecked = true;
            Check(Capture().Shapes.Sum(s => s.Count) == 3 && views[1].Canvas.Strokes.Count == 1, "Text-only print must preserve annotations");

            // Version 1 compatibility: no font or shape properties were required in old files.
            var legacy = JsonSerializer.Deserialize<DocumentData>("{\"Version\":1,\"Pages\":[[{\"Text\":\"旧形式\",\"X\":120,\"Y\":440}]]}")!;
            Check(legacy.Pages[0][0].FontFamily == "Yu Gothic" && legacy.Shapes.Count == 0, "Version 1 defaults");
            var config = new AppSettings { TemplateFolder = Path.GetFullPath("."), SaveFolder = folder, TemplateFiles = [Path.GetFullPath("cyokakuheikoikensyo.pdf")] };
            Check(config.GetTemplates().Count(t => t.Path.EndsWith("cyokakuheikoikensyo.pdf")) == 1, "Template deduplication");
            var configCopy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(config))!;
            Check(configCopy.SaveFolder == folder && configCopy.GetTemplates().Count > 0, "Settings roundtrip");
            await CheckAccessConnection(folder, config);

            mode = "Text"; TextTool.IsChecked = true; ApplyMode(); DocumentTitle.Text = "cyokakuheikoikensyo.pdf";
            changingTemplate = true; TemplateBox.ItemsSource = config.GetTemplates(); TemplateBox.SelectedIndex = 0; changingTemplate = false;
            Zoom.Value = .72;
            RenderVisual((FrameworkElement)Content, Path.Combine(folder, "editor.png"), 1300, 880);
            var settingsWindow = new SettingsWindow(config);
            RenderVisual((FrameworkElement)settingsWindow.Content, Path.Combine(folder, "settings.png"), 650, 650);
            settingsWindow.Close();

            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: empty frames; undo/redo text/font/shapes/move/delete/ink/erase; rectangle selection; styles and ZIP roundtrip; text-only print; legacy defaults; template settings; UI rendering.");
            MarkSaved(); Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString());
            if (original != null) MarkSaved();
            Application.Current.Shutdown(1);
        }
    }
    private void CheckTextPlacement()
    {
        var canvas = views[0].Canvas;
        foreach (var font in new[] { "Yu Gothic", "Yu Mincho" })
        foreach (var size in new double[] { 8, 14, 48 })
        {
            FontFamilyBox.SelectedItem = font;
            FontSizeBox.SelectedItem = size;
            var box = AddTextAt(canvas, new Point(150, 300));
            canvas.UpdateLayout();
            var caret = box.GetRectFromCharacterIndex(0);
            Check(!caret.IsEmpty && Math.Abs(InkCanvas.GetTop(box) + caret.Top + caret.Height / 2 - 300) < 1,
                $"{font}/{size}: click must align with the first line center");
            var top = InkCanvas.GetTop(box);
            var initialHeight = box.Height;
            box.Text = "記入例\r\n";
            canvas.UpdateLayout();
            Check(box.Height > initialHeight && InkCanvas.GetTop(box) == top, "Return grows downward including an empty last line");
            box.Text += "二行目";
            canvas.UpdateLayout();
            var last = box.GetRectFromCharacterIndex(box.Text.Length);
            Check(last.Bottom <= box.ActualHeight, "Last line must fit inside the frame");
            Check(Capture().Pages[0].Single().Text == "記入例\r\n二行目", "Saved text uses CRLF");
            box.Width = 60;
            box.Text = string.Concat(Enumerable.Repeat("折り返し", 12));
            canvas.UpdateLayout();
            Check(box.Height > initialHeight * 2 && InkCanvas.GetTop(box) == top, "Wrapping grows downward");
            canvas.Children.Remove(box);
        }
        FontFamilyBox.SelectedItem = "Yu Gothic";
        FontSizeBox.SelectedItem = 14d;
        var edge = AddTextAt(canvas, new Point(canvas.Width, canvas.Height));
        edge.Text = string.Concat(Enumerable.Repeat("行\r\n", 20));
        canvas.UpdateLayout();
        Check(InkCanvas.GetLeft(edge) >= 0 && InkCanvas.GetTop(edge) >= 0 &&
            InkCanvas.GetTop(edge) + edge.Height <= canvas.Height, "Frame stays within page edges");
        canvas.Children.Remove(edge);
        ResetHistory();
    }
    private async Task CheckStablePageViewport()
    {
        UpdateLayout();
        for (int pageIndex = 0; pageIndex < 2; pageIndex++)
        {
            var canvas = views[pageIndex].Canvas;
            var top = canvas.TranslatePoint(new Point(), Pages).Y;
            DocumentScroll.ScrollToVerticalOffset(top + 20);
            UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(PageIndicator.Text == $"PAGE  {pageIndex + 1:00} / 05", "Toolbar must show the visible page");
            foreach (var tool in new[] { "Ink", "Rectangle", "Ellipse", "Line", "Select" })
            {
                mode = tool; ApplyMode(); Focus();
                var beforeOffset = DocumentScroll.VerticalOffset;
                var beforePoint = canvas.TranslatePoint(new Point(100, 150), DocumentScroll);
                canvas.Focus();
                canvas.BringIntoView(); // Same request WPF raises on editor focus.
                UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var afterPoint = canvas.TranslatePoint(new Point(100, 150), DocumentScroll);
                Check(Math.Abs(DocumentScroll.VerticalOffset - beforeOffset) < .01 && (beforePoint - afterPoint).Length < .01,
                    $"{tool}: focusing page {pageIndex + 1} must not move the page under the pointer");
            }
        }
        mode = "Text"; ApplyMode(); DocumentScroll.ScrollToTop(); UpdateLayout();
    }
    private void CheckColors()
    {
        var canvas = views[0].Canvas;
        mode = "Select"; ApplyMode();
        canvas.Select(new StrokeCollection(), canvas.Children.Cast<UIElement>().ToArray());
        ColorBox.SelectedIndex = 2;
        Check(Capture().Pages[0][0].ColorHex == "#FFDC2626" && Capture().Shapes[0].All(s => s.ColorHex == "#FFDC2626"), "Selected text and shapes recolored together");
        Undo();
        Check(Capture().Pages[0][0].ColorHex == "#FF000000" && Capture().Shapes[0].All(s => s.ColorHex == "#FF000000"), "Undo color");
        Redo(); Check(Capture().Pages[0][0].ColorHex == "#FFDC2626", "Redo color");
        views[1].Canvas.Select(views[1].Canvas.Strokes);
        ColorBox.SelectedIndex = 7;
        Check(views[1].Canvas.Strokes[0].DrawingAttributes.Color == ColorValue, "Selected ink recolored");
        Undo(); Check(views[1].Canvas.Strokes[0].DrawingAttributes.Color == Colors.Black, "Undo ink color");
        Redo(); Check(views[1].Canvas.Strokes[0].DrawingAttributes.Color.ToString() == "#FF2563EB", "Redo ink color");
        mode = "Rectangle"; ApplyMode();
        BeginGesture(canvas, new Point(150, 650));
        Check(shapePreview!.Data.ColorHex == "#FF2563EB", "Preview color");
        EndGesture(new Point(200, 680));
        Check(Capture().Shapes[0].Last().ColorHex == "#FF2563EB", "New shape color");
        Undo(); Check(Capture().Shapes[0].Count == 3, "Undo new colored shape");
        Check(canvas.DefaultDrawingAttributes.Color.ToString() == "#FF2563EB", "New pen color");
    }
}
