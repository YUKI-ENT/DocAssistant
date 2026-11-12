using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DocAssistant;

public partial class MainWindow
{
    private bool certificateSaveConflict;
    private ICertificateStore CertificateDatabase() => new SqliteCertificateStore(settings.CertificateDatabasePath);
    private void BindCertificateToCurrentPatient(MedicalCertificate data)
    {
        string group = RsbaseFileName.PatientId(certificateCurrentText);
        if (group != "" && data.RsbasePatientId() == group) data.PatientKey = certificateCurrentKey;
    }
    private void LoadLatestCertificateForCurrentPatient()
    {
        string group = RsbaseFileName.PatientId(certificateCurrentText);
        if (!certificateAutoLoadEnabled || certificateCurrentKey == "" || group == "") return;
        CertificateTry(() =>
        {
            if (certificateDirty) SaveCertificateToDatabase(false);
            var latest = CertificateDatabase().Latest(group);
            if (latest == null)
            {
                DisplayCertificate(NewCertificateForCurrentPatient());
                certificateStatus.Text = "この患者の保存済み診断書はありません。患者情報を取り込みました。本文を入力してください。";
                return;
            }
            BindCertificateToCurrentPatient(latest);
            DisplayCertificate(latest);
            PreviewCertificate();
            certificateStatus.Text = $"患者の最新の診断書（第{latest.Revision}版・発行日 {latest.IssueDate:yyyy/MM/dd}）を読み込みました。";
        });
    }
    private void UpdateCertificateAge()
    {
        if (certificateAge == null) return;
        var age = MedicalCertificate.AgeOn(certificateBirthday.SelectedDate, certificateDate.SelectedDate ?? DateTime.Today);
        certificateAge.Text = age == null ? "年齢：未確定" : $"年齢：{age}歳";
    }
    private void SaveCertificateToDatabase(bool automatic)
    {
        var data = CaptureCertificate();
        try
        {
            certificate = CertificateDatabase().Save(data);
            certificateDirty = false; certificateSaveConflict = false; certificateSaveTimer.Stop();
            certificateStatus.Text = $"{(automatic ? "自動保存" : "保存")}しました（第{certificate.Revision}版）。保存履歴から再開できます。";
        }
        catch (InvalidOperationException) { certificateSaveConflict = true; throw; }
    }
    private void OpenCertificateHistory(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardCertificate()) return;
        CertificateTry(() =>
        {
            var store = CertificateDatabase();
            string group = RsbaseFileName.PatientId(certificateCurrentText);
            var dialog = new CertificateHistoryWindow(store, group, settings.SaveFolder) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedCertificate == null) return;
            var data = dialog.SelectedCertificate;
            if (dialog.CopyAsNew)
            {
                data.DocumentId = Guid.NewGuid().ToString("N"); data.Revision = 0; data.EnvironmentId = ""; data.IssueDate = DateTime.Today;
            }
            // Stored documents identify the patient by chart number within this database,
            // independently of the old workstation's MDB file path.
            BindCertificateToCurrentPatient(data);
            DisplayCertificate(data);
            if (dialog.CopyAsNew) { certificateDirty = true; SaveCertificateToDatabase(false); }
            else certificateStatus.Text = $"第{data.Revision}版を開きました。修正は新しい版として保存し、過去の内容は残ります。";
        });
    }
    private void BackupCertificates(object sender, RoutedEventArgs e) => CertificateTry(() =>
    {
        if (certificateDirty) SaveCertificateToDatabase(false);
        var dialog = new SaveFileDialog { Filter = "診断書DBバックアップ|*.sqlite", DefaultExt = ".sqlite", AddExtension = true,
            FileName = "DocAssistant-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sqlite", InitialDirectory = settings.SaveFolder };
        if (dialog.ShowDialog(this) != true) return;
        CertificateDatabase().Backup(dialog.FileName); certificateStatus.Text = "入力内容・全改訂履歴・発行時PDFをバックアップしました。";
    });
    private void RestoreCertificates(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardCertificate()) return;
        CertificateTry(() =>
        {
            var dialog = new OpenFileDialog { Filter = "診断書DBバックアップ|*.sqlite", InitialDirectory = settings.SaveFolder };
            if (dialog.ShowDialog(this) != true) return;
            SqliteCertificateStore.VerifyBackup(dialog.FileName);
            if (MessageBox.Show(this, "現在の診断書DB全体を選択したバックアップで置き換えます。\n現在のDBは、同じフォルダーへ復元前バックアップとして退避します。復元しますか？", "診断書DBの復元", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            new SqliteCertificateStore(settings.CertificateDatabasePath).Restore(dialog.FileName);
            DisplayCertificate(new MedicalCertificate { Clinic = JsonSerializer.Deserialize<CertificateClinic>(JsonSerializer.Serialize(settings.CertificateClinic))! });
            certificateStatus.Text = "DBを復元しました。保存履歴から文書を開いてください。復元前DBも保存されています。";
        });
    }
}

internal sealed class CertificateHistoryWindow : Window
{
    internal MedicalCertificate? SelectedCertificate { get; private set; }
    internal bool CopyAsNew { get; private set; }
    internal CertificateHistoryWindow(ICertificateStore store, string patientGroup, string outputFolder)
    {
        Title = "英文診断書の保存履歴"; Width = 1000; Height = 660; MinWidth = 680; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) });
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 245, 248));
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "保存履歴から編集を再開・コピー・発行時PDFの再出力", FontSize = 20, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        top.Children.Add(new TextBlock { Text = "保存DB：" + store.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var filter = new WrapPanel(); top.Children.Add(filter);
        var currentOnly = new CheckBox { Content = "現在の患者のみ（全枝番）", IsChecked = patientGroup != "", IsEnabled = patientGroup != "", Margin = new Thickness(0, 6, 16, 8) }; filter.Children.Add(currentOnly);
        var search = new TextBox { MinWidth = 220, Margin = new Thickness(0, 0, 0, 10), ToolTip = "患者番号・氏名で絞り込み" }; filter.Children.Add(search);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new ListBox { FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 10) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto); root.Children.Add(list);
        IReadOnlyList<CertificateHistoryEntry> entries = [];
        void ShowEntries()
        {
            var selected = list.SelectedItem;
            list.ItemsSource = entries.Where(item => (currentOnly.IsChecked != true || RsbaseFileName.PatientId("カルテ番号：" + item.ChartNumber) == patientGroup) &&
                (search.Text.Trim() == "" || item.Patient.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || item.ChartNumber.Contains(search.Text.Trim()))).ToList();
            if (selected != null && list.Items.Contains(selected)) list.SelectedItem = selected;
            status.Text = $"{list.Items.Count}件。過去版を編集すると最新の変更との競合を検出します。過去版から別文書を作る場合は「コピーして新規」を使用してください。";
        }
        void Refresh() { entries = store.History(); ShowEntries(); }
        void Try(Action action) { try { action(); } catch (Exception ex) { status.Text = "処理できませんでした：" + ex.Message; } }
        Button ActionButton(string text, RoutedEventHandler action)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0) }; b.Click += action; buttons.Children.Add(b); return b;
        }
        void Open(bool copy) => Try(() =>
        {
            if (list.SelectedItem is not CertificateHistoryEntry entry) { status.Text = "文書を選択してください。"; return; }
            var latest = entries.Where(e => e.DocumentId == entry.DocumentId).Max(e => e.Revision);
            if (!copy && latest != entry.Revision) { status.Text = "過去の版です。最新の版を開くか「コピーして新規」を使用してください。"; return; }
            SelectedCertificate = store.Load(entry.DocumentId, entry.Revision); CopyAsNew = copy; DialogResult = true;
        });
        ActionButton("編集を再開", (_, _) => Open(false)); ActionButton("コピーして新規", (_, _) => Open(true));
        ActionButton("発行時PDFを再出力", (_, _) => Try(() =>
        {
            if (list.SelectedItem is not CertificateHistoryEntry entry || entry.IssueId == "") { status.Text = "PDFを保存した履歴を選択してください。"; return; }
            var pdf = store.LoadPdf(entry.IssueId);
            var save = new SaveFileDialog { Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true, FileName = entry.FileName, InitialDirectory = outputFolder };
            if (save.ShowDialog(this) != true) return;
            CertificateRenderer.WriteBytes(save.FileName, pdf); store.RecordExport(entry.IssueId, save.FileName);
            Refresh(); status.Text = "発行時と同じPDFを再出力しました。本文や発行日は変更していません。";
        }));
        ActionButton("発行時PDFを確認", async (_, _) =>
        {
            if (list.SelectedItem is not CertificateHistoryEntry entry || entry.IssueId == "") { status.Text = "PDFを保存した履歴を選択してください。"; return; }
            try
            {
                var pages = await PdfRenderer.RenderAsync(store.LoadPdf(entry.IssueId));
                var panel = new StackPanel { Margin = new Thickness(12) };
                foreach (var page in pages) panel.Children.Add(new Image { Source = page.Image, Margin = new Thickness(0, 0, 0, 14) });
                new Window { Title = "発行時PDF", Width = 820, Height = 900, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                    Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
            }
            catch (Exception ex) { status.Text = "PDFを確認できませんでした：" + ex.Message; }
        });
        ActionButton("再取得", (_, _) => Try(Refresh));
        currentOnly.Checked += (_, _) => ShowEntries(); currentOnly.Unchecked += (_, _) => ShowEntries(); search.TextChanged += (_, _) => ShowEntries();
        Try(Refresh);
    }
}
