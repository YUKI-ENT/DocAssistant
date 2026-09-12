using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DocAssistant;
public partial class MainWindow : Window
{
    private sealed record PageView(BitmapSource Background, InkCanvas Canvas, Canvas Adorner, double Width, double Height);
    private readonly List<PageView> views = [];
    private byte[]? original;
    private string mode = "Text";
    private bool dirty, busy, closeRequested, ready, restoring, changingTemplate;
    private AppSettings settings = new();
    private TemplateItem? currentTemplate;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"DocAssistant {AppVersion.Display} — ハイブリッド記入";
        AppName.Text = $"DocAssistant {AppVersion.Display}";
        EnableChartDrag(ChartDraft);
        EnableChartDrag(ChartTodayBasic);
        EnableChartDrag(ChartTodayMedication);
        EnableChartDrag(ChartTodayTests);
        EnableChartDrag(ChartTodayProcedures);
        EnableChartDrag(ChartTodayInjections);
        FontFamilyBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s).ToList();
        FontFamilyBox.SelectedItem = "Yu Gothic";
        FontSizeBox.ItemsSource = new double[] { 8, 10, 12, 14, 16, 18, 20, 24, 32, 48 };
        FontSizeBox.SelectedItem = 14d;
        ThicknessBox.ItemsSource = new double[] { .5, 1, 1.7, 2, 3, 4, 6, 8, 12 };
        ThicknessBox.SelectedItem = 1.7;
        DashBox.SelectedIndex = 0;
        InitializeColors();
        typingTimer.Tick += (_, _) => CommitHistory();
        ready = true;
        UpdateHistoryButtons();
        var args = Environment.GetCommandLineArgs();
        Loaded += async (_, _) =>
        {
            if (args.Contains("--llm-test")) { InitializeLlm(); await TestLlmAsync(); return; }
            if (args.Contains("--smoke") || args.Contains("--editor-test")) { await Smoke(); return; }
            if (args.Contains("--close-test"))
            {
                if (args.Contains("--extra-window")) _ = new Window();
                if (args.Contains("--with-pdf")) await LoadPdf(await File.ReadAllBytesAsync("cyokakuheikoikensyo.pdf"), null, null);
                if (args.Contains("--while-busy")) { await Guard(async () => { Close(); await Task.Delay(100); }); return; }
                await Task.Delay(300); Close(); return;
            }
            StartChartMonitor();
            await Guard(async () =>
            {
                try { settings = AppSettings.Load(); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                { MessageBox.Show(this, "設定を読み込めなかったため、既定値で起動します。\n" + ex.Message, "設定"); }
                InitializeLlm();
                RefreshTemplates();
                var startup = TemplateBox.Items.OfType<TemplateItem>().FirstOrDefault(t => t.Path == settings.DefaultTemplate);
                if (startup != null)
                {
                    await OpenPdfPath(startup.Path);
                    changingTemplate = true; TemplateBox.SelectedItem = currentTemplate = startup; changingTemplate = false;
                }
            });
        };
    }

    private bool CanReplace()
    {
        FinishEditing();
        return !dirty || MessageBox.Show(this, "未保存の変更を破棄しますか？", "DocAssistant", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        LifecycleLog.Write($"Window.Closing busy={busy} dirty={dirty} windows={Application.Current.Windows.Count}");
        if (busy) { closeRequested = true; e.Cancel = true; Status.Text = "処理完了後に終了します。"; return; }
        e.Cancel = !CanReplace();
        LifecycleLog.Write(e.Cancel ? "Close cancelled by user" : "Close accepted");
    }
    protected override void OnClosed(EventArgs e)
    {
        typingTimer.Stop();
        StopChartMonitor();
        llmCancellation?.Cancel();
        llmClient.Dispose();
        LifecycleLog.Write("Window.Closed");
        base.OnClosed(e);
    }
    private async Task Guard(Func<Task> action)
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "処理できませんでした", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            busy = false; IsEnabled = true;
            if (closeRequested) { closeRequested = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
        }
    }
    private void RefreshTemplates()
    {
        changingTemplate = true;
        try { TemplateBox.ItemsSource = settings.GetTemplates(); TemplateBox.SelectedItem = TemplateBox.Items.OfType<TemplateItem>().FirstOrDefault(t => t.Path == currentTemplate?.Path); }
        finally { changingTemplate = false; }
    }
    private async void TemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingTemplate || busy || TemplateBox.SelectedItem is not TemplateItem item) return;
        var previous = currentTemplate;
        if (CanReplace())
            await Guard(async () => { await OpenPdfPath(item.Path); currentTemplate = item; });
        changingTemplate = true; TemplateBox.SelectedItem = currentTemplate ?? previous; changingTemplate = false;
    }
    private void OpenAccess(object sender, RoutedEventArgs e)
    {
        FinishEditing();
        new AccessWindow(settings) { Owner = this }.ShowDialog();
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        FinishEditing();
        var dialog = new SettingsWindow(settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            settings = dialog.Result;
            try { RefreshTemplates(); Status.Text = "保存先とテンプレートの設定を更新しました。"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "テンプレート一覧"); }
        }
    }
    private async Task OpenPdfPath(string path)
    {
        await LoadPdf(await File.ReadAllBytesAsync(path), null, null);
        DocumentTitle.Text = Path.GetFileName(path);
    }
    private async void OpenPdf(object sender, RoutedEventArgs e)
    {
        if (!CanReplace()) return;
        var dialog = new OpenFileDialog { Filter = "PDF|*.pdf" };
        if (dialog.ShowDialog(this) == true) await Guard(async () =>
        {
            await OpenPdfPath(dialog.FileName);
            changingTemplate = true; TemplateBox.SelectedItem = currentTemplate = null; changingTemplate = false;
        });
    }
    private async Task LoadPdf(byte[] bytes, DocumentData? data, List<byte[]>? ink)
    {
        var rendered = await PdfRenderer.RenderAsync(bytes);
        if (data != null && (data.Version is not (1 or 2 or 3) || data.Pages.Count != rendered.Count))
            throw new InvalidDataException("編集データの形式またはページ数が一致しません。");
        var prepared = new List<PageView>();
        restoring = true;
        try
        {
            for (int i = 0; i < rendered.Count; i++)
            {
                var page = rendered[i];
                var canvas = new InkCanvas { Width = page.Width, Height = page.Height, Background = Brushes.Transparent, ClipToBounds = true };
                var adorner = new Canvas { Width = page.Width, Height = page.Height, IsHitTestVisible = false, ClipToBounds = true };
                if (ink != null) { using var stream = new MemoryStream(ink[i]); canvas.Strokes = new StrokeCollection(stream); }
                if (data != null) foreach (var text in data.Pages[i]) AddText(canvas, text);
                if (data != null && i < data.Shapes.Count) foreach (var shape in data.Shapes[i]) AddShape(canvas, shape);
                AttachEditor(canvas);
                prepared.Add(new PageView(page.Image, canvas, adorner, page.Width, page.Height));
            }
            CancelGesture();
            activeText = null;
            views.Clear(); views.AddRange(prepared); Pages.Children.Clear(); original = bytes;
            foreach (var view in views)
            {
                var grid = new Grid { Width = view.Width, Height = view.Height, Background = Brushes.White, AllowDrop = true, Tag = view.Canvas };
                // Receive text before InkCanvas/TextBox class handlers consume the drag events.
                grid.PreviewDragEnter += ChartDragOver;
                grid.PreviewDragOver += ChartDragOver;
                grid.PreviewDrop += ChartDrop;
                grid.Children.Add(new Image { Source = view.Background, Stretch = Stretch.Fill });
                grid.Children.Add(view.Canvas); grid.Children.Add(view.Adorner);
                Pages.Children.Add(new Border { Child = grid, Margin = new Thickness(0, 16, 0, 8), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 220, 231)), BorderThickness = new Thickness(1), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = .12 } });
            }
            EmptyState.Visibility = Visibility.Collapsed;
            ApplyMode(); ResetHistory();
            UpdatePageIndicator();
            Status.Text = $"{views.Count} ページを読み込みました。";
        }
        finally { restoring = false; }
    }
    private DocumentData Capture() => new()
    {
        Pages = views.Select(view => view.Canvas.Children.OfType<TextBox>().Where(box => !string.IsNullOrWhiteSpace(box.Text)).Select(box => new TextData
        {
            Text = box.Text.ReplaceLineEndings("\r\n"), X = InkCanvas.GetLeft(box), Y = InkCanvas.GetTop(box), Width = box.Width, Height = box.Height, FontSize = box.FontSize, FontFamily = box.FontFamily.Source, ColorHex = ((SolidColorBrush)box.Foreground).Color.ToString()
        }).ToList()).ToList(),
        Shapes = views.Select(view => view.Canvas.Children.OfType<ShapeElement>().Select(shape => new ShapeData
        {
            Kind = shape.Data.Kind, X = InkCanvas.GetLeft(shape), Y = InkCanvas.GetTop(shape), Width = shape.Width, Height = shape.Height, Thickness = shape.Data.Thickness, Dotted = shape.Data.Dotted, Reverse = shape.Data.Reverse, ColorHex = shape.Data.ColorHex
        }).ToList()).ToList()
    };
    private async void SaveProject(object sender, RoutedEventArgs e)
    {
        if (original == null) return;
        FinishEditing();
        var dialog = new SaveFileDialog { Filter = "DocAssistant 編集データ|*.pdfwrite", DefaultExt = ".pdfwrite", InitialDirectory = settings.SaveFolder };
        if (dialog.ShowDialog(this) != true) return;
        await Guard(async () => { await WriteProject(dialog.FileName); MarkSaved(); Status.Text = "編集データを保存しました（元PDF・文字・手書き・図形）。"; });
    }
    private async Task WriteProject(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var output = zip.CreateEntry("original.pdf").Open()) await output.WriteAsync(original!);
                using (var output = zip.CreateEntry("document.json").Open()) await JsonSerializer.SerializeAsync(output, Capture());
                for (int i = 0; i < views.Count; i++) { using var output = zip.CreateEntry($"ink/{i}.isf").Open(); views[i].Canvas.Strokes.Save(output); }
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async void OpenProject(object sender, RoutedEventArgs e)
    {
        if (!CanReplace()) return;
        var dialog = new OpenFileDialog { Filter = "DocAssistant 編集データ|*.pdfwrite", InitialDirectory = settings.SaveFolder };
        if (dialog.ShowDialog(this) == true) await Guard(async () =>
        {
            await ReadProject(dialog.FileName); DocumentTitle.Text = Path.GetFileName(dialog.FileName);
            changingTemplate = true; TemplateBox.SelectedItem = currentTemplate = null; changingTemplate = false;
        });
    }
    private async Task ReadProject(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        async Task<byte[]> Read(string name) { using var input = (zip.GetEntry(name) ?? throw new InvalidDataException($"{name} がありません。")).Open(); using var output = new MemoryStream(); await input.CopyToAsync(output); return output.ToArray(); }
        var bytes = await Read("original.pdf");
        var data = JsonSerializer.Deserialize<DocumentData>(await Read("document.json")) ?? throw new InvalidDataException("編集データが空です。");
        var ink = new List<byte[]>(); for (int i = 0; i < data.Pages.Count; i++) ink.Add(await Read($"ink/{i}.isf"));
        await LoadPdf(bytes, data, ink);
    }
    private async void Print(object sender, RoutedEventArgs e)
    {
        if (original == null) return;
        FinishEditing();
        await Guard(() =>
        {
            var dialog = new PrintDialog(); if (dialog.ShowDialog() != true) return Task.CompletedTask;
            dialog.PrintDocument(BuildPrintDocument(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight).DocumentPaginator, "DocAssistant");
            return Task.CompletedTask;
        });
    }
    private FixedDocument BuildPrintDocument(double width, double height)
    {
        var document = new FixedDocument();
        foreach (var view in views)
        {
            var page = new FixedPage { Width = width, Height = height, Background = Brushes.White };
            var content = new Grid { Width = view.Width, Height = view.Height, Background = Brushes.White, ClipToBounds = true };
            content.Children.Add(new Image { Source = view.Background, Stretch = Stretch.Fill });
            var overlay = new Canvas { Width = view.Width, Height = view.Height };
            foreach (TextBox box in view.Canvas.Children.OfType<TextBox>())
            {
                var text = new TextBlock { Text = box.Text, Width = box.Width, Height = box.Height, FontFamily = box.FontFamily, FontSize = box.FontSize, Foreground = box.Foreground, TextWrapping = TextWrapping.Wrap, ClipToBounds = true, Padding = new Thickness(2) };
                Canvas.SetLeft(text, InkCanvas.GetLeft(box)); Canvas.SetTop(text, InkCanvas.GetTop(box)); overlay.Children.Add(text);
            }
            if (PrintInk.IsChecked == true)
            {
                foreach (var shape in view.Canvas.Children.OfType<ShapeElement>())
                {
                    var copy = new ShapeElement(shape.Data) { Width = shape.Width, Height = shape.Height };
                    Canvas.SetLeft(copy, InkCanvas.GetLeft(shape)); Canvas.SetTop(copy, InkCanvas.GetTop(shape)); overlay.Children.Add(copy);
                }
            }
            content.Children.Add(overlay);
            if (PrintInk.IsChecked == true) content.Children.Add(new InkPresenter { Width = view.Width, Height = view.Height, Strokes = view.Canvas.Strokes.Clone(), IsHitTestVisible = false });
            var scale = Math.Min(page.Width / view.Width, page.Height / view.Height); content.LayoutTransform = new ScaleTransform(scale, scale);
            FixedPage.SetLeft(content, (page.Width - view.Width * scale) / 2); FixedPage.SetTop(content, (page.Height - view.Height * scale) / 2);
            page.Children.Add(content); var pageContent = new PageContent(); ((System.Windows.Markup.IAddChild)pageContent).AddChild(page); document.Pages.Add(pageContent);
        }
        return document;
    }
}
