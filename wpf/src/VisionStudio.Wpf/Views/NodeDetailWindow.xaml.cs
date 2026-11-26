using System.Windows;
using VisionStudio.Wpf.ViewModels;

namespace VisionStudio.Wpf.Views;

/// <summary>
/// 节点详情弹窗：双击画布上的极简节点时弹出，集中编辑参数并查看端口输出。
/// </summary>
public partial class NodeDetailWindow : Window
{
    public NodeDetailWindow(NodeDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is NodeDetailViewModel vm && vm.TryApplyChanges())
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        NodePreviewViewer.Dispose();
        base.OnClosed(e);
    }
}
