using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>
/// 主窗口根 ViewModel：聚合四大核心工作区（流程编排、生产运行、设备监视、追溯诊断）。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(
        WorkflowEditorViewModel editor,
        ProductionRunViewModel production,
        DeviceMonitorViewModel devices,
        TraceDiagnosticsViewModel diagnostics,
        string dataRoot)
    {
        Editor = editor;
        Production = production;
        Devices = devices;
        Diagnostics = diagnostics;
        DataRoot = dataRoot;
    }

    public WorkflowEditorViewModel Editor { get; }
    public ProductionRunViewModel Production { get; }
    public DeviceMonitorViewModel Devices { get; }
    public TraceDiagnosticsViewModel Diagnostics { get; }
    public string DataRoot { get; }
    public string Version => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "未知版本";

    [ObservableProperty]
    private int _selectedTabIndex;
}
