using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PDFWriter;

public partial class MainWindow
{
    private const string ChartDragFormat = "PDFWriter.ChartText";

    private static void EnableChartDrag(TextBoxBase source)
    {
        source.IsInactiveSelectionHighlightEnabled = true;
        Point? start = null;
        string selected = "";
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            if (e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.None) return;
            var point = e.GetPosition(source);
            bool inside = false;
            if (source is TextBox box)
            {
                int index = box.GetCharacterIndexFromPoint(point, false);
                inside = box.SelectionLength > 0 && index >= box.SelectionStart && index < box.SelectionStart + box.SelectionLength;
                selected = box.SelectedText;
            }
            else if (source is RichTextBox rich)
            {
                var position = rich.GetPositionFromPoint(point, false);
                inside = !rich.Selection.IsEmpty && position != null && position.CompareTo(rich.Selection.Start) >= 0 && position.CompareTo(rich.Selection.End) < 0;
                selected = rich.Selection.Text;
            }
            if (!inside || string.IsNullOrWhiteSpace(selected)) return;
            start = point;
            source.CaptureMouse();
            e.Handled = true;
        };
        source.PreviewMouseMove += (_, e) =>
        {
            if (start is not Point origin || e.LeftButton != MouseButtonState.Pressed) return;
            // Once a selected range has been grabbed, tiny movements belong to
            // the drag gesture too. Do not let TextBox's selection handler move
            // one end of the highlight before the drag threshold is reached.
            e.Handled = true;
            var point = e.GetPosition(source);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            source.ReleaseMouseCapture();
            var data = new DataObject();
            data.SetData(ChartDragFormat, selected);
            data.SetData(DataFormats.UnicodeText, selected);
            try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy); }
            finally
            {
                start = null;
                selected = "";
                ClearChartDragSelection(source);
            }
            e.Handled = true;
        };
        source.PreviewMouseLeftButtonUp += (_, _) => { start = null; source.ReleaseMouseCapture(); };
        source.LostMouseCapture += (_, _) => start = null;
    }

    private static void ClearChartDragSelection(TextBoxBase source)
    {
        // End only our drag state, without moving focus back from the PDF.
        if (source.IsMouseCaptured) source.ReleaseMouseCapture();
        if (source is TextBox box) box.Select(box.SelectionStart, 0);
        else if (source is RichTextBox rich) rich.Selection.Select(rich.Selection.Start, rich.Selection.Start);
    }

    private static string? GetDraggedChartText(IDataObject data)
    {
        foreach (var format in new[] { ChartDragFormat, DataFormats.UnicodeText, DataFormats.Text })
            if (data.GetDataPresent(format) && data.GetData(format) is string text && !string.IsNullOrWhiteSpace(text)) return text;
        return null;
    }

    private void ChartDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && original != null && GetDraggedChartText(e.Data) != null &&
            (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ChartDrop(object sender, DragEventArgs e)
    {
        ChartDragOver(sender, e);
        if (e.Effects != DragDropEffects.Copy || GetDraggedChartText(e.Data) is not string text) return;
        var canvas = (InkCanvas)((FrameworkElement)sender).Tag;
        InsertChartText(canvas, e.GetPosition(canvas), text);
    }

    private void InsertChartText(InkCanvas canvas, Point point, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        FinishEditing();
        CancelGesture();
        TextTool.IsChecked = true;
        point.X = Math.Clamp(point.X, 0, Math.Max(0, canvas.Width - 30));
        point.Y = Math.Clamp(point.Y, 0, Math.Max(0, canvas.Height - FontSizeValue * 1.5));
        var box = AddTextAt(canvas, point);
        box.Text = text.ReplaceLineEndings("\r\n");
        GrowTextBox(canvas, box);
        activeText = box;
        box.Focus();
        box.CaretIndex = box.Text.Length;
        CommitHistory();
        Status.Text = "選択したカルテ情報を挿入しました。文字の編集・移動ができます。";
    }
}

