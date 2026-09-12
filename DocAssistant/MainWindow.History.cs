using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;

namespace DocAssistant;
public partial class MainWindow
{
    private readonly List<string> history = [];
    private int historyIndex = -1;
    private string savedSnapshot = "";
    private readonly DispatcherTimer typingTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private string Snapshot() => JsonSerializer.Serialize(new EditorSnapshot
    {
        Document = Capture(),
        Ink = views.Select(view => { using var stream = new MemoryStream(); view.Canvas.Strokes.Save(stream); return Convert.ToBase64String(stream.ToArray()); }).ToList()
    });
    private void ResetHistory()
    {
        typingTimer.Stop();
        history.Clear(); history.Add(Snapshot()); historyIndex = 0;
        savedSnapshot = history[0]; dirty = false; UpdateHistoryButtons();
    }
    private void MarkSaved()
    {
        CommitHistory();
        savedSnapshot = Snapshot(); dirty = false;
    }
    private void CommitHistory()
    {
        typingTimer.Stop();
        if (restoring || original == null) return;
        var snapshot = Snapshot();
        if (historyIndex < 0 || history[historyIndex] != snapshot)
        {
            if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(snapshot);
            // Snapshots share the rendered PDF in memory; only annotations are copied.
            while (history.Count > 80 || (history.Count > 2 && history.Sum(s => (long)s.Length * 2) > 32 * 1024 * 1024)) history.RemoveAt(0);
            historyIndex = history.Count - 1;
        }
        dirty = snapshot != savedSnapshot;
        UpdateHistoryButtons();
    }
    private void PendingEdit()
    {
        if (restoring) return;
        dirty = true;
        typingTimer.Stop(); typingTimer.Start();
        UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons()
    {
        if (UndoButton == null) return;
        UndoButton.IsEnabled = original != null && (historyIndex > 0 || typingTimer.IsEnabled);
        RedoButton.IsEnabled = original != null && historyIndex + 1 < history.Count;
    }
    private void FinishEditing()
    {
        if (original == null) return;
        CleanupEmptyText();
        CommitHistory();
    }
    private void RestoreSnapshot(string snapshot)
    {
        restoring = true;
        typingTimer.Stop();
        try
        {
            CancelGesture();
            activeText = null;
            var data = JsonSerializer.Deserialize<EditorSnapshot>(snapshot)!;
            for (int i = 0; i < views.Count; i++)
            {
                var canvas = views[i].Canvas;
                canvas.Select(new StrokeCollection(), Array.Empty<UIElement>());
                canvas.Children.Clear(); canvas.Strokes.Clear();
                using var stream = new MemoryStream(Convert.FromBase64String(data.Ink[i]));
                canvas.Strokes.Add(new StrokeCollection(stream));
                foreach (var text in data.Document.Pages[i]) AddText(canvas, text);
                if (i < data.Document.Shapes.Count) foreach (var shape in data.Document.Shapes[i]) AddShape(canvas, shape);
            }
            ApplyMode();
            dirty = snapshot != savedSnapshot;
        }
        finally { restoring = false; UpdateHistoryButtons(); }
    }
    private void Undo()
    {
        FinishEditing();
        if (historyIndex <= 0) return;
        RestoreSnapshot(history[--historyIndex]);
        Status.Text = "直前の操作を取り消しました。";
    }
    private void Redo()
    {
        FinishEditing();
        if (historyIndex + 1 >= history.Count) return;
        RestoreSnapshot(history[++historyIndex]);
        Status.Text = "操作をやり直しました。";
    }
    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Redo();
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (LlmPane.IsKeyboardFocusWithin) return;
        if (WorkspaceTabs.SelectedIndex != 0) return;
        if (busy) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.Z) { if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Redo(); else Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveProject(sender, e); e.Handled = true; }
        }
        else if (e.Key == Key.Delete && mode == "Select") { DeleteSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelGesture(); Keyboard.ClearFocus(); CleanupEmptyText(); CommitHistory(); }
    }
    private void DeleteSelection()
    {
        CommitHistory();
        foreach (var view in views)
        {
            var canvas = view.Canvas;
            var strokes = canvas.GetSelectedStrokes().ToArray();
            var elements = canvas.GetSelectedElements().ToArray();
            canvas.Select(new StrokeCollection(), Array.Empty<UIElement>());
            foreach (var stroke in strokes) canvas.Strokes.Remove(stroke);
            foreach (var element in elements) canvas.Children.Remove(element);
        }
        CommitHistory();
    }
}
