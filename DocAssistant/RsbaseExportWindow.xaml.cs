using System.Windows;
using System.Windows.Controls;

namespace DocAssistant;

public partial class RsbaseExportWindow : Window
{
    public string FileName { get; private set; } = "";
    public RsbaseExportWindow(string id, string title)
    {
        InitializeComponent();
        PatientId.Text = id; RegistrationTitle.Text = title; OutputDate.SelectedDate = DateTime.Today;
        UpdatePreview();
    }
    private string Build() => RsbaseFileName.Build(PatientId.Text, Sequence.Text,
        OutputDate.SelectedDate ?? throw new ArgumentException("日付を選択してください。"), RegistrationTitle.Text);
    private void Changed(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void DateChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void UpdatePreview()
    {
        if (Preview == null) return;
        try { Preview.Text = Build(); }
        catch (ArgumentException ex) { Preview.Text = ex.Message; }
    }
    private void Accept(object sender, RoutedEventArgs e)
    {
        try { FileName = Build(); DialogResult = true; }
        catch (ArgumentException ex) { Preview.Text = ex.Message; }
    }
}
