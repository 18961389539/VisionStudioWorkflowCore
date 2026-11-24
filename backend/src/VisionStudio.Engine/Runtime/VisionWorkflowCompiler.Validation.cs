using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

public sealed partial class VisionWorkflowCompiler
{
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

        foreach (var circleNode in workflow.Nodes.Where(node =>
            string.Equals(node.Type, "measure.caliperCircle", StringComparison.OrdinalIgnoreCase)))
        {
            var circleSource = workflow.Edges.FirstOrDefault(edge =>
                edge.TargetNodeId.Equals(circleNode.Id, StringComparison.OrdinalIgnoreCase)
                && edge.TargetPort.Equals("circle", StringComparison.OrdinalIgnoreCase));
            if (circleSource is null || !byId.TryGetValue(circleSource.SourceNodeId, out var sourceNode)
                || !string.Equals(sourceNode.Type, "roi.transform", StringComparison.OrdinalIgnoreCase))
                continue;

            var transformSource = workflow.Edges.FirstOrDefault(edge =>
                edge.TargetNodeId.Equals(circleNode.Id, StringComparison.OrdinalIgnoreCase)
                && edge.TargetPort.Equals("transform", StringComparison.OrdinalIgnoreCase));
            if (transformSource is not null
                && transformSource.SourceNodeId.Equals(circleSource.SourceNodeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Node '{circleNode.Id}' receives an already corrected circle and its transform from ROI transform '{sourceNode.Id}'. Remove that transform input; applying both would shift the circle twice.");
        }

        ValidateImageWarpFrames(workflow, byId);
        ValidateMeasurementImageFrames(workflow, byId);
        ValidateMeasurementGeometryFrames(workflow, byId);
        ValidatePlaneMapInputs(workflow, byId);
        foreach (var node in workflow.Nodes.Where(n => n.Type == "geometry.intersections"))
        {
            var operation = node.GetString("operator", "lineCircle");
            string[] ports = operation == "circles" ? ["circle", "circle2"] : operation == "segments" ? ["line", "line2"] : ["line", "circle"];
            var frames = ports.Select(p => FindInputEdge(workflow.Edges, node.Id, p)).Where(e => e is not null)
                .Select(e => ResolveGeometryFrame(e!.SourceNodeId, e.SourcePort, workflow, byId, new(StringComparer.OrdinalIgnoreCase)))
                .Where(f => f is { IsTraceable: true }).ToArray();
            if (frames.Select(f => f!.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidOperationException($"Node '{node.Id}' intersection inputs use different coordinate frames.");
        }
        ValidateRoiFrames(workflow, byId);

        foreach (var node in workflow.Nodes)
        {
            var registration = registry.Require(node.Type);
            var catalog = registration.Catalog;
            var connectedInputPorts = workflow.Edges
                .Where(edge => !WorkflowGraph.IsControlEdge(edge)
                    && edge.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Select(edge => edge.TargetPort)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var required in catalog.Inputs.Where(p => p.DataType != VisionDataType.Control && p.IsRequired(node, catalog)))
            {
                if (!connectedInputPorts.Contains(required.Name))
                    throw new InvalidOperationException($"Required input '{node.Id}.{required.Name}' ({required.DataType}) is not connected.");
            }

            var roiParameterNames = BuiltInNodeCatalog.RoiParameterNames(catalog.Type, catalog.Capabilities);
            var roiInputConnected = catalog.Inputs.Any(input =>
                input.Name.Equals("roi", StringComparison.OrdinalIgnoreCase)
                && input.DataType == VisionDataType.Roi)
                && connectedInputPorts.Contains("roi");
            foreach (var roiParameter in roiParameterNames)
            {
                if (node.Parameters?.ContainsKey(roiParameter) != true) continue;
                // A connected ROI is the effective runtime value for ROI-port nodes, so malformed
                // dormant local ROI data must not block compilation. ROI Transform also returns
                // before reading its ROI while capturing a reference, allowing users to replace it.
                if (roiParameter.Equals("roi", StringComparison.OrdinalIgnoreCase)
                    && (roiInputConnected
                        || catalog.Type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase)
                        && node.GetBool("captureReference", false)))
                    continue;
                try { _ = node.GetRoi(roiParameter); }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException($"Node '{node.Id}' has an invalid ROI setting: {exception.Message}", exception);
                }
            }

            ValidateNodeParameters(node, catalog, connectedInputPorts);
            registration.DefinitionValidator?.ValidateDefinition(node, catalog, connectedInputPorts);
        }
    }

    private static void ValidateNodeParameters(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputPorts)
    {
        foreach (var requirement in catalog.MinimumConnectedInputs ?? [])
        {
            var count = catalog.Inputs.Count(port => port.DataType == requirement.DataType && connectedInputPorts.Contains(port.Name));
            if (count < requirement.Count)
                throw new InvalidOperationException(
                    $"Node '{node.Id}' ({catalog.Type}) requires at least {requirement.Count} connected {requirement.DataType} inputs; received {count}.");
        }

        if (node.Parameters is null || node.Parameters.Count == 0) return;

        foreach (var parameter in catalog.Parameters)
        {
            var descriptor = OperatorParameterForms.Resolve(node, catalog, parameter);
            if (!IsParameterVisible(node, catalog, descriptor)) continue;
            if (descriptor.OverriddenByPorts?.Any(connectedInputPorts.Contains) == true) continue;
            if (!node.Parameters.TryGetValue(descriptor.Name, out var value)) continue;
            if (descriptor.Optional && value.ValueKind == JsonValueKind.Null) continue;

            var prefix = $"Node '{node.Id}' parameter '{descriptor.Name}'";
            switch (descriptor.Type)
            {
                case "number":
                {
                    if (value.ValueKind != JsonValueKind.Number)
                        throw new InvalidOperationException($"{prefix} expects a number, but received {Describe(value)}.{DefaultHint(value)}");
                    if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                        throw new InvalidOperationException($"{prefix} value {value.GetRawText()} cannot be represented as a number.");
                    if (descriptor.Integer && number != Math.Truncate(number))
                        throw new InvalidOperationException($"{prefix} expects an integer, but received {value.GetRawText()}.");
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

    private static bool IsParameterVisible(NodeDefinition node, NodeCatalogItem catalog, ParameterDescriptor descriptor)
    {
        return (descriptor.VisibleWhen is null || Matches(descriptor.VisibleWhen))
            && (descriptor.VisibleWhenAll is null || descriptor.VisibleWhenAll.All(Matches));

        bool Matches(ParameterVisibility condition)
        {
            if (condition.Values.Count == 0) return false;
            var controller = catalog.Parameters.FirstOrDefault(parameter =>
                parameter.Name.Equals(condition.Parameter, StringComparison.OrdinalIgnoreCase));
            if (controller is null) return false;
            var actual = NodeParameterValue.Resolve(node, catalog, controller.Name) ?? string.Empty;
            return condition.Values.Any(expected => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
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
}
