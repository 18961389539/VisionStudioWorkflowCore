using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Engine;
using VisionStudio.Wpf.Controls.ImageViewer;
using VisionStudio.Wpf.Hosting;
using VisionStudio.Wpf.Localization;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>工具箱中的节点目录条目。</summary>
public sealed record ToolboxNodeItem(
    string Type,
    string DisplayName,
    string Category,
    string CategoryZh,
    string Description);

/// <summary>工具箱分类分组（支持搜索时自动展开）。</summary>
public sealed partial class ToolboxCategoryGroup : ObservableObject
{
    public ToolboxCategoryGroup(string categoryZh, IReadOnlyList<ToolboxNodeItem> items, bool isExpanded = false)
    {
        CategoryZh = categoryZh;
        Items = items;
        _isExpanded = isExpanded;
    }

    public string CategoryZh { get; }
    public IReadOnlyList<ToolboxNodeItem> Items { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>可加载的示例或已保存工作流摘要。</summary>
public sealed record WorkflowFileChoice(
    string Id,
    string DisplayName,
    string SourceKind,
    string? FilePath = null);

/// <summary>节点输出图像选项，用于在主图像浏览区切换不同节点的中间图像。</summary>
public sealed record NodeImageChoice(
    string Label,
    RunNodeImage Image);

public enum UnsavedChangesDecision { Save, Discard, Cancel }

/// <summary>
/// 工作流编辑器主 ViewModel：通过方案 C 进程内宿主直接编译、运行并可视化流程结果与节点图像。
/// </summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly InProcessVisionHost _host;
    private readonly IVisionImageViewerAdapter _imageViewer;
    private readonly List<ToolboxNodeItem> _allToolboxItems = [];
    private readonly Action<WorkflowNodeViewModel, IReadOnlyList<RunNodeImage>> _openDetailDialog;
    private readonly WorkflowHistoryManager _history = new();
    private readonly WorkflowClipboard _clipboard = new();
    private CancellationTokenSource? _runCts;
    private IReadOnlyList<RunNodeImage> _latestNodeImages = [];
    private IReadOnlyList<VisionOverlay> _latestOverlays = [];
    private string? _loadedRevision;
    private string _cleanDocument = string.Empty;
    private bool _loadingDocument;
    private long _runGeneration;
    private readonly Func<UnsavedChangesDecision>? _confirmUnsaved;
    private readonly Func<(string Id, string Name)?>? _promptSaveAs;
    private readonly SemaphoreSlim _choicesGate = new(1, 1);
    private Task<bool> _refreshChoicesTask = Task.FromResult(true);
    private string? _activeDebugSessionId;

    public WorkflowEditorViewModel(
        InProcessVisionHost host,
        IVisionImageViewerAdapter imageViewer,
        Action<WorkflowNodeViewModel, IReadOnlyList<RunNodeImage>> openDetailDialog,
        Func<UnsavedChangesDecision>? confirmUnsaved = null,
        Func<(string Id, string Name)?>? promptSaveAs = null)
    {
        _host = host;
        _imageViewer = imageViewer;
        _openDetailDialog = openDetailDialog;
        _confirmUnsaved = confirmUnsaved;
        _promptSaveAs = promptSaveAs;

        PendingConnection = new PendingConnectionViewModel(ConnectPorts);
        DisconnectConnectorCommand = new RelayCommand<ConnectorViewModel?>(DisconnectConnector, _ => CanEdit);
        Nodes.CollectionChanged += OnNodesChanged;
        Connections.CollectionChanged += (_, _) => NotifyDocumentChanged();

        BuildToolbox();
        LoadDefaultOrFirstSample();
    }

    public ObservableCollection<ToolboxCategoryGroup> FilteredToolbox { get; } = [];
    public ObservableCollection<WorkflowNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<WorkflowConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<WorkflowNodeViewModel> SelectedNodes { get; } = [];
    public ObservableCollection<WorkflowFileChoice> AvailableWorkflows { get; } = [];
    public ObservableCollection<NodeImageChoice> NodeImageChoices { get; } = [];
    public ObservableCollection<NodeRunReport> LatestNodeReports { get; } = [];
    public ObservableCollection<WorkflowValidationIssue> ValidationIssues { get; } = [];

    public PendingConnectionViewModel PendingConnection { get; }
    public IRelayCommand<ConnectorViewModel?> DisconnectConnectorCommand { get; }

    [ObservableProperty]
    private string _workflowId = "wpf-demo";

    [ObservableProperty]
    private string _workflowName = "VisionStudio · 桌面工作流";

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private WorkflowNodeViewModel? _selectedNode;

    [ObservableProperty]
    private WorkflowFileChoice? _selectedWorkflowChoice;

    [ObservableProperty]
    private NodeImageChoice? _selectedNodeImage;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDocumentBusy;

    [ObservableProperty]
    private bool _isClosing;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _lastRunStateKey = "Idle";

    [ObservableProperty]
    private bool _hasActiveDebugSession;

    [ObservableProperty]
    private bool _isDebugHalted;

    [ObservableProperty]
    private string _debugSessionStatusText = "未开启断点调试";

    public bool CanEdit => !IsBusy && !IsDocumentBusy && !IsClosing;
    public bool CanUndo => CanEdit && _history.CanUndo;
    public bool CanRedo => CanEdit && _history.CanRedo;
    public bool CanPaste => CanEdit && _clipboard.HasContent;

    private bool EnsureEditable()
    {
        if (CanEdit) return true;
        StatusMessage = "运行或文档操作期间不能修改流程，请先停止或等待操作完成。";
        return false;
    }

    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnIsDocumentBusyChanged(bool value) => NotifyAvailability();
    partial void OnIsClosingChanged(bool value) => NotifyAvailability();
    partial void OnHasActiveDebugSessionChanged(bool value) => NotifyAvailability();
    partial void OnIsDebugHaltedChanged(bool value) => NotifyAvailability();
    partial void OnSelectedNodeChanged(WorkflowNodeViewModel? value) => NotifyAvailability();
    partial void OnSelectedWorkflowChoiceChanged(WorkflowFileChoice? value) => NotifyAvailability();
    partial void OnWorkflowNameChanged(string value) => NotifyDocumentChanged();
    partial void OnWorkflowIdChanged(string value) => NotifyDocumentChanged();

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanPaste));
        LoadSelectedWorkflowCommand.NotifyCanExecuteChanged();
        NewWorkflowCommand.NotifyCanExecuteChanged();
        SaveWorkflowCommand.NotifyCanExecuteChanged();
        SaveAsWorkflowCommand.NotifyCanExecuteChanged();
        DeleteSavedWorkflowCommand.NotifyCanExecuteChanged();
        ValidateWorkflowCommand.NotifyCanExecuteChanged();
        RunWorkflowCommand.NotifyCanExecuteChanged();
        RunToSelectedNodeCommand.NotifyCanExecuteChanged();
        ToggleBreakpointCommand.NotifyCanExecuteChanged();
        StartOrContinueDebugCommand.NotifyCanExecuteChanged();
        RunCachedDebugNodeCommand.NotifyCanExecuteChanged();
        StopDebugSessionCommand.NotifyCanExecuteChanged();
        AddNodeFromToolboxCommand.NotifyCanExecuteChanged();
        DeleteSelectedNodesCommand.NotifyCanExecuteChanged();
        CopySelectedNodesCommand.NotifyCanExecuteChanged();
        PasteNodesCommand.NotifyCanExecuteChanged();
        DuplicateSelectedNodesCommand.NotifyCanExecuteChanged();
        AutoLayoutCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OpenNodeDetailCommand.NotifyCanExecuteChanged();
        ImportWorkflowCommand.NotifyCanExecuteChanged();
        ExportWorkflowCommand.NotifyCanExecuteChanged();
        DisconnectConnectorCommand?.NotifyCanExecuteChanged();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
            foreach (WorkflowNodeViewModel node in args.OldItems) node.PropertyChanged -= OnNodeChanged;
        if (args.NewItems is not null)
            foreach (WorkflowNodeViewModel node in args.NewItems) node.PropertyChanged += OnNodeChanged;
        NotifyDocumentChanged();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WorkflowNodeViewModel.Title) or nameof(WorkflowNodeViewModel.Location))
            NotifyDocumentChanged();
    }

    public void NotifyDocumentChanged()
    {
        if (!_loadingDocument && _cleanDocument.Length > 0)
            IsDirty = JsonSerializer.Serialize(ExportCurrentDefinition(), JsonOptions) != _cleanDocument;
    }

    public async Task<bool> ConfirmDiscardChangesAsync()
    {
        NotifyDocumentChanged();
        if (!IsDirty) return true;
        var decision = _confirmUnsaved?.Invoke() ?? MessageBox.Show(
            "流程有未保存的修改。是否先保存？\n选择“否”放弃修改，选择“取消”保留当前流程。",
            "未保存的流程", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Yes => UnsavedChangesDecision.Save,
            MessageBoxResult.No => UnsavedChangesDecision.Discard,
            _ => UnsavedChangesDecision.Cancel
        };
        return decision == UnsavedChangesDecision.Discard
            || (decision == UnsavedChangesDecision.Save && await SaveCurrentAsync());
    }

    public async Task PrepareCloseAsync()
    {
        IsClosing = true;
        _runCts?.Cancel();
        await CloseDebugSessionAsync();
        var commands = new IAsyncRelayCommand[]
        {
            RunWorkflowCommand, RunToSelectedNodeCommand, StartOrContinueDebugCommand, RunCachedDebugNodeCommand,
            SaveWorkflowCommand, SaveAsWorkflowCommand, DeleteSavedWorkflowCommand, NewWorkflowCommand,
            LoadSelectedWorkflowCommand, ValidateWorkflowCommand, ImportWorkflowCommand, ExportWorkflowCommand
        };
        await Task.WhenAll(commands.Select(command => command.ExecutionTask ?? Task.CompletedTask).Append(_refreshChoicesTask));
        _runCts?.Dispose();
        _runCts = null;
    }

    [ObservableProperty]
    private string _statusMessage = "就绪（进程内引擎已连接）";

    [ObservableProperty]
    private string _lastRunSummary = "尚未运行";

    partial void OnSearchKeywordChanged(string value) => RebuildFilteredToolbox();

    partial void OnSelectedNodeImageChanged(NodeImageChoice? value)
    {
        if (value is null)
        {
            return;
        }
        var bmp = VisionImageInterop.FromEncodedBytes(value.Image.Bytes);
        _imageViewer.SetImage(bmp, value.Label, fitToView: true);
        _imageViewer.SetOverlays(_latestOverlays);
    }

    private void BuildToolbox()
    {
        _allToolboxItems.Clear();
        foreach (var item in _host.NodeRegistry.Catalog.OrderBy(x => x.Category).ThenBy(x => x.DisplayName))
        {
            _allToolboxItems.Add(new ToolboxNodeItem(
                item.Type,
                VisionCatalogZhMapper.TranslateNodeName(item),
                item.Category,
                VisionCatalogZhMapper.TranslateCategory(item.Category),
                item.Description ?? string.Empty));
        }
        RebuildFilteredToolbox();
    }

    private void RebuildFilteredToolbox()
    {
        FilteredToolbox.Clear();
        var kw = SearchKeyword.Trim();
        var hasKeyword = !string.IsNullOrEmpty(kw);
        var matched = !hasKeyword
            ? _allToolboxItems
            : _allToolboxItems.Where(x =>
                x.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || x.Type.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || x.CategoryZh.Contains(kw, StringComparison.OrdinalIgnoreCase));

        var index = 0;
        foreach (var group in matched.GroupBy(x => x.CategoryZh).OrderBy(g => g.Key))
        {
            var expand = hasKeyword || index == 0;
            FilteredToolbox.Add(new ToolboxCategoryGroup(group.Key, group.ToArray(), expand));
            index++;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchKeyword = string.Empty;

    public Task<bool> RefreshWorkflowChoices() => _refreshChoicesTask = RefreshWorkflowChoicesCoreAsync();

    private async Task<bool> RefreshWorkflowChoicesCoreAsync()
    {
        await _choicesGate.WaitAsync();
        try
        {
            var saved = await _host.WorkflowStore.ListAsync(CancellationToken.None);
            AvailableWorkflows.Clear();
            foreach (var doc in saved)
            {
                AvailableWorkflows.Add(new WorkflowFileChoice(doc.Id, $"[已保存] {doc.Name}", "Store"));
            }

            var samplesDir = _host.SamplesDirectory;
            if (Directory.Exists(samplesDir))
            {
                foreach (var file in Directory.EnumerateFiles(samplesDir, "*-workflow.json").OrderBy(x => x))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    AvailableWorkflows.Add(new WorkflowFileChoice(fileName, $"[示例] {fileName}", "Sample", file));
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _host.ReportError(ex, "刷新流程列表");
            StatusMessage = $"刷新流程列表失败: {ex.Message}";
            return false;
        }
        finally { _choicesGate.Release(); }
    }

    private void LoadDefaultOrFirstSample()
    {
        var linear = Path.Combine(_host.SamplesDirectory, "linear-workflow.json");
        if (File.Exists(linear))
        {
            LoadFromJsonFile(linear);
            return;
        }

        // 若未找到示例文件，创建一个最小的默认合成图像→阈值流程
        var fallback = new WorkflowDefinition(
            "wpf-default",
            "默认视觉检测流程",
            [
                new NodeDefinition("src-1", "image.synthetic", null, new NodePosition(80, 140), null),
                new NodeDefinition("thr-1", "image.threshold", null, new NodePosition(380, 140), null),
                new NodeDefinition("res-1", "flow.result", null, new NodePosition(680, 140), null)
            ],
            [
                new EdgeDefinition("e1", "src-1", "next", "thr-1", "exec", EdgeKinds.Control),
                new EdgeDefinition("e2", "src-1", "image", "thr-1", "image", EdgeKinds.Data),
                new EdgeDefinition("e3", "thr-1", "next", "res-1", "exec", EdgeKinds.Control)
            ]);
        LoadDefinition(fallback, resetRevision: true);
    }

    public void LoadFromJsonFile(string path)
    {
        var json = File.ReadAllText(path);
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonOptions);
        if (def is null) throw new InvalidOperationException("流程文件内容不能为空。");
        LoadDefinition(def, resetRevision: true);
        StatusMessage = $"已加载流程: {def.Name}";
    }

    public void LoadDefinition(WorkflowDefinition definition, bool resetRevision, string? revision = null)
    {
        LoadGraphSnapshot(definition);
        WorkflowId = definition.Id;
        WorkflowName = definition.Name;
        _loadedRevision = resetRevision ? null : revision;
        _history.Clear();
        ValidationIssues.Clear();
        _cleanDocument = JsonSerializer.Serialize(ExportCurrentDefinition(), JsonOptions);
        IsDirty = false;
        _ = CloseDebugSessionAsync();
        NotifyAvailability();
    }

    private void LoadGraphSnapshot(WorkflowDefinition definition)
    {
        var breakpoints = Nodes.Where(n => n.HasBreakpoint).Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = new ObservableCollection<WorkflowNodeViewModel>();
        var connections = new ObservableCollection<WorkflowConnectionViewModel>();
        WorkflowDocumentMapper.LoadIntoGraph(definition, _host.NodeRegistry, nodes, connections);
        _loadingDocument = true;
        try
        {
            foreach (var node in Nodes) node.PropertyChanged -= OnNodeChanged;
            Nodes.Clear();
            Connections.Clear();
            foreach (var node in nodes)
            {
                if (breakpoints.Contains(node.Id)) node.HasBreakpoint = true;
                Nodes.Add(node);
            }
            foreach (var connection in connections) Connections.Add(connection);
            SelectedNode = null;
            SelectedNodes.Clear();
            ClearRunOutput();
        }
        finally { _loadingDocument = false; }
    }

    private string SerializeSnapshot() => JsonSerializer.Serialize(ExportCurrentDefinition(), JsonOptions);

    private void PushUndoSnapshot(string actionName)
    {
        if (_loadingDocument) return;
        _history.Push(actionName, SerializeSnapshot());
        NotifyAvailability();
    }

    public WorkflowDefinition ExportCurrentDefinition()
        => WorkflowDocumentMapper.BuildDefinition(WorkflowId, WorkflowName, Nodes, Connections);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task LoadSelectedWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        var choice = SelectedWorkflowChoice;
        if (choice is null)
        {
            StatusMessage = "请先选择要打开的流程。";
            return;
        }
        if (!await ConfirmDiscardChangesAsync()) return;
        IsDocumentBusy = true;
        try
        {
            if (choice.SourceKind == "Sample" && choice.FilePath is not null)
            {
                LoadFromJsonFile(choice.FilePath);
                return;
            }
            var doc = await _host.WorkflowStore.LoadWithRevisionAsync(choice.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("所选流程已不存在，请刷新列表。");
            LoadDefinition(doc.Workflow, resetRevision: false, revision: doc.Revision);
            StatusMessage = $"已打开工作流: {doc.Workflow.Name}";
        }
        catch (Exception exception) { ReportError(exception, "打开流程"); }
        finally { IsDocumentBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task NewWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        if (!await ConfirmDiscardChangesAsync()) return;
        var fresh = new WorkflowDefinition(
            $"wf-{Guid.NewGuid():N}",
            "新建视觉流程",
            [],
            []);
        LoadDefinition(fresh, resetRevision: true);
        _imageViewer.SetImage(null, "已新建空白工作流");
        _imageViewer.SetOverlays([]);
        StatusMessage = "已新建空白工作流";
        IsDirty = true;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddNodeFromToolbox(ToolboxNodeItem? item) => AddNodeFromToolboxAt(item, null);

    public WorkflowNodeViewModel? AddNodeFromToolboxAt(ToolboxNodeItem? item, Point? location)
    {
        if (!EnsureEditable() || item is null)
        {
            return null;
        }

        var catalog = _host.NodeRegistry.Catalog.FirstOrDefault(x => x.Type.Equals(item.Type, StringComparison.OrdinalIgnoreCase));
        var index = 1;
        var shortPrefix = item.Type.Split('.').LastOrDefault() ?? "node";
        string id;
        do
        {
            id = $"{shortPrefix}-{index++}";
        } while (Nodes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

        PushUndoSnapshot($"添加节点 {item.DisplayName}");
        var pos = location ?? new Point(120 + (Nodes.Count % 5) * 60, 120 + (Nodes.Count % 5) * 45);
        var nodeVm = new WorkflowNodeViewModel(id, item.Type, catalog, null, pos, null);
        Nodes.Add(nodeVm);
        SelectedNode = nodeVm;
        StatusMessage = $"已添加节点: {nodeVm.Title} ({nodeVm.Id})";
        return nodeVm;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteSelectedNodes()
    {
        if (!EnsureEditable()) return;
        var targets = SelectedNodes.Count > 0
            ? SelectedNodes.ToArray()
            : (SelectedNode is not null ? [SelectedNode] : []);

        if (targets.Length == 0)
        {
            return;
        }

        PushUndoSnapshot($"删除 {targets.Length} 个节点");
        foreach (var node in targets)
        {
            var toRemove = Connections.Where(c => ReferenceEquals(c.Source.Node, node) || ReferenceEquals(c.Target.Node, node)).ToArray();
            foreach (var conn in toRemove)
            {
                RemoveConnection(conn);
            }
            Nodes.Remove(node);
        }

        SelectedNode = null;
        StatusMessage = $"已删除 {targets.Length} 个节点";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo) return;
        var current = SerializeSnapshot();
        var entry = _history.PopUndo(current);
        if (entry is null) return;
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(entry.SnapshotJson, JsonOptions);
        if (def is not null)
        {
            LoadGraphSnapshot(def);
            NotifyDocumentChanged();
            StatusMessage = $"已撤销：{entry.ActionName}";
        }
        NotifyAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (!CanRedo) return;
        var current = SerializeSnapshot();
        var entry = _history.PopRedo(current);
        if (entry is null) return;
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(entry.SnapshotJson, JsonOptions);
        if (def is not null)
        {
            LoadGraphSnapshot(def);
            NotifyDocumentChanged();
            StatusMessage = $"已重做：{entry.ActionName}";
        }
        NotifyAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void CopySelectedNodes()
    {
        var targets = SelectedNodes.Count > 0
            ? SelectedNodes.ToArray()
            : (SelectedNode is not null ? [SelectedNode] : []);
        if (targets.Length == 0)
        {
            StatusMessage = "请先选中要复制的节点。";
            return;
        }
        _clipboard.Copy(targets, Connections);
        NotifyAvailability();
        StatusMessage = $"已复制 {targets.Length} 个节点";
    }

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteNodes()
    {
        if (!EnsureEditable() || !_clipboard.HasContent) return;
        var (pastedNodes, pastedEdges) = _clipboard.CreatePaste(Nodes);
        if (pastedNodes.Count == 0) return;

        PushUndoSnapshot($"粘贴 {pastedNodes.Count} 个节点");
        var catalogByType = _host.NodeRegistry.Catalog.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);
        var addedMap = new Dictionary<string, WorkflowNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        SelectedNodes.Clear();
        foreach (var node in pastedNodes)
        {
            catalogByType.TryGetValue(node.Type, out var catalog);
            var pos = node.Position is { } p ? new Point(p.X, p.Y) : new Point(120, 120);
            var vm = new WorkflowNodeViewModel(node.Id, node.Type, catalog, node.Name, pos, node.Parameters);
            Nodes.Add(vm);
            SelectedNodes.Add(vm);
            addedMap[node.Id] = vm;
            SelectedNode = vm;
        }

        foreach (var edge in pastedEdges)
        {
            if (!addedMap.TryGetValue(edge.SourceNodeId, out var src) || !addedMap.TryGetValue(edge.TargetNodeId, out var dst))
                continue;
            var fallback = edge.IsControl ? VisionDataType.Control : VisionDataType.Any;
            var srcPort = src.GetOrCreatePort(edge.SourcePort, isInput: false, fallback);
            var dstPort = dst.GetOrCreatePort(edge.TargetPort, isInput: true, fallback);
            srcPort.IsConnected = true;
            dstPort.IsConnected = true;
            Connections.Add(new WorkflowConnectionViewModel(edge.Id, srcPort, dstPort));
        }

        NotifyDocumentChanged();
        StatusMessage = $"已粘贴 {pastedNodes.Count} 个节点";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DuplicateSelectedNodes()
    {
        CopySelectedNodes();
        if (_clipboard.HasContent) PasteNodes();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AutoLayout()
    {
        if (!EnsureEditable() || Nodes.Count == 0) return;
        PushUndoSnapshot("自动排版");
        var positions = WorkflowAutoLayout.ComputePositions(Nodes, Connections);
        foreach (var node in Nodes)
        {
            if (positions.TryGetValue(node.Id, out var pos))
                node.Location = pos;
        }
        NotifyDocumentChanged();
        StatusMessage = $"已完成 {Nodes.Count} 个节点的拓扑自动排版";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void OpenNodeDetail(WorkflowNodeViewModel? node)
    {
        if (!EnsureEditable()) return;
        var target = node ?? SelectedNode;
        if (target is null)
        {
            return;
        }
        var before = SerializeSnapshot();
        _openDetailDialog(target, _latestNodeImages);
        var after = SerializeSnapshot();
        if (after != before)
        {
            _history.Push($"编辑节点 {target.Title}", before);
            NotifyDocumentChanged();
            NotifyAvailability();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ValidateWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        IsDocumentBusy = true;
        ValidationIssues.Clear();
        try
        {
            var def = ExportCurrentDefinition();
            WorkflowDocumentMapper.ValidateDefinition(def);
            var compiled = await _host.CompileAndValidateAsync(def);
            var maturityFindings = NodeMaturityGate.Scan(def);
            foreach (var finding in maturityFindings)
            {
                ValidationIssues.Add(new WorkflowValidationIssue(
                    finding.NodeId,
                    string.Equals(finding.Maturity, nameof(NodeMaturity.Experimental), StringComparison.OrdinalIgnoreCase) ? "Experimental" : "Preview",
                    $"节点 {finding.NodeId} ({finding.NodeType}) 成熟度为 {finding.Maturity}：{finding.Reason ?? "发布前需确认"}"));
            }
            StatusMessage = maturityFindings.Count == 0
                ? $"编译校验通过：共 {compiled.OrderedNodeIds.Count} 个可执行节点，{compiled.PipelineSegments.Count} 个融合管线段"
                : $"编译校验通过（含 {maturityFindings.Count} 条成熟度提示）：{compiled.OrderedNodeIds.Count} 个可执行节点";
        }
        catch (Exception ex)
        {
            var matchedNode = Nodes.FirstOrDefault(n => ex.Message.Contains(n.Id, StringComparison.OrdinalIgnoreCase))?.Id;
            ValidationIssues.Add(new WorkflowValidationIssue(matchedNode, "错误", ex.Message));
            StatusMessage = $"校验失败：{ex.Message}";
            _host.ReportError(ex, "校验流程");
        }
        finally { IsDocumentBusy = false; }
    }

    [RelayCommand]
    private void FocusValidationIssue(WorkflowValidationIssue? issue)
    {
        if (issue is null || string.IsNullOrWhiteSpace(issue.NodeId)) return;
        var node = Nodes.FirstOrDefault(n => n.Id.Equals(issue.NodeId, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;
        SelectedNode = node;
        SelectedNodes.Clear();
        SelectedNodes.Add(node);
        StatusMessage = $"已定位到节点 {node.Title} ({node.Id})";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveWorkflowAsync() => await SaveCurrentAsync();

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        var defaultId = $"{WorkflowId}-copy";
        var defaultName = $"{WorkflowName} 副本";
        var target = _promptSaveAs?.Invoke() ?? PromptSaveAsViaDialog(defaultId, defaultName);
        if (target is null) return;

        IsDocumentBusy = true;
        try
        {
            var def = ExportCurrentDefinition() with { Id = target.Value.Id, Name = target.Value.Name };
            WorkflowDocumentMapper.ValidateDefinition(def);
            var existingRev = await _host.WorkflowStore.GetRevisionAsync(target.Value.Id, CancellationToken.None);
            var outcome = await _host.WorkflowStore.SaveAsync(target.Value.Id, def, CancellationToken.None, existingRev ?? string.Empty);
            WorkflowId = target.Value.Id;
            WorkflowName = target.Value.Name;
            _loadedRevision = outcome.Revision;
            _cleanDocument = JsonSerializer.Serialize(ExportCurrentDefinition(), JsonOptions);
            NotifyDocumentChanged();
            await RefreshWorkflowChoices();
            StatusMessage = $"已另存为：{WorkflowName} ({WorkflowId})";
        }
        catch (Exception ex) { ReportError(ex, "另存流程"); }
        finally { IsDocumentBusy = false; }
    }

    private static (string Id, string Name)? PromptSaveAsViaDialog(string defaultId, string defaultName)
    {
        var dialog = new Views.InputPromptWindow(
            "另存工作流",
            "新流程 ID（字母/数字/-/_）",
            defaultId,
            "新流程名称",
            defaultName,
            "将当前画布保存为一条独立的新流程记录。",
            validate: (id, name, _) =>
            {
                if (id.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
                    return "流程 ID 只能包含字母、数字、连字符(-)与下划线(_)。";
                return string.IsNullOrWhiteSpace(name) ? "新流程名称不能为空。" : null;
            });
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? (dialog.PrimaryValue.Trim(), dialog.SecondaryValue.Trim()) : null;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task DeleteSavedWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        var choice = SelectedWorkflowChoice;
        if (choice is null || choice.SourceKind != "Store")
        {
            StatusMessage = "只能删除已保存到数据库的流程（示例流程不可删除）。";
            return;
        }
        if (MessageBox.Show($"确定要永久删除已保存流程“{choice.DisplayName}”吗？此操作无法撤销。",
            "确认删除流程", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        IsDocumentBusy = true;
        try
        {
            var deleted = await _host.WorkflowStore.DeleteAsync(choice.Id, CancellationToken.None);
            if (deleted && string.Equals(WorkflowId, choice.Id, StringComparison.OrdinalIgnoreCase))
            {
                _loadedRevision = null;
                IsDirty = true;
            }
            await RefreshWorkflowChoices();
            StatusMessage = deleted ? $"已删除流程：{choice.Id}" : $"未找到流程：{choice.Id}";
        }
        catch (Exception ex) { ReportError(ex, "删除流程"); }
        finally { IsDocumentBusy = false; }
    }

    public async Task<bool> SaveCurrentAsync()
    {
        if (IsDocumentBusy)
        {
            StatusMessage = "文档操作尚未结束，请稍后保存。";
            return false;
        }
        IsDocumentBusy = true;
        try
        {
            var def = ExportCurrentDefinition();
            WorkflowDocumentMapper.ValidateDefinition(def);
            var outcome = await _host.WorkflowStore.SaveAsync(WorkflowId, def, CancellationToken.None, _loadedRevision ?? string.Empty);
            _loadedRevision = outcome.Revision;
            _cleanDocument = JsonSerializer.Serialize(def, JsonOptions);
            NotifyDocumentChanged();
            var refreshed = await RefreshWorkflowChoices();
            StatusMessage = refreshed
                ? $"工作流已保存：{WorkflowName} (修订 {outcome.Revision[..8]})"
                : $"流程已保存，但刷新列表失败；修订 {outcome.Revision[..8]}，请重新打开列表。";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            _host.ReportError(ex, "保存流程");
            return false;
        }
        finally { IsDocumentBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RunToSelectedNodeAsync()
    {
        var target = SelectedNode;
        if (target is null)
        {
            StatusMessage = "请先在画布上选中一个目标节点。";
            return;
        }
        await ExecuteRunAsync(
            new VisionRunOptions(DebugRunMode.RunNode, TargetNodeId: target.Id, CaptureNodeImages: true),
            $"正在运行到节点：{target.Title} ({target.Id})...");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ToggleBreakpoint()
    {
        var targets = SelectedNodes.Count > 0
            ? SelectedNodes.ToArray()
            : (SelectedNode is not null ? [SelectedNode] : []);
        if (targets.Length == 0)
        {
            StatusMessage = "请先选中节点以切换断点。";
            return;
        }
        foreach (var node in targets) node.HasBreakpoint = !node.HasBreakpoint;
        var bpCount = Nodes.Count(n => n.HasBreakpoint);
        StatusMessage = bpCount > 0
            ? $"当前已设置 {bpCount} 个断点"
            : "已清除所有断点";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task StartOrContinueDebugAsync()
    {
        if (!CanEdit) return;
        var breakpoints = Nodes.Where(n => n.HasBreakpoint).Select(n => n.Id).ToArray();
        if (!HasActiveDebugSession && breakpoints.Length == 0)
        {
            if (SelectedNode is null)
            {
                StatusMessage = "请先按 F9 在某个节点设置断点，或选中节点后再启动断点调试。";
                return;
            }
            SelectedNode.HasBreakpoint = true;
            breakpoints = [SelectedNode.Id];
        }

        IsBusy = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            if (!HasActiveDebugSession || string.IsNullOrWhiteSpace(_activeDebugSessionId))
            {
                foreach (var node in Nodes) node.ResetExecutionState();
                ClearRunOutput();
                var def = ExportCurrentDefinition();
                var options = new VisionRunOptions(
                    DebugRunMode.Breakpoints,
                    Breakpoints: breakpoints,
                    CaptureNodeImages: true);
                StatusMessage = "正在启动断点调试会话...";
                var (sessionId, result) = await Task.Run(() => _host.DebugSessions.CreateAsync(def, options, ct), ct);
                _activeDebugSessionId = sessionId;
                HasActiveDebugSession = true;
                ApplyDebugResult(result);
            }
            else
            {
                StatusMessage = "正在从断点继续执行...";
                var result = await Task.Run(() => _host.DebugSessions.ContinueAsync(_activeDebugSessionId, ct), ct);
                ApplyDebugResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "断点调试已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"调试异常：{ex.Message}";
            _host.ReportError(ex, "断点调试");
            await CloseDebugSessionAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunCachedDebugNode))]
    private async Task RunCachedDebugNodeAsync()
    {
        if (!HasActiveDebugSession || string.IsNullOrWhiteSpace(_activeDebugSessionId) || SelectedNode is null)
            return;

        IsBusy = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var target = SelectedNode;
        try
        {
            StatusMessage = $"正在使用缓存上游重跑单节点：{target.Title}...";
            var result = await Task.Run(() => _host.DebugSessions.RunNodeAsync(_activeDebugSessionId, target.Id, ct), ct);
            ApplyDebugResult(result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"缓存单步执行失败：{ex.Message}";
            _host.ReportError(ex, "缓存单步调试");
        }
        finally { IsBusy = false; }
    }

    private bool CanRunCachedDebugNode() => CanEdit && HasActiveDebugSession && SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(HasActiveDebugSession))]
    private async Task StopDebugSessionAsync()
    {
        await CloseDebugSessionAsync();
        foreach (var node in Nodes)
        {
            if (node.ExecutionState == NodeExecutionVisualState.Paused)
                node.ExecutionState = NodeExecutionVisualState.Idle;
        }
        StatusMessage = "已结束断点调试会话";
    }

    private async Task CloseDebugSessionAsync()
    {
        var sessionId = _activeDebugSessionId;
        _activeDebugSessionId = null;
        HasActiveDebugSession = false;
        IsDebugHalted = false;
        DebugSessionStatusText = "未开启断点调试";
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            try { await _host.DebugSessions.DeleteAsync(sessionId); }
            catch { /* 会话可能已过期释放 */ }
        }
    }

    private void ApplyDebugResult(WorkflowRunResult result)
    {
        ApplyRunResult(result);
        var isHalted = string.Equals(result.DebugState, "Breakpoint", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(result.HaltNodeId);
        if (isHalted && !string.IsNullOrWhiteSpace(result.HaltNodeId))
        {
            IsDebugHalted = true;
            var haltedNode = Nodes.FirstOrDefault(n => n.Id.Equals(result.HaltNodeId, StringComparison.OrdinalIgnoreCase));
            if (haltedNode is not null)
            {
                haltedNode.ExecutionState = NodeExecutionVisualState.Paused;
                haltedNode.StatusBadgeText = "断点暂停";
                SelectedNode = haltedNode;
            }
            DebugSessionStatusText = $"断点暂停于 {result.HaltNodeId}（可继续或改参后单步重跑）";
            StatusMessage = DebugSessionStatusText;
        }
        else
        {
            _ = CloseDebugSessionAsync();
            DebugSessionStatusText = result.Success ? "调试会话已完成" : $"调试会话结束：{result.Error}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RunWorkflowAsync()
        => await ExecuteRunAsync(new VisionRunOptions(DebugRunMode.Full, CaptureNodeImages: true), "正在进程内执行工作流...");

    private async Task ExecuteRunAsync(VisionRunOptions baseOptions, string startMessage)
    {
        if (!CanEdit)
        {
            return;
        }

        await CloseDebugSessionAsync();
        IsBusy = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var generation = ++_runGeneration;

        foreach (var node in Nodes)
        {
            node.ResetExecutionState();
        }
        ClearRunOutput();
        LastRunStateKey = "Running";
        LastRunSummary = startMessage;
        StatusMessage = startMessage;

        try
        {
            var def = ExportCurrentDefinition();
            var progressOptions = baseOptions with
            {
                CaptureNodeImages = true,
                ProgressObserver = evt =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (generation != _runGeneration || !IsBusy) return;
                        var node = Nodes.FirstOrDefault(n => n.Id.Equals(evt.NodeId, StringComparison.OrdinalIgnoreCase));
                        if (node is null)
                        {
                            return;
                        }
                        if (evt.State == "Running")
                        {
                            node.ExecutionState = NodeExecutionVisualState.Running;
                            node.StatusBadgeText = "运行中";
                        }
                        else if (evt.Success is { } ok)
                        {
                            node.ExecutionState = ok ? NodeExecutionVisualState.Succeeded : NodeExecutionVisualState.Failed;
                            node.StatusBadgeText = ok && evt.DurationMs is { } ms ? $"{ms:0.0} ms" : (ok ? "完成" : "失败");
                        }
                    });
                }
            };

            var result = await Task.Run(() => _host.RunWorkflowAsync(def, progressOptions, ct), ct);
            ApplyRunResult(result);
        }
        catch (OperationCanceledException)
        {
            LastRunStateKey = "Warning";
            LastRunSummary = "已取消运行";
            StatusMessage = "工作流运行已取消";
        }
        catch (Exception ex)
        {
            LastRunStateKey = "Failed";
            LastRunSummary = $"异常：{ex.Message}";
            StatusMessage = $"运行异常：{ex.Message}";
            _host.ReportError(ex, "运行流程");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        _runCts?.Cancel();
        StatusMessage = "正在取消运行...";
    }

    private void ReportError(Exception exception, string operation)
    {
        _host.ReportError(exception, operation);
        StatusMessage = $"{operation}失败：{exception.Message}";
    }

    public void InspectReportNode(NodeRunReport? report)
    {
        if (report is null) return;
        var node = Nodes.FirstOrDefault(n => n.Id.Equals(report.NodeId, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;
        SelectedNode = node;
        if (CanEdit) OpenNodeDetail(node);
    }

    private void ClearRunOutput()
    {
        SelectedNodeImage = null;
        NodeImageChoices.Clear();
        LatestNodeReports.Clear();
        _latestNodeImages = [];
        _latestOverlays = [];
        _imageViewer.SetImage(null, "尚无当前运行图像");
        _imageViewer.SetOverlays([]);
        LastRunSummary = "尚未运行";
        LastRunStateKey = "Idle";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ImportWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        var dialog = new OpenFileDialog { Filter = "流程 JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog() != true || !await ConfirmDiscardChangesAsync()) return;
        IsDocumentBusy = true;
        try
        {
            LoadFromJsonFile(dialog.FileName);
            IsDirty = true;
        }
        catch (Exception exception) { ReportError(exception, "导入流程"); }
        finally { IsDocumentBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportWorkflowAsync()
    {
        if (!EnsureEditable()) return;
        var dialog = new SaveFileDialog { Filter = "流程 JSON|*.json", FileName = $"{WorkflowId}.json" };
        if (dialog.ShowDialog() != true) return;
        IsDocumentBusy = true;
        try
        {
            await DesktopFileWriter.WriteAsync(dialog.FileName, JsonSerializer.SerializeToUtf8Bytes(ExportCurrentDefinition(), JsonOptions));
            StatusMessage = $"已导出流程：{dialog.FileName}";
        }
        catch (Exception exception) { ReportError(exception, "导出流程"); }
        finally { IsDocumentBusy = false; }
    }

    private void ApplyRunResult(WorkflowRunResult result)
    {
        _latestOverlays = result.Overlays;
        _latestNodeImages = result.NodeImages ?? [];

        foreach (var report in result.NodeReports)
        {
            LatestNodeReports.Add(report);
            var node = Nodes.FirstOrDefault(n => n.Id.Equals(report.NodeId, StringComparison.OrdinalIgnoreCase));
            node?.ApplyReport(report);
        }

        foreach (var img in _latestNodeImages)
        {
            var nodeName = Nodes.FirstOrDefault(n => n.Id.Equals(img.NodeId, StringComparison.OrdinalIgnoreCase))?.Title ?? img.NodeId;
            NodeImageChoices.Add(new NodeImageChoice($"{nodeName}.{img.PortName} ({img.Width}×{img.Height})", img));
        }

        if (result.PreviewAvailable && result.PreviewJpeg is not null)
        {
            var bmp = VisionImageInterop.FromEncodedBytes(result.PreviewJpeg);
            _imageViewer.SetImage(bmp, $"输出预览 ({result.QualityDisposition ?? "OK"})", fitToView: true);
            _imageViewer.SetOverlays(result.Overlays);
        }
        else if (NodeImageChoices.Count > 0)
        {
            SelectedNodeImage = NodeImageChoices[^1];
        }
        else
        {
            _imageViewer.SetOverlays(result.Overlays);
        }

        LastRunSummary = result.Success
            ? $"成功 · 耗时 {result.TotalDurationMs:0.0} ms · 判定 {result.QualityDisposition ?? "OK"} · 标注 {result.Overlays.Count}"
            : $"失败 ({result.ErrorCode ?? "error"}) · {result.Error}";
        LastRunStateKey = result.Success ? "Succeeded" : "Failed";
        StatusMessage = LastRunSummary;
    }

    private void ConnectPorts(ConnectorViewModel source, ConnectorViewModel target)
    {
        if (!CanEdit)
        {
            StatusMessage = "操作执行期间不能修改连线。";
            return;
        }
        if (Connections.Any(c => ReferenceEquals(c.Source, source) && ReferenceEquals(c.Target, target)))
        {
            return;
        }

        PushUndoSnapshot($"连接 {source.Node.Title}.{source.PortName} → {target.Node.Title}.{target.PortName}");
        // 控制/数据端口：同一输入数据端口若已有连线，先替换旧连线避免歧义
        if (!target.IsControl)
        {
            var existing = Connections.FirstOrDefault(c => ReferenceEquals(c.Target, target));
            if (existing is not null)
            {
                RemoveConnection(existing);
            }
        }

        var conn = new WorkflowConnectionViewModel($"e-{Guid.NewGuid():N}"[..10], source, target);
        source.IsConnected = true;
        target.IsConnected = true;
        Connections.Add(conn);
    }

    private void DisconnectConnector(ConnectorViewModel? connector)
    {
        if (connector is null)
        {
            return;
        }

        var toRemove = Connections.Where(c => ReferenceEquals(c.Source, connector) || ReferenceEquals(c.Target, connector)).ToArray();
        if (toRemove.Length == 0) return;

        PushUndoSnapshot($"断开端口 {connector.Node.Title}.{connector.PortName}");
        foreach (var conn in toRemove)
        {
            RemoveConnection(conn);
        }
    }

    public void RemoveConnection(WorkflowConnectionViewModel conn)
    {
        Connections.Remove(conn);
        conn.Source.IsConnected = Connections.Any(c => ReferenceEquals(c.Source, conn.Source));
        conn.Target.IsConnected = Connections.Any(c => ReferenceEquals(c.Target, conn.Target));
    }
}
