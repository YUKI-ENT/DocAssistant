using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DocAssistant;

public partial class MainWindow
{
    private ReferralPatient? currentReferralPatient, editingReferralPatient;
    private IReadOnlyList<ReferralLetter> referralLetters = [];
    private ReferralLetter? referralBaseline;
    private int referralIndex = -1;
    private bool changingReferral, referralDirty, referralBusy, referralLoaded, referralSaveUncertain;
    private long referralPatientVersion;
    private string referralAttention = "";
    private IReadOnlyDictionary<long, string>? referralSavedPrescriptions;
    private string referralSavedPrescriptionsStatus = "";

    private void TrackReferralPatient(AccessPatientDisplay display)
    {
        string Read(string name) => display.Text.Split('\n').Select(l => l.TrimEnd('\r').Split('：', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0] == name)?[1] ?? "";
        var next = display.Detected && !string.IsNullOrWhiteSpace(display.DatabasePath) &&
            long.TryParse(Read("カルテ番号"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? new ReferralPatient(display.DatabasePath, id, Read("氏名")) : null;
        referralAttention = next == null ? "" : display.PatientMemo;
        if (ReferralAddAttention != null) ReferralAddAttention.IsEnabled = next?.SameIdentity(editingReferralPatient) == true && !string.IsNullOrWhiteSpace(referralAttention);
        if ((next == null && currentReferralPatient == null) || next?.SameIdentity(currentReferralPatient) == true) return;
        currentReferralPatient = next; referralPatientVersion++;
        UpdateReferralControls();
        if (!referralDirty && !referralBusy && WorkspaceTabs.SelectedItem == ReferralTab)
            _ = LoadReferralHistoryAsync(false);
    }

    private void UpdateReferralControls()
    {
        if (ReferralFields == null) return;
        bool matches = editingReferralPatient?.SameIdentity(currentReferralPatient) == true;
        ReferralPatientWarning.Visibility = editingReferralPatient != null && !matches ? Visibility.Visible : Visibility.Collapsed;
        ReferralPatientLabel.Text = editingReferralPatient?.Display ?? currentReferralPatient?.Display ?? "Accessで患者を表示してください。";
        ReferralMedication.IsEnabled = matches;
        ReferralAddMedication.IsEnabled = matches && !referralBusy && referralBaseline != null && ReferralMedication.SelectedItem is ReferralPrescription;
        ReferralAddAttention.IsEnabled = matches && !string.IsNullOrWhiteSpace(referralAttention);
        ReferralFields.IsEnabled = !referralBusy && referralBaseline != null;
        ReferralOlder.IsEnabled = !referralBusy && referralLetters.Count > 0 && referralIndex < referralLetters.Count - 1;
        ReferralNewer.IsEnabled = !referralBusy && referralIndex > 0;
        ReferralReload.IsEnabled = !referralBusy && currentReferralPatient != null;
        ReferralNew.IsEnabled = !referralBusy && !referralSaveUncertain && matches;
        ReferralCopy.IsEnabled = !referralBusy && !referralSaveUncertain && matches && referralBaseline != null;
        ReferralOpenAccess.IsEnabled = !referralBusy && !referralSaveUncertain && matches && referralBaseline != null;
        ReferralSave.IsEnabled = !referralBusy && referralBaseline != null;
        ReferralPosition.Text = referralBaseline == null ? (referralLoaded ? "過去の紹介状はありません。「新規」から作成できます。" : "未取得") :
            referralBaseline.Number == null ? "新規の下書き · 未保存" :
            $"紹介番号 {referralBaseline.Number} · {referralIndex + 1} / {referralLetters.Count}件（新しい順・全枝番）" +
                (referralDirty ? " · 未保存の変更あり" : "") +
                $" · カルテ番号 {referralBaseline.ChartNumber / 10}-{referralBaseline.ChartNumber % 10}";
    }

    private bool CanDiscardReferral()
    {
        return !referralDirty || MessageBox.Show(this, "紹介状の未保存の変更を破棄しますか？", "紹介状",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async void ReloadReferrals(object sender, RoutedEventArgs e)
    {
        if (referralBusy || !CanDiscardReferral()) return;
        await LoadReferralHistoryAsync(true);
    }

    private async Task LoadReferralHistoryAsync(bool refreshChoices)
    {
        if (referralBusy) return;
        var patient = currentReferralPatient;
        if (patient == null)
        {
            if (!referralDirty) { editingReferralPatient = null; referralLetters = []; referralLoaded = false; DisplayReferral(null, -1); }
            ReferralStatus.Text = "Accessで患者を表示してから再取得してください。";
            return;
        }
        var version = referralPatientVersion;
        referralBusy = true; UpdateReferralControls(); ReferralStatus.Text = "紹介状と紹介先候補を取得中…";
        try
        {
            if (chartSession == null || chartSession.MonitorTimedOut) throw new InvalidOperationException("カルテ情報の「再取得」でAccessに接続してください。");
            var history = await chartSession.ReadReferralsAsync(patient, refreshChoices);
            if (chartClosed || version != referralPatientVersion) { ReferralStatus.Text = "取得中に患者が変わりました。再取得してください。"; return; }
            referralSavedPrescriptions = history.SavedPrescriptions;
            referralSavedPrescriptionsStatus = history.SavedPrescriptionsStatus;
            referralLetters = history.Letters; editingReferralPatient = patient; referralLoaded = true; referralSaveUncertain = false;
            changingReferral = true;
            try
            {
                ReferralDestination1.ItemsSource = history.Choices?.Destinations1;
                ReferralDestination2.ItemsSource = history.Choices?.Destinations2;
                ReferralDoctor.ItemsSource = history.Choices?.Doctors;
                ReferralDiagnosis.ItemsSource = history.Diagnoses;
                ReferralPurpose.ItemsSource = history.Purposes;
                ReferralTemplate.ItemsSource = history.Templates;
                ReferralTemplate.SelectedIndex = -1;
                ReferralMedication.ItemsSource = ReferralPrescription.FromHistory(history.Medication);
                ReferralMedication.SelectedIndex = -1;
                ReferralMedication.ToolTip = history.Medication?.Status ?? "投薬履歴を取得できませんでした。再取得してください。";
            }
            finally { changingReferral = false; }
            ReferralChoicesHint.Text = history.ChoicesStatus;
            DisplayReferral(referralLetters.FirstOrDefault(), referralLetters.Count > 0 ? 0 : -1);
            ReferralStatus.Text = $"紹介状 {referralLetters.Count}件を取得しました。";
        }
        catch (Exception ex)
        {
            ReferralStatus.Text = ex is InvalidOperationException ? ex.Message : "紹介状を取得できません。Accessの接続先と紹介状テーブルを確認してください。";
        }
        finally { FinishReferralOperation(); }
    }

    private void DisplayReferral(ReferralLetter? letter, int index)
    {
        changingReferral = true;
        try
        {
            referralBaseline = letter; referralIndex = index;
            ReferralSavedPrescription.Text = letter?.Number is long savedId
                ? !string.IsNullOrEmpty(referralSavedPrescriptionsStatus) ? referralSavedPrescriptionsStatus
                    : referralSavedPrescriptions?.TryGetValue(savedId, out var savedText) == true && !string.IsNullOrWhiteSpace(savedText)
                        ? savedText : "この紹介状に登録された処方はありません。"
                : "新規の紹介状には、別途保存された処方はありません。";
            ReferralDate.SelectedDate = letter?.Date;
            ReferralDestination1.Text = letter?.Destination1 ?? ""; ReferralDestination2.Text = letter?.Destination2 ?? "";
            ReferralDoctor.Text = letter?.Doctor ?? ""; ReferralDiagnosis.Text = letter?.Diagnosis ?? "";
            ReferralPurpose.Text = letter?.Purpose ?? ""; ReferralTreatment.Text = letter?.Treatment ?? "";
            ReferralTests.Text = letter?.Tests ?? ""; ReferralRemarks.Text = letter?.Remarks ?? "";
            ReferralTestResults.Text = letter?.TestResults ?? "";
            referralDirty = letter != null && letter.Number == null;
        }
        finally { changingReferral = false; }
        UpdateReferralControls();
    }

    private ReferralLetter CaptureReferral() => new(referralBaseline?.Number,
        referralBaseline?.ChartNumber ?? throw new InvalidOperationException("紹介状を選択してください。"),
        ReferralDate.SelectedDate?.Date == referralBaseline.Date?.Date ? referralBaseline.Date : ReferralDate.SelectedDate,
        ReferralDestination1.Text, ReferralDestination2.Text, ReferralDoctor.Text, ReferralDiagnosis.Text,
        ReferralPurpose.Text, ReferralTreatment.Text, ReferralTests.Text, ReferralRemarks.Text, ReferralTestResults.Text);

    private void ReferralChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || changingReferral || referralBaseline == null) return;
        referralDirty = referralBaseline.Number == null || CaptureReferral() != referralBaseline;
        UpdateReferralControls();
    }
    private void OlderReferral(object sender, RoutedEventArgs e) => MoveReferral(1);
    private void NewerReferral(object sender, RoutedEventArgs e) => MoveReferral(-1);
    private void MoveReferral(int offset)
    {
        var index = referralIndex < 0 ? 0 : referralIndex + offset;
        if (referralBusy || index < 0 || index >= referralLetters.Count || !CanDiscardReferral()) return;
        DisplayReferral(referralLetters[index], index);
    }
    private void NewReferral(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralSaveUncertain || editingReferralPatient?.SameIdentity(currentReferralPatient) != true || !CanDiscardReferral()) return;
        DisplayReferral(new(null, editingReferralPatient.ChartNumber, DateTime.Today), -1);
        ReferralStatus.Text = "新規の下書きです。「Accessに保存」で登録します。";
    }
    private void CopyReferral(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralSaveUncertain || referralBaseline == null || editingReferralPatient?.SameIdentity(currentReferralPatient) != true) return;
        var copy = CaptureReferral().CopyFor(editingReferralPatient.ChartNumber, DateTime.Today);
        DisplayReferral(copy, -1);
        ReferralStatus.Text = "内容をコピーして今日付の下書きを作りました。元の紹介状は変更しません。";
    }

    private async void SaveReferral(object sender, RoutedEventArgs e) => await SaveOrOpenReferralAsync(false);
    private async void OpenReferralInAccess(object sender, RoutedEventArgs e) => await SaveOrOpenReferralAsync(true);

    private async Task SaveOrOpenReferralAsync(bool openInAccess)
    {
        if (referralBusy) { ReferralStatus.Text = "紹介状を処理中です。完了までお待ちください。"; return; }
        if (referralBaseline == null) { ReferralStatus.Text = "紹介状を選択するか、新規作成してください。"; return; }
        if (referralSaveUncertain) { ReferralStatus.Text = "前回の保存結果が未確認です。Access側の紹介状を確認してから再取得してください。下書きは保持しています。"; return; }
        if (editingReferralPatient?.SameIdentity(currentReferralPatient) != true)
        { ReferralStatus.Text = "保存できません。Accessの患者と編集中の紹介状の患者が一致していません。対象患者をAccessで表示してください。"; return; }
        if (!referralDirty && !openInAccess) { ReferralStatus.Text = "変更はありません。この紹介状は保存済みです。"; return; }
        var draft = CaptureReferral();
        if (draft.Date == null || !DateTime.TryParse(ReferralDate.Text, out var enteredDate) || enteredDate.Date != draft.Date.Value.Date)
        { ReferralStatus.Text = "正しい日付を入力してください。"; return; }
        bool saving = referralDirty;
        var patient = editingReferralPatient;
        referralBusy = true; UpdateReferralControls(); ReferralStatus.Text = saving ? "Accessに保存中…" : "Accessの紹介状を開いています…";
        try
        {
            if (chartSession == null || chartSession.MonitorTimedOut) throw new InvalidOperationException("カルテ情報の「再取得」でAccessに接続してください。");
            if (saving)
            {
                var saved = await chartSession.SaveReferralAsync(patient, referralBaseline, draft);
                referralLetters = referralLetters.Where(l => l.Number != saved.Number).Append(saved).OrderByDescending(l => l.Number).ToArray();
                DisplayReferral(saved, Array.FindIndex(referralLetters.ToArray(), l => l.Number == saved.Number));
                ReferralStatus.Text = $"紹介番号 {saved.Number} をAccessに保存しました。印刷はAccess側で行ってください。";
                saving = false;
            }
            if (openInAccess)
            {
                if (patient.SameIdentity(currentReferralPatient) != true)
                    throw new InvalidOperationException("患者が変わりました。紹介状の対象患者を確認してください。");
                await chartSession.OpenReferralAsync(patient, referralBaseline!);
                ReferralStatus.Text = $"紹介番号 {referralBaseline!.Number} をAccessで開きました。プレビュー・印刷はAccess側のボタンで行ってください。";
            }
        }
        catch (Exception ex)
        {
            referralSaveUncertain = saving && ex is not InvalidOperationException;
            ReferralStatus.Text = ex is InvalidOperationException ? ex.Message : !saving ?
                "Accessの紹介状を開けませんでした。保存済みの内容は保持しています。Access側の表示を確認してください。" :
                "保存完了を確認できません。Access側の紹介状を確認してください。下書きは保持しています。";
            if (ex is not InvalidOperationException) ReferralStatus.Text += $"\n詳細：{ex.Message}（0x{ex.HResult:X8}）";
        }
        finally { FinishReferralOperation(); }
    }

    private void InsertReferralTemplate(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralBaseline == null || ReferralTemplate.SelectedItem is not string text) return;
        int position = ((sender as Button)?.Tag as string) switch
        {
            "Start" => 0,
            "End" => ReferralTests.Text.Length,
            _ => ReferralTests.CaretIndex
        };
        ReferralTests.Select(position, 0);
        ReferralTests.SelectedText = text;
        ReferralTests.CaretIndex = position + text.Length;
        ReferralTests.Focus();
    }

    private void ReferralMedicationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ready) UpdateReferralControls();
    }

    private void AddReferralMedication(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralBaseline == null || editingReferralPatient?.SameIdentity(currentReferralPatient) != true ||
            ReferralMedication.SelectedItem is not ReferralPrescription prescription) return;
        var existing = ReferralTests.Text.ReplaceLineEndings("\r\n");
        var prefix = existing.Length == 0 || existing.EndsWith("\r\n\r\n") ? "" : existing.EndsWith("\r\n") ? "\r\n" : "\r\n\r\n";
        ReferralTests.Select(ReferralTests.Text.Length, 0);
        ReferralTests.SelectedText = prefix + prescription.Text + "\r\n";
        ReferralTests.CaretIndex = ReferralTests.Text.Length;
        ReferralTests.Focus();
        ReferralTests.ScrollToEnd();
        ReferralStatus.Text = $"{prescription.DateLabel}の処方を検査欄の文末に追加しました。";
    }

    private void AddReferralAttention(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralBaseline == null || editingReferralPatient?.SameIdentity(currentReferralPatient) != true ||
            string.IsNullOrWhiteSpace(referralAttention)) return;
        ReferralRemarks.Text += (ReferralRemarks.Text.Length == 0 || ReferralRemarks.Text.EndsWith("\n") ? "" : "\r\n") + referralAttention;
        ReferralRemarks.CaretIndex = ReferralRemarks.Text.Length;
        ReferralRemarks.Focus();
        ReferralStatus.Text = "注意リストを備考欄の末尾に追加しました。";
    }

    private void FinishReferralOperation()
    {
        referralBusy = false; UpdateReferralControls();
        if (closeRequested) { closeRequested = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
