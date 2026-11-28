namespace VisionStudio.Abstractions;

public interface IVisionNodeExecutor
{
    string Type { get; }
    ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken);
}

/// <summary>Checks node-specific parameters and wiring during compilation, without executing the node.</summary>
public interface IVisionNodeDefinitionValidator
{
    void ValidateDefinition(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs);
}

public static class NodeSummaryKeys
{
    public const string AlgorithmBackend = "algorithmBackend";
    public const string InferenceBackend = "inferenceBackend";
    public const string RecognitionBackend = "recognitionBackend";
    public const string CalibrationBackend = "calibrationBackend";
    public const string MatchingBackend = "matchingBackend";
    public const string ScriptBackend = "scriptBackend";
    public const string CaliperDiagnosticsAvailable = "caliperDiagnosticsAvailable";
    public const string CaliperEdgeDiagnosticsAvailable = "caliperEdgeDiagnosticsAvailable";
    public const string DispositionScope = "dispositionScope";
    public const string BranchDisposition = "branchDisposition";
    public const string ParallelDisposition = "parallelDisposition";
    public const string BranchDispositions = "branchDispositions";
    public const string ParallelRegionId = "parallelRegionId";
    public const string ParallelBranch = "parallelBranch";
    public const string DispositionSetBy = "dispositionSetBy";
    public const string DispositionPrevious = "dispositionPrevious";
    public const string DispositionReset = "dispositionReset";
}

public sealed record NodeExecutorResult(
    IReadOnlyDictionary<string, VisionValue> Outputs,
    IReadOnlyDictionary<string, object?> Summary,
    IReadOnlyList<VisionOverlay>? Overlays = null);

public sealed class NodeExecutionContext
{
    private readonly IReadOnlyDictionary<string, VisionValue> _inputs;

    public NodeExecutionContext(IReadOnlyDictionary<string, VisionValue> inputs)
        : this(inputs, workflowScope: null)
    {
    }

    /// <param name="workflowScope">
    /// Identity of the compiled workflow plan being executed (workflow id + content fingerprint + catalog revision).
    /// Host-owned executors key per-node instance caches by this scope, so two workflows that reuse a node id never
    /// share a cached model/template/state. Null means the caller does not track a workflow identity.
    /// </param>
    public NodeExecutionContext(IReadOnlyDictionary<string, VisionValue> inputs, string? workflowScope)
        : this(inputs, workflowScope, runId: null)
    {
    }

    public NodeExecutionContext(IReadOnlyDictionary<string, VisionValue> inputs, string? workflowScope, string? runId)
        : this(inputs, workflowScope, runId, modelAssets: null)
    {
    }

    /// <param name="modelAssets">
    /// 模型资产读取端口（P0）。为 null 时，引用 modelAssetId 的节点必须失败并说明宿主未提供资产源，
    /// 而不是回退到内联模型——静默换模型比报错危险得多。
    /// </param>
    public NodeExecutionContext(
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        string? runId,
        IModelAssetSource? modelAssets)
    {
        _inputs = inputs;
        WorkflowScope = workflowScope;
        RunId = runId;
        ModelAssets = modelAssets;
    }

    public IReadOnlyDictionary<string, VisionValue> Inputs => _inputs;

    /// <summary>Stable workflow identity for data that outlives a compiled plan.</summary>
    public string? WorkflowId { get; init; }

    /// <summary>True for single-node, partial or breakpoint executions.</summary>
    public bool IsDebugRun { get; init; }

    /// <summary>模型资产读取端口；由运行时从当前运行的宿主注入，节点不直接依赖 Api。</summary>
    public IModelAssetSource? ModelAssets { get; }

    /// <summary>Workflow plan identity of this execution; null when the caller does not track one.</summary>
    public string? WorkflowScope { get; }

    /// <summary>Identity of this run, distinct from the reusable compiled plan scope.</summary>
    public string? RunId { get; }

    /// <summary>
    /// 最近一层结构化循环（flow.while / flow.repeat / flow.forEach）的节点 id；循环体外为 null。
    /// 由运行时按编译期绑定写入，节点可用它证明自己作用于最内层循环，而不是沿用上一次运行的标记。
    /// </summary>
    public string? EnclosingLoopId { get; init; }

    /// <summary>节点开始执行时所属循环的零基迭代号；循环体外为 null。</summary>
    public int? LoopIteration { get; init; }

    public VisionValue RequireValue(string port)
    {
        if (!_inputs.TryGetValue(port, out var value))
            throw new InvalidOperationException($"Input '{port}' is missing.");
        return value;
    }

    public T Require<T>(string port) => RequireValue(port).Require<T>(port);

    public bool TryGetValue(string port, out VisionValue value) => _inputs.TryGetValue(port, out value!);

    public double RequireNumber(string port)
    {
        var value = RequireValue(port);
        if (value.Type is not (VisionDataType.Double or VisionDataType.Integer))
            throw new InvalidOperationException($"Input '{port}' has type {value.Type}; expected Double or Integer.");

        return value.Value switch
        {
            byte v => v,
            short v => v,
            int v => v,
            long v => v,
            float v => v,
            double v => v,
            decimal v => (double)v,
            _ => throw new InvalidOperationException($"Input '{port}' is not numeric (actual: {value.Value?.GetType().Name ?? "null"}).")
        };
    }
}
