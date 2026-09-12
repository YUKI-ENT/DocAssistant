using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PDFWriter;

public partial class MainWindow
{
    private readonly LlmClient llmClient = new();
    private CancellationTokenSource? llmCancellation;
    private long llmPatientVersion;
    private string llmPatient = "";
    private sealed record PayloadOption(string Name, CheckBox Enabled);
    private readonly List<PayloadOption> llmOptions = [];
    private bool changingLlmPeriod;

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
        EnableChartDrag(LlmResult);
        foreach (var name in new[] { "所見", "投薬", "処置" })
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
        finally { llmCancellation = null; LlmInputs.IsEnabled = LlmGenerate.IsEnabled = true; LlmCancel.IsEnabled = false; }
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
        if (ranges.Length == 0) throw new InvalidOperationException("所見・投薬・処置から送信対象を選択してください。");
        if (chartSession == null || chartReading || chartSession.MonitorTimedOut)
            throw new InvalidOperationException("カルテ取得が完了してから実行してください。必要に応じて再取得してください。");
        var version = llmPatientVersion;
        chartReading = true; chartTimer.Stop(); ChartRefreshButton.IsEnabled = false;
        AccessPatientDisplay display;
        try
        {
            display = await chartSession.PollPatientAsync(true, ranges.Any(r => r.Name == "投薬"), ranges.Any(r => r.Name == "処置"), all);
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
                    var current = range.Name == "投薬" ? display.TodayMedication : display.TodayProcedures;
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
                var history = range.Name == "投薬" ? display.Medication : display.Procedures;
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
        var endpoint = ReadLlmEndpoint();
        var model = (LlmModels.SelectedItem as string ?? ""); var prompt = LlmPromptText.Text;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("モデルとプロンプトを指定してください。");
        PersistLlm(); SetClinicalText(LlmResult, "");
        LlmStatus.Text = "送信対象を取得中…";
        var payload = await PrepareLlmPayload(token);
        var version = llmPatientVersion;
        LlmStatus.Text = $"{endpoint.Host}:{endpoint.Port} / {model} で生成中…";
        var result = await llmClient.GenerateAsync(endpoint, LlmKey.Password, model, prompt, payload, token);
        settings.Llm.Model = model;
        settings.Save();
        token.ThrowIfCancellationRequested();
        if (version != llmPatientVersion || chartClosed) return;
        SetClinicalText(LlmResult, result); LlmStatus.Text = "生成完了。結果をコピー、または選択してPDFへドラッグできます。";
    });
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
        llmPatient = display.Text; llmPatientVersion++; llmCancellation?.Cancel();
        if (LlmPeriod.SelectedItem as string == "全期間")
            LlmPeriodHint.Text = "初診日を生成時に取得します（所見はAccessの取得範囲内）。";
        SetClinicalText(LlmResult, "");
        LlmStatus.Text = "患者情報が変わったため、前の結果をクリアしました。";
    }
}
