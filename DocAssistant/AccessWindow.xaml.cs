using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DocAssistant;
public partial class AccessWindow : Window
{
    private readonly AppSettings settings;
    private AccessSession? session;
    private AccessInspection? inspection;
    private bool busy, closed, connected;
    public AccessWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        DatabasePathBox.Text = settings.AccessDatabasePath;
        if (string.IsNullOrWhiteSpace(DatabasePathBox.Text))
        {
            var candidate = Path.Combine(settings.TemplateFolder, "DYNA_cnt_ons.mdb");
            if (File.Exists(candidate)) DatabasePathBox.Text = candidate;
        }
        FormNameBox.Text = settings.AccessFormName;
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Access データベース|*.mdb;*.accdb;*.mde;*.accde", Title = "電子カルテのクライアントMDBを選択" };
        if (dialog.ShowDialog(this) == true) DatabasePathBox.Text = dialog.FileName;
    }
    private void ConnectionInputChanged(object sender, TextChangedEventArgs e)
    {
        if (ControlGrid == null) return;
        ClearInspection();
        if (sender == DatabasePathBox) connected = false;
        RefreshButtons();
    }
    private void ClearInspection()
    {
        inspection = null;
        if (MaterialBox != null) MaterialBox.Clear();
        ControlGrid.ItemsSource = null;
        CopyButton.IsEnabled = false;
        OpenFormsLabel.Text = "開いているフォーム：未取得";
    }
    private void RefreshButtons()
    {
        if (ConnectButton == null || CopyButton == null) return;
        BrowseButton.IsEnabled = AttachButton.IsEnabled = ConnectButton.IsEnabled = DatabasePathBox.IsEnabled = FormNameBox.IsEnabled = !busy;
        RefreshButton.IsEnabled = OpenFormButton.IsEnabled = !busy && connected;
        CopyButton.IsEnabled = !busy && inspection != null;
        PatientButton.IsEnabled = !busy && connected;
        MaterialCopyButton.IsEnabled = !busy;
    }
    private async Task Run(Func<Task<AccessInspection>> action)
    {
        if (busy) return;
        busy = true;
        ClearInspection(); RefreshButtons();
        ConnectionStatus.Text = "Accessに接続中です。起動・ログイン・確認画面が出た場合はAccess側で操作してください。";
        try
        {
            var result = await action();
            if (closed) return;
            connected = true;
            ShowInspection(result);
        }
        catch (Exception ex)
        {
            if (closed) return;
            connected = false;
            ConnectionStatus.Text = "接続・読み込みを完了できませんでした。";
            NotesBox.Text = ex is COMException
                ? $"Access COMエラー 0x{ex.HResult:X8}\n{ex.Message}\nAccess側で対象MDBと「患者マスター」が開けること、リンク先・ログイン・権限をご確認ください。"
                : ex.Message;
            NotesBox.Text += $"\nDocAssistant実行：{(Environment.Is64BitProcess ? 64 : 32)}bit / .NET {Environment.Version}\n接続方式：Access COM（Jet/ACE OLE DBへの直接接続は使用していません）";
            NotesBox.Text += "\n" + session?.Diagnostics;
        }
        finally { busy = false; if (!closed) RefreshButtons(); }
    }
    private async void Connect(object sender, RoutedEventArgs e) => await ConnectCore(true);
    private async void Attach(object sender, RoutedEventArgs e) => await ConnectCore(false);
    private async Task ConnectCore(bool openIfMissing)
    {
        var path = DatabasePathBox.Text.Trim();
        var form = FormNameBox.Text.Trim();
        await Run(async () =>
        {
            AccessSession.ValidatePath(path);
            if (string.IsNullOrWhiteSpace(form)) throw new InvalidOperationException("対象フォーム名を入力してください。");
            settings.AccessDatabasePath = Path.GetFullPath(path); settings.AccessFormName = form; settings.Save();
            session?.Dispose(); session = new AccessSession();
            return await session.ConnectAsync(path, form, openIfMissing);
        });
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await RefreshCore(false);
    private async void OpenForm(object sender, RoutedEventArgs e) => await RefreshCore(true);
    private async Task RefreshCore(bool open)
    {
        if (session == null) return;
        var form = FormNameBox.Text.Trim();
        await Run(() => session.RefreshAsync(form, open));
    }
    internal void ShowInspection(AccessInspection result)
    {
        inspection = result;
        ControlGrid.ItemsSource = result.Controls;
        OpenFormsLabel.Text = "開いているフォーム：" + (result.OpenForms.Count == 0 ? "なし" : string.Join("、", result.OpenForms));
        ConnectionStatus.Text = result.FormFound ? $"接続済み / {result.TargetForm} / {result.Controls.Count}項目" : $"Accessに接続済み / {result.TargetForm} の表示待ち";
        NotesBox.Text = result.Notes.Count == 0 ? "フォーム構造を取得しました。氏名・住所・生年月日・カルテ記載に対応する項目名と連結先を確認できます。" : string.Join(Environment.NewLine, result.Notes);
        CopyButton.IsEnabled = true;
    }
    private void CopyStructure(object sender, RoutedEventArgs e)
    {
        if (inspection == null) return;
        try { Clipboard.SetText(JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); }
        catch (ExternalException) { NotesBox.Text = "クリップボードを使用できません。少し待ってから再度コピーしてください。"; }
    }
    private async void ReadPatient(object sender, RoutedEventArgs e)
    {
        if (busy || session == null) return;
        busy = true;
        MaterialBox.Clear();
        RefreshButtons();
        try
        {
            var text = await session.ReadPatientAsync();
            if (closed) return;
            MaterialBox.Text = text;
            NotesBox.Text = $"患者情報9項目を取得しました（{DateTime.Now:HH:mm:ss}）。";
        }
        catch (Exception ex)
        {
            if (!closed) NotesBox.Text = ex.Message;
        }
        finally { busy = false; if (!closed) RefreshButtons(); }
    }
    private void CopyMaterial(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(MaterialBox.Text)) return;
        try { Clipboard.SetText(MaterialBox.Text); }
        catch (ExternalException) { NotesBox.Text = "クリップボードを使用できません。再度お試しください。"; }
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowClosed(object? sender, EventArgs e)
    {
        closed = true;
        session?.Dispose(); session = null;
    }
}
