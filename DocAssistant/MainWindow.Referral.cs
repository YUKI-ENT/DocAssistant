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

    private void TrackReferralPatient(AccessPatientDisplay display)
    {
        string Read(string name) => display.Text.Split('\n').Select(l => l.TrimEnd('\r').Split('：', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0] == name)?[1] ?? "";
        var next = display.Detected && !string.IsNullOrWhiteSpace(display.DatabasePath) &&
            long.TryParse(Read("カルテ番号"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0
            ? new ReferralPatient(display.DatabasePath, id, Read("氏名")) : null;
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
        ReferralFields.IsEnabled = !referralBusy && referralBaseline != null;
        ReferralOlder.IsEnabled = !referralBusy && referralLetters.Count > 0 && referralIndex < referralLetters.Count - 1;
        ReferralNewer.IsEnabled = !referralBusy && referralIndex > 0;
        ReferralReload.IsEnabled = !referralBusy && currentReferralPatient != null;
        ReferralNew.IsEnabled = !referralBusy && !referralSaveUncertain && matches;
        ReferralCopy.IsEnabled = !referralBusy && !referralSaveUncertain && matches && referralBaseline != null;
        ReferralSave.IsEnabled = !referralBusy && !referralSaveUncertain && matches && referralDirty && referralBaseline != null;
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
            referralLetters = history.Letters; editingReferralPatient = patient; referralLoaded = true; referralSaveUncertain = false;
            changingReferral = true;
            try
            {
                ReferralDestination1.ItemsSource = history.Choices?.Destinations1;
                ReferralDestination2.ItemsSource = history.Choices?.Destinations2;
                ReferralDoctor.ItemsSource = history.Choices?.Doctors;
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

    private async void SaveReferral(object sender, RoutedEventArgs e)
    {
        if (referralBusy || referralSaveUncertain || !referralDirty || referralBaseline == null || editingReferralPatient?.SameIdentity(currentReferralPatient) != true) return;
        var draft = CaptureReferral();
        if (draft.Date == null || !DateTime.TryParse(ReferralDate.Text, out var enteredDate) || enteredDate.Date != draft.Date.Value.Date)
        { ReferralStatus.Text = "正しい日付を入力してください。"; return; }
        referralBusy = true; UpdateReferralControls(); ReferralStatus.Text = "Accessに保存中…";
        try
        {
            if (chartSession == null || chartSession.MonitorTimedOut) throw new InvalidOperationException("カルテ情報の「再取得」でAccessに接続してください。");
            var saved = await chartSession.SaveReferralAsync(editingReferralPatient, referralBaseline, draft);
            referralLetters = referralLetters.Where(l => l.Number != saved.Number).Append(saved).OrderByDescending(l => l.Number).ToArray();
            DisplayReferral(saved, Array.FindIndex(referralLetters.ToArray(), l => l.Number == saved.Number));
            ReferralStatus.Text = $"紹介番号 {saved.Number} をAccessに保存しました。印刷はAccess側で行ってください。";
        }
        catch (Exception ex)
        {
            referralSaveUncertain = ex is not InvalidOperationException;
            ReferralStatus.Text = ex is InvalidOperationException ? ex.Message :
                "保存完了を確認できません。連打せず、Access側の紹介状を確認してください。下書きは保持しています。";
        }
        finally { FinishReferralOperation(); }
    }

    private void FinishReferralOperation()
    {
        referralBusy = false; UpdateReferralControls();
        if (closeRequested) { closeRequested = false; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
