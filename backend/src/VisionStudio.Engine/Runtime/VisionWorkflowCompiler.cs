using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Compiles the vendor-neutral visual graph into Workflow Core JSON DSL.
/// V0.29 first resolves explicit Control edges into a recursive structured IR, then emits
/// Workflow Core primitives while retaining VisionPipelineStep fusion inside every nested region.
/// </summary>
public sealed class VisionWorkflowCompiler(VisionNodeRegistry registry)
{
    private const string ReservedPipelinePrefix = "__vs_pipeline_";
    private const int PlanSchemaVersion = 1;
    private readonly object _identityGate = new();
    private long _catalogRevision = -1;
    private string _catalogIdentity = string.Empty;
    /// <summary>
    /// DSL 是给人读、给 Workflow Core 解析的工件（非 HTML 场景）：默认编码器会把条件表达式里的
    /// 引号转义成 \u0022，视图/日志里难以辨认；relaxed 编码输出标准的 \" 转义，两种形式解析等价。
    /// </summary>
    private static readonly JsonSerializerOptions DslJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string GetPlanCacheKey(WorkflowDefinition workflow)
    {
        string identity;
        lock (_identityGate)
        {
            var revision = registry.Revision;
            if (_catalogRevision != revision)
            {
                _catalogIdentity = VisionCatalogSnapshot.ComputeHash(registry.Catalog);
                _catalogRevision = revision;
            }
            identity = _catalogIdentity;
        }
        // Include the full identity: sanitized workflow IDs can otherwise alias each other.
        var input = System.Text.Json.JsonSerializer.Serialize(new
        {
            workflow.Id,
            Workflow = WorkflowFingerprint.Compute(workflow),
            Catalog = identity,
            Compiler = typeof(VisionWorkflowCompiler).Module.ModuleVersionId,
            Schema = PlanSchemaVersion
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private readonly string _visionStepType = typeof(VisionNodeStep).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionNodeStep assembly name.");

    private readonly string _pipelineStepType = typeof(VisionPipelineStep).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionPipelineStep assembly name.");

    private readonly string _dataType = typeof(VisionWorkflowData).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionWorkflowData assembly name.");

    public CompiledWorkflow Compile(WorkflowDefinition workflow)
    {
        if (workflow.Nodes.Count == 0)
            throw new InvalidOperationException("Workflow has no nodes.");
        if (workflow.Nodes.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != workflow.Nodes.Count)
            throw new InvalidOperationException("Workflow contains duplicate node IDs.");
        if (workflow.Nodes.Any(x => x.Id.StartsWith(ReservedPipelinePrefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Node IDs beginning with '{ReservedPipelinePrefix}' are reserved for the compiled hybrid runtime.");

        ValidateCatalogAndEdges(workflow);

        var hash = GetPlanCacheKey(workflow);
        var workflowCoreId = $"vision-{Sanitize(workflow.Id)}-{hash}";
        var context = new CompileContext();

        var controlEdges = workflow.Edges.Where(WorkflowGraph.IsControlEdge).ToArray();
        var compiled = controlEdges.Length == 0
            ? CompileLegacyLinear(workflow, context)
            : CompileStructured(workflow, controlEdges, context);

        var dsl = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"] = workflowCoreId,
            ["Version"] = 1,
            ["DataType"] = _dataType,
            ["Steps"] = compiled.Steps
        }, DslJsonOptions);

        return new CompiledWorkflow(
            workflowCoreId,
            1,
            dsl,
            compiled.VisualOrder,
            context.SnapshotSegments(),
            compiled.ControlRegions);
    }

    private void ValidateCatalogAndEdges(WorkflowDefinition workflow)
    {
        var byId = workflow.Nodes.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes)
            registry.Require(node.Type);

        var targetPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in workflow.Edges)
        {
            if (!byId.TryGetValue(edge.SourceNodeId, out var sourceNode) ||
                !byId.TryGetValue(edge.TargetNodeId, out var targetNode))
                throw new InvalidOperationException($"Edge '{edge.Id}' references a missing node.");

            var sourceCatalog = registry.Require(sourceNode.Type).Catalog;
            var targetCatalog = registry.Require(targetNode.Type).Catalog;
            var sourcePort = sourceCatalog.Outputs.FirstOrDefault(p => p.Name.Equals(edge.SourcePort, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Output port '{edge.SourceNodeId}.{edge.SourcePort}' does not exist.");
            var targetPort = targetCatalog.Inputs.FirstOrDefault(p => p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{edge.TargetNodeId}.{edge.TargetPort}' does not exist.");

            var control = sourcePort.DataType == VisionDataType.Control || targetPort.DataType == VisionDataType.Control;
            if (control)
            {
                if (sourcePort.DataType != VisionDataType.Control || targetPort.DataType != VisionDataType.Control)
                    throw new InvalidOperationException($"Control edge '{edge.Id}' must connect Control -> Control, got {sourcePort.DataType} -> {targetPort.DataType}.");
                if (!WorkflowGraph.IsControlEdge(edge))
                    throw new InvalidOperationException($"Edge '{edge.Id}' connects control ports but kind is not 'control'.");
            }
            else
            {
                if (WorkflowGraph.IsControlEdge(edge))
                    throw new InvalidOperationException($"Edge '{edge.Id}' is marked control but connects {sourcePort.DataType} -> {targetPort.DataType}.");
                if (!VisionNodeDispatcher.AreCompatible(sourcePort.DataType, targetPort.DataType))
                    throw new InvalidOperationException($"Data edge '{edge.Id}' type mismatch: {sourcePort.DataType} -> {targetPort.DataType}.");

                var inputKey = $"{edge.TargetNodeId}:{edge.TargetPort}";
                if (!targetPorts.Add(inputKey))
                    throw new InvalidOperationException($"Input '{edge.TargetNodeId}.{edge.TargetPort}' has multiple data sources. One source per data input is required.");
            }
        }

        foreach (var node in workflow.Nodes)
        {
            var catalog = registry.Require(node.Type).Catalog;
            foreach (var required in catalog.Inputs.Where(p => p.Required && p.DataType != VisionDataType.Control))
            {
                if (!workflow.Edges.Any(e => !WorkflowGraph.IsControlEdge(e) &&
                    e.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase) &&
                    e.TargetPort.Equals(required.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Required input '{node.Id}.{required.Name}' ({required.DataType}) is not connected.");
            }

            ValidateNodeParameters(node, catalog);
        }
    }

    /// <summary>
    /// Validates catalog-declared parameters before compilation so bad recipes are rejected instead of
    /// silently falling back to defaults inside the JsonParameters getters. Parameters absent from the
    /// node use their declared defaults; undeclared keys (for example the ROI payload consumed by
    /// VisionRoiParameterExtensions) are intentionally ignored.
    /// </summary>
    private static void ValidateNodeParameters(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (node.Parameters is null || node.Parameters.Count == 0) return;

        foreach (var descriptor in catalog.Parameters)
        {
            if (!node.Parameters.TryGetValue(descriptor.Name, out var value)) continue;

            var prefix = $"Node '{node.Id}' parameter '{descriptor.Name}'";
            switch (descriptor.Type)
            {
                case "number":
                {
                    if (value.ValueKind != JsonValueKind.Number)
                        throw new InvalidOperationException($"{prefix} expects a number, but received {Describe(value)}.{DefaultHint(value)}");
                    if (!value.TryGetDouble(out var number))
                        throw new InvalidOperationException($"{prefix} value {value.GetRawText()} cannot be represented as a number.");
                    if (descriptor.Min is { } min && number < min)
                        throw new InvalidOperationException($"{prefix} value {number} is below the minimum {min}.");
                    if (descriptor.Max is { } max && number > max)
                        throw new InvalidOperationException($"{prefix} value {number} is above the maximum {max}.");
                    break;
                }

                case "boolean":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidOperationException($"{prefix} expects a boolean, but received {Describe(value)}.{DefaultHint(value)}");
                    break;

                case "select":
                {
                    var allowed = FormatOptions(descriptor.Options);
                    var expectation = allowed.Length == 0 ? "a string" : $"one of [{allowed}]";
                    if (value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException($"{prefix} expects {expectation}, but received {Describe(value)}.{DefaultHint(value)}");
                    var text = value.GetString() ?? string.Empty;
                    if (descriptor.Options is { Count: > 0 } options &&
                        !options.Any(o => string.Equals(o.Value, text, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"{prefix} value '{text}' is not an allowed option. Allowed: {allowed}.");
                    break;
                }

                case "text":
                case "textarea":
                    if (value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException($"{prefix} expects text, but received {Describe(value)}.{DefaultHint(value)}");
                    break;
            }
        }
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.String => $"a string ('{value.GetString()}')",
        JsonValueKind.Number => $"a number ({value.GetRawText()})",
        JsonValueKind.True => "a boolean (true)",
        JsonValueKind.False => "a boolean (false)",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "an unsupported JSON value"
    };

    private static string DefaultHint(JsonElement value)
        => value.ValueKind == JsonValueKind.Null ? " Remove the parameter to fall back to its default value." : string.Empty;

    private static string FormatOptions(IReadOnlyList<ParameterOption>? options)
        => options is null ? string.Empty : string.Join(", ", options.Select(o => o.Value));

    private CompileResult CompileStructured(
        WorkflowDefinition workflow,
        IReadOnlyList<EdgeDefinition> controlEdges,
        CompileContext context)
    {
        var ir = new StructuredWorkflowIrBuilder(workflow, controlEdges).Build();
        var steps = CompileSequence(ir.Root, continuationStepId: null, context);
        return new CompileResult(steps, ir.VisualOrder, ir.Regions);
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileSequence(
        StructuredSequenceIr sequence,
        string? continuationStepId,
        CompileContext context)
    {
        var steps = new List<Dictionary<string, object?>>();
        var items = sequence.Items;

        for (var i = 0; i < items.Count;)
        {
            if (items[i] is StructuredNodeIr firstNode && IsPipelineEligible(firstNode.Node))
            {
                var segment = new List<NodeDefinition>();
                var j = i;
                while (j < items.Count && items[j] is StructuredNodeIr pipelineNode && IsPipelineEligible(pipelineNode.Node))
                {
                    segment.Add(pipelineNode.Node);
                    j++;
                }

                var next = j < items.Count ? FirstStepId(items[j]) : continuationStepId;
                steps.Add(CreatePipelineStep(segment, next, context));
                i = j;
                continue;
            }

            var item = items[i];
            var nextStepId = i + 1 < items.Count ? FirstStepId(items[i + 1]) : continuationStepId;
            switch (item)
            {
                case StructuredNodeIr node:
                    steps.Add(CreateVisionStep(node.Node.Id, nextStepId));
                    break;

                case StructuredIfIr conditional:
                    steps.AddRange(CompileIf(conditional, nextStepId, context));
                    break;

                case StructuredParallelIr parallel:
                    steps.AddRange(CompileParallel(parallel, nextStepId, context));
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported structured IR item '{item.GetType().Name}'.");
            }
            i++;
        }

        return steps;
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileIf(
        StructuredIfIr conditional,
        string? continuationStepId,
        CompileContext context)
    {
        var node = conditional.Node;
        var truePrimitiveId = $"{node.Id}.__if_true";
        var falsePrimitiveId = $"{node.Id}.__if_false";
        var conditionExpression = $"data.GetBranchCondition({JsonSerializer.Serialize(node.Id)})";

        var marker = CreateVisionStep(node.Id, truePrimitiveId);
        var truePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = truePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = conditionExpression },
            ["Do"] = new object[] { CompileSequence(conditional.TrueBranch, continuationStepId: null, context).ToArray() },
            ["NextStepId"] = falsePrimitiveId,
            ["ErrorBehavior"] = "Terminate"
        };
        var falsePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = falsePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"{conditionExpression} == false" },
            ["Do"] = new object[] { CompileSequence(conditional.FalseBranch, continuationStepId: null, context).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            falsePrimitive["NextStepId"] = continuationStepId;

        return [marker, truePrimitive, falsePrimitive];
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileParallel(
        StructuredParallelIr parallel,
        string? continuationStepId,
        CompileContext context)
    {
        ValidateParallelCapabilities(parallel.Branch1, parallel.Node.Id, "branch1");
        ValidateParallelCapabilities(parallel.Branch2, parallel.Node.Id, "branch2");

        var sequenceId = $"{parallel.Node.Id}.__parallel";
        // V0.64: 两条分支若全部由可融合节点组成，则合并为一个流水线段，由 VisionPipelineExecutor
        // 按分支分组并发执行（此前用 Sequence 顺序执行两条分支，时间线上永远不会重叠）。
        // 含非融合节点（flow.result、设备节点等）的分支仍回退为顺序 Sequence，语义保守。
        if (TryCollectFusedBranchNodes(parallel.Branch1, out var branch1Nodes) &&
            TryCollectFusedBranchNodes(parallel.Branch2, out var branch2Nodes))
        {
            var fusedNodes = branch1Nodes.Concat(branch2Nodes).ToArray();
            var fusedMarker = CreateVisionStep(parallel.Node.Id, sequenceId);
            return [fusedMarker, CreatePipelineStep(fusedNodes, continuationStepId, context, stepIdOverride: sequenceId)];
        }

        var marker = CreateVisionStep(parallel.Node.Id, sequenceId);
        var sequence = new Dictionary<string, object?>
        {
            ["Id"] = sequenceId,
            ["StepType"] = "WorkflowCore.Primitives.Sequence, WorkflowCore",
            ["Do"] = new object[]
            {
                CompileSequence(parallel.Branch1, continuationStepId: null, context).ToArray(),
                CompileSequence(parallel.Branch2, continuationStepId: null, context).ToArray()
            },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            sequence["NextStepId"] = continuationStepId;

        return [marker, sequence];
    }

    /// <summary>分支可整体融合为一个流水线段的条件：全部为可融合普通节点（无嵌套控制结构）。</summary>
    private bool TryCollectFusedBranchNodes(StructuredSequenceIr branch, out List<NodeDefinition> nodes)
    {
        nodes = [];
        if (branch.Items.Count == 0) return false;
        foreach (var item in branch.Items)
        {
            if (item is not StructuredNodeIr node || !IsPipelineEligible(node.Node))
            {
                nodes = [];
                return false;
            }
            nodes.Add(node.Node);
        }
        return true;
    }

    private void ValidateParallelCapabilities(StructuredSequenceIr sequence, string parallelId, string branchName)
    {
        foreach (var node in EnumerateNodes(sequence))
        {
            var catalog = registry.Require(node.Type).Catalog;
            if (catalog.Capabilities?.SupportsParallel == false)
                throw new InvalidOperationException(
                    $"Node '{node.Id}' ({node.Type}) declares SupportsParallel=false and cannot execute inside Parallel '{parallelId}' {branchName}.");
        }
    }

    private static IEnumerable<NodeDefinition> EnumerateNodes(StructuredSequenceIr sequence)
    {
        foreach (var item in sequence.Items)
        {
            switch (item)
            {
                case StructuredNodeIr node:
                    yield return node.Node;
                    break;
                case StructuredIfIr conditional:
                    yield return conditional.Node;
                    foreach (var child in EnumerateNodes(conditional.TrueBranch)) yield return child;
                    foreach (var child in EnumerateNodes(conditional.FalseBranch)) yield return child;
                    break;
                case StructuredParallelIr parallel:
                    yield return parallel.Node;
                    foreach (var child in EnumerateNodes(parallel.Branch1)) yield return child;
                    foreach (var child in EnumerateNodes(parallel.Branch2)) yield return child;
                    break;
            }
        }
    }

    private string FirstStepId(StructuredIrItem item) => item switch
    {
        StructuredNodeIr node => IsPipelineEligible(node.Node) ? PipelineStepId(node.Node.Id) : node.Node.Id,
        StructuredIfIr conditional => conditional.Node.Id,
        StructuredParallelIr parallel => parallel.Node.Id,
        _ => throw new InvalidOperationException($"Unsupported structured IR item '{item.GetType().Name}'.")
    };

    private CompileResult CompileLegacyLinear(WorkflowDefinition workflow, CompileContext context)
    {
        var byId = workflow.Nodes.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var incoming = workflow.Nodes.ToDictionary(x => x.Id, _ => new List<EdgeDefinition>(), StringComparer.OrdinalIgnoreCase);
        var outgoing = workflow.Nodes.ToDictionary(x => x.Id, _ => new List<EdgeDefinition>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in workflow.Edges)
        {
            if (!byId.ContainsKey(edge.SourceNodeId) || !byId.ContainsKey(edge.TargetNodeId))
                throw new InvalidOperationException($"Edge '{edge.Id}' references a missing node.");
            outgoing[edge.SourceNodeId].Add(edge);
            incoming[edge.TargetNodeId].Add(edge);
        }

        if (incoming.Values.Any(x => x.Count > 1) || outgoing.Values.Any(x => x.Count > 1))
            throw new InvalidOperationException("Legacy workflows without Control edges must be a single linear chain. Add explicit Control edges for structured branching.");

        var starts = workflow.Nodes.Where(n => incoming[n.Id].Count == 0).ToArray();
        if (starts.Length != 1)
            throw new InvalidOperationException($"Legacy workflow requires exactly one start node; found {starts.Length}.");

        var ordered = new List<NodeDefinition>(workflow.Nodes.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = starts[0];
        while (true)
        {
            if (!visited.Add(current.Id)) throw new InvalidOperationException("Workflow contains a cycle.");
            ordered.Add(current);
            if (outgoing[current.Id].Count == 0) break;
            current = byId[outgoing[current.Id][0].TargetNodeId];
        }

        if (ordered.Count != workflow.Nodes.Count)
            throw new InvalidOperationException("Legacy workflow requires every node to belong to the same chain.");

        var steps = CreateExecutionSteps(ordered, continuationStepId: null, context);
        return new CompileResult(steps, ordered.Select(x => x.Id).ToArray(), []);
    }

    private IReadOnlyList<Dictionary<string, object?>> CreateExecutionSteps(
        IReadOnlyList<NodeDefinition> nodes,
        string? continuationStepId,
        CompileContext context)
    {
        var units = new List<ExecutionUnit>();
        for (var i = 0; i < nodes.Count;)
        {
            var node = nodes[i];
            if (!IsPipelineEligible(node))
            {
                units.Add(new ExecutionUnit(node.Id, [node], false));
                i++;
                continue;
            }

            var segmentNodes = new List<NodeDefinition>();
            while (i < nodes.Count && IsPipelineEligible(nodes[i]))
            {
                segmentNodes.Add(nodes[i]);
                i++;
            }
            units.Add(new ExecutionUnit(PipelineStepId(segmentNodes[0].Id), segmentNodes, true));
        }

        var steps = new List<Dictionary<string, object?>>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var next = i + 1 < units.Count ? units[i + 1].StepId : continuationStepId;
            var unit = units[i];
            steps.Add(unit.IsPipeline
                ? CreatePipelineStep(unit.Nodes, next, context)
                : CreateVisionStep(unit.Nodes[0].Id, next));
        }
        return steps;
    }

    private Dictionary<string, object?> CreatePipelineStep(
        IReadOnlyList<NodeDefinition> nodes,
        string? nextStepId,
        CompileContext context,
        string? stepIdOverride = null)
    {
        var segmentId = stepIdOverride ?? PipelineStepId(nodes[0].Id);
        context.RegisterSegment(segmentId, nodes.Select(x => x.Id).ToArray());
        var step = new Dictionary<string, object?>
        {
            ["Id"] = segmentId,
            ["StepType"] = _pipelineStepType,
            ["Inputs"] = new Dictionary<string, string>
            {
                ["SegmentId"] = JsonSerializer.Serialize(segmentId)
            },
            ["ErrorBehavior"] = "Terminate"
        };
        if (nextStepId is not null) step["NextStepId"] = nextStepId;
        return step;
    }

    private Dictionary<string, object?> CreateVisionStep(string nodeId, string? nextStepId)
    {
        var step = new Dictionary<string, object?>
        {
            ["Id"] = nodeId,
            ["StepType"] = _visionStepType,
            ["Inputs"] = new Dictionary<string, string>
            {
                ["NodeId"] = JsonSerializer.Serialize(nodeId)
            },
            ["ErrorBehavior"] = "Terminate"
        };
        if (nextStepId is not null) step["NextStepId"] = nextStepId;
        return step;
    }

    private bool IsPipelineEligible(NodeDefinition node)
        => VisionExecutionPolicy.IsPipelineEligible(registry.Require(node.Type).Catalog);

    private static string PipelineStepId(string firstNodeId)
        => ReservedPipelinePrefix + Convert.ToHexString(Encoding.UTF8.GetBytes(firstNodeId)).ToLowerInvariant();

    private static string Sanitize(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private sealed record ExecutionUnit(string StepId, IReadOnlyList<NodeDefinition> Nodes, bool IsPipeline);

    private sealed class CompileContext
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _segments = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterSegment(string segmentId, IReadOnlyList<string> nodeIds)
        {
            if (_segments.TryGetValue(segmentId, out var existing))
            {
                if (!existing.SequenceEqual(nodeIds, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Pipeline segment id collision at '{segmentId}'.");
                return;
            }
            _segments.Add(segmentId, nodeIds);
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotSegments()
            => new Dictionary<string, IReadOnlyList<string>>(_segments, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CompileResult(
        IReadOnlyList<Dictionary<string, object?>> Steps,
        IReadOnlyList<string> VisualOrder,
        IReadOnlyList<StructuredControlRegion> ControlRegions);
}

public sealed record CompiledWorkflow(
    string WorkflowCoreId,
    int Version,
    string DslJson,
    IReadOnlyList<string> OrderedNodeIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments,
    IReadOnlyList<StructuredControlRegion> ControlRegions);
