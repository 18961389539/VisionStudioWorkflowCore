using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionStudio.Wpf.ViewModels;

namespace VisionStudio.Wpf.Views;

public partial class TraceDiagnosticsView : UserControl
{
    public TraceDiagnosticsView()
    {
        InitializeComponent();
    }

    private void OnTraceRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TraceDiagnosticsViewModel vm && vm.ViewTraceCommand.CanExecute(null))
        {
            vm.ViewTraceCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCopyTraceDetailsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TraceDiagnosticsViewModel vm && !string.IsNullOrWhiteSpace(vm.TraceDetails))
        {
            Clipboard.SetText(vm.TraceDetails);
            vm.StatusMessage = "已复制运行详情 JSON 到剪贴板。";
        }
    }
}
