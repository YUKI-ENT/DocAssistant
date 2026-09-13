using System.Windows;
using System.Windows.Controls;

namespace DocAssistant;

public partial class MainWindow
{
    private bool llmPaneCollapsed, chartPaneCollapsed;
    private double expandedLlmWidth = 400, expandedChartWidth = 380;

    private void ToggleLlmPane(object sender, RoutedEventArgs e)
    {
        FinishEditing();
        if (!llmPaneCollapsed) expandedLlmWidth = LlmColumn.ActualWidth;
        llmPaneCollapsed = !llmPaneCollapsed;
        LlmPane.Visibility = llmPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedLlmPane.Visibility = llmPaneCollapsed ? Visibility.Visible : Visibility.Collapsed;
        LlmSplitter.Visibility = llmPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LlmSplitterColumn.Width = new GridLength(llmPaneCollapsed ? 0 : 6);
        LlmColumn.MinWidth = llmPaneCollapsed ? 40 : 320;
        LlmColumn.Width = new GridLength(llmPaneCollapsed ? 40 : Math.Clamp(expandedLlmWidth, 320,
            Math.Max(320, WorkspaceColumn.ActualWidth - 246)));
        UpdateWorkspaceMinimum();
        (llmPaneCollapsed ? ExpandLlmButton : CollapseLlmButton).Focus();
    }

    private void ToggleChartPane(object sender, RoutedEventArgs e)
    {
        FinishEditing();
        if (!chartPaneCollapsed) expandedChartWidth = ChartColumn.ActualWidth;
        chartPaneCollapsed = !chartPaneCollapsed;
        ChartPane.Visibility = chartPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedChartPane.Visibility = chartPaneCollapsed ? Visibility.Visible : Visibility.Collapsed;
        ChartSplitter.Visibility = chartPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ChartSplitterColumn.Width = new GridLength(chartPaneCollapsed ? 0 : 6);
        ChartColumn.MinWidth = chartPaneCollapsed ? 40 : 280;
        if (!chartPaneCollapsed && !llmPaneCollapsed)
        {
            // Make room when the AI pane was widened while the chart was folded.
            var total = WorkspaceColumn.ActualWidth + ChartColumn.ActualWidth;
            LlmColumn.Width = new GridLength(Math.Clamp(LlmColumn.ActualWidth, 320, Math.Max(320, total - (240 + 6 + 280 + 6))));
        }
        UpdateWorkspaceMinimum();
        ChartColumn.Width = new GridLength(chartPaneCollapsed ? 40 : Math.Clamp(expandedChartWidth, 280,
            Math.Max(280, WorkspaceColumn.ActualWidth + ChartColumn.ActualWidth - WorkspaceColumn.MinWidth - 6)));
        (chartPaneCollapsed ? ExpandChartButton : CollapseChartButton).Focus();
    }

    private void LlmPaneSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceMinimum();

    private void UpdateWorkspaceMinimum()
    {
        if (WorkspaceColumn == null || LlmColumn == null) return;
        WorkspaceColumn.MinWidth = 240 + (llmPaneCollapsed ? 40 : 6 + LlmColumn.Width.Value);
    }

    private async void WorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !ReferenceEquals(e.OriginalSource, WorkspaceTabs)) return;
        FinishEditing();
        CancelGesture();
        Status.Text = WorkspaceTabs.SelectedIndex == 0
            ? "PDF編集 · カルテ情報をドラッグして挿入できます。"
            : WorkspaceTabs.SelectedItem == ReferralTab ? "紹介状作成 · 保存はAccessの紹介状テーブル、印刷はAccess側で行います。"
            : "文書作成機能は準備中です。右側のカルテ情報は引き続き参照できます。";
        if (WorkspaceTabs.SelectedItem == ReferralTab && !referralDirty &&
            (!referralLoaded || editingReferralPatient?.SameIdentity(currentReferralPatient) != true))
            await LoadReferralHistoryAsync(false);
    }
}
