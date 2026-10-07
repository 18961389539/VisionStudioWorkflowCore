using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Per-run in-memory state. Workflow Core owns scheduling; this object owns native vision data.
/// persistState=false is required because OpenCV Mat must never cross a persistence boundary.
/// </summary>
public sealed class VisionWorkflowData : IDisposable
{
    private readonly object _resourceGate = new();
    private readonly object _debugGate = new();
    private readonly List<IDisposable> _ownedResources = [];
    private readonly ConcurrentQueue<NodeRunReport> _nodeReports = new();
    private readonly ConcurrentQueue<VisionOverlay> _overlays = new();
    private readonly ConcurrentDictionary<string, bool> _branchConditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ControlFlowDecision> _controlFlowDecisions = new(StringComparer.OrdinalIgnoreCase);
    private long _timelineOrigin;
    private long _executionSequence;
    private HashSet<string> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _executedNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RunNodeImage> _nodeImages = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxNodeImages = 32;
    /// <summary>单次运行全部节点图像的 JPEG 字节总预算：高分辨率/多图像节点不能只靠张数上限约束内存。</summary>
    private const long MaxNodeImagesTotalBytes = 16L * 1024 * 1024;
    private long _nodeImageBytes;
    private bool _resumeSkipCompleted;
    private string? _forceExecuteNodeId;
    private WorkflowDefinition _definition = new("empty", "Empty", [], []);
    private IReadOnlyDictionary<string, NodeDefinition> _nodesById =
        new Dictionary<string, NodeDefinition>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<EdgeDefinition>> _incomingDataEdges =
        new Dictionary<string, IReadOnlyList<EdgeDefinition>>(StringComparer.OrdinalIgnoreCase);

    public WorkflowDefinition Definition
    {
        get => _definition;
        set
        {
            _definition = value;
            _nodesById = value.Nodes
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            _incomingDataEdges = value.Edges
                .Where(e => !WorkflowGraph.IsControlEdge(e))
                .GroupBy(e => e.TargetNodeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<EdgeDefinition>)x.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Identity of the compiled plan currently executing this data, set by the runner from its plan lease
    /// (workflow id + content fingerprint + catalog revision). Host-owned executors key PerNode plugin
    /// instances by it so two workflows that reuse a node id never share state; the plan cache releases
    /// the matching instances when the entry leaves the cache.
    /// </summary>
    public string? WorkflowScope { get; set; }

    public NodeDefinition RequireNode(string nodeId) => _nodesById.TryGetValue(nodeId, out var node)
        ? node
        : throw new InvalidOperationException($"Node '{nodeId}' was not found in the visual workflow definition.");

    public IReadOnlyList<EdgeDefinition> IncomingDataEdges(string nodeId) =>
        _incomingDataEdges.TryGetValue(nodeId, out var edges) ? edges : [];

    public ConcurrentDictionary<string, IReadOnlyDictionary<string, VisionValue>> OutputsByNode { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    // Retained for source compatibility with V0.28 plugins; the recursive compiler no longer reads
    // this global value because nested If nodes must not overwrite their parent's condition.
    public bool BranchCondition { get; set; }

    public void SetBranchCondition(string nodeId, bool condition)
    {
        BranchCondition = condition;
        _branchConditions[nodeId] = condition;
        _controlFlowDecisions[nodeId] = new ControlFlowDecision(
            nodeId,
            "flow.if",
            [condition ? "true" : "false"],
            condition ? "true" : "false",
            condition);
    }

    public bool GetBranchCondition(string nodeId) => _branchConditions.TryGetValue(nodeId, out var value)
        ? value
        : throw new InvalidOperationException($"Branch condition for If node '{nodeId}' has not been evaluated.");

    public void MarkParallelBranches(string nodeId)
        => _controlFlowDecisions[nodeId] = new ControlFlowDecision(
            nodeId,
            "flow.parallel",
            ["branch1", "branch2"]);

    public IReadOnlyList<ControlFlowDecision> SnapshotControlFlowDecisions()
        => _controlFlowDecisions.Values.OrderBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();
    public string? QualityDisposition { get; set; }
    public string? Error { get; set; }

    /// <summary>Branch-scoped disposition tracker; Empty unless the workflow contains Parallel regions.</summary>
    public ParallelDispositionTracker ParallelDispositions { get; private set; } = ParallelDispositionTracker.Empty;

    /// <summary>Configures Parallel branch disposition scoping from the compiled structured regions.</summary>
    public void ConfigureParallelDispositions(IReadOnlyList<StructuredControlRegion> regions)
        => ParallelDispositions = ParallelDispositionTracker.FromRegions(regions);

    /// <summary>
    /// Records one flow.result. Inside a Parallel branch the value is held in the branch slot
    /// (any-NG) until the paired Join merges it; otherwise it sets the run disposition directly.
    /// </summary>
    public DispositionContribution RecordDisposition(string nodeId, string disposition)
    {
        if (ParallelDispositions.TryGetBranchScope(nodeId, out var regionId, out var branchName))
        {
            var branchDisposition = ParallelDispositions.ContributeBranchDisposition(regionId, branchName, disposition);
            return new DispositionContribution(disposition, ScopedToBranch: true, regionId, branchName, branchDisposition);
        }

        QualityDisposition = disposition;
        return new DispositionContribution(disposition, ScopedToBranch: false);
    }

    /// <summary>
    /// Applies a Parallel join aggregation. Top-level joins merge into the run disposition with
    /// any-NG semantics; nested joins already propagated into their parent branch slot.
    /// </summary>
    public ParallelJoinAggregation? ApplyParallelJoin(string joinNodeId)
    {
        var aggregation = ParallelDispositions.AggregateJoin(joinNodeId);
        if (aggregation is { Aggregate: { } aggregate, PropagatedToRegionId: null })
            QualityDisposition = QualityDisposition is null
                ? aggregate
                : ParallelDispositionTracker.Combine(QualityDisposition, aggregate);
        return aggregation;
    }

    public DebugRunMode DebugMode { get; private set; } = DebugRunMode.Full;
    public string? TargetNodeId { get; private set; }
    public bool Halted { get; private set; }
    public string? HaltNodeId { get; private set; }
    public string? HaltReason { get; private set; }
    public bool ReportStartReached { get; private set; }

    public void Configure(VisionRunOptions? options)
    {
        options ??= new VisionRunOptions();
        DebugMode = options.Mode;
        TargetNodeId = options.TargetNodeId;
        _breakpoints = new HashSet<string>(options.Breakpoints ?? [], StringComparer.OrdinalIgnoreCase);
        ReportStartReached = DebugMode is not (DebugRunMode.RunFromNode or DebugRunMode.RunNode);
        CaptureNodeImages = options.CaptureNodeImages;
    }

    public StepDebugDecision BeforeNode(string nodeId)
    {
        lock (_debugGate)
        {
            if (_forceExecuteNodeId is { } forced && forced.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            {
                // Debug-session "run node with cached inputs": execute exactly this node even while halted.
                _forceExecuteNodeId = null;
                return StepDebugDecision.Execute;
            }

            if (Halted) return StepDebugDecision.Skip;

            if (_resumeSkipCompleted && _executedNodes.ContainsKey(nodeId))
            {
                // Session resume: nodes already executed in earlier segments stay skipped. Their cached
                // outputs feed newly executed nodes and their side effects are never re-fired.
                return StepDebugDecision.Skip;
            }

            if (DebugMode == DebugRunMode.Breakpoints && _breakpoints.Contains(nodeId))
            {
                Halted = true;
                HaltNodeId = nodeId;
                HaltReason = "Breakpoint reached before node execution.";
                return StepDebugDecision.HaltBefore;
            }

            if ((DebugMode is DebugRunMode.RunFromNode or DebugRunMode.RunNode) && !ReportStartReached &&
                nodeId.Equals(TargetNodeId, StringComparison.OrdinalIgnoreCase))
            {
                ReportStartReached = true;
            }

            return StepDebugDecision.Execute;
        }
    }

    public NodeExecutionPhase CurrentPhase =>
        (DebugMode is DebugRunMode.RunFromNode or DebugRunMode.RunNode) && !ReportStartReached
            ? NodeExecutionPhase.Warmup
            : NodeExecutionPhase.Run;

    public void AfterNode(string nodeId)
    {
        lock (_debugGate)
        {
            if (DebugMode == DebugRunMode.RunNode && nodeId.Equals(TargetNodeId, StringComparison.OrdinalIgnoreCase))
            {
                Halted = true;
                HaltNodeId = nodeId;
                HaltReason = "Run Node target completed.";
            }
        }
    }

    /// <summary>
    /// Debug-session resume: clears the halt that stopped the previous segment (the hit breakpoint is
    /// consumed so the next segment steps past it) and switches into skip-completed mode so already
    /// executed nodes are not re-run and their cached outputs keep feeding downstream inputs.
    /// </summary>
    public void ResumeFromHalt()
    {
        lock (_debugGate)
        {
            if (HaltNodeId is { } haltedNode) _breakpoints.Remove(haltedNode);
            Halted = false;
            HaltNodeId = null;
            HaltReason = null;
            _resumeSkipCompleted = true;
        }
    }

    /// <summary>Forces the next observation of <paramref name="nodeId"/> to execute (debug-session run-node).</summary>
    public void ForceExecuteNode(string nodeId)
    {
        lock (_debugGate) _forceExecuteNodeId = nodeId;
    }

    public bool HasExecuted(string nodeId) => _executedNodes.ContainsKey(nodeId);

    public IReadOnlyList<string> ExecutedNodeIds =>
        _executedNodes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Clears a captured execution error without touching reports/outputs (used by interactive node runs).</summary>
    public void ClearError() => Error = null;

    /// <summary>When true, each node's image output is captured as JPEG for interactive inspection.</summary>
    public bool CaptureNodeImages { get; private set; }

    /// <summary>
    /// Captures at most one image output (first image-typed port) of a node as JPEG. Live Mats are
    /// disposed with the run data, so re-executions overwrite the previous capture for the node.
    /// Bounded by <see cref="MaxNodeImages"/> and a total JPEG byte budget
    /// (<see cref="MaxNodeImagesTotalBytes"/>) and never allowed to fail the run.
    /// </summary>
    public void CaptureNodeImage(string nodeId, IReadOnlyDictionary<string, VisionValue> outputs)
    {
        if (!CaptureNodeImages) return;
        var replacing = _nodeImages.TryGetValue(nodeId, out var previous);
        if (!replacing)
        {
            if (_nodeImages.Count >= MaxNodeImages) return;
            if (Interlocked.Read(ref _nodeImageBytes) >= MaxNodeImagesTotalBytes) return;
        }
        foreach (var (portName, value) in outputs)
        {
            if (value.Type != VisionDataType.Image || value.Value is not IVisionImage image || image.NativeImage is not Mat mat) continue;
            try
            {
                Cv2.ImEncode(".jpg", mat, out var jpeg);
                // 字节记账：重执行覆盖同节点旧图时先扣旧长度；预算用尽则放弃本次捕获（宁缺勿爆内存）。
                var nextBytes = Interlocked.Read(ref _nodeImageBytes)
                    - (replacing ? previous!.Jpeg.Length : 0)
                    + jpeg.LongLength;
                if (nextBytes > MaxNodeImagesTotalBytes) return;
                _nodeImages[nodeId] = new RunNodeImage(nodeId, portName, jpeg, image.Width, image.Height);
                Interlocked.Exchange(ref _nodeImageBytes, nextBytes);
            }
            catch { /* 图像捕获失败不应影响运行 */ }
            return;
        }
    }

    public IReadOnlyList<RunNodeImage> SnapshotNodeImages() => _nodeImages.Values.ToArray();

    public void BeginExecutionTimeline()
    {
        Interlocked.Exchange(ref _executionSequence, 0);
        Volatile.Write(ref _timelineOrigin, Stopwatch.GetTimestamp());
    }

    public (long Sequence, double StartOffsetMs) BeginNodeObservation()
    {
        var sequence = Interlocked.Increment(ref _executionSequence);
        return (sequence, CurrentExecutionOffsetMs());
    }

    public double CurrentExecutionOffsetMs()
    {
        var origin = Volatile.Read(ref _timelineOrigin);
        return origin == 0 ? 0 : Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
    }

    public void AddReport(NodeRunReport report)
    {
        _nodeReports.Enqueue(report);
        if (report.Success) _executedNodes[report.NodeId] = true;
    }
    public IReadOnlyList<NodeRunReport> SnapshotReports() => _nodeReports.ToArray();

    public void AddOverlays(string nodeId, IReadOnlyList<VisionOverlay>? overlays)
    {
        if (overlays is null) return;
        foreach (var overlay in overlays)
            _overlays.Enqueue(overlay.ForNode(nodeId));
    }

    public IReadOnlyList<VisionOverlay> SnapshotOverlays() => _overlays.ToArray();

    public void TrackOutputs(IReadOnlyDictionary<string, VisionValue> outputs)
    {
        lock (_resourceGate)
        {
            foreach (var resource in outputs.Values.Select(v => v.Value).OfType<IDisposable>())
            {
                if (_ownedResources.Any(x => ReferenceEquals(x, resource))) continue;
                _ownedResources.Add(resource);
            }
        }
    }

    public ReplayInputArtifact? BuildReplayInput()
    {
        var reports = SnapshotReports();
        foreach (var report in reports)
        {
            if (!OutputsByNode.TryGetValue(report.NodeId, out var outputs)) continue;
            if (!outputs.TryGetValue("image", out var value) || value.Type != VisionDataType.Image || value.Value is not IVisionImage image) continue;
            if (image.NativeImage is not Mat mat) continue;
            Cv2.ImEncode(".png", mat, out var png);
            return new ReplayInputArtifact(png, report.NodeId, image.Width, image.Height);
        }
        return null;
    }

    public RunImageArtifacts CaptureArtifacts(string runId, string disposition, RunArtifactOptions options)
    {
        var reports = SnapshotReports();
        Mat? preview = null;
        Mat? replay = null;
        string? source = null;
        if (RunArtifactSampling.Include(runId, disposition, options.PreviewSampleEvery))
        {
            foreach (var report in reports.Reverse())
            {
                preview = ImageForNode(report.NodeId);
                if (preview is not null) break;
            }
        }
        if (RunArtifactSampling.Include(runId, disposition, options.ReplaySampleEvery, replay: true))
        {
            foreach (var report in reports)
            {
                replay = ImageForNode(report.NodeId);
                if (replay is not null) { source = report.NodeId; break; }
            }
        }
        return RunImageArtifacts.Capture(preview, replay, source, options.MaxRawBytes);
    }

    private Mat? ImageForNode(string id)
        => OutputsByNode.TryGetValue(id, out var outputs) && outputs.TryGetValue("image", out var value)
            && value.Type == VisionDataType.Image && value.Value is IVisionImage image
                ? image.NativeImage as Mat : null;

    public VisionPreview? BuildPreview()
    {
        var reports = SnapshotReports();
        for (var i = reports.Count - 1; i >= 0; i--)
        {
            var nodeId = reports[i].NodeId;
            if (!OutputsByNode.TryGetValue(nodeId, out var outputs)) continue;
            if (!outputs.TryGetValue("image", out var value) || value.Type != VisionDataType.Image || value.Value is not IVisionImage image) continue;
            if (image.NativeImage is not Mat mat) continue;
            Cv2.ImEncode(".jpg", mat, out var jpeg);
            return new VisionPreview(jpeg, image.Width, image.Height);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_resourceGate)
        {
            for (var i = _ownedResources.Count - 1; i >= 0; i--)
            {
                try { _ownedResources[i].Dispose(); }
                catch { /* best-effort cleanup in MVP */ }
            }
            _ownedResources.Clear();
        }
    }
}

public enum StepDebugDecision
{
    Execute,
    HaltBefore,
    Skip
}
