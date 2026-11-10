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
        // 目录能力位是唯一事实源：内置节点在 BuiltInNodeCatalog 组装时已得到显式模式
        // （NodeExecutionModeCatalog），插件必须自行声明；未声明的一律走保守的 WorkflowCore。
        var declared = catalog.Capabilities?.ExecutionMode ?? VisionExecutionMode.Auto;
        return declared == VisionExecutionMode.Auto ? VisionExecutionMode.WorkflowCore : declared;
    }

    public static bool IsPipelineEligible(NodeCatalogItem catalog) =>
        Resolve(catalog) == VisionExecutionMode.Pipeline;

    /// <summary>Non-Full debug modes cannot safely resume a cached node graph across loop iterations or Try retries.</summary>
    public static bool HasIterativeOrRetryControl(IEnumerable<NodeDefinition> nodes) =>
        nodes.Any(node => FlowSemantics.IsIterativeOrRetry(node.Type));
}
