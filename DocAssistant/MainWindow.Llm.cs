using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DocAssistant;

public partial class MainWindow
{
    private readonly LlmClient llmClient = new();
    private CancellationTokenSource? llmCancellation;
    private long llmPatientVersion;
    private string llmPatient = "";
    private sealed record PayloadOption(string Name, CheckBox Enabled);
    private readonly List<PayloadOption> llmOptions = [];
    private bool changingLlmPeriod;
    private bool llmRequestUsesPatient, llmResultUsesPatient, llmMessageUsesPatient;

    internal static DateTime? LlmPeriodStart(string period, DateTime today) => period switch
    {
        "5年" => today.Date.AddYears(-5), "3年" => today.Date.AddYears(-3),
        "1年" => today.Date.AddYears(-1), "6か月" => today.Date.AddMonths(-6),
        "当日のみ" => today.Date, _ => null
    };
    private void LlmPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmStartDate == null || LlmPeriodHint == null) return;
        string period = LlmPeriod.SelectedItem as string ?? "日付指定";
        changingLlmPeriod = true;
        try
        {
            LlmStartDate.IsEnabled = period != "全期間";
            if (period == "全期間") LlmStartDate.SelectedDate = null;
            else if (LlmPeriodStart(period, DateTime.Today) is DateTime start) LlmStartDate.SelectedDate = start;
            LlmPeriodHint.Text = period == "全期間" ? "初診日を生成時に取得します（所見はAccessの取得範囲内）。" : "開始日から今日まで（両端を含む）";
        }
        finally { changingLlmPeriod = false; }
    }
    private void LlmStartDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingLlmPeriod && LlmPeriod != null) LlmPeriod.SelectedItem = "日付指定";
    }

    private void InitializeLlm()
    {
        settings.Llm ??= new();
        settings.Llm.Prompts ??= [];
        LlmAddress.Text = settings.Llm.Address;
        LlmPort.Text = settings.Llm.Port.ToString(CultureInfo.InvariantCulture);
        LlmModels.ItemsSource = string.IsNullOrWhiteSpace(settings.Llm.Model) ? Array.Empty<string>() : new[] { settings.Llm.Model };
        LlmModels.SelectedItem = settings.Llm.Model;
        LlmStartDate.SelectedDate = DateTime.Today.AddMonths(-3);
        LlmPeriod.ItemsSource = new[] { "全期間", "5年", "3年", "1年", "6か月", "当日のみ", "日付指定" };
        LlmPeriod.SelectedItem = "日付指定";
        RefreshLlmPrompts(settings.Llm.PromptIndex);
        if (string.IsNullOrEmpty(LlmMessage.Text)) LlmMessage.Text = LlmPromptText.Text;
        LlmAttachmentChanged(this, new RoutedEventArgs());
        EnableChartDrag(LlmResult);
        foreach (var name in new[] { "所見", "投薬", "処置", "検査", "注射" })
        {
            var check = new CheckBox { Content = name, IsChecked = true, Margin = new(0, 7, 22, 4) };
            LlmPayloadOptions.Children.Add(check);
            llmOptions.Add(new(name, check));
        }
    }
    private void RefreshLlmPrompts(int index)
    {
        LlmPrompts.ItemsSource = null;
        LlmPrompts.ItemsSource = settings.Llm.Prompts;
        LlmPrompts.SelectedIndex = settings.Llm.Prompts.Count == 0 ? -1 : Math.Clamp(index, 0, settings.Llm.Prompts.Count - 1);
    }
    private void LlmPromptChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmPromptText == null) return;
        var prompt = LlmPrompts.SelectedItem as LlmPrompt;
        LlmPromptName.Text = prompt?.Name ?? "";
        LlmPromptText.Text = prompt?.Text ?? "";
    }
    private Uri ReadLlmEndpoint()
    {
        if (!int.TryParse(LlmPort.Text, out int port)) throw new InvalidOperationException("ポートには数値を指定してください。");
        return LlmClient.BaseUri(LlmAddress.Text.Trim(), port);
    }
    private void PersistLlm()
    {
        _ = ReadLlmEndpoint();
        settings.Llm.Address = LlmAddress.Text.Trim();
        settings.Llm.Port = int.Parse(LlmPort.Text);
        settings.Llm.PromptIndex = LlmPrompts.SelectedIndex;
        settings.Save();
    }
    private void SaveLlmSettings(object sender, RoutedEventArgs e)
    {
        try { PersistLlm(); LlmStatus.Text = "接続設定を保存しました。"; }
        catch (Exception ex) { LlmStatus.Text = ex.Message; }
    }
    private void SaveLlmPrompt(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LlmPromptName.Text) || string.IsNullOrWhiteSpace(LlmPromptText.Text))
                throw new InvalidOperationException("プロンプト名と本文を入力してください。");
            var prompt = LlmPrompts.SelectedItem as LlmPrompt;
            if (prompt == null) { prompt = new(); settings.Llm.Prompts.Add(prompt); }
            prompt.Name = LlmPromptName.Text.Trim(); prompt.Text = LlmPromptText.Text;
            int index = settings.Llm.Prompts.IndexOf(prompt);
            settings.Llm.PromptIndex = index; settings.Save(); RefreshLlmPrompts(index);
            LlmStatus.Text = "プロンプトを保存しました。";
        }
        catch (Exception ex) { LlmStatus.Text = ex.Message; }
    }
    private void AddLlmPrompt(object sender, RoutedEventArgs e)
    {
        LlmPrompts.SelectedIndex = -1; LlmPromptName.Text = "新しいプロンプト"; LlmPromptText.Clear();
    }
    private void DeleteLlmPrompt(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LlmPrompts.SelectedItem is not LlmPrompt prompt) return;
            settings.Llm.Prompts.Remove(prompt); settings.Llm.PromptIndex = 0;
            settings.Save(); RefreshLlmPrompts(0);
        }
        catch (Exception ex) { LlmStatus.Text = ex.Message; }
    }
    private async Task RunLlm(Func<CancellationToken, Task> action)
    {
        if (llmCancellation != null) return;
        using var cancellation = new CancellationTokenSource();
        llmCancellation = cancellation;
        LlmInputs.IsEnabled = LlmGenerate.IsEnabled = false; LlmCancel.IsEnabled = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { LlmStatus.Text = "中止しました（または応答がタイムアウトしました）。"; }
        catch (Exception ex) { LlmStatus.Text = ex is JsonException or KeyNotFoundException or InvalidOperationException ? ex.Message : "処理に失敗しました。接続先とAccessの状態を確認してください。"; }
        finally { llmCancellation = null; llmRequestUsesPatient = false; LlmInputs.IsEnabled = LlmGenerate.IsEnabled = true; LlmCancel.IsEnabled = false; }
    }
    private async void LoadLlmModels(object sender, RoutedEventArgs e) => await RunLlm(async token =>
    {
        var endpoint = ReadLlmEndpoint();
        LlmStatus.Text = "モデル一覧を取得中…";
        var models = await llmClient.ModelsAsync(endpoint, LlmKey.Password, token);
        var selected = LlmModels.SelectedItem as string;
        LlmModels.ItemsSource = models;
        LlmModels.SelectedItem = models.Contains(settings.Llm.Model) ? settings.Llm.Model : models.Contains(selected) ? selected : models.FirstOrDefault();
        PersistLlm(); LlmStatus.Text = $"{models.Length}件のモデルを取得しました。";
    });
    private async Task WaitForChartUpdateAsync(CancellationToken token)
    {
        var version = llmPatientVersion;
        while (chartReading && chartUpdateCompletion is { } completion)
        {
            LlmStatus.Text = "カルテの更新完了を待っています…";
            await completion.Task.WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
        if (chartClosed) throw new OperationCanceledException(token);
        if (version != llmPatientVersion)
            throw new InvalidOperationException("患者情報が変わりました。表示を確認してもう一度実行してください。");
    }
    private async Task<string> PrepareLlmPayload(CancellationToken token)
    {
        var today = DateTime.Today;
        string period = LlmPeriod.SelectedItem as string ?? "日付指定";
        bool all = period == "全期間";
        var selectedStart = all ? DateTime.MinValue : LlmPeriodStart(period, today) ?? LlmStartDate.SelectedDate;
        if (selectedStart is not DateTime start || start.Date > today)
            throw new InvalidOperationException("開始日には今日以前の日付を指定してください。");
        var ranges = llmOptions.Where(o => o.Enabled.IsChecked == true)
            .Select(o => (o.Name, Start: start.Date, End: today)).ToArray();
        if (ranges.Length == 0) throw new InvalidOperationException("所見・投薬・処置・検査・注射から送信対象を選択してください。");
        await WaitForChartUpdateAsync(token);
        if (chartSession == null || chartSession.MonitorTimedOut)
            throw new InvalidOperationException("カルテを取得できません。Accessの状態を確認して「再取得」を押してください。");
        var version = llmPatientVersion;
        LlmStatus.Text = "送信対象を取得中…";
        chartReading = true; chartTimer.Stop(); ChartRefreshButton.IsEnabled = false;
        AccessPatientDisplay display;
        try
        {
            display = await chartSession.PollPatientAsync(true, ranges.Any(r => r.Name == "投薬"), ranges.Any(r => r.Name == "処置"), all, ranges.Any(r => r.Name == "検査"), ranges.Any(r => r.Name == "注射"));
            ShowChart(display);
        }
        finally
        {
            chartReading = false;
            if (!chartClosed) { ChartRefreshButton.IsEnabled = true; if (!chartSession.MonitorTimedOut) chartTimer.Start(); }
        }
        token.ThrowIfCancellationRequested();
        if (version != llmPatientVersion || string.IsNullOrWhiteSpace(display.Text) || !display.Detected)
            throw new InvalidOperationException("患者情報が変わりました。表示を確認してもう一度実行してください。");
        if (all)
        {
            if (display.FirstVisit is not DateTime first || first > today)
                throw new InvalidOperationException("初診日を確認できません。日付指定を使用してください。");
            ranges = ranges.Select(r => (r.Name, Start: first, r.End)).ToArray();
            LlmPeriodHint.Text = $"初診日 {first:yyyy/MM/dd} 〜 今日（所見はAccessの取得範囲内）";
        }
        return BuildLlmPayload(display, ranges);
    }
    internal static string BuildLlmPayload(AccessPatientDisplay display, (string Name, DateTime Start, DateTime End)[] ranges)
    {
        var sections = new List<object>();
        var todayVisits = FindTodayLlmVisits(display, DateTime.Today);
        foreach (var range in ranges)
        {
            bool InRange(DateTime? date) => date.HasValue && date.Value.Date >= range.Start && date.Value.Date <= range.End;
            var supplements = new Dictionary<string, string>();
            if (InRange(DateTime.Today))
            {
                if (range.Name == "所見")
                {
                    foreach (var visit in (display.DraftClinical?.Notes ?? []).Where(n => todayVisits.Contains(n.Visit) && !string.IsNullOrWhiteSpace(n.Text)).GroupBy(n => n.Visit))
                        supplements[visit.Key] = AccessSession.FormatNotes(visit, false, false);
                    if (todayVisits.Count > 0 && (display.DraftClinical == null || display.DraftStatus.Contains("取得できません") || display.DraftClinical.Status.Contains("取得できません")))
                        throw new InvalidOperationException("当日所見のデータを取得できません。再取得してください。");
                }
                else
                {
                    var current = range.Name switch { "投薬" => display.TodayMedication, "処置" => display.TodayProcedures, "検査" => display.TodayTests, "注射" => display.TodayInjections, _ => null };
                    if (current != null && todayVisits.Contains(current.Visit) && !string.IsNullOrWhiteSpace(current.Text))
                        supplements[current.Visit] = current.Text;
                    if (todayVisits.Count > 0 && (current == null || current.Status.Contains("取得できません")))
                        throw new InvalidOperationException("当日の" + range.Name + "を取得できません。再取得してください。");
                }
            }
            string text;
            if (range.Name == "所見")
            {
                if (display.Clinical == null || display.NotesStatus.Contains("取得できません"))
                    throw new InvalidOperationException("所見を取得できません。Accessで再取得してください。");
                text = AccessSession.FormatNotes((display.Clinical.Notes ?? []).Where(n => InRange(n.Date) && !supplements.ContainsKey(n.Visit)), true, false);
            }
            else
            {
                var history = range.Name switch { "投薬" => display.Medication, "処置" => display.Procedures, "検査" => display.Tests, "注射" => display.Injections, _ => null };
                if (history == null) throw new InvalidOperationException(range.Name + "を取得できません。");
                text = string.Join("\n\n", history.Days.Where(d => DateTime.TryParseExact(d.Key, "yyyy/MM/dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) && InRange(date)).Select(d => d.Key + "\n" +
                        (supplements.Count == 0 || d.Rows == null ? d.Text : string.Join("\n", d.Rows.Where(r => !supplements.ContainsKey(r.Visit))
                            .Select(r => $"受診 {r.Visit} · {r.Name}　数量：{r.Quantity}")))));
            }
            sections.Add(new { category = range.Name, start = range.Start.ToString("yyyy-MM-dd"), end = range.End.ToString("yyyy-MM-dd"),
                source = range.Name == "所見" ? "受診カルテサブ（表示範囲・保存済み）: " + display.NotesStatus : "保存済みの" + range.Name + "テーブル", text });
            foreach (var supplement in supplements)
                sections.Add(new { category = range.Name, date = DateTime.Today.ToString("yyyy-MM-dd"), visit = supplement.Key,
                    source = "当日表示を優先（日付・受診コードが一致する履歴を置換）", text = supplement.Value });
        }
        return JsonSerializer.Serialize(new { sections }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    internal static HashSet<string> FindTodayLlmVisits(AccessPatientDisplay display, DateTime today)
    {
        return (display.Clinical?.Notes ?? []).Concat(display.Clinical?.Orders ?? [])
            .Where(n => n.Date?.Date == today.Date && !string.IsNullOrWhiteSpace(n.Visit))
            .Select(n => n.Visit).ToHashSet(StringComparer.Ordinal);
    }
    private async void GenerateLlm(object sender, RoutedEventArgs e) => await RunLlm(async token =>
    {
        await SendLlmMessageAsync(token, llmClient);
        settings.Llm.Model = LlmModels.SelectedItem as string ?? "";
        PersistLlm();
    });

    private async Task SendLlmMessageAsync(CancellationToken token, LlmClient client)
    {
        var endpoint = ReadLlmEndpoint();
        var model = LlmModels.SelectedItem as string ?? ""; var prompt = LlmMessage.Text;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("モデルと今回の送信文を指定してください。");
        bool attach = LlmAttachChart.IsChecked == true;
        bool usesPatient = attach || llmMessageUsesPatient;
        llmRequestUsesPatient = usesPatient;
        var version = llmPatientVersion;
        // With attachment off, do not read/wait for Access, validate dates, or add a
        // hidden template, empty payload message, or previous conversation history.
        var payload = attach ? await PrepareLlmPayload(token) : null;
        token.ThrowIfCancellationRequested();
        LlmStatus.Text = $"{endpoint.Host}:{endpoint.Port} / {model} で生成中…";
        var result = await client.GenerateAsync(endpoint, LlmKey.Password, model, prompt, payload, token);
        token.ThrowIfCancellationRequested();
        if (usesPatient && version != llmPatientVersion) throw new OperationCanceledException(token);
        if (chartClosed) return;
        SetClinicalText(LlmResult, result); llmResultUsesPatient = usesPatient;
        LlmStatus.Text = "生成完了。結果を入力欄に取り込んで続けて指示できます。";
    }

    private void LlmAttachmentChanged(object sender, RoutedEventArgs e)
    {
        if (LlmPayloadSettings == null || LlmSendHint == null) return;
        bool attach = LlmAttachChart.IsChecked == true;
        LlmPayloadSettings.Visibility = attach ? Visibility.Visible : Visibility.Collapsed;
        LlmSendHint.Text = attach ? "送信文に加えて、選択したカルテ情報を送信します。"
            : "入力欄の文章だけを送信します。Accessへの接続は不要です。";
    }

    private void LlmMessageChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(LlmMessage.Text)) llmMessageUsesPatient = false;
    }

    private void UseLlmTemplate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LlmPromptText.Text)) { LlmStatus.Text = "定型プロンプトを選択・入力してください。"; return; }
        LlmMessage.Text = LlmPromptText.Text; llmMessageUsesPatient = false;
        LlmMessage.Focus(); LlmMessage.CaretIndex = LlmMessage.Text.Length;
        LlmStatus.Text = "定型文を入力欄に取り込みました。今回だけの変更は定型文には保存されません。";
    }

    private void UseLlmResult(object sender, RoutedEventArgs e)
    {
        var text = new TextRange(LlmResult.Document.ContentStart, LlmResult.Document.ContentEnd).Text.Trim();
        if (text.Length == 0) { LlmStatus.Text = "取り込む結果がありません。"; return; }
        LlmMessage.Text = "\r\n\r\n" + text; llmMessageUsesPatient = llmResultUsesPatient;
        LlmAttachChart.IsChecked = false;
        LlmMessage.Focus(); LlmMessage.CaretIndex = 0; LlmMessage.ScrollToHome();
        LlmStatus.Text = "結果を取り込み、カルテ添付をOFFにしました。先頭に「以下を英訳してください」などの指示を入力してください。";
    }

    private void ClearLlmMessage(object sender, RoutedEventArgs e)
    {
        LlmMessage.Clear(); llmMessageUsesPatient = false; LlmMessage.Focus();
    }
    private void CancelLlm(object sender, RoutedEventArgs e) => llmCancellation?.Cancel();
    private void CopyLlmResult(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new TextRange(LlmResult.Document.ContentStart, LlmResult.Document.ContentEnd).Text.Trim();
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (Exception) { LlmStatus.Text = "クリップボードにコピーできませんでした。"; }
    }
    private void TrackLlmPatient(AccessPatientDisplay display)
    {
        if (llmPatient == display.Text) return;
        llmPatient = display.Text; llmPatientVersion++;
        bool cleared = llmRequestUsesPatient || llmResultUsesPatient || llmMessageUsesPatient;
        if (llmRequestUsesPatient) llmCancellation?.Cancel();
        if (LlmPeriod.SelectedItem as string == "全期間")
            LlmPeriodHint.Text = "初診日を生成時に取得します（所見はAccessの取得範囲内）。";
        if (llmResultUsesPatient) { SetClinicalText(LlmResult, ""); llmResultUsesPatient = false; }
        if (llmMessageUsesPatient) { LlmMessage.Clear(); llmMessageUsesPatient = false; }
        if (cleared) LlmStatus.Text = "患者情報が変わったため、その患者の結果・取り込んだ送信文をクリアしました。";
    }
}
