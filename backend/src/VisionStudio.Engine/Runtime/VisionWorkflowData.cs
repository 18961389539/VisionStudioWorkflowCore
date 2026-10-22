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
    /// <summary>序列化节点图像捕获的预算检查与记账：并行分支会并发捕获，读-判-写必须原子。</summary>
    private readonly object _imageGate = new();
    private readonly List<IDisposable> _ownedResources = [];
    private readonly ConcurrentQueue<NodeRunReport> _nodeReports = new();
    private readonly ConcurrentQueue<VisionOverlay> _overlays = new();
    private readonly ConcurrentDictionary<string, bool> _branchConditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _switchSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _trySucceeded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tryRegionGate = new();
    private readonly object _tryAttemptGate = new();
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _tryBodyNodeIds;
    private string? _activeTryNodeId;
    private int _activeTryAttempt;
    private readonly ConcurrentDictionary<string, ControlFlowDecision> _controlFlowDecisions = new(StringComparer.OrdinalIgnoreCase);
    private long _timelineOrigin;
    private long _executionSequence;
    private HashSet<string> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _executedNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<NodeImageKey, RunNodeImage> _nodeImages = new(NodeImageKeyComparer.Instance);
    private const int MaxNodeImages = 32;
    /// <summary>单次运行全部节点图像的编码字节总预算：高分辨率/多图像节点不能只靠张数上限约束内存。</summary>
    private const long MaxNodeImagesTotalBytes = 16L * 1024 * 1024;
    private long _nodeImageBytes;
    private readonly Dictionary<string, RunCaliperCircleDiagnostic> _caliperDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RunCaliperEdgeDiagnostic> _caliperEdgeDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private const long MaxCaliperDiagnosticBytes = 16L * 1024 * 1024;
    private long _caliperDiagnosticBytes;
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
    public string? RunId { get; set; }

    /// <summary>
    /// A02：本次执行的设备命令所有权令牌（由运行入口的租约环境捕获）。节点运行时在真实派发前
    /// 重入该环境——即使 Workflow Core 的调度边界丢失执行上下文，派发仍携带正确的所有权身份；
    /// 令牌被换代/撤销后，旧执行的后续派发会在派发边界被拒绝。
    /// </summary>
    public IDeviceCommandOwnershipToken? Ownership { get; set; }

    public NodeDefinition RequireNode(string nodeId) => _nodesById.TryGetValue(nodeId, out var node)
        ? node
        : throw new InvalidOperationException($"Node '{nodeId}' was not found in the visual workflow definition.");

    public IReadOnlyList<EdgeDefinition> IncomingDataEdges(string nodeId) =>
        _incomingDataEdges.TryGetValue(nodeId, out var edges) ? edges : [];

    public ConcurrentDictionary<string, IReadOnlyDictionary<string, VisionValue>> OutputsByNode { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounded, frozen per-node lists consumed by the structured Workflow Core Foreach primitive.</summary>
    public ConcurrentDictionary<string, IReadOnlyList<VisionDetection>> ForEachCollections { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    // Retained for source compatibility with V0.28 plugins; the recursive compiler no longer reads
    // this global value because nested If nodes must not overwrite their parent's condition.
    public bool BranchCondition { get; set; }

    public void SetBranchCondition(string nodeId, bool condition, string nodeType = "flow.if")
    {
        BranchCondition = condition;
        _branchConditions[nodeId] = condition;
        _controlFlowDecisions[nodeId] = new ControlFlowDecision(
            nodeId,
            nodeType,
            [condition ? "true" : "false"],
            condition ? "true" : "false",
            condition);
    }

    public bool GetBranchCondition(string nodeId) => _branchConditions.TryGetValue(nodeId, out var value)
        ? value
        : throw new InvalidOperationException($"Branch condition for If node '{nodeId}' has not been evaluated.");

    /// <summary>Read-only view for Workflow Core's expression evaluator, which restricts arbitrary method calls.</summary>
    public IReadOnlyDictionary<string, bool> BranchConditions => _branchConditions;

    public void SetSwitchSelection(string nodeId, string selectedPort)
    {
        if (!selectedPort.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !System.Text.RegularExpressions.Regex.IsMatch(selectedPort, "^case([1-9]|1[0-6])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"Switch '{nodeId}' selected invalid branch '{selectedPort}'.");
        _switchSelections[nodeId] = selectedPort;
        _controlFlowDecisions[nodeId] = new ControlFlowDecision(nodeId, "flow.switch", [selectedPort], selectedPort);
    }

    public string GetSwitchSelection(string nodeId) => _switchSelections.TryGetValue(nodeId, out var value)
        ? value
        : throw new InvalidOperationException($"Branch selection for Switch node '{nodeId}' has not been evaluated.");

    public IReadOnlyDictionary<string, string> SwitchSelections => _switchSelections;

    public IReadOnlyDictionary<string, bool> TrySucceeded => _trySucceeded;

    public IReadOnlyList<string> GetTryBodyNodeIds(string nodeId)
    {
        lock (_tryRegionGate)
        {
            _tryBodyNodeIds ??= new StructuredWorkflowIrBuilder(
                Definition,
                Definition.Edges.Where(WorkflowGraph.IsControlEdge).ToArray()).Build().TryBodies;
            return _tryBodyNodeIds.TryGetValue(nodeId, out var ids)
                ? ids
                : throw new InvalidOperationException($"Try region '{nodeId}' is missing from the compiled workflow structure.");
        }
    }

    public void SetTryOutcome(string nodeId, bool succeeded, string? error, int attempts)
    {
        _trySucceeded[nodeId] = succeeded;
        _controlFlowDecisions[nodeId] = new ControlFlowDecision(nodeId, "flow.try", [succeeded ? "success" : "error"], succeeded ? "success" : "error", succeeded);
    }

    public void BeginTryAttempt(string nodeId, int attempt, IReadOnlyList<string> bodyNodeIds)
    {
        // A previous attempt may have produced early body outputs before a later node failed.
        // Drop the entire attempt's cached values before retrying; data edges into the error region
        // are rejected by the compiler, so failed/partial results cannot leak into recovery logic.
        foreach (var bodyNodeId in bodyNodeIds)
            OutputsByNode.TryRemove(bodyNodeId, out _);
        lock (_tryAttemptGate) { _activeTryNodeId = nodeId; _activeTryAttempt = attempt; }
    }

    public void ClearTryBodyOutputs(IEnumerable<string> bodyNodeIds)
    {
        foreach (var bodyNodeId in bodyNodeIds)
            OutputsByNode.TryRemove(bodyNodeId, out _);
    }

    public (string NodeId, int Attempt)? CurrentTryAttempt
    {
        get { lock (_tryAttemptGate) return _activeTryNodeId is null ? null : (_activeTryNodeId, _activeTryAttempt); }
    }

    public void EndTryAttempt()
    {
        lock (_tryAttemptGate) { _activeTryNodeId = null; _activeTryAttempt = 0; }
    }

    public void ClearHandledTryError(string nodeId)
    {
        if (Error?.StartsWith($"{nodeId}: ", StringComparison.OrdinalIgnoreCase) == true)
            Error = null;
        if (FaultedNodeId?.Equals(nodeId, StringComparison.OrdinalIgnoreCase) == true)
        {
            FaultedException = null;
            FaultedNodeId = null;
        }
    }

    public void MarkParallelBranches(string nodeId)
        => _controlFlowDecisions[nodeId] = new ControlFlowDecision(
            nodeId,
            "flow.parallel",
            ["branch1", "branch2"]);

    public IReadOnlyList<ControlFlowDecision> SnapshotControlFlowDecisions()
        => _controlFlowDecisions.Values.OrderBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();
    public string? QualityDisposition { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// R10/F08（2026-10-12 复核）：最近一次节点故障的**原始异常**与节点标识。WorkflowCore 会
    /// 捕获节点异常并以 Suspended/Error 返回，运行器外层 catch 不会经过——结果返回路径据此向
    /// 宿主观察者报告完整异常链（每次运行只记录首个故障，避免重复刷日志）。
    /// </summary>
    public Exception? FaultedException { get; set; }
    public string? FaultedNodeId { get; set; }
    public string? LastFaultedNodeId { get; set; }

    /// <summary>Branch-scoped disposition tracker; Empty unless the workflow contains Parallel regions.</summary>
    public ParallelDispositionTracker ParallelDispositions { get; private set; } = ParallelDispositionTracker.Empty;
    private HashSet<string> _forEachBodyNodeIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configures Parallel branch scoping and ForEach any-NG aggregation from structured regions.</summary>
    public void ConfigureParallelDispositions(IReadOnlyList<StructuredControlRegion> regions)
    {
        ParallelDispositions = ParallelDispositionTracker.FromRegions(regions);
        _forEachBodyNodeIds = regions
            .Where(region => region.Kind.Equals("ForEach", StringComparison.OrdinalIgnoreCase))
            .SelectMany(region => region.BranchNodeIds.TryGetValue("body", out var nodes) ? nodes : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records one flow.result. Inside a Parallel branch the value is held in the branch slot
    /// (any-NG) until the paired Join merges it. Results in a ForEach body also use any-NG across
    /// iterations, while an unscoped result elsewhere keeps its explicit overwrite semantics.
    /// </summary>
    public DispositionContribution RecordDisposition(string nodeId, string disposition)
    {
        if (ParallelDispositions.TryGetBranchScope(nodeId, out var regionId, out var branchName))
        {
            var branchDisposition = ParallelDispositions.ContributeBranchDisposition(regionId, branchName, disposition);
            return new DispositionContribution(disposition, ScopedToBranch: true, regionId, branchName, branchDisposition);
        }

        if (_forEachBodyNodeIds.Contains(nodeId))
            QualityDisposition = QualityDisposition is null
                ? disposition
                : ParallelDispositionTracker.Combine(QualityDisposition, disposition);
        else
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

    /// <summary>When true, each node's image output is captured for interactive inspection.</summary>
    public bool CaptureNodeImages { get; private set; }

    /// <summary>
    /// Captures every image-typed output of a node. Mask ports use lossless PNG; other images use JPEG.
    /// Live Mats are disposed with the run data, so re-executions replace the previous captures for that node.
    /// Bounded by <see cref="MaxNodeImages"/> and a total encoded-byte budget
    /// (<see cref="MaxNodeImagesTotalBytes"/>) and never allowed to fail the run.
    /// </summary>
    public void CaptureNodeImage(string nodeId, IReadOnlyDictionary<string, VisionValue> outputs)
    {
        if (!CaptureNodeImages) return;
        var captures = new List<RunNodeImage>();
        foreach (var (portName, value) in outputs)
        {
            if (value.Type is not (VisionDataType.Image or VisionDataType.DomainImage) || value.Value is not IVisionImage image || image.NativeImage is not Mat mat) continue;
            try
            {
                var maskPort = portName.Contains("mask", StringComparison.OrdinalIgnoreCase);
                var extension = maskPort ? ".png" : ".jpg";
                if (!Cv2.ImEncode(extension, mat, out var bytes)) continue;
                captures.Add(new RunNodeImage(nodeId, portName, bytes, image.Width, image.Height,
                    maskPort ? "image/png" : "image/jpeg"));
            }
            catch { /* 图像捕获失败不应影响运行 */ }
        }

        // 更新、移除旧输出以及预算记账作为一个整体串行化。若节点重执行时输出端口集合发生变化，
        // 不应让上一次执行留下的端口图像继续冒充本次结果。
        lock (_imageGate)
        {
            foreach (var key in _nodeImages.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key.NodeId, nodeId)).ToArray())
            {
                if (_nodeImages.TryRemove(key, out var previous)) _nodeImageBytes -= previous.Bytes.Length;
            }

            foreach (var capture in captures)
            {
                if (_nodeImages.Count >= MaxNodeImages) continue;
                var nextBytes = _nodeImageBytes + capture.Bytes.LongLength;
                if (nextBytes > MaxNodeImagesTotalBytes) continue;
                _nodeImages[new NodeImageKey(nodeId, capture.PortName)] = capture;
                _nodeImageBytes = nextBytes;
            }
        }
    }

    public IReadOnlyList<RunNodeImage> SnapshotNodeImages() => _nodeImages.Values
        .OrderBy(image => image.NodeId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(image => image.PortName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void ClearCaliperDiagnostic(string nodeId)
    {
        lock (_imageGate)
        {
            if (_caliperDiagnostics.Remove(nodeId, out var previous)) _caliperDiagnosticBytes -= previous.Data.EstimatedBytes;
            if (_caliperEdgeDiagnostics.Remove(nodeId, out var previousEdge)) _caliperDiagnosticBytes -= previousEdge.Data.EstimatedBytes;
        }
    }

    public bool CaptureCaliperDiagnostic(string nodeId, long sequence, CaliperCircleDiagnostic diagnostic)
    {
        if (!CaptureNodeImages) return false;
        lock (_imageGate)
        {
            if (_caliperDiagnostics.Remove(nodeId, out var previous)) _caliperDiagnosticBytes -= previous.Data.EstimatedBytes;
            if (_caliperDiagnosticBytes + diagnostic.EstimatedBytes > MaxCaliperDiagnosticBytes) return false;
            _caliperDiagnostics[nodeId] = new(nodeId, sequence, diagnostic);
            _caliperDiagnosticBytes += diagnostic.EstimatedBytes;
            return true;
        }
    }

    public IReadOnlyList<RunCaliperCircleDiagnostic> SnapshotCaliperDiagnostics()
    {
        lock (_imageGate) return _caliperDiagnostics.Values.ToArray();
    }

    public bool CaptureCaliperEdgeDiagnostic(string nodeId, long sequence, CaliperEdgeDiagnostic diagnostic)
    {
        if (!CaptureNodeImages) return false;
        lock (_imageGate)
        {
            if (_caliperEdgeDiagnostics.Remove(nodeId, out var previous)) _caliperDiagnosticBytes -= previous.Data.EstimatedBytes;
            if (_caliperDiagnosticBytes + diagnostic.EstimatedBytes > MaxCaliperDiagnosticBytes) return false;
            _caliperEdgeDiagnostics[nodeId] = new(nodeId, sequence, diagnostic);
            _caliperDiagnosticBytes += diagnostic.EstimatedBytes;
            return true;
        }
    }

    public IReadOnlyList<RunCaliperEdgeDiagnostic> SnapshotCaliperEdgeDiagnostics()
    {
        lock (_imageGate) return _caliperEdgeDiagnostics.Values.ToArray();
    }

    private readonly record struct NodeImageKey(string NodeId, string PortName);

    private sealed class NodeImageKeyComparer : IEqualityComparer<NodeImageKey>
    {
        public static NodeImageKeyComparer Instance { get; } = new();

        public bool Equals(NodeImageKey left, NodeImageKey right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.NodeId, right.NodeId)
                && StringComparer.OrdinalIgnoreCase.Equals(left.PortName, right.PortName);

        public int GetHashCode(NodeImageKey key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.NodeId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.PortName));
    }

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
