using VisionStudio.Abstractions;

namespace VisionStudio.Engine;

/// <summary>
/// 内置节点的执行模式归类（2026-11-10 · 设计审计 P0-1 第一步）。
/// 把"类型 → Pipeline/WorkflowCore"从 VisionExecutionPolicy 的 if 链收敛到目录能力位：
/// 每个内置节点在发布目录时都会得到显式 ExecutionMode，策略层只读能力，不再猜类型。
/// 新内置节点若不在下表中，会显式落到 WorkflowCore（保守默认），并触发
/// NodeExecutionModeTests 的显式声明闸门，提醒作者做出选择。
/// </summary>
internal static class NodeExecutionModeCatalog
{
    private static readonly HashSet<string> PipelineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image.synthetic", "image.reduceDomain", "image.flatFieldCorrect", "image.dynamicThreshold", "image.difference",
        "image.threshold", "image.grayscale", "image.channel", "image.filter", "image.morphology", "image.crop",
        "image.resize", "image.rotate", "image.perspective", "image.register", "image.warpAffine", "image.colorConvert",
        "image.contrastNormalize", "image.illuminationNormalize", "image.sharpen", "image.clahe", "image.polarUnwrap",
        "image.bilateral", "image.whiteBalance", "image.qualityMetrics", "image.depthScale", "image.channelMix",
        "image.undistort",
        "contour.fromPoints", "contour.toPoints", "contour.convexity", "contour.matchXld",
        "robot.guidance2d", "robot.j4TcpCompensation"
    };

    private static readonly string[] PipelinePrefixes =
    [
        "region.", "roi.", "defect.", "image.blur.", "image.threshold.", "text.", "match.", "value.",
        "logic.", "collection.", "feature.", "measure.", "tolerance.", "geometry.", "calibration.", "coordinate."
    ];

    public static bool IsPipeline(string type) =>
        PipelineTypes.Contains(type) || PipelinePrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static NodeCatalogItem Apply(NodeCatalogItem item)
    {
        if (item.Capabilities?.ExecutionMode is { } declared && declared != VisionExecutionMode.Auto)
            return item;

        var mode = string.Equals(item.PluginId, "builtin", StringComparison.OrdinalIgnoreCase) && IsPipeline(item.Type)
            ? VisionExecutionMode.Pipeline
            : VisionExecutionMode.WorkflowCore;
        var capabilities = item.Capabilities ?? new VisionToolCapabilities();
        return item with { Capabilities = capabilities with { ExecutionMode = mode } };
    }
}
