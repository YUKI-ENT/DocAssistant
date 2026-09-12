using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PDFWriter;

public partial class MainWindow
{
    private sealed class LlmTestHandler : HttpMessageHandler
    {
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Response = "{\"data\":[{\"id\":\"local-model\"}]}";
        internal Uri? Uri;
        internal string? Body, Authorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
    private async Task TestLlmAsync()
    {
        var folder = Path.GetFullPath("tmp/llm-test"); Directory.CreateDirectory(folder);
        try
        {
            var endpoint = LlmClient.BaseUri("localhost", 11434);
            Check(endpoint.ToString() == "http://localhost:11434/v1/", "Ollama URL");
            Check(LlmClient.BaseUri("http://localhost/v1/", 1234).ToString() == "http://localhost:1234/v1/", "LM Studio URL");
            Check(LlmClient.BaseUri("https://example.org/proxy", 443).AbsolutePath == "/proxy/v1/", "Proxy prefix");
            foreach (var address in new[] { "ftp://localhost", "http://user:pass@localhost", "http://localhost?secret=x" })
            {
                bool rejected = false; try { LlmClient.BaseUri(address, 1234); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Reject invalid endpoint");
            }
            var handler = new LlmTestHandler(); using var client = new LlmClient(handler);
            Check((await client.ModelsAsync(endpoint, "", default)).Single() == "local-model", "Parse model list");
            Check(handler.Uri?.AbsolutePath == "/v1/models" && handler.Body == null, "GET model endpoint");
            handler.Response = "{\"choices\":[{\"message\":{\"content\":\"生成テスト\"}}]}";
            Check(await client.GenerateAsync(endpoint, "test-key", "local-model", "要約", "所見", default) == "生成テスト", "Parse generated text");
            using var body = JsonDocument.Parse(handler.Body!);
            Check(handler.Uri?.AbsolutePath == "/v1/chat/completions" && handler.Authorization == "Bearer test-key", "Completion endpoint and authentication");
            Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "所見" &&
                body.RootElement.GetProperty("model").GetString() == "local-model" && !body.RootElement.GetProperty("stream").GetBoolean(), "OpenAI request body");
            handler.Status = HttpStatusCode.Unauthorized;
            bool failed = false; try { await client.ModelsAsync(endpoint, "", default); } catch (InvalidOperationException ex) { failed = ex.Message.Contains("401"); }
            Check(failed, "HTTP failure reported");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            failed = false; try { await client.ModelsAsync(endpoint, "", cancel.Token); } catch (OperationCanceledException) { failed = true; }
            Check(failed, "Cancellation honored");
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Check(restored.Llm.Prompts.Count == settings.Llm.Prompts.Count && restored.Llm.Port == settings.Llm.Port, "LLM settings roundtrip");
            TrackLlmPatient(new(true, "", "患者A"));
            SetClinicalText(LlmResult, "患者Aの結果");
            using var pending = new CancellationTokenSource(); llmCancellation = pending;
            TrackLlmPatient(new(true, "", "患者B"));
            Check(pending.IsCancellationRequested && new System.Windows.Documents.TextRange(LlmResult.Document.ContentStart, LlmResult.Document.ContentEnd).Text.Trim() == "", "Patient change cancels request and clears payload");
            llmCancellation = null;
            Check(llmOptions.Count == 3 && LlmStartDate.SelectedDate <= DateTime.Today, "Shared start date");
            Check(LlmPeriodStart("1年", new DateTime(2024, 2, 29)) == new DateTime(2023, 2, 28), "Leap-year shortcut");
            Check(LlmPeriodStart("6か月", new DateTime(2026, 8, 31)) == new DateTime(2026, 2, 28), "Month-end shortcut");
            foreach (var period in new[] { "5年", "3年", "1年", "6か月", "当日のみ" })
            {
                LlmPeriod.SelectedItem = period;
                Check(LlmStartDate.SelectedDate == LlmPeriodStart(period, DateTime.Today), "Shortcut updates date: " + period);
            }
            LlmStartDate.SelectedDate = DateTime.Today.AddDays(-7);
            Check((string)LlmPeriod.SelectedItem == "日付指定", "Manual date switches to custom");
            LlmPeriod.SelectedItem = "全期間";
            Check(!LlmStartDate.IsEnabled && LlmStartDate.SelectedDate == null, "All-time defers to verified first visit");
            LlmPeriod.SelectedItem = "6か月";
            var day = new DateTime(2020, 9, 12);
            var display = new AccessPatientDisplay(true, "", "患者B", Clinical: new("取得済み", Notes:
                [new(day.AddDays(-1), "1", 10, 0, "範囲外"), new(day, "2", 10, 0, "境界日の所見"), new(null, "3", 10, 0, "日付不明")]),
                Medication: new("B", "", [new("2020/09/11", "", "薬剤A"), new("2020/09/12", "", "薬剤B")]),
                Procedures: new("B", "", [new("2020/09/12", "", "処置A")]));
            var payload = BuildLlmPayload(display, [("所見", day, day), ("投薬", day.AddDays(-1), day.AddDays(-1))]);
            Check(payload.Contains("境界日の所見") && payload.Contains("薬剤A") && !payload.Contains("範囲外") &&
                !payload.Contains("日付不明") && !payload.Contains("薬剤B") && !payload.Contains("処置A"), "Inclusive independent date filters and excluded categories");
            Check(BuildLlmPayload(display, [("処置", day, day)]).Contains("処置A"), "Separate procedure payload");
            var today = DateTime.Today;
            var current = display with
            {
                Clinical = new("取得済み", Notes: [new(today, "today", 10, 0, "●" + today.ToString("yy/MM/dd"))]),
                DraftClinical = new("取得済み", Notes: [new(null, "today", 10, 0, "当日の実所見")]),
                TodayMedication = new("当日の薬剤", Visit: "today"), TodayProcedures = new("当日の処置", Visit: "today"),
                Medication = new("B", "", [new(today.ToString("yyyy/MM/dd"), "", "重複する薬剤", Rows:
                    [new(today, "today", 10, 0, "重複する薬剤", "1"), new(today, "other", 10, 0, "別受診の薬剤", "2")])]),
                Procedures = new("B", "", [])
            };
            var currentPayload = BuildLlmPayload(current, [("所見", today, today), ("投薬", today, today), ("処置", today, today)]);
            Check(currentPayload.Contains("当日の実所見") && currentPayload.Contains("当日の薬剤") && currentPayload.Contains("当日の処置") &&
                currentPayload.Contains("別受診の薬剤") && !currentPayload.Contains("重複する薬剤"), "Pending visit supplements and visit-specific deduplication");
            Check(!BuildLlmPayload(current, [("所見", today.AddDays(-1), today.AddDays(-1))]).Contains("当日の実所見"), "No supplement outside range");
            Check(!BuildLlmPayload(current with { TodayMedication = new("別受診の入力", Visit: "unrelated") }, [("投薬", today, today)]).Contains("別受診の入力"), "Never supplement another visit");
            Check(FindTodayLlmVisits(current with { Clinical = new("", Notes: [new(today, "today", 10, 0, "●" + today.ToString("yy/MM/dd")), new(today, "today", 10, 1, "確定済み本文")]) }, today).Count == 1,
                "Today is selected regardless of finalized body");
            Check(FindTodayLlmVisits(current with { Clinical = new("", Notes: [new(today.AddDays(-1), "today", 10, 0, "●" + today.AddDays(-1).ToString("yy/MM/dd"))]) }, today).Count == 0, "Do not infer today's date for historical drafts");
            var finalized = current with { Clinical = new("", Notes: [new(today, "today", 10, 0, "履歴の長文\n2行目\n3行目\n4行目"), new(today, "other", 10, 0, "別受診の所見")]) };
            var replaced = BuildLlmPayload(finalized, [("所見", today, today)]);
            Check(replaced.Contains("当日の実所見") && replaced.Contains("別受診の所見") && !replaced.Contains("履歴の長文"), "Replace long finalized notes for matching visit only");
            var emptyCurrent = finalized with { DraftClinical = new("取得済み", Visits: ["today"]) };
            Check(BuildLlmPayload(emptyCurrent, [("所見", today, today)]).Contains("履歴の長文"), "Empty current notes preserve saved notes");
            Check(BuildLlmPayload(current with { TodayMedication = new("", "データなし", "today") }, [("投薬", today, today)]).Contains("重複する薬剤"), "Empty current medication preserves saved rows");
            foreach (var category in new[] { "所見", "投薬", "処置" })
            {
                bool blocked = false;
                try { BuildLlmPayload(current with { DraftClinical = null, TodayMedication = null, TodayProcedures = new("", "取得できません") }, [(category, today, today)]); }
                catch (InvalidOperationException) { blocked = true; }
                Check(blocked, "Missing current data blocks sending: " + category);
            }
            LlmModels.ItemsSource = new[] { "local-model" }; LlmModels.SelectedIndex = 0; LlmStatus.Text = "テスト用表示 · 外部送信なし";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            RenderVisual(this, Path.Combine(folder, "window.png"), 1740, 940);
            LlmConnectionExpander.IsExpanded = true;
            RenderVisual(this, Path.Combine(folder, "connection.png"), 1740, 940);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: endpoint, models, completion payload, authentication, HTTP errors, cancellation, settings, patient isolation, pane layout");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
