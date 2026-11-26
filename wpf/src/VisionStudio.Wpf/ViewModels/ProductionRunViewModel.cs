using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Api.Spc;
using VisionStudio.Engine;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.Views;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>
/// 生产运行与工位监控 ViewModel：进程内直接调用 <see cref="ProductionRuntimeService"/>、
/// <see cref="JobStore"/>、<see cref="AlarmStore"/> 与 <see cref="SpcSeriesReader"/>。
/// </summary>
public sealed partial class ProductionRunViewModel : DesktopWorkspaceViewModel
{
    private readonly IVisionImageViewerAdapter? _previewViewer;
    private readonly Func<WorkflowDefinition>? _getCurrentWorkflow;
    private readonly Action<WorkflowDefinition>? _loadWorkflowToEditor;
    private string? _lastDisplayedPreviewRunId;

    public ProductionRunViewModel(
        InProcessVisionHost host,
        IVisionImageViewerAdapter? previewViewer = null,
        Func<WorkflowDefinition>? getCurrentWorkflow = null,
        Action<WorkflowDefinition>? loadWorkflowToEditor = null) : base(host)
    {
        _previewViewer = previewViewer;
        _getCurrentWorkflow = getCurrentWorkflow;
        _loadWorkflowToEditor = loadWorkflowToEditor;

        RefreshCommand = CreateCommand("刷新生产状态", RefreshCoreAsync);
        InitializeTrialCommand = CreateCommand("初始化试用工单", async ct =>
        {
            var job = await DesktopTrialInitializer.InitializeAsync(Host, ct);
            await RefreshCoreAsync(ct);
            SelectedJob = Jobs.FirstOrDefault(item => item.Id == job.Id);
            StatusMessage = "示例工单已发布；尚未启动生产。";
        });
        PublishEditorWorkflowCommand = CreateCommand("发布当前流程为工单", PublishEditorWorkflowAsync,
            () => _getCurrentWorkflow is not null);
        PublishSelectedVersionCommand = CreateCommand("发布/回滚所选版本", PublishSelectedVersionAsync,
            () => IsStatusConfirmed && SelectedManagedJob is not null && SelectedJobVersion is not null);
        LoadJobVersionToEditorCommand = CreateCommand("载入版本到编辑器", LoadJobVersionToEditorAsync,
            () => _loadWorkflowToEditor is not null && SelectedManagedJob is not null && SelectedJobVersion is not null);

        StartProductionCommand = CreateCommand("启动生产", StartProductionAsync,
            () => IsStatusConfirmed && SelectedJob is not null && SelectedStation?.State == ProductionRuntimeState.Stopped);
        StopProductionCommand = CreateCommand("停止生产", StopProductionAsync,
            () => SelectedStation is not null && (!IsStatusConfirmed || SelectedStation.State != ProductionRuntimeState.Stopped));
        RecoverProductionCommand = CreateCommand("故障恢复", RecoverProductionAsync,
            () => IsStatusConfirmed && SelectedStation?.State == ProductionRuntimeState.Faulted);
        TriggerOnceCommand = CreateCommand("单次触发", TriggerOnceAsync,
            () => IsStatusConfirmed && SelectedStation?.State == ProductionRuntimeState.Running);
        StartAllStationsCommand = CreateCommand("全部启动", async ct =>
        {
            await Host.Production.StartAllAsync(ct);
            await RefreshCoreAsync(ct);
            StatusMessage = "已尝试启动所有已配置工单的工位。";
        }, () => IsStatusConfirmed && Stations.Count > 0);
        StopAllStationsCommand = CreateCommand("全部停止", async ct =>
        {
            await Host.Production.StopAllAsync(ct);
            await RefreshCoreAsync(ct);
            StatusMessage = "已请求停止全部工位。";
        }, () => Stations.Count > 0);

        AcknowledgeAlarmCommand = CreateCommand("确认报警", async ct =>
        {
            var alarm = SelectedAlarm ?? throw new InvalidOperationException("请先选择报警。");
            await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.alarm.acknowledge.intent", alarm.Id, 202, ct);
            await Host.Alarms.AcknowledgeAsync(alarm.Id, ct);
            await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.alarm.acknowledge", alarm.Id, 200, ct);
            await RefreshCoreAsync(ct);
            StatusMessage = "报警已确认；确认不代表设备故障已经恢复。";
        }, () => IsStatusConfirmed && SelectedAlarm is not null && !SelectedAlarm.Acknowledged);

        AnalyzeSpcCommand = CreateCommand("刷新 SPC 分析", AnalyzeSpcCoreAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InitializeTrialCommand { get; }
    public IAsyncRelayCommand PublishEditorWorkflowCommand { get; }
    public IAsyncRelayCommand PublishSelectedVersionCommand { get; }
    public IAsyncRelayCommand LoadJobVersionToEditorCommand { get; }
    public IAsyncRelayCommand StartProductionCommand { get; }
    public IAsyncRelayCommand StopProductionCommand { get; }
    public IAsyncRelayCommand RecoverProductionCommand { get; }
    public IAsyncRelayCommand TriggerOnceCommand { get; }
    public IAsyncRelayCommand StartAllStationsCommand { get; }
    public IAsyncRelayCommand StopAllStationsCommand { get; }
    public IAsyncRelayCommand AcknowledgeAlarmCommand { get; }
    public IAsyncRelayCommand AnalyzeSpcCommand { get; }

    public ObservableCollection<ProductionStationRuntimeStatus> Stations { get; } = [];
    public ObservableCollection<JobDescriptor> Jobs { get; } = [];
    public ObservableCollection<JobDescriptor> AllJobs { get; } = [];
    public ObservableCollection<JobVersionInfo> SelectedJobVersions { get; } = [];
    public ObservableCollection<AlarmRecord> ActiveAlarms { get; } = [];
    public ObservableCollection<SpcDatasetSummary> SpcDatasets { get; } = [];
    public ObservableCollection<SpcViolation> SpcViolations { get; } = [];
    public ObservableCollection<SpcChartPoint> SpcPoints { get; } = [];

    [ObservableProperty]
    private ProductionStationRuntimeStatus? _selectedStation;

    [ObservableProperty]
    private JobDescriptor? _selectedJob;

    [ObservableProperty]
    private JobDescriptor? _selectedManagedJob;

    [ObservableProperty]
    private JobVersionInfo? _selectedJobVersion;

    [ObservableProperty]
    private AlarmRecord? _selectedAlarm;

    [ObservableProperty]
    private bool _showAllAlarms;

    [ObservableProperty]
    private string _latestResultSummary = "暂无生产周期结果";

    [ObservableProperty]
    private SpcDatasetSummary? _selectedSpcDataset;

    [ObservableProperty]
    private string _spcLslText = string.Empty;

    [ObservableProperty]
    private string _spcUslText = string.Empty;

    [ObservableProperty]
    private string _spcSummaryText = "选择 SPC 数据集后点击分析";

    [ObservableProperty]
    private int _totalStations;

    [ObservableProperty]
    private int _runningStations;

    [ObservableProperty]
    private int _faultedStations;

    [ObservableProperty]
    private long _totalCycles;

    [ObservableProperty]
    private long _totalOk;

    [ObservableProperty]
    private long _totalNg;

    [ObservableProperty]
    private long _totalErrors;

    [ObservableProperty]
    private string _yieldRateText = "-";

    [ObservableProperty]
    private int _activeAlarmCount;

    partial void OnSelectedStationChanged(ProductionStationRuntimeStatus? value) => NotifyCommands();
    partial void OnSelectedJobChanged(JobDescriptor? value)
    {
        if (value is not null) SelectedManagedJob = value;
        NotifyCommands();
    }
    partial void OnSelectedManagedJobChanged(JobDescriptor? value)
    {
        RefreshManagedJobVersions();
        NotifyCommands();
    }
    partial void OnSelectedJobVersionChanged(JobVersionInfo? value) => NotifyCommands();
    partial void OnSelectedAlarmChanged(AlarmRecord? value) => NotifyCommands();
    partial void OnSelectedSpcDatasetChanged(SpcDatasetSummary? value) => NotifyCommands();
    partial void OnShowAllAlarmsChanged(bool value)
    {
        if (RefreshCommand.CanExecute(null)) _ = RefreshCommand.ExecuteAsync(null);
    }

    public Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    private void RefreshManagedJobVersions()
    {
        var prevVer = SelectedJobVersion?.Version;
        SelectedJobVersions.Clear();
        if (SelectedManagedJob is null) return;
        foreach (var v in SelectedManagedJob.Versions.OrderByDescending(x => x.Version))
            SelectedJobVersions.Add(v);
        SelectedJobVersion = SelectedJobVersions.FirstOrDefault(v => v.Version == prevVer) ?? SelectedJobVersions.FirstOrDefault();
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var statuses = Host.Production.ListStatuses();
        var jobs = await Host.Jobs.ListAsync(cancellationToken);
        var alarms = await Host.Alarms.ListAsync(activeOnly: !ShowAllAlarms, skip: 0, take: 100, cancellationToken);
        var spcDatasets = await Host.SpcReader.ListDatasetsAsync(50, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var stationId = SelectedStation?.StationId;
        var jobId = SelectedJob?.Id;
        var managedJobId = SelectedManagedJob?.Id;
        var alarmId = SelectedAlarm?.Id;
        var spcName = SelectedSpcDataset?.Dataset;

        Stations.Clear();
        foreach (var s in statuses)
        {
            Stations.Add(s);
        }
        SelectedStation = Stations.FirstOrDefault(station => station.StationId == stationId) ?? Stations.FirstOrDefault();

        AllJobs.Clear();
        Jobs.Clear();
        foreach (var j in jobs)
        {
            AllJobs.Add(j);
            if (j.PublishedVersion is not null) Jobs.Add(j);
        }
        SelectedJob = Jobs.FirstOrDefault(job => job.Id == jobId) ?? Jobs.FirstOrDefault();
        SelectedManagedJob = AllJobs.FirstOrDefault(job => job.Id == managedJobId) ?? SelectedJob ?? AllJobs.FirstOrDefault();

        ActiveAlarms.Clear();
        foreach (var a in alarms.Items)
        {
            ActiveAlarms.Add(a);
        }
        SelectedAlarm = ActiveAlarms.FirstOrDefault(alarm => alarm.Id == alarmId);

        SpcDatasets.Clear();
        foreach (var ds in spcDatasets) SpcDatasets.Add(ds);
        SelectedSpcDataset = SpcDatasets.FirstOrDefault(x => x.Dataset == spcName) ?? SpcDatasets.FirstOrDefault();

        TotalStations = Stations.Count;
        RunningStations = Stations.Count(s => s.State == ProductionRuntimeState.Running);
        FaultedStations = Stations.Count(s => s.State == ProductionRuntimeState.Faulted);
        TotalCycles = Stations.Sum(s => s.CycleCount);
        TotalOk = Stations.Sum(s => s.OkCount);
        TotalNg = Stations.Sum(s => s.NgCount);
        TotalErrors = Stations.Sum(s => s.ErrorCount);
        var inspected = TotalOk + TotalNg;
        YieldRateText = inspected > 0 ? $"{TotalOk * 100.0 / inspected:0.0}%" : "-";
        ActiveAlarmCount = ActiveAlarms.Count(a => !a.Acknowledged);

        await UpdateLivePreviewAsync(cancellationToken);

        ConfirmSnapshot();
        StatusMessage = $"已刷新：工位 {Stations.Count} 个，已发布工单 {Jobs.Count} 个，{(ShowAllAlarms ? "全部" : "活动")}报警 {ActiveAlarms.Count} 条";
    }

    private async Task UpdateLivePreviewAsync(CancellationToken cancellationToken)
    {
        if (SelectedStation is null)
        {
            LatestResultSummary = "暂无选中工位";
            return;
        }

        var latest = Host.Production.GetLatestResult(SelectedStation.StationId);
        if (latest is null)
        {
            LatestResultSummary = $"工位 {SelectedStation.StationId} 暂无周期输出";
            return;
        }

        LatestResultSummary = $"Run {latest.RunId[..Math.Min(8, latest.RunId.Length)]} · 判定 {latest.Disposition} · 耗时 {latest.DurationMs:0.0} ms · {latest.CompletedAt:HH:mm:ss}";
        if (_previewViewer is null || string.Equals(_lastDisplayedPreviewRunId, latest.RunId, StringComparison.Ordinal))
            return;

        var previewPath = Host.Traces.FindPreviewPath(latest.RunId);
        if (previewPath is not null && File.Exists(previewPath))
        {
            var bytes = await File.ReadAllBytesAsync(previewPath, cancellationToken);
            var overlays = await Host.Traces.GetOverlaysAsync(latest.RunId, cancellationToken) ?? [];
            _previewViewer.SetImage(
                VisionImageInterop.FromEncodedBytes(bytes),
                $"产线实时预览 · {SelectedStation.StationId} · {latest.Disposition}",
                fitToView: true);
            _previewViewer.SetOverlays(overlays);
            _lastDisplayedPreviewRunId = latest.RunId;
        }
    }

    private async Task PublishEditorWorkflowAsync(CancellationToken cancellationToken)
    {
        if (_getCurrentWorkflow is null) return;
        var workflow = _getCurrentWorkflow();
        WorkflowDocumentMapper.ValidateDefinition(workflow);
        await Host.CompileAndValidateAsync(workflow, cancellationToken);
        var findings = NodeMaturityGate.Scan(workflow);

        var checkboxText = findings.Count > 0
            ? $"确认包含 {findings.Count} 个未达到 Stable 的节点（Preview/Experimental）并仍要发布"
            : null;
        var dialog = new InputPromptWindow(
            "发布当前流程为工单/配方",
            "工单 ID（字母/数字/-/_，已存在则追加新版本）",
            workflow.Id,
            "工单名称",
            workflow.Name,
            findings.Count > 0
                ? $"检测到 {findings.Count} 个非 Stable 节点（{NodeMaturityGate.Describe(findings)}），必须勾选确认后才能发布。"
                : "将当前编辑器中的流程固化为不可变版本并发布到生产可用列表。",
            checkboxLabel: checkboxText,
            checkboxDefault: false,
            validate: (id, name, ack) =>
            {
                if (id.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
                    return "工单 ID 只能包含字母、数字、连字符(-)与下划线(_)。";
                if (string.IsNullOrWhiteSpace(name))
                    return "工单名称不能为空。";
                if (findings.Count > 0 && !ack)
                    return "包含非 Stable 节点，必须勾选确认才能发布。";
                return null;
            });
        if (Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) return;

        var jobId = dialog.PrimaryValue.Trim();
        var jobName = dialog.SecondaryValue.Trim();
        var existing = await Host.Jobs.GetAsync(jobId, cancellationToken);
        int targetVersion;
        DateTimeOffset expectedUpdatedAt;

        if (existing is null)
        {
            var created = await Host.Jobs.CreateAsync(
                new CreateJobRequest(jobId, jobName, "由 WPF 桌面编辑器发布", workflow, "桌面初始发布"),
                cancellationToken);
            targetVersion = 1;
            expectedUpdatedAt = created.UpdatedAt;
        }
        else
        {
            var ver = await Host.Jobs.AddVersionAsync(
                jobId,
                new SaveJobVersionRequest(workflow, $"桌面更新 V{existing.LatestVersion + 1}"),
                cancellationToken);
            targetVersion = ver.Version;
            var reloaded = await Host.Jobs.GetAsync(jobId, cancellationToken) ?? existing;
            expectedUpdatedAt = reloaded.UpdatedAt;
        }

        var manifest = await Host.Dependencies.CaptureAsync(workflow, cancellationToken);
        await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.job.publish.intent", $"{jobId}@v{targetVersion}", 202, cancellationToken);
        var published = await Host.Jobs.PublishAsync(jobId, targetVersion, "Publish", manifest, cancellationToken, expectedUpdatedAt);
        await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.job.publish", $"{jobId}@v{targetVersion}", 200, cancellationToken);

        await RefreshCoreAsync(cancellationToken);
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == published.Id);
        SelectedManagedJob = AllJobs.FirstOrDefault(j => j.Id == published.Id);
        StatusMessage = $"已发布工单 {published.Name} ({published.Id}) V{targetVersion}";
    }

    private async Task PublishSelectedVersionAsync(CancellationToken cancellationToken)
    {
        var job = SelectedManagedJob ?? SelectedJob ?? throw new InvalidOperationException("请先选择工单。");
        var ver = SelectedJobVersion ?? throw new InvalidOperationException("请先选择版本号。");
        var snapshot = await Host.Jobs.GetVersionAsync(job.Id, ver.Version, cancellationToken)
            ?? throw new InvalidOperationException($"版本 V{ver.Version} 不存在。");
        await Host.CompileAndValidateAsync(snapshot.Workflow, cancellationToken);

        var findings = NodeMaturityGate.Scan(snapshot.Workflow);
        var action = job.PublishedVersion is { } pub && ver.Version < pub ? "Rollback" : "Publish";
        if (findings.Count > 0)
        {
            var confirm = MessageBox.Show(
                $"工单 {job.Name} V{ver.Version} 包含 {findings.Count} 个非 Stable 节点：\n{NodeMaturityGate.Describe(findings)}\n\n是否确认执行 {action}？",
                "节点成熟度发布确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var manifest = await Host.Dependencies.CaptureAsync(snapshot.Workflow, cancellationToken);
        await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.job.publish.intent", $"{job.Id}@v{ver.Version}", 202, cancellationToken);
        var updated = await Host.Jobs.PublishAsync(job.Id, ver.Version, action, manifest, cancellationToken, job.UpdatedAt);
        await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.job.publish", $"{job.Id}@v{ver.Version}", 200, cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == updated.Id);
        StatusMessage = $"工单 {updated.Name} 已{(action == "Rollback" ? "回滚" : "发布")}到 V{ver.Version}";
    }

    private async Task LoadJobVersionToEditorAsync(CancellationToken cancellationToken)
    {
        if (_loadWorkflowToEditor is null) return;
        var job = SelectedManagedJob ?? SelectedJob ?? throw new InvalidOperationException("请先选择工单。");
        var ver = SelectedJobVersion ?? throw new InvalidOperationException("请先选择版本号。");
        var snapshot = await Host.Jobs.GetVersionAsync(job.Id, ver.Version, cancellationToken)
            ?? throw new InvalidOperationException($"版本 V{ver.Version} 不存在。");
        _loadWorkflowToEditor(snapshot.Workflow);
        StatusMessage = $"已将工单 {job.Name} V{ver.Version} 载入编辑器";
    }

    private Task TriggerOnceAsync(CancellationToken cancellationToken)
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工位。");
        var enqueue = Host.Production.Trigger(station.StationId);
        StatusMessage = enqueue is null
            ? $"工位 {station.StationId} 当前不需要触发（或非外部触发模式）"
            : $"已向工位 {station.StationId} 入队单次触发 ({enqueue.Event.EventId})";
        return RefreshCoreAsync(cancellationToken);
    }

    private async Task AnalyzeSpcCoreAsync(CancellationToken cancellationToken)
    {
        if (SelectedSpcDataset is null)
        {
            var list = await Host.SpcReader.ListDatasetsAsync(50, cancellationToken);
            SpcDatasets.Clear();
            foreach (var d in list) SpcDatasets.Add(d);
            SelectedSpcDataset = SpcDatasets.FirstOrDefault();
        }

        if (SelectedSpcDataset is null)
        {
            SpcSummaryText = "暂无 SPC 数据集（需在流程中使用 output.database 落库数值结果）";
            SpcViolations.Clear();
            SpcPoints.Clear();
            return;
        }

        double? lsl = double.TryParse(SpcLslText, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null;
        double? usl = double.TryParse(SpcUslText, NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : null;
        var series = await Host.SpcReader.ReadSeriesAsync(SelectedSpcDataset.Dataset, null, null, 2000, cancellationToken);
        var options = SpcAnalysisOptions.Resolve(SpcChartTypes.Individuals, 1, SpcGroupings.Sequential, lsl, usl);
        var analysis = SpcCalculator.Analyze(series, options);

        SpcViolations.Clear();
        foreach (var v in analysis.PrimaryChart.Violations) SpcViolations.Add(v);
        SpcPoints.Clear();
        foreach (var pt in analysis.PrimaryChart.Points.TakeLast(200)) SpcPoints.Add(pt);

        var capText = analysis.Capability is { } cap
            ? $" · Cp={cap.Cp?.ToString("0.00") ?? "-"} Cpk={cap.Cpk?.ToString("0.00") ?? "-"} 超规={cap.OutOfSpecificationCount}"
            : string.Empty;
        SpcSummaryText = $"数据集 {analysis.Dataset}：样本 {analysis.SamplesUsed}，均值 {analysis.Mean?.ToString("0.###") ?? "-"}，" +
            $"CL={analysis.PrimaryChart.CenterLine?.ToString("0.###") ?? "-"}，" +
            $"UCL={analysis.PrimaryChart.UpperControlLimit?.ToString("0.###") ?? "-"}，" +
            $"LCL={analysis.PrimaryChart.LowerControlLimit?.ToString("0.###") ?? "-"}，" +
            $"违规 {SpcViolations.Count} 项{capText}";
    }

    private async Task StartProductionAsync(CancellationToken cancellationToken)
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工位。");
        var job = SelectedJob ?? throw new InvalidOperationException("请先选择已发布工单。");
        await DesktopTrialInitializer.RecordActionAsync(Host, "wpf.production.start.intent", station.StationId, 202, cancellationToken);
        var result = await Host.Production.StartAsync(station.StationId, job.Id, cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        StatusMessage = $"工位 {station.StationId} 当前状态：{result.State}";
    }

    private async Task StopProductionAsync(CancellationToken cancellationToken)
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工位。");
        var result = await Host.Production.StopAsync(station.StationId, cancellationToken);
        if (result.State != ProductionRuntimeState.Stopped)
            throw new InvalidOperationException($"停止尚未完成：{result.LastError}");
        await RefreshCoreAsync(cancellationToken);
        StatusMessage = $"工位 {station.StationId} 已停止";
    }

    private async Task RecoverProductionAsync(CancellationToken cancellationToken)
    {
        var station = SelectedStation ?? throw new InvalidOperationException("请先选择工位。");
        var result = await Host.Production.RecoverAsync(station.StationId, cancellationToken);
        await RefreshCoreAsync(cancellationToken);
        StatusMessage = $"工位 {station.StationId} 恢复后状态：{result.State}";
    }
}
