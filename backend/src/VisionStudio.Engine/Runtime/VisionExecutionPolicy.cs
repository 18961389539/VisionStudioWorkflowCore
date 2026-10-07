namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Decides whether a node should stay as an individual Workflow Core step or can be
/// fused into the lightweight in-process vision pipeline. Plugins default to the
/// conservative WorkflowCore path unless they explicitly opt in through capabilities.
/// </summary>
public static class VisionExecutionPolicy
{
    public static VisionExecutionMode Resolve(NodeCatalogItem catalog)
    {
        var declared = catalog.Capabilities?.ExecutionMode ?? VisionExecutionMode.Auto;
        if (declared != VisionExecutionMode.Auto)
            return declared;

        if (!string.Equals(catalog.PluginId, "builtin", StringComparison.OrdinalIgnoreCase))
            return VisionExecutionMode.WorkflowCore;

        var type = catalog.Type;
        if (type.Equals("image.synthetic", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.threshold", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("measure.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("geometry.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("calibration.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("coordinate.", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("robot.guidance2d", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("robot.j4TcpCompensation", StringComparison.OrdinalIgnoreCase))
            return VisionExecutionMode.Pipeline;

        return VisionExecutionMode.WorkflowCore;
    }

    public static bool IsPipelineEligible(NodeCatalogItem catalog) =>
        Resolve(catalog) == VisionExecutionMode.Pipeline;
}
