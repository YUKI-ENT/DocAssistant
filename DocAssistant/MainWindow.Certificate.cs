using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DocAssistant;

public partial class MainWindow
{
    private MedicalCertificate certificate = new();
    private readonly TextBox[] certificateSections = new TextBox[4];
    private TextBox certificateName = null!;
    private ComboBox certificateSex = null!;
    private TextBlock certificateAge = null!;
    private readonly System.Windows.Threading.DispatcherTimer certificateSaveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool certificateSaveTimerReady;
    private readonly TextBox[] certificateClinic = new TextBox[4];
    private DatePicker certificateBirthday = null!, certificateDate = null!;
    private TextBlock certificateStatus = null!, certificatePatient = null!;
    private StackPanel certificatePreview = null!;
    private bool certificateDirty, changingCertificate;
    private string certificateCurrentKey = "", certificateCurrentText = "";
    private bool certificateAutoLoadEnabled;

    private void InitializeCertificate()
    {
        var root = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        root.SetValue(TextElement.FontSizeProperty, 14d);
        root.SetValue(TextElement.FontWeightProperty, FontWeights.Normal);
        root.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { SaveCertificate(root, e); e.Handled = true; }
        };
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        Button Action(string label, RoutedEventHandler action)
        {
            var b = new Button { Content = label }; b.Click += action; toolbar.Children.Add(b); return b;
        }
        Action("新規 / 患者情報取込", NewCertificate);
        Action("保存履歴", OpenCertificateHistory); Action("保存", SaveCertificate);
        Action("プレビュー更新", (_, _) => CertificateTry(() => PreviewCertificate()));
        Action("PDF出力", ExportCertificate);
        var management = Action("データ管理", (sender, _) => { if (sender is Button b) { b.ContextMenu.PlacementTarget = b; b.ContextMenu.IsOpen = true; } });
        management.ContextMenu = new ContextMenu();
        foreach (var (label, handler) in new (string, RoutedEventHandler)[] { ("旧下書きを取り込む", OpenCertificate), ("DBをバックアップ", BackupCertificates), ("バックアップから復元", RestoreCertificates) })
        {
            var item = new MenuItem { Header = label }; item.Click += handler; management.ContextMenu.Items.Add(item);
        }
        certificateStatus = new TextBlock { Text = "新規で患者情報を取り込むか、手入力で作成できます。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 10), Foreground = Brushes.Teal };
        DockPanel.SetDock(certificateStatus, Dock.Top); root.Children.Add(certificateStatus);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 }); root.Children.Add(grid);
        var fields = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        certificatePreview = new StackPanel { Margin = new Thickness(12) };
        var previewScroll = new ScrollViewer { Content = certificatePreview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = new SolidColorBrush(Color.FromRgb(222, 228, 235)) };
        Grid.SetColumn(previewScroll, 2); grid.Children.Add(previewScroll);
        void Label(string title) => fields.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap });
        TextBox Input(bool multi = false)
        {
            var box = new TextBox { AcceptsReturn = multi, TextWrapping = TextWrapping.Wrap, MinHeight = multi ? 80 : 34, MaxHeight = multi ? 220 : 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            box.TextChanged += CertificateChanged; fields.Children.Add(box); return box;
        }
        certificatePatient = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray }; fields.Children.Add(certificatePatient);
        Label("患者ローマ字氏名（名 → 姓・旅券の綴りを確認）"); certificateName = Input();
        Label("生年月日"); certificateBirthday = new DatePicker(); certificateBirthday.SelectedDateChanged += CertificateChanged; fields.Children.Add(certificateBirthday);
        Label("発行日"); certificateDate = new DatePicker { SelectedDate = DateTime.Today }; certificateDate.SelectedDateChanged += CertificateChanged; fields.Children.Add(certificateDate);
        Label("性別 / 年齢（発行日時点・自動計算）");
        var demographicRow = new WrapPanel();
        certificateSex = new ComboBox { ItemsSource = MedicalCertificate.SexOptions, SelectedIndex = 0, MinWidth = 110 };
        certificateSex.SelectionChanged += CertificateChanged; demographicRow.Children.Add(certificateSex);
        certificateAge = new TextBlock { Margin = new Thickness(16, 6, 0, 0) }; demographicRow.Children.Add(certificateAge); fields.Children.Add(demographicRow);
        fields.Children.Add(new TextBlock { Text = "AIによる作成・修正は中央のAIアシスタントを使用し、生成結果を項目ごとに取り込めます。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 0) });
        for (int i = 0; i < 4; i++)
        {
            Label($"{MedicalCertificate.Labels[i]} / {MedicalCertificate.Headings[i]}"); certificateSections[i] = Input(true);
            if (i < 2) certificateSections[i].MinHeight = 48;
        }
        int clinicStart = fields.Children.Count;
        Label("医院情報（英文・この文書に使用）");
        string[] clinicLabels = ["医院名", "住所", "連絡先（電話番号等）", "医師名・資格"];
        for (int i = 0; i < 4; i++) { Label(clinicLabels[i]); certificateClinic[i] = Input(i == 1); }
        var defaults = new WrapPanel(); fields.Children.Add(defaults);
        var restore = new Button { Content = "医院の既定情報を反映" }; defaults.Children.Add(restore);
        restore.Click += (_, _) => { if (MessageBox.Show(this, "この文書の医院情報を設定の既定値で置き換えますか？", "医院情報", MessageBoxButton.YesNo) == MessageBoxResult.Yes) SetCertificateClinic(settings.CertificateClinic); };
        var saveDefaults = new Button { Content = "医院の既定情報として保存" }; defaults.Children.Add(saveDefaults);
        saveDefaults.Click += (_, _) => CertificateTry(() => { var previous = settings.CertificateClinic; settings.CertificateClinic = ReadCertificateClinic(); try { settings.Save(); } catch { settings.CertificateClinic = previous; throw; } certificateStatus.Text = "医院の既定情報を保存しました。"; });
        var clinicPanel = new StackPanel();
        while (fields.Children.Count > clinicStart) { var element = fields.Children[clinicStart]; fields.Children.RemoveAt(clinicStart); clinicPanel.Children.Add(element); }
        fields.Children.Add(new Expander { Header = "医院情報を確認・変更", Content = clinicPanel, Margin = new Thickness(0, 12, 0, 12) });
        certificateSaveTimer.Tick += (_, _) => { certificateSaveTimer.Stop(); if (certificateDirty) CertificateTry(() => SaveCertificateToDatabase(true)); };
        certificateSaveTimerReady = true;
        UpdateCertificateAge();
        CertificateTab.Content = root;
        changingCertificate = false; certificateDirty = false;
    }

    private void CertificateChanged(object? sender, EventArgs e)
    {
        if (changingCertificate || certificateStatus == null) return;
        certificateDirty = true; certificatePreview.Children.Clear(); UpdateCertificateAge();
        if (certificateSaveTimerReady) { certificateSaveTimer.Stop(); certificateSaveTimer.Start(); }
        certificateStatus.Text = "変更あり。プレビューを更新して全ページを確認してください。";
    }

    private void TrackCertificatePatient(AccessPatientDisplay display)
    {
        string id = MedicalCertificate.Read(display.Text, "カルテ番号");
        string key = display.Detected && !string.IsNullOrEmpty(id) ? display.DatabasePath + "|" + id : "";
        certificateCurrentText = key == "" ? "" : display.Text;
        if (key != certificateCurrentKey)
        {
            certificateCurrentKey = key;
            if (certificateAutoLoadEnabled && key != "" && WorkspaceTabs.SelectedItem == CertificateTab)
                LoadLatestCertificateForCurrentPatient();
        }
        if (certificatePatient != null) certificatePatient.Text = certificate.PatientSource +
            (certificate.PatientKey != "" && certificate.PatientKey != key ? "\n⚠ カルテの患者と作成中の患者が異なります。文書は元の患者のまま保持しています。" : "");
    }

    private bool CanDiscardCertificate()
    {
        if (!certificateDirty) return true;
        certificateSaveTimer.Stop();
        try { SaveCertificateToDatabase(false); return true; }
        catch (Exception ex)
        {
            bool discard = MessageBox.Show(this, "診断書を保存できませんでした。変更を破棄しますか？\n" + ex.Message, "英文診断書", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
            if (!discard && !certificateSaveConflict) certificateSaveTimer.Start();
            return discard;
        }
    }
    private CertificateClinic ReadCertificateClinic() => new() { Name = certificateClinic[0].Text.Trim(), Address = certificateClinic[1].Text.Trim(), Contact = certificateClinic[2].Text.Trim(), Physician = certificateClinic[3].Text.Trim() };
    private void SetCertificateClinic(CertificateClinic clinic)
    {
        string[] values = [clinic.Name, clinic.Address, clinic.Contact, clinic.Physician];
        for (int i = 0; i < 4; i++) certificateClinic[i].Text = values[i];
    }
    private MedicalCertificate CaptureCertificate()
    {
        return new MedicalCertificate { DocumentId = certificate.DocumentId, Revision = certificate.Revision, EnvironmentId = certificate.EnvironmentId, PatientKey = certificate.PatientKey, ChartNumber = certificate.ChartNumber, PatientSource = certificate.PatientSource,
            Name = certificateName.Text.Trim(), Sex = certificateSex.SelectedIndex > 0 ? certificateSex.SelectedItem as string ?? "" : "", Birthday = certificateBirthday.SelectedDate, IssueDate = certificateDate.SelectedDate ?? throw new InvalidOperationException("発行日を入力してください。"),
            Material = certificate.Material, Sections = certificateSections.Select(t => t.Text).ToArray(), Clinic = ReadCertificateClinic() };
    }
    private void DisplayCertificate(MedicalCertificate data)
    {
        certificateSaveTimer.Stop();
        changingCertificate = true;
        try
        {
            certificate = data; certificateName.Text = data.Name; certificateBirthday.SelectedDate = data.Birthday;
            certificateDate.SelectedDate = data.IssueDate; certificateSex.SelectedItem = data.Sex == "" ? "未登録" : data.Sex;
            for (int i = 0; i < 4; i++) { certificateSections[i].Text = data.Sections[i] ?? ""; }
            SetCertificateClinic(data.Clinic); certificatePreview.Children.Clear();
            certificatePatient.Text = data.PatientSource + (data.PatientKey != "" && data.PatientKey != certificateCurrentKey ? "\n⚠ カルテの患者と作成中の患者が異なります。" : "");
        }
        finally { changingCertificate = false; }
        certificateDirty = false; certificateSaveConflict = false; UpdateCertificateAge();
    }
    private void NewCertificate(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardCertificate()) return;
        DisplayCertificate(NewCertificateForCurrentPatient());
        certificateDirty = true; certificateSaveTimer.Start(); certificateStatus.Text = "新規作成。氏名は名→姓の候補です。旅券の綴り・長音と生年月日を確認してください。姓・名の区切りが不明な場合は手入力してください。";
    }
    private MedicalCertificate NewCertificateForCurrentPatient()
    {
        var text = certificateCurrentText;
        return new MedicalCertificate { PatientKey = certificateCurrentKey,
            ChartNumber = MedicalCertificate.Read(text, "カルテ番号"),
            PatientSource = text == "" ? "手入力の診断書" : "取込元：" + MedicalCertificate.Read(text, "氏名") + " / " + MedicalCertificate.Read(text, "カルテ番号"),
            Name = MedicalCertificate.CertificateNameCandidate(MedicalCertificate.Read(text, "フリガナ")), Sex = MedicalCertificate.ReadSex(text), Birthday = MedicalCertificate.ReadBirthday(text),
            Clinic = JsonSerializer.Deserialize<CertificateClinic>(JsonSerializer.Serialize(settings.CertificateClinic))! };
    }
    private void CertificateTry(Action action)
    {
        certificateSaveTimer.Stop();
        try { action(); }
        catch (Exception ex) { certificateStatus.Text = "処理できませんでした：" + ex.Message; }
        finally { if (certificateDirty && !certificateSaveConflict) certificateSaveTimer.Start(); }
    }
    private void SaveCertificate(object sender, RoutedEventArgs e) => CertificateTry(() => SaveCertificateToDatabase(false));
    private void OpenCertificate(object sender, RoutedEventArgs e)
    {
        if (!CanDiscardCertificate()) return;
        CertificateTry(() =>
        {
            var dialog = new OpenFileDialog { Filter = "英文診断書の下書き|*.medcert", InitialDirectory = settings.SaveFolder };
            if (dialog.ShowDialog(this) != true) return;
            var data = MedicalCertificate.Parse(File.ReadAllText(dialog.FileName));
            data.DocumentId = Guid.NewGuid().ToString("N"); data.Revision = 0; data.EnvironmentId = "";
            DisplayCertificate(data); certificateDirty = true;
            SaveCertificateToDatabase(false); certificateStatus.Text = "旧下書きを取り込み、4項目の新規文書としてDBに保存しました。削除対象の旧項目は取り込まず、元ファイルは保持しています。";
        });
    }
    private List<System.Windows.Media.Imaging.BitmapSource> PreviewCertificate()
    {
        var pages = CertificateRenderer.Render(CaptureCertificate());
        certificatePreview.Children.Clear();
        for (int i = 0; i < pages.Count; i++)
        {
            certificatePreview.Children.Add(new TextBlock { Text = $"{i + 1} / {pages.Count}", Margin = new Thickness(0, 4, 0, 5) });
            certificatePreview.Children.Add(new Image { Source = pages[i], Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 16) });
        }
        certificateStatus.Text = $"プレビュー：A4縦 {pages.Count}ページ。医学的内容・旅券表記・署名欄を確認してください。";
        return pages;
    }
    private void ExportCertificate(object sender, RoutedEventArgs e) => CertificateTry(() =>
    {
        var data = CaptureCertificate();
        if (string.IsNullOrWhiteSpace(data.Name) || data.Birthday == null || data.Age == null || string.IsNullOrWhiteSpace(data.Sex) || string.IsNullOrWhiteSpace(data.Clinic.Name) || string.IsNullOrWhiteSpace(data.Clinic.Physician) || !data.Sections.Any(s => !string.IsNullOrWhiteSpace(s)))
            throw new InvalidOperationException("患者氏名・発行日以前の生年月日・性別・医院名・医師名・本文を入力してください。");
        if (data.PatientKey != "" && data.PatientKey != certificateCurrentKey) throw new InvalidOperationException("カルテの患者と作成中の患者が一致していません。対象患者を再表示してから出力してください。");
        var pages = PreviewCertificate();
        string fileName = "MedicalCertificate_" + data.IssueDate.ToString("yyyyMMdd") + ".pdf";
        if (settings.CertificateRsbaseNaming)
        {
            var naming = new RsbaseExportWindow(data.RsbasePatientId(), settings.CertificateRegistrationTitle) { Owner = this };
            if (naming.ShowDialog() != true) return;
            fileName = naming.FileName;
        }
        var dialog = new SaveFileDialog { Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true, FileName = fileName, InitialDirectory = settings.SaveFolder };
        if (dialog.ShowDialog(this) != true) return;
        var pdf = CertificateRenderer.CreatePdf(pages);
        var store = CertificateDatabase();
        var issue = store.PrepareIssue(data, pdf, System.IO.Path.GetFileName(dialog.FileName));
        certificate = issue.Data; certificateDirty = false; certificateSaveConflict = false;
        try
        {
            CertificateRenderer.WriteBytes(dialog.FileName, pdf); store.RecordExport(issue.Id, dialog.FileName);
            certificateStatus.Text = $"PDF（{pages.Count}ページ）を出力し、第{certificate.Revision}版と発行時PDFをDBに保存しました。";
        }
        catch (Exception ex) { certificateStatus.Text = "入力内容とPDFはDBに保存済みですが、出力の完了を記録できませんでした。保存履歴から再出力できます：" + ex.Message; }
    });

}
