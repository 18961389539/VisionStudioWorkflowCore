using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Diagnostics;
using VisionStudio.Engine;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine.Runtime;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.Views;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>
/// 追溯记录、节点耗时遥测、数据集批量验证、资产健康与 Serilog 实时日志诊断 ViewModel。
/// </summary>
public sealed partial class TraceDiagnosticsViewModel : DesktopWorkspaceViewModel
{
    private readonly Func<WorkflowDefinition>? _getCurrentWorkflow;
    private readonly Action<WorkflowDefinition>? _loadWorkflowToEditor;

    public TraceDiagnosticsViewModel(
        InProcessVisionHost host,
        Func<WorkflowDefinition>? getCurrentWorkflow = null,
        Action<WorkflowDefinition>? loadWorkflowToEditor = null) : base(host)
    {
        _getCurrentWorkflow = getCurrentWorkflow;
        _loadWorkflowToEditor = loadWorkflowToEditor;

        foreach (var item in Host.LogSink.Snapshot())
        {
            Logs.Add(item);
        }

        RefreshCommand = CreateCommand("刷新诊断", RefreshCoreAsync);
        ViewTraceCommand = CreateCommand("读取运行详情", LoadSelectedTraceDetailAsync, () => SelectedTrace is not null);
        LoadTraceWorkflowToEditorCommand = CreateCommand("载入运行快照到编辑器", LoadTraceWorkflowToEditorAsync,
            () => _loadWorkflowToEditor is not null && SelectedTrace is not null);
        ExportBundleCommand = CreateCommand("导出诊断包", async ct =>
        {
            var dialog = new SaveFileDialog { Filter = "诊断包 ZIP|*.zip", FileName = $"VisionStudio-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (dialog.ShowDialog() != true) return;
            await ExportBundleAsync(dialog.FileName, ct);
            StatusMessage = $"诊断包已导出：{dialog.FileName}；日志可能包含本机路径，请按需分享。";
        });

        CreateValidationDatasetCommand = CreateCommand("新建验证数据集", CreateValidationDatasetAsync);
        AddTraceToDatasetCommand = CreateCommand("将选中追溯加入数据集", AddTraceToDatasetAsync,
            () => SelectedValidationDataset is not null && SelectedTrace is { HasReplayInput: true });
        RunValidationDatasetCommand = CreateCommand("执行数据集批量验证", RunValidationDatasetAsync,
            () => SelectedValidationDataset is not null && _getCurrentWorkflow is not null);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ViewTraceCommand { get; }
    public IAsyncRelayCommand LoadTraceWorkflowToEditorCommand { get; }
    public IAsyncRelayCommand ExportBundleCommand { get; }
    public IAsyncRelayCommand CreateValidationDatasetCommand { get; }
    public IAsyncRelayCommand AddTraceToDatasetCommand { get; }
    public IAsyncRelayCommand RunValidationDatasetCommand { get; }

    public ObservableCollection<RunTraceRecord> TraceRecords { get; } = [];
    public ObservableCollection<NodeRunReport> SelectedTraceNodeReports { get; } = [];
    public ObservableCollection<AssetHealthSnapshot> AssetHealth { get; } = [];
    public ObservableCollection<NodeDurationStatistic> NodeDurations { get; } = [];
    public ObservableCollection<NodeSpanRecord> RecentNodeSpans { get; } = [];
    public ObservableCollection<ValidationDatasetSummary> ValidationDatasets { get; } = [];
    public ObservableCollection<ValidationDatasetItem> ValidationDatasetItems { get; } = [];
    public ObservableCollection<ValidationRunRecord> ValidationRuns { get; } = [];
    public ObservableCollection<ValidationResultRecord> ValidationResults { get; } = [];
    public ObservableCollection<WpfLogEntry> Logs { get; } = [];

    [ObservableProperty]
    private string _diagnosticsSummaryText = "尚未刷新";

    [ObservableProperty]
    private string _traceFilter = string.Empty;

    [ObservableProperty]
    private RunTraceRecord? _selectedTrace;

    [ObservableProperty]
    private string _traceDetails = "选择运行记录，然后查看详情。";

    [ObservableProperty]
    private ValidationDatasetSummary? _selectedValidationDataset;

    [ObservableProperty]
    private ValidationRunRecord? _selectedValidationRun;

    [ObservableProperty]
    private string _validationSummaryText = "选择或创建验证数据集后执行批量回归";

    [ObservableProperty]
    private int _healthyCount;

    [ObservableProperty]
    private int _degradedCount;

    [ObservableProperty]
    private int _faultedCount;

    [ObservableProperty]
    private int _offlineCount;

    partial void OnSelectedTraceChanged(RunTraceRecord? value)
    {
        NotifyCommands();
        SelectedTraceNodeReports.Clear();
        if (value is not null)
        {
            foreach (var r in value.NodeReports) SelectedTraceNodeReports.Add(r);
        }
    }

    partial void OnSelectedValidationDatasetChanged(ValidationDatasetSummary? value)
    {
        NotifyCommands();
        _ = LoadSelectedDatasetDetailsAsync();
    }

    partial void OnSelectedValidationRunChanged(ValidationRunRecord? value)
    {
        NotifyCommands();
        _ = LoadSelectedValidationRunDetailsAsync();
    }

    public Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var traces = await Host.Traces.ListAsync(take: 100, jobId: null, disposition: null, cancellationToken);
        var datasets = await Host.ValidationDatasets.ListDatasetsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var runId = SelectedTrace?.RunId;
        var datasetId = SelectedValidationDataset?.Id;

        TraceRecords.Clear();
        foreach (var t in traces.Where(trace => string.IsNullOrWhiteSpace(TraceFilter)
            || trace.RunId.Contains(TraceFilter, StringComparison.OrdinalIgnoreCase)
            || trace.WorkflowName.Contains(TraceFilter, StringComparison.OrdinalIgnoreCase)))
        {
            TraceRecords.Add(t);
        }
        SelectedTrace = TraceRecords.FirstOrDefault(trace => trace.RunId == runId);

        var assets = Host.Diagnostics.Assets();
        AssetHealth.Clear();
        foreach (var a in assets) AssetHealth.Add(a);

        NodeDurations.Clear();
        foreach (var d in Host.NodeDurations.Snapshot()) NodeDurations.Add(d);

        RecentNodeSpans.Clear();
        foreach (var s in Host.NodeSpans.Snapshot(64).Recent) RecentNodeSpans.Add(s);

        ValidationDatasets.Clear();
        foreach (var ds in datasets) ValidationDatasets.Add(ds);
        SelectedValidationDataset = ValidationDatasets.FirstOrDefault(d => d.Id == datasetId) ?? ValidationDatasets.FirstOrDefault();

        Logs.Clear();
        foreach (var entry in Host.LogSink.Snapshot()) Logs.Add(entry);
        var summary = Host.Diagnostics.Summary();
        HealthyCount = summary.Healthy;
        DegradedCount = summary.Degraded;
        FaultedCount = summary.Faulted;
        OfflineCount = summary.Offline;
        ConfirmSnapshot();
        DiagnosticsSummaryText = $"资产健康：{summary.Healthy} 正常 / {summary.Degraded} 降级 / {summary.Faulted} 故障 / {summary.Offline} 离线 · 追溯 {TraceRecords.Count} 条 · 节点统计 {NodeDurations.Count} 类";
        StatusMessage = "追溯、节点遥测与日志已刷新。";
    }

    private async Task LoadSelectedTraceDetailAsync(CancellationToken cancellationToken)
    {
        var trace = SelectedTrace ?? throw new InvalidOperationException("请先选择运行记录。");
        var detail = await Host.Traces.GetAsync(trace.RunId, cancellationToken)
            ?? throw new InvalidOperationException("所选运行记录已不存在。");
        SelectedTraceNodeReports.Clear();
        foreach (var report in detail.NodeReports) SelectedTraceNodeReports.Add(report);
        TraceDetails = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        StatusMessage = $"已加载运行 {detail.RunId} 详情（{detail.NodeReports.Count} 个节点报告）";
    }

    private async Task LoadTraceWorkflowToEditorAsync(CancellationToken cancellationToken)
    {
        if (_loadWorkflowToEditor is null) return;
        var trace = SelectedTrace ?? throw new InvalidOperationException("请先选择运行记录。");
        var snapshot = await Host.Traces.GetWorkflowSnapshotAsync(trace.RunId, cancellationToken)
            ?? throw new InvalidOperationException("所选运行记录未包含流程快照。");
        _loadWorkflowToEditor(snapshot);
        StatusMessage = $"已将运行 {trace.RunId} 的流程快照载入编辑器";
    }

    private async Task LoadSelectedDatasetDetailsAsync()
    {
        ValidationDatasetItems.Clear();
        ValidationRuns.Clear();
        ValidationResults.Clear();
        if (SelectedValidationDataset is null) return;
        try
        {
            var detail = await Host.ValidationDatasets.GetDatasetAsync(SelectedValidationDataset.Id, CancellationToken.None);
            foreach (var item in detail.Items) ValidationDatasetItems.Add(item);
            var runs = await Host.ValidationDatasets.ListRunsAsync(SelectedValidationDataset.Id, CancellationToken.None);
            foreach (var run in runs) ValidationRuns.Add(run);
            SelectedValidationRun = ValidationRuns.FirstOrDefault();
        }
        catch { /* 后台切换加载失败时保持界面稳定 */ }
    }

    private async Task LoadSelectedValidationRunDetailsAsync()
    {
        ValidationResults.Clear();
        if (SelectedValidationRun is null)
        {
            ValidationSummaryText = "暂无验证运行记录";
            return;
        }
        try
        {
            var run = await Host.ValidationDatasets.GetRunAsync(SelectedValidationRun.RunId, includeResults: true, CancellationToken.None);
            if (run.Results is not null)
                foreach (var r in run.Results) ValidationResults.Add(r);
            ValidationSummaryText = run.Summary is { } s
                ? $"状态 {run.Status} · 样本 {s.Completed}/{s.Total} · 准确率 {s.Accuracy:P1} · TrueOK={s.TrueOk} TrueNG={s.TrueNg} FalseOK={s.FalseOk} FalseNG={s.FalseNg} Err={s.Errors} · P95={s.P95DurationMs:0.0} ms"
                : $"状态 {run.Status} · 已完成 {run.CompletedCount}/{run.RequestedCount}";
        }
        catch { /* 后台读取运行结果失败时不弹窗干扰 */ }
    }

    private async Task CreateValidationDatasetAsync(CancellationToken cancellationToken)
    {
        var dialog = new InputPromptWindow(
            "新建验证数据集",
            "数据集名称",
            $"回归集-{DateTime.Now:MMdd-HHmm}",
            "描述（可选）",
            "WPF 桌面端批量回归测试集");
        if (Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var created = await Host.ValidationDatasets.CreateDatasetAsync(
            new CreateValidationDatasetRequest(dialog.PrimaryValue.Trim(), dialog.SecondaryValue.Trim()),
            cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        SelectedValidationDataset = ValidationDatasets.FirstOrDefault(d => d.Id == created.Dataset.Id);
        StatusMessage = $"已创建验证数据集：{created.Dataset.Name}";
    }

    private async Task AddTraceToDatasetAsync(CancellationToken cancellationToken)
    {
        var dataset = SelectedValidationDataset ?? throw new InvalidOperationException("请先选择验证数据集。");
        var trace = SelectedTrace ?? throw new InvalidOperationException("请先选择包含回放输入的运行记录。");
        var expectedDefault = string.Equals(trace.Disposition, "NG", StringComparison.OrdinalIgnoreCase) ? "NG" : "OK";
        var dialog = new InputPromptWindow(
            "加入验证数据集",
            "期望判定（OK 或 NG）",
            expectedDefault,
            "备注（可选）",
            trace.WorkflowName,
            $"将运行 {trace.RunId} 的回放样本加入数据集“{dataset.Name}”。",
            validate: (exp, _, _) => exp.Equals("OK", StringComparison.OrdinalIgnoreCase) || exp.Equals("NG", StringComparison.OrdinalIgnoreCase)
                ? null
                : "期望判定只能填 OK 或 NG。");
        if (Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        await Host.ValidationDatasets.AddItemsAsync(
            dataset.Id,
            new AddValidationDatasetItemsRequest([
                new ValidationDatasetItemInput("TRACE", trace.RunId, dialog.PrimaryValue.Trim().ToUpperInvariant(), dialog.SecondaryValue.Trim())
            ]),
            cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        await LoadSelectedDatasetDetailsAsync();
        StatusMessage = $"已将运行 {trace.RunId} 加入数据集 {dataset.Name}";
    }

    private async Task RunValidationDatasetAsync(CancellationToken cancellationToken)
    {
        if (_getCurrentWorkflow is null) return;
        var dataset = SelectedValidationDataset ?? throw new InvalidOperationException("请先选择验证数据集。");
        var workflow = _getCurrentWorkflow();
        var run = await Host.ValidationRunner.StartAsync(
            dataset.Id,
            new ValidationRunRequest(Workflow: workflow),
            cancellationToken);

        StatusMessage = $"已启动数据集批量验证 {run.RunId}...";
        for (var i = 0; i < 60 && !cancellationToken.IsCancellationRequested; i++)
        {
            await Task.Delay(250, cancellationToken);
            var current = await Host.ValidationDatasets.GetRunAsync(run.RunId, includeResults: true, cancellationToken);
            if (current.Status is not "Running")
            {
                await LoadSelectedDatasetDetailsAsync();
                SelectedValidationRun = ValidationRuns.FirstOrDefault(r => r.RunId == run.RunId) ?? current;
                StatusMessage = $"批量验证完成 ({current.Status})";
                return;
            }
        }
        await LoadSelectedDatasetDetailsAsync();
        StatusMessage = $"批量验证正在后台继续运行 ({run.RunId})，请稍后刷新查看结果";
    }

    public async Task ExportBundleAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await Host.Services.GetRequiredService<DiagnosticBundleService>().CreateAsync(cancellationToken);
        await DesktopFileWriter.WriteAsync(path, bytes, cancellationToken);
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Host.LogSink.Clear();
        Logs.Clear();
    }

    [RelayCommand]
    private void ClearTraceFilter() => TraceFilter = string.Empty;
}
