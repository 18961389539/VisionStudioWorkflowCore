using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Abstractions;

public sealed record WorkflowDefinition(
    string Id,
    string Name,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges)
{
    /// <summary>
    /// Persisted document schema version. Documents saved before this field existed deserialize to the
    /// current version: the layout is unchanged and the store only accepts what this host can interpret.
    /// A document from a newer host is rejected on load instead of being edited and re-saved with the
    /// unknown parts silently dropped (upgrade/rollback safety on the shop floor).
    /// </summary>
    public int SchemaVersion { get; init; } = WorkflowDocumentSchema.CurrentVersion;
}

public static class WorkflowDocumentSchema
{
    /// <summary>Schema written by this host. Bump when a field's meaning changes, not when fields are added.</summary>
    public const int CurrentVersion = 1;
}

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

public sealed record PortDescriptor(string Name, VisionDataType DataType, bool Required = true)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParameterVisibility? RequiredWhen { get; init; }

    public bool IsRequired(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (Required) return true;
        if (RequiredWhen is not { } condition) return false;
        var fallback = Convert.ToString(catalog.Parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals(condition.Parameter, StringComparison.OrdinalIgnoreCase))?.DefaultValue,
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var actual = node.GetString(condition.Parameter, fallback);
        return condition.Values.Any(expected => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ParameterOption(string Label, string Value);

public sealed record ParameterVisibility(string Parameter, IReadOnlyList<string> Values);

public sealed record ParameterPresentation(string? Label = null, string? Description = null,
    IReadOnlyList<ParameterOption>? Suggestions = null, double? Min = null, double? Max = null, double? Step = null,
    object? DefaultValue = null, string? Unit = null);

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
    bool Internal = false)
{
    /// <summary>Additional visibility conditions; every condition must match.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ParameterVisibility>? VisibleWhenAll { get; init; }

    /// <summary>Null or an omitted value selects the executor's context-dependent default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; init; }

    /// <summary>Require whole numbers; Step only controls editor increments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Integer { get; init; }

    /// <summary>
    /// 连接这些输入端口时忽略本参数（端口值优先）。用于 path/url/matrixA 等"参数或接线二选一"
    /// 的字段，编译器不再按节点类型判断。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OverriddenByPorts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariantParameter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, ParameterPresentation>? Variants { get; init; }
}

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
    IReadOnlyList<NodeResourceDeclaration>? Resources = null,
    /// <summary>
    /// 显式图像坐标帧语义。null = 未声明：输出沿输入帧（无输入时使用节点私有源帧）；
    /// 非空 = 按首条匹配规则解释，插件因此无需让宿主按节点类型字符串猜测。
    /// </summary>
    IReadOnlyList<NodeFrameRule>? Frames = null,
    /// <summary>
    /// 只输出 transform（不输出图像）的节点的帧语义。null = 未声明（宿主不再推断它的 Source/Target）。
    /// </summary>
    NodeTransformFrameDeclaration? TransformFrames = null,
    /// <summary>
    /// ROI / 几何 / 检测等非图像输出的帧来源。null = 未声明：仅有图像输出的节点按自身图像帧推断。
    /// </summary>
    IReadOnlyList<NodeOutputFrameRule>? OutputFrames = null,
    /// <summary>
    /// ROI 输入与图像输入的绑定（ROI 帧校验用）。null = 未声明：不校验该 ROI 输入。
    /// </summary>
    IReadOnlyList<NodeRoiImageBinding>? RoiImageBindings = null)
{
    /// <summary>该目录是否给出了显式动作声明（与"未声明、走兼容回退"区分）。</summary>
    public bool DeclaresActions => Actions is not null;

    /// <summary>该目录是否声明了图像坐标帧语义。</summary>
    public bool DeclaresFrames => Frames is not null;

    /// <summary>该目录是否声明了 transform 帧语义。</summary>
    public bool DeclaresTransformFrames => TransformFrames is not null;

    /// <summary>该目录是否声明了非图像输出的帧来源。</summary>
    public bool DeclaresOutputFrames => OutputFrames is not null;

    /// <summary>该目录是否声明了 ROI 与图像输入的绑定。</summary>
    public bool DeclaresRoiImageBindings => RoiImageBindings is not null;
}

/// <summary>
/// 图像输出帧策略：节点执行后其 image 输出落在哪个坐标帧。
/// </summary>
public enum NodeImageFramePolicy
{
    /// <summary>沿用输入图像帧；没有图像输入时使用 image:{nodeId}:source。</summary>
    PreserveInput,
    /// <summary>新建 image:{nodeId}:spatial（空间变换后无法再用输入像素坐标解释）。</summary>
    NewSpatialFrame,
    /// <summary>新建 image:{nodeId}:{FrameSuffix}，用于裁剪、去畸变、标定平面等有固定语义的帧。</summary>
    NewFixedFrame,
    /// <summary>使用 transform 输入的 TargetFrame；缺失时回退 image:{nodeId}:spatial。</summary>
    TransformTarget,
    /// <summary>沿用 InputPort 的输入帧；未连接时使用 FallbackFrame（可为 null）。</summary>
    InputFrame
}

/// <summary>
/// transform 输出的帧语义：声明 transform 端口的 SourceFrame/TargetFrame 如何补全。
/// </summary>
public enum NodeTransformFramePolicy
{
    None,
    /// <summary>Source = 输入图像帧，Target = 本节点图像输出帧。</summary>
    Spatial,
    /// <summary>Source = roi:{nodeId}:reference，Target = InputPort 的图像帧。</summary>
    RoiReference,
    /// <summary>Source = 输入图像帧，Target = ReferencePort 的图像帧。</summary>
    ReferenceInput,
    /// <summary>Source = 模板输入帧（或 match:{nodeId}:template），Target = 图像输入帧。</summary>
    TemplateToImage,
    /// <summary>Source = PrimaryPort 的 Source，Target = SecondaryPort 的 Target，并校验中间帧衔接。</summary>
    Compose,
    /// <summary>交换 PrimaryPort 的 Source/Target。</summary>
    Invert
}

/// <summary>
/// 只输出 transform 的节点的帧声明。与 <see cref="NodeFrameRule"/> 分开，避免让"图像输出策略"
/// 在纯坐标节点上出现无意义的取值。
/// </summary>
public sealed record NodeTransformFrameDeclaration(
    NodeTransformFramePolicy Policy,
    string PrimaryPort = "transform",
    string? SecondaryPort = null,
    string ImagePort = "image",
    string TemplatePort = "template");

/// <summary>
/// 非图像输出（ROI / 直线 / 圆 / 检测框 / 区域 / 轮廓）的帧来源。
/// </summary>
public enum NodeOutputFrameSource
{
    /// <summary>输入图像端口（InputPort，默认 image）的帧。</summary>
    ImageInput,
    /// <summary>本节点图像输出的帧（经图像帧声明解析）。</summary>
    ImageOutput,
    /// <summary>几何输入端口（InputPort）的帧，递归解析。</summary>
    GeometryInput,
    /// <summary>detections 输入端口（InputPort，默认 detections）的帧。</summary>
    DetectionInput,
    /// <summary>区域输入端口（InputPort，默认 region）的帧。</summary>
    RegionInput,
    /// <summary>轮廓输入端口（InputPort，默认 contours）的帧。</summary>
    ContourInput,
    /// <summary>拟合点输入（point1..N）所在的帧。</summary>
    FittedPoints
}

/// <summary>
/// 一条非图像输出帧规则。OutputPort 为 null 表示该节点的所有非图像输出；
/// 条件为空表示无条件匹配，否则按节点有效参数值匹配，规则按声明顺序取首条。
/// </summary>
public sealed record NodeOutputFrameRule(
    NodeOutputFrameSource Source,
    string? OutputPort = null,
    string? InputPort = null,
    string? WhenParameter = null,
    IReadOnlyList<string>? WhenValues = null);

/// <summary>ROI 输入端口与图像输入端口的绑定；ROI 的帧必须与该图像一致。</summary>
public sealed record NodeRoiImageBinding(string RoiPort, string ImagePort);

/// <summary>
/// 一条图像帧语义规则。条件为空表示无条件匹配；否则按节点有效参数值（节点值优先、目录默认值兜底）
/// 匹配 <see cref="WhenValues"/>，规则按声明顺序求值，首条匹配生效。
/// </summary>
public sealed record NodeFrameRule(
    NodeImageFramePolicy Policy,
    string InputPort = "image",
    string? FrameSuffix = null,
    string? FrameLabel = null,
    string? FallbackFrame = null,
    NodeTransformFramePolicy Transform = NodeTransformFramePolicy.None,
    string? ReferencePort = null,
    string? TransformPort = null,
    string? WhenParameter = null,
    IReadOnlyList<string>? WhenValues = null);
