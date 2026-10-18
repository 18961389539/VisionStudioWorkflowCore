using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Abstractions;

public sealed record WorkflowDefinition(
    string Id,
    string Name,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges);

public sealed record NodeDefinition(
    string Id,
    string Type,
    string? Name,
    NodePosition? Position,
    Dictionary<string, JsonElement>? Parameters);

public sealed record NodePosition(double X, double Y);

public sealed record EdgeDefinition(
    string Id,
    string SourceNodeId,
    string SourcePort,
    string TargetNodeId,
    string TargetPort,
    string? Kind = null)
{
    public bool IsControl => string.Equals(Kind, "control", StringComparison.OrdinalIgnoreCase);
}

public sealed record PortDescriptor(string Name, VisionDataType DataType, bool Required = true);

public sealed record ParameterOption(string Label, string Value);

public sealed record ParameterVisibility(string Parameter, IReadOnlyList<string> Values);

public sealed record ParameterDescriptor(
    string Name,
    string Label,
    string Type,
    object? DefaultValue = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<ParameterOption>? Options = null,
    string? Unit = null,
    string? Group = null,
    string? Description = null,
    ParameterVisibility? VisibleWhen = null,
    /// <summary>
    /// 宿主注入或画布资产参数：不渲染到配置面板、不写入新节点默认值，
    /// 但必须在目录中声明，避免执行器读取"目录里不存在"的键。
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Internal = false);

public enum VisionExecutionMode
{
    Auto,
    WorkflowCore,
    Pipeline
}

public sealed record VisionToolCapabilities(
    bool SupportsRoi = false,
    bool EmitsOverlay = false,
    bool Deterministic = true,
    bool SupportsRunNode = true,
    bool SupportsParallel = true,
    VisionExecutionMode ExecutionMode = VisionExecutionMode.Auto);

/// <summary>
/// 扩展安全契约（2026-10-11 复审「插件动作契约未统一」整改）：显式动作分类（SideEffectKind）。
/// 节点是否驱动物理设备必须由**目录声明**表达，而不是由宿主硬编码节点类型字符串或
/// SupportsRunNode 能力位替代——插件扩展后两者会随入口各自分歧。
/// </summary>
public enum NodeActionKind
{
    /// <summary>写设备/PLC 标签。</summary>
    DeviceWrite,
    /// <summary>下发机器人运动指令。</summary>
    RobotCommand,
    /// <summary>触发相机快门/采集动作。</summary>
    CameraTrigger
}

/// <summary>
/// 扩展安全契约：完成证据契约——"已下发"与"已完成"的判定依据。
/// </summary>
public enum NodeActionCompletion
{
    /// <summary>同步完成：调用返回即物理动作完成（写标签、快门触发）。</summary>
    Synchronous,
    /// <summary>协议确认：必须有握手/到位证据才算完成；证据缺失时保守要求人工核对（机器人指令）。</summary>
    ProtocolConfirmed
}

/// <summary>
/// 一条显式动作声明：分类 + 完成证据（+ 可选参数条件）。声明进入统一授权/意图/完成度判定
/// （DeviceSideEffectClassifier / 调试审批 / 资源仲裁共用同一求值器）。
/// </summary>
/// <param name="WhenParameter">
/// 可选：仅当该节点参数取到指定值时动作才成立（例如"取图节点只在 triggerBeforeGrab=true 时才触发相机"）。
/// null = 恒成立；声明条件的动作按节点**有效参数值**（节点值优先、目录默认值兜底）求值。
/// </param>
public sealed record NodeActionDeclaration(
    NodeActionKind Kind,
    NodeActionCompletion Completion,
    string? WhenParameter = null,
    string? WhenValue = null);

/// <summary>
/// 动作声明的参数条件求值器——Engine（调试审批）与 Api（动作分类/意图/完成度）共用的单一事实源，
/// 避免"同一声明在不同入口各自解释"的分歧。
/// </summary>
public static class NodeActionContract
{
    /// <summary>该动作声明在给定节点上是否成立（无参数条件 ⇒ 恒成立）。</summary>
    public static bool Applies(NodeDefinition node, NodeCatalogItem catalog, NodeActionDeclaration action)
    {
        if (string.IsNullOrWhiteSpace(action.WhenParameter)) return true;
        var actual = ResolveParameter(node, catalog, action.WhenParameter);
        return string.Equals(Normalize(actual), Normalize(action.WhenValue), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>该节点是否存在成立的动作声明（按实际参数求值）。</summary>
    public static bool NodeHasAction(NodeDefinition node, NodeCatalogItem catalog)
        => catalog.Actions is { } actions && actions.Any(action => Applies(node, catalog, action));

    private static string? ResolveParameter(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        if (node.Parameters is not null && node.Parameters.TryGetValue(name, out var raw))
        {
            return raw.ValueKind switch
            {
                JsonValueKind.String => raw.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => raw.GetRawText(),
                _ => null
            };
        }
        return catalog.Parameters
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.DefaultValue?.ToString();
    }

    private static string? Normalize(string? value)
        => value is null ? null : bool.TryParse(value, out var flag) ? (flag ? "true" : "false") : value.Trim();
}

/// <summary>
/// 扩展安全契约：显式资源请求——声明承载硬件资源 ID 的参数名。
/// 仲裁/依赖捕获按声明收集资源，而不是按 cameraId/deviceId/robotId 参数名约定猜测；
/// 未声明资源的目录沿用既有参数名约定（兼容回退）。
/// Kind 取值：camera | device | robot | cameraSyncGroup（大小写不敏感）。
/// </summary>
public sealed record NodeResourceDeclaration(string Kind, string Parameter);

public sealed record NodeCatalogItem(
    string Type,
    string DisplayName,
    string Category,
    IReadOnlyList<PortDescriptor> Inputs,
    IReadOnlyList<PortDescriptor> Outputs,
    IReadOnlyList<ParameterDescriptor> Parameters,
    string? Description = null,
    string? PluginId = null,
    VisionToolCapabilities? Capabilities = null,
    /// <summary>
    /// 显式动作声明。null = 未声明（沿用宿主兼容分类）；空列表 = 明确声明无副作用；非空 = 声明的动作。
    /// </summary>
    IReadOnlyList<NodeActionDeclaration>? Actions = null,
    /// <summary>显式资源请求（声明承载资源 ID 的参数名）；null = 沿用参数名约定收集。</summary>
    IReadOnlyList<NodeResourceDeclaration>? Resources = null)
{
    /// <summary>该目录是否给出了显式动作声明（与"未声明、走兼容回退"区分）。</summary>
    public bool DeclaresActions => Actions is not null;
}
