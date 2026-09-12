using System.Windows;
using System.Windows.Controls;

namespace DocAssistant;

public partial class MainWindow
{
    private void WorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || !ReferenceEquals(e.OriginalSource, WorkspaceTabs)) return;
        FinishEditing();
        CancelGesture();
        Status.Text = WorkspaceTabs.SelectedIndex == 0
            ? "PDF編集 · カルテ情報をドラッグして挿入できます。"
            : "文書作成機能は準備中です。右側のカルテ情報は引き続き参照できます。";
    }
}
