using System.Windows;
using System.Windows.Threading;

namespace PDFWriter;

public partial class MainWindow
{
    private AccessSession? chartSession;
    private readonly DispatcherTimer chartTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool chartReading, chartClosed;

    private void StartChartMonitor()
    {
        if (chartClosed) return;
        chartSession = new AccessSession();
        chartTimer.Tick += ChartTick;
        _ = UpdateChartAsync();
    }
    private async void ChartTick(object? sender, EventArgs e) => await UpdateChartAsync();

    private async Task UpdateChartAsync(bool forceNotes = false)
    {
        if (chartClosed || chartReading || chartSession == null) return;
        chartTimer.Stop();
        chartReading = true;
        ChartRefreshButton.IsEnabled = false;
        try
        {
            var result = await chartSession.PollPatientAsync(forceNotes);
            if (chartClosed) return;
            ShowChart(result);
            chartTimer.Interval = TimeSpan.FromMilliseconds(result.Detected ? 750 : 2000);
        }
        catch (Exception)
        {
            if (chartClosed) return;
            // Do not retain a previous patient's values when the source cannot be verified.
            ShowChart(new(false, chartSession.MonitorTimedOut
                ? "Accessが応答しないため監視を停止しました。Access側を確認して「再取得」を押してください。"
                : "患者情報を確認できません。Accessで患者マスターを表示してください。自動で再確認します。"));
            chartTimer.Interval = TimeSpan.FromSeconds(3);
        }
        finally
        {
            chartReading = false;
            if (!chartClosed)
            {
                ChartRefreshButton.IsEnabled = true;
                if (!chartSession.MonitorTimedOut) chartTimer.Start();
            }
        }
    }
    private void ShowChart(AccessPatientDisplay display)
    {
        ChartStatus.Text = display.Status;
        ShowMedicationHistory(BuildVisitHistory(display));
        ChartDraftStatus.Text = display.DraftStatus;
        SetClinicalText(ChartDraft, display.DraftText);
        ChartNotesStatus.Text = display.NotesStatus;
        ChartNotesStatus.Visibility = display.NotesStatus.Contains("取得できません") || display.NotesStatus.Contains("上限")
            ? Visibility.Visible : Visibility.Collapsed;
        // Preserve selection while polling the same values so the user can copy text.
        if (shownPatientText != display.Text)
        {
            shownPatientText = display.Text;
            ShowPatientFields(display.Text);
        }
        ChartPlaceholder.Visibility = string.IsNullOrEmpty(display.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private string shownPatientText = "";

    private void ShowPatientFields(string text)
    {
        PatientFieldsGrid.Children.Clear();
        PatientFieldsGrid.RowDefinitions.Clear();
        PatientFieldsGrid.ColumnDefinitions.Clear();
        PatientFieldsGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        PatientFieldsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var fields = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('：', 2)).Where(parts => parts.Length == 2).ToArray();
        string Read(string name) => fields.FirstOrDefault(parts => parts[0] == name)?[1] ?? "";
        int row = 0;
        foreach (var parts in fields.OrderBy(parts => parts[0] == "カルテ番号" ? 0 : 1))
        {
            if (parts[0] is "フリガナ" or "性別") continue;
            string value = parts[1];
            if (parts[0] == "氏名")
            {
                value = string.IsNullOrWhiteSpace(value) ? "未登録" : value;
                if (!string.IsNullOrWhiteSpace(Read("フリガナ"))) value += $"（{Read("フリガナ")}）";
                if (!string.IsNullOrWhiteSpace(Read("性別"))) value += $" {Read("性別")}";
            }
            if (parts[0] == "カルテ番号" && long.TryParse(value, out var number)) value = $"{number / 10}-{number % 10}";
            PatientFieldsGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = new System.Windows.Controls.TextBlock
            {
                Text = parts[0] + "：", FontSize = 12, Foreground = System.Windows.Media.Brushes.SlateGray,
                Margin = new Thickness(0, 5, 10, 5), VerticalAlignment = VerticalAlignment.Top
            };
            var box = ChartValue(string.IsNullOrWhiteSpace(value) ? "未登録" : value);
            EnableChartDrag(box);
            System.Windows.Controls.Grid.SetRow(label, row);
            System.Windows.Controls.Grid.SetRow(box, row);
            System.Windows.Controls.Grid.SetColumn(box, 1);
            PatientFieldsGrid.Children.Add(label);
            PatientFieldsGrid.Children.Add(box);
            row++;
        }
    }

    internal static MedicationHistory BuildVisitHistory(AccessPatientDisplay display)
    {
        var notes = display.Clinical?.Notes ?? [];
        var orders = display.Clinical?.Orders ?? [];
        static string DateKey(DateTime? date) => date?.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) ?? "日付不明";
        var keys = notes.Concat(orders).Select(n => DateKey(n.Date)).Distinct()
            .OrderBy(k => k == "日付不明").ThenByDescending(k => k, StringComparer.Ordinal);
        var days = keys.Select(key => new MedicationDay(key, key,
            "",
            AccessSession.FormatNotes(notes.Where(n => DateKey(n.Date) == key), false, false),
            AccessSession.FormatNotes(orders.Where(n => DateKey(n.Date) == key), false, false))).ToArray();
        string patient = display.Text;
        return new(patient, "", days);
    }

    private async void RefreshChart(object sender, RoutedEventArgs e)
    {
        if (chartReading || chartClosed) return;
        if (chartSession == null || chartSession.MonitorTimedOut)
        {
            chartSession?.Dispose();
            chartSession = new AccessSession();
        }
        await UpdateChartAsync(forceNotes: true);
    }
    private void StopChartMonitor()
    {
        chartClosed = true;
        chartTimer.Stop();
        chartTimer.Tick -= ChartTick;
        chartSession?.Dispose();
    }
}

