using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStudio.Abstractions;
using VisionStudio.Api;
using VisionStudio.Engine;
using VisionStudio.Wpf.Localization;

namespace VisionStudio.Wpf.ViewModels;

/// <summary>节点在画布上的运行视觉状态。</summary>
public enum NodeExecutionVisualState
{
    Idle,
    Running,
    Paused,
    Succeeded,
    Failed
}

/// <summary>流程校验或成熟度告警条目。</summary>
public sealed record WorkflowValidationIssue(
    string? NodeId,
    string Severity,
    string Message);

/// <summary>
/// 节点输入/输出端口 ViewModel，绑定到 Nodify <c>Connector</c>。
/// </summary>
public sealed partial class ConnectorViewModel : ObservableObject
{
    public ConnectorViewModel(
        WorkflowNodeViewModel node,
        string portName,
        VisionDataType dataType,
        bool isInput,
        bool required)
    {
        Node = node;
        PortName = portName;
        DataType = dataType;
        IsInput = isInput;
        Required = required;
        IsControl = dataType == VisionDataType.Control;
        TypeDisplay = VisionCatalogZhMapper.TranslateDataType(dataType);
        Title = IsControl
            ? (isInput ? "执行" : portName)
            : $"{portName} ({TypeDisplay})";
    }

    public WorkflowNodeViewModel Node { get; }
    public string PortName { get; }
    public VisionDataType DataType { get; }
    public bool IsInput { get; }
    public bool Required { get; }
    public bool IsControl { get; }
    public string TypeDisplay { get; }
    public string Title { get; }
    public string TooltipText => IsControl
        ? $"{PortName} · 控制流 ({(IsInput ? "输入" : "输出")})"
        : $"{PortName} · {TypeDisplay} · {(IsInput ? "输入" : "输出")}{(Required ? "（必填）" : "")}";

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>判断两个端口是否允许建立连接。</summary>
    public static bool CanConnect(ConnectorViewModel a, ConnectorViewModel b)
    {
        if (ReferenceEquals(a, b) || ReferenceEquals(a.Node, b.Node))
        {
            return false;
        }
        if (a.IsInput == b.IsInput)
        {
            return false;
        }

        var source = a.IsInput ? b : a;
        var target = a.IsInput ? a : b;

        if (source.IsControl || target.IsControl)
        {
            return source.IsControl && target.IsControl;
        }

        return source.DataType == target.DataType
            || source.DataType == VisionDataType.Any
            || target.DataType == VisionDataType.Any;
    }
}

/// <summary>
/// 画布上的精简节点 ViewModel：仅呈现节点名称、紧凑状态及端口，详细参数在双击弹出的详情窗口编辑。
/// </summary>
public sealed partial class WorkflowNodeViewModel : ObservableObject
{
    public WorkflowNodeViewModel(
        string id,
        string type,
        NodeCatalogItem? catalog,
        string? customName,
        Point location,
        Dictionary<string, JsonElement>? parameters)
    {
        Id = id;
        Type = type;
        Catalog = catalog;
        CategoryZh = catalog is not null ? VisionCatalogZhMapper.TranslateCategory(catalog.Category) : "扩展节点";
        DefaultDisplayName = catalog is not null
            ? VisionCatalogZhMapper.TranslateNodeName(catalog)
            : VisionCatalogZhMapper.TranslateNodeName(type);
        _title = string.IsNullOrWhiteSpace(customName) ? DefaultDisplayName : customName;
        _location = location;
        Parameters = parameters is not null
            ? new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (catalog is not null)
        {
            foreach (var input in catalog.Inputs)
            {
                Inputs.Add(new ConnectorViewModel(this, input.Name, input.DataType, isInput: true, input.Required));
            }
            foreach (var output in catalog.Outputs)
            {
                Outputs.Add(new ConnectorViewModel(this, output.Name, output.DataType, isInput: false, output.Required));
            }
        }
    }

    public string Id { get; }
    public string Type { get; }
    public NodeCatalogItem? Catalog { get; }
    public string CategoryZh { get; }
    public string DefaultDisplayName { get; }
    public NodeMaturity Maturity => Catalog?.Maturity ?? NodeMaturity.Stable;
    public bool IsNonStableMaturity => Maturity != NodeMaturity.Stable;
    public string MaturityBadge => Maturity switch
    {
        NodeMaturity.Preview => "Preview",
        NodeMaturity.Experimental => "Exp",
        _ => string.Empty
    };
    public Dictionary<string, JsonElement> Parameters { get; }
    public ObservableCollection<ConnectorViewModel> Inputs { get; } = [];
    public ObservableCollection<ConnectorViewModel> Outputs { get; } = [];

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private bool _hasBreakpoint;

    [ObservableProperty]
    private NodeExecutionVisualState _executionState = NodeExecutionVisualState.Idle;

    [ObservableProperty]
    private string _statusBadgeText = "待命";

    [ObservableProperty]
    private double? _lastDurationMs;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private NodeRunReport? _lastReport;

    /// <summary>根据节点类型和最新运行状态更新精简徽标文本。</summary>
    public void ApplyReport(NodeRunReport report)
    {
        LastReport = report;
        LastDurationMs = report.DurationMs;
        LastError = report.Error;
        ExecutionState = report.Success ? NodeExecutionVisualState.Succeeded : NodeExecutionVisualState.Failed;
        StatusBadgeText = report.Success
            ? $"{report.DurationMs:0.0} ms"
            : "失败";
    }

    public void ResetExecutionState()
    {
        ExecutionState = NodeExecutionVisualState.Idle;
        StatusBadgeText = "待命";
        LastDurationMs = null;
        LastError = null;
        LastReport = null;
    }

    /// <summary>确保按名称查找端口（兼容动态端口或缺失目录）。</summary>
    public ConnectorViewModel GetOrCreatePort(string portName, bool isInput, VisionDataType fallbackType)
    {
        var list = isInput ? Inputs : Outputs;
        var existing = list.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new ConnectorViewModel(this, portName, fallbackType, isInput, required: false);
        list.Add(created);
        return created;
    }
}

/// <summary>
/// 节点之间的连线 ViewModel。
/// </summary>
public sealed partial class WorkflowConnectionViewModel : ObservableObject
{
    public WorkflowConnectionViewModel(string edgeId, ConnectorViewModel source, ConnectorViewModel target)
    {
        EdgeId = edgeId;
        Source = source;
        Target = target;
        IsControl = source.IsControl || target.IsControl;
    }

    public string EdgeId { get; }
    public ConnectorViewModel Source { get; }
    public ConnectorViewModel Target { get; }
    public bool IsControl { get; }
}

/// <summary>
/// Nodify 拖拽连线时的临时连接上下文。
/// </summary>
public sealed partial class PendingConnectionViewModel : ObservableObject
{
    private readonly Action<ConnectorViewModel, ConnectorViewModel> _connectAction;

    public PendingConnectionViewModel(Action<ConnectorViewModel, ConnectorViewModel> connectAction)
    {
        _connectAction = connectAction;
        StartCommand = new RelayCommand<ConnectorViewModel?>(c => _source = c);
        FinishCommand = new RelayCommand<ConnectorViewModel?>(target =>
        {
            if (_source is not null && target is not null && ConnectorViewModel.CanConnect(_source, target))
            {
                var output = _source.IsInput ? target : _source;
                var input = _source.IsInput ? _source : target;
                _connectAction(output, input);
            }
            _source = null;
        });
    }

    [ObservableProperty]
    private ConnectorViewModel? _source;

    public IRelayCommand<ConnectorViewModel?> StartCommand { get; }
    public IRelayCommand<ConnectorViewModel?> FinishCommand { get; }
}

/// <summary>
/// <see cref="WorkflowDefinition"/> 与 WPF 画布 ViewModel 之间的无损双向映射器。
/// </summary>
public static class WorkflowDocumentMapper
{
    public static void ValidateDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowStore.EnsureSupportedSchema(definition, definition.Id);
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Nodes is null || definition.Edges is null)
        {
            throw new InvalidOperationException("流程必须包含 ID、节点和连线列表。");
        }
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Type)
                || !nodeIds.Add(node.Id))
            {
                throw new InvalidOperationException("节点 ID/类型不能为空，节点 ID 不能重复。");
            }
        }
        var edgeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in definition.Edges)
        {
            if (edge is null || string.IsNullOrWhiteSpace(edge.Id) || !edgeIds.Add(edge.Id)
                || !nodeIds.Contains(edge.SourceNodeId) || !nodeIds.Contains(edge.TargetNodeId)
                || string.IsNullOrWhiteSpace(edge.SourcePort) || string.IsNullOrWhiteSpace(edge.TargetPort)
                || (edge.Kind is not null && edge.Kind != EdgeKinds.Control && edge.Kind != EdgeKinds.Data))
            {
                throw new InvalidOperationException($"连线 {edge?.Id} 的 ID、节点、端口或类型无效。");
            }
        }
    }

    public static void LoadIntoGraph(
        WorkflowDefinition definition,
        VisionNodeRegistry registry,
        ObservableCollection<WorkflowNodeViewModel> nodes,
        ObservableCollection<WorkflowConnectionViewModel> connections)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        ValidateDefinition(definition);

        nodes.Clear();
        connections.Clear();

        var catalogByType = registry.Catalog.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);
        var nodeMap = new Dictionary<string, WorkflowNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < definition.Nodes.Count; i++)
        {
            var node = definition.Nodes[i];
            catalogByType.TryGetValue(node.Type, out var catalog);
            var location = node.Position is { } pos
                ? new Point(pos.X, pos.Y)
                : new Point(60 + (i % 4) * 280, 80 + (i / 4) * 180);

            var vm = new WorkflowNodeViewModel(node.Id, node.Type, catalog, node.Name, location, node.Parameters);
            nodes.Add(vm);
            nodeMap[node.Id] = vm;
        }

        foreach (var edge in definition.Edges)
        {
            if (!nodeMap.TryGetValue(edge.SourceNodeId, out var srcNode)
                || !nodeMap.TryGetValue(edge.TargetNodeId, out var dstNode))
            {
                throw new InvalidOperationException($"连线 {edge.Id} 引用了不存在的节点。");
            }

            var fallbackType = edge.IsControl ? VisionDataType.Control : VisionDataType.Any;
            var srcPort = srcNode.GetOrCreatePort(edge.SourcePort, isInput: false, fallbackType);
            var dstPort = dstNode.GetOrCreatePort(edge.TargetPort, isInput: true, fallbackType);

            srcPort.IsConnected = true;
            dstPort.IsConnected = true;
            connections.Add(new WorkflowConnectionViewModel(edge.Id, srcPort, dstPort));
        }
    }

    public static WorkflowDefinition BuildDefinition(
        string workflowId,
        string workflowName,
        IEnumerable<WorkflowNodeViewModel> nodes,
        IEnumerable<WorkflowConnectionViewModel> connections)
    {
        var nodeDtos = nodes.Select(n =>
        {
            var customName = string.Equals(n.Title, n.DefaultDisplayName, StringComparison.Ordinal)
                ? null
                : n.Title;
            var clonedParams = n.Parameters.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            return new NodeDefinition(
                n.Id,
                n.Type,
                customName,
                new NodePosition(Math.Round(n.Location.X, 1), Math.Round(n.Location.Y, 1)),
                clonedParams);
        }).ToArray();

        var edgeDtos = connections.Select(c => new EdgeDefinition(
            c.EdgeId,
            c.Source.Node.Id,
            c.Source.PortName,
            c.Target.Node.Id,
            c.Target.PortName,
            c.IsControl ? EdgeKinds.Control : EdgeKinds.Data)).ToArray();

        return new WorkflowDefinition(workflowId, workflowName, nodeDtos, edgeDtos);
    }

    public static string FormatValueSummary(object? value) => value switch
    {
        null => "null",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "是 (true)" : "否 (false)",
        _ => value.ToString() ?? string.Empty
    };
}
