namespace VisionStudio.Abstractions;

public interface IVisionNodeExecutor
{
    string Type { get; }
    ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken);
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
    {
        _inputs = inputs;
        WorkflowScope = workflowScope;
    }

    public IReadOnlyDictionary<string, VisionValue> Inputs => _inputs;

    /// <summary>Workflow plan identity of this execution; null when the caller does not track one.</summary>
    public string? WorkflowScope { get; }

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
