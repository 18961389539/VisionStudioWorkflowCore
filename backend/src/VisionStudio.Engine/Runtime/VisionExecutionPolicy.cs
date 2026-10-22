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
            type.Equals("contour.fromPoints", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("region.", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.reduceDomain", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("defect.", StringComparison.OrdinalIgnoreCase) ||
            type is "image.flatFieldCorrect" or "image.dynamicThreshold" or "image.difference" ||
            type.Equals("image.threshold", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.grayscale", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.channel", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.filter", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("image.blur.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("image.threshold.", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.morphology", StringComparison.OrdinalIgnoreCase) ||
            type is "image.topHat" or "image.bottomHat" or "image.morphGradient" ||
            type.Equals("image.crop", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.resize", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.rotate", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.perspective", StringComparison.OrdinalIgnoreCase) ||
            type is "image.register" or "image.warpAffine" ||
            type is "image.colorConvert" or "image.contrastNormalize" or "image.illuminationNormalize" or "image.sharpen" ||
            type.StartsWith("text.", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("image.undistort", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("match.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("value.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("logic.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("collection.", StringComparison.OrdinalIgnoreCase) ||
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

    /// <summary>Non-Full debug modes cannot safely resume a cached node graph across loop iterations or Try retries.</summary>
    public static bool HasIterativeOrRetryControl(IEnumerable<NodeDefinition> nodes) =>
        nodes.Any(node => node.Type.Equals("flow.forEach", StringComparison.OrdinalIgnoreCase) ||
                          node.Type.Equals("flow.try", StringComparison.OrdinalIgnoreCase));
}
