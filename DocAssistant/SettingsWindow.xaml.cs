using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DocAssistant;
public partial class SettingsWindow : Window
{
    private readonly List<TemplateItem> registered;
    public AppSettings Result { get; private set; }
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        SaveFolder.Text = settings.SaveFolder;
        TemplateFolder.Text = settings.TemplateFolder;
        registered = (settings.TemplateFiles ?? []).Select(p => new TemplateItem(Path.GetFileNameWithoutExtension(p), p)).ToList();
        Refresh(settings.DefaultTemplate);
    }
    private void Refresh(string? selected = null)
    {
        selected ??= (StartupTemplate.SelectedItem as TemplateItem)?.Path ?? "";
        RegisteredFiles.ItemsSource = null; RegisteredFiles.ItemsSource = registered;
        try
        {
            var items = new AppSettings { TemplateFolder = TemplateFolder.Text, TemplateFiles = registered.Select(t => t.Path).ToList() }.GetTemplates();
            items.Insert(0, new TemplateItem("表示しない", ""));
            StartupTemplate.ItemsSource = items;
            StartupTemplate.SelectedItem = items.FirstOrDefault(t => t.Path == selected) ?? items[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "テンプレート一覧"); }
    }
    private void BrowseSave(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "既定の保存先" };
        if (dialog.ShowDialog(this) == true) SaveFolder.Text = dialog.FolderName;
    }
    private void BrowseTemplates(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "テンプレートフォルダ" };
        if (dialog.ShowDialog(this) == true) { TemplateFolder.Text = dialog.FolderName; Refresh(); }
    }
    private void FolderEdited(object sender, RoutedEventArgs e) => Refresh();
    private void AddTemplate(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF|*.pdf", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
            if (!registered.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) registered.Add(new TemplateItem(Path.GetFileNameWithoutExtension(path), path));
        Refresh();
    }
    private void RemoveTemplate(object sender, RoutedEventArgs e)
    {
        if (RegisteredFiles.SelectedItem is TemplateItem item) { registered.Remove(item); Refresh(); }
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(SaveFolder.Text)) throw new IOException("保存先には存在するフォルダを選択してください。");
            if (!string.IsNullOrWhiteSpace(TemplateFolder.Text) && !Directory.Exists(TemplateFolder.Text)) throw new IOException("テンプレートフォルダが見つかりません。");
            Refresh();
            var updated = new AppSettings { SaveFolder = Path.GetFullPath(SaveFolder.Text), TemplateFolder = TemplateFolder.Text.Trim(), TemplateFiles = registered.Select(t => t.Path).ToList(), DefaultTemplate = (StartupTemplate.SelectedItem as TemplateItem)?.Path ?? "" };
            updated.AccessDatabasePath = Result.AccessDatabasePath;
            updated.AccessFormName = Result.AccessFormName;
            updated.Llm = Result.Llm;
            updated.Save(); Result = updated; DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "設定を保存できませんでした"); }
    }
}
