namespace VisionStudio.Engine;

public enum NodeExecutionPhase
{
    Warmup,
    Run
}

public sealed record VisionValueSnapshot(
    string Type,
    string Display,
    object? Value = null);

public sealed record NodeRunReport(
    string NodeId,
    string NodeType,
    bool Success,
    double DurationMs,
    IReadOnlyDictionary<string, object?> Summary,
    string? Error = null,
    NodeExecutionPhase Phase = NodeExecutionPhase.Run,
    IReadOnlyDictionary<string, VisionValueSnapshot>? Inputs = null,
    IReadOnlyDictionary<string, VisionValueSnapshot>? Outputs = null,
    long ExecutionSequence = 0,
    double StartOffsetMs = 0,
    double EndOffsetMs = 0);

public enum DebugRunMode
{
    Full,
    RunNode,
    RunFromNode,
    Breakpoints
}

public sealed record VisionRunOptions(
    DebugRunMode Mode = DebugRunMode.Full,
    string? TargetNodeId = null,
    IReadOnlyList<string>? Breakpoints = null,
    int TimeoutMs = 10000,
    [property: System.Text.Json.Serialization.JsonIgnore] RunArtifactOptions? Artifacts = null,
    bool AllowSideEffects = false,
    bool CaptureNodeImages = false);

public sealed record ControlFlowDecision(
    string NodeId,
    string NodeType,
    IReadOnlyList<string> ActiveBranches,
    string? SelectedBranch = null,
    bool? Condition = null);

public sealed record ReplayInputArtifact(
    byte[] Png,
    string SourceNodeId,
    int Width,
    int Height);

/// <summary>
/// One image output captured while a run executes (interactive debugging support).
/// Encoded as JPEG at capture time because live Mats are disposed with the run data.
/// </summary>
public sealed record RunNodeImage(
    string NodeId,
    string PortName,
    byte[] Jpeg,
    int Width,
    int Height);

public sealed record WorkflowRunResult(
    string RunId,
    bool Success,
    double TotalDurationMs,
    bool PreviewAvailable,
    int PreviewWidth,
    int PreviewHeight,
    IReadOnlyList<NodeRunReport> NodeReports,
    IReadOnlyList<VisionOverlay> Overlays,
    byte[]? PreviewJpeg,
    string? Error = null,
    string DebugState = "Complete",
    string? HaltNodeId = null,
    string? HaltReason = null,
    string? QualityDisposition = null,
    IReadOnlyList<ControlFlowDecision>? ControlFlowDecisions = null,
    ReplayInputArtifact? ReplayInput = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public RunImageArtifacts? DeferredArtifacts { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Structured failure classification (<see cref="VisionRunErrorCodes"/>) for runs that did not complete
    /// normally. <see cref="Error"/> stays the human-readable message; trace records persist this code so
    /// timeout, cancellation, node faults and host faults remain distinguishable.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Per-node image outputs captured during the run (only when <see cref="VisionRunOptions.CaptureNodeImages"/>
    /// is enabled). Kept out of JSON payloads: images are served through the run image endpoints.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RunNodeImage>? NodeImages { get; init; }
}

/// <summary>Structured run failure codes carried by <see cref="WorkflowRunResult.ErrorCode"/>.</summary>
public static class VisionRunErrorCodes
{
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string NodeFailure = "node_failure";
    public const string RuntimeError = "runtime_error";
}

public sealed record DebugRunRequest(WorkflowDefinition Workflow, VisionRunOptions Options);
