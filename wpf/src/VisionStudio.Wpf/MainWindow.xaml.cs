using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.ViewModels;
using VisionStudio.Wpf.Views;

namespace VisionStudio.Wpf;

/// <summary>
/// WPF 主窗口：托管四大工作区并组装方案 C 进程内 ViewModel 与节点详情弹窗回调。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly InProcessVisionHost _host;
    private readonly DispatcherTimer _refreshTimer;
    private bool _closing;
    private bool _allowClose;
    private readonly bool _interactive;

    public MainWindow(InProcessVisionHost host, bool interactive = true)
    {
        _host = host;
        _interactive = interactive;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        var editorVm = new WorkflowEditorViewModel(
            host,
            EditorView.ImageViewerAdapter,
            OpenNodeDetailDialog);
        var productionVm = new ProductionRunViewModel(
            host,
            ProductionView.PreviewViewerAdapter,
            () => editorVm.ExportCurrentDefinition(),
            def => LoadWorkflowIntoEditorAsync(def));
        var devicesVm = new DeviceMonitorViewModel(host, DevicesView.CameraViewerAdapter);
        var diagnosticsVm = new TraceDiagnosticsViewModel(
            host,
            () => editorVm.ExportCurrentDefinition(),
            def => LoadWorkflowIntoEditorAsync(def));

        _viewModel = new MainWindowViewModel(
            editorVm,
            productionVm,
            devicesVm,
            diagnosticsVm,
            host.DataRoot);

        DataContext = _viewModel;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += OnRefreshTick;
        Loaded += async (_, _) =>
        {
            await editorVm.RefreshWorkflowChoices();
            _refreshTimer.Start();
        };
    }

    private void OpenNodeDetailDialog(WorkflowNodeViewModel node, IReadOnlyList<Engine.RunNodeImage> nodeImages)
    {
        var detailVm = new NodeDetailViewModel(
            node,
            nodeImages,
            (bmp, caption) =>
            {
                EditorView.ImageViewerAdapter.SetImage(bmp, caption, fitToView: true);
            },
            () => EditorView.ImageViewerAdapter.TryGetActiveUserRoi(),
            roi => EditorView.ImageViewerAdapter.ShowUserRoi(roi));

        var dialog = new NodeDetailWindow(detailVm)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true) _viewModel.Editor.NotifyDocumentChanged();
    }

    private async void LoadWorkflowIntoEditorAsync(Abstractions.WorkflowDefinition definition)
    {
        if (!await _viewModel.Editor.ConfirmDiscardChangesAsync()) return;
        _viewModel.Editor.LoadDefinition(definition, resetRevision: true);
        _viewModel.SelectedTabIndex = 0;
    }

    private async void OnMainTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || _viewModel is null || _closing)
        {
            return;
        }

        if (_viewModel.SelectedTabIndex != 2)
        {
            _viewModel.Devices.StopLivePreview();
        }
        await RefreshSelectedWorkspaceAsync();
    }

    private async void OnRefreshTick(object? sender, EventArgs args) => await RefreshSelectedWorkspaceAsync();

    private Task RefreshSelectedWorkspaceAsync()
    {
        if (_closing) return Task.CompletedTask;
        var command = _viewModel.SelectedTabIndex switch
        {
            1 => _viewModel.Production.RefreshCommand,
            2 => _viewModel.Devices.RefreshCommand,
            3 => _viewModel.Diagnostics.RefreshCommand,
            _ => null
        };
        return command?.CanExecute(null) == true ? command.ExecuteAsync(null) : Task.CompletedTask;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || _viewModel is null) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (!await _viewModel.Editor.ConfirmDiscardChangesAsync()) return;
            IsEnabled = false;
            _refreshTimer.Stop();
            await Task.WhenAll(
                _viewModel.Editor.PrepareCloseAsync(),
                _viewModel.Production.PrepareCloseAsync(),
                _viewModel.Devices.PrepareCloseAsync(),
                _viewModel.Diagnostics.PrepareCloseAsync());
            await _host.StopProductionAsync();
            await _host.DisposeAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _host.ReportError(exception, "关闭应用");
            if (_interactive)
                MessageBox.Show($"关闭失败，窗口保留以便重试：\n{exception.Message}",
                    "未完成关闭", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!_allowClose)
            {
                _closing = false;
                IsEnabled = true;
                _viewModel.Editor.IsClosing = false;
                _viewModel.Production.IsClosing = false;
                _viewModel.Devices.IsClosing = false;
                _viewModel.Diagnostics.IsClosing = false;
                _refreshTimer.Start();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
        }
        (EditorView?.ImageViewerAdapter as IDisposable)?.Dispose();
        (ProductionView?.PreviewViewerAdapter as IDisposable)?.Dispose();
        (DevicesView?.CameraViewerAdapter as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
