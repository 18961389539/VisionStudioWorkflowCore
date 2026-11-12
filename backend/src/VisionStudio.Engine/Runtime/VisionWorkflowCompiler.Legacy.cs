using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

public sealed partial class VisionWorkflowCompiler
{
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

    private Dictionary<string, object?> CreateVisionStep(string nodeId, string? nextStepId, string? loopNodeId = null)
    {
        var inputs = new Dictionary<string, string>
        {
            ["NodeId"] = JsonSerializer.Serialize(nodeId)
        };
        if (!string.IsNullOrWhiteSpace(loopNodeId))
            inputs["LoopNodeId"] = JsonSerializer.Serialize(loopNodeId);
        var step = new Dictionary<string, object?>
        {
            ["Id"] = nodeId,
            ["StepType"] = _visionStepType,
            ["Inputs"] = inputs,
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
        private readonly List<CompileWarning> _warnings = [];

        public CompileContext(IReadOnlyList<EdgeDefinition> edges) => Edges = edges;
        public IReadOnlyList<EdgeDefinition> Edges { get; }

        public void AddWarning(CompileWarning warning) => _warnings.Add(warning);
        public IReadOnlyList<CompileWarning> SnapshotWarnings() => [.. _warnings];

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
