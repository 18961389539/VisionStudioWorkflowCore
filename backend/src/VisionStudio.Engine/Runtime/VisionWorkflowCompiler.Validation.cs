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
            var catalog = registry.Require(node.Type).Catalog;
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
            if (node.Type == "output.saveImage") Nodes.ImageSavePlan.ValidateConnections(node, connectedInputPorts);
            ValidateMeasurementGeometry(node, catalog, connectedInputPorts);
            ValidateCaliperWidthLimits(node, catalog);
            ValidateContourThicknessLimits(node, catalog);
            ValidateCaliperCircleArc(node, catalog);
            ValidateCaliperSampleRequirements(node, catalog);
        }
    }

    /// <summary>
    /// Measurement transforms are meaningful only in the pixel frame of the image being sampled.
    /// Transform2D values carry named frames, but several image-producing nodes use generic names
    /// such as CurrentImage/CropImage. Trace the graph as well so unrelated image branches cannot
    /// silently appear to share a frame just because their labels match.
    /// </summary>

    private static void ValidateNodeParameters(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputPorts)
    {
        if (catalog.Type is "measure.fitLine" or "measure.fitCircle")
        {
            var minimum = catalog.Type == "measure.fitCircle" ? 3 : 2;
            var count = catalog.Inputs.Count(port => port.DataType == VisionDataType.Point2D && connectedInputPorts.Contains(port.Name));
            if (count < minimum)
                throw new InvalidOperationException($"Node '{node.Id}' ({catalog.Type}) requires at least {minimum} connected point inputs; received {count}.");
        }
        if (catalog.Type == "measure.regionIntensity") Nodes.RegionIntensityNode.ValidateParameters(node);
        if (catalog.Type == "image.flatFieldCorrect")
        {
            var mode = node.GetString("referenceMode", "gainOffset");
            if (mode != "offset" && !connectedInputPorts.Contains("bright"))
                throw new InvalidOperationException($"Node '{node.Id}' gain correction requires a bright reference.");
            if (mode != "gain" && node.GetString("darkPolicy", "skip") == "fail" && !connectedInputPorts.Contains("dark"))
                throw new InvalidOperationException($"Node '{node.Id}' requires a dark reference under its current policy.");
        }
        if (node.Parameters is null || node.Parameters.Count == 0) return;

        foreach (var parameter in catalog.Parameters)
        {
            var descriptor = OperatorParameterForms.Resolve(node, catalog, parameter);
            if (!IsParameterVisible(node, catalog, descriptor)) continue;
            if (IsParameterOverriddenByConnectedInput(catalog, descriptor.Name, connectedInputPorts)) continue;
            if (catalog.Type == "jlvision.restoration" && node.GetString("operator", "harmonic") is "psfMotion" or "psfDefocus"
                && connectedInputPorts.Contains("image") && descriptor.Name is "width" or "height") continue;
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
        if (catalog.Type == "device.waitTag")
        {
            foreach (var name in new[] { "timeoutMs", "pollMs", "stableMs" })
            {
                var value = GetNumericParameter(node, catalog, name);
                if (value != Math.Truncate(value))
                    throw new InvalidOperationException($"Node '{node.Id}' {name} must be an integer number of milliseconds.");
            }
            var timeout = GetNumericParameter(node, catalog, "timeoutMs");
            if (GetNumericParameter(node, catalog, "pollMs") > timeout || GetNumericParameter(node, catalog, "stableMs") > timeout)
                throw new InvalidOperationException($"Node '{node.Id}' pollMs and stableMs must not exceed timeoutMs.");
        }
        if (catalog.Type == "flow.if" && node.GetString("mode", "threshold").Equals("range", StringComparison.OrdinalIgnoreCase))
        {
            var minimum = node.GetDouble("rangeMin", 0);
            var maximum = node.GetDouble("rangeMax", 100);
            if (minimum > maximum)
                throw new InvalidOperationException($"Node '{node.Id}' rangeMin {minimum} is greater than rangeMax {maximum}.");
        }
    }

    private static bool IsParameterOverriddenByConnectedInput(
        NodeCatalogItem catalog,
        string parameterName,
        IReadOnlySet<string> connectedInputPorts)
    {
        var parameter = catalog.Parameters.FirstOrDefault(candidate =>
            candidate.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase));
        return parameter?.OverriddenByPorts is { Count: > 0 } ports
            && ports.Any(port => connectedInputPorts.Contains(port));
    }

    private static void ValidateMeasurementGeometry(
        NodeDefinition node,
        NodeCatalogItem catalog,
        IReadOnlySet<string> connectedInputPorts)
    {
        if (connectedInputPorts.Contains("searchLine")
            && catalog.Inputs.Any(input => input.Name.Equals("searchLine", StringComparison.OrdinalIgnoreCase)
                && input.DataType == VisionDataType.Line2D))
            return;
        if (catalog.Type is not ("feature.edgeSubpixel" or "measure.caliper" or "measure.caliperWidth" or "measure.caliperArray" or "measure.caliperLine"))
            return;

        var dx = GetNumericParameter(node, catalog, "endX") - GetNumericParameter(node, catalog, "startX");
        var dy = GetNumericParameter(node, catalog, "endY") - GetNumericParameter(node, catalog, "startY");
        if (Math.Sqrt(dx * dx + dy * dy) < 2)
            throw new InvalidOperationException(
                $"Node '{node.Id}' search line must be at least 2 px long. Adjust its start/end coordinates or connect a valid searchLine input.");
    }

    private static void ValidateCaliperWidthLimits(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (!catalog.Type.Equals("measure.caliperWidth", StringComparison.OrdinalIgnoreCase)
            && !catalog.Type.Equals("measure.caliperArray", StringComparison.OrdinalIgnoreCase)) return;
        var minimum = GetNumericParameter(node, catalog, "minWidth");
        var maximum = GetNumericParameter(node, catalog, "maxWidth");
        if (maximum > 0 && maximum < minimum)
            throw new InvalidOperationException($"Node '{node.Id}' maxWidth must be zero (unlimited) or at least minWidth.");
        if (node.GetString("direction", "strongest").Equals("closestwidth", StringComparison.OrdinalIgnoreCase)
            && GetNumericParameter(node, catalog, "nominalWidth") <= 0)
            throw new InvalidOperationException($"Node '{node.Id}' nominalWidth must be positive for closest-width selection.");
    }

    private static void ValidateContourThicknessLimits(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (!catalog.Type.Equals("measure.contourThickness", StringComparison.OrdinalIgnoreCase)) return;
        var minimum = GetNumericParameter(node, catalog, "minThickness");
        var maximum = GetNumericParameter(node, catalog, "maxThickness");
        if (maximum > 0 && maximum < minimum)
            throw new InvalidOperationException($"Node '{node.Id}' maxThickness must be zero (unlimited) or at least minThickness.");
        var budget = GetNumericParameter(node, catalog, "maxSamplesPerBoundary");
        if (budget != Math.Truncate(budget))
            throw new InvalidOperationException($"Node '{node.Id}' maxSamplesPerBoundary must be an integer.");
    }

    private static void ValidateCaliperCircleArc(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (!catalog.Type.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase)) return;
        var start = GetNumericParameter(node, catalog, "angleStartDeg");
        var end = GetNumericParameter(node, catalog, "angleEndDeg");
        if (Math.Abs(end - start) < 1e-9)
            throw new InvalidOperationException(
                $"Node '{node.Id}' arc start and end angles are equal, producing only one radial caliper. Widen the arc angle range to fit a circle.");
    }

    private static void ValidateCaliperSampleRequirements(NodeDefinition node, NodeCatalogItem catalog)
    {
        if (catalog.Type.Equals("measure.caliperLine", StringComparison.OrdinalIgnoreCase))
        {
            var count = RoundedIntParameter(node, catalog, "caliperCount", 9, 3, 101);
            var minimum = RoundedIntParameter(node, catalog, "minValidCalipers", 3, 3, 101);
            if (minimum > count)
                throw new InvalidOperationException(
                    $"Node '{node.Id}' requires {minimum} valid calipers, but caliperCount is {count}. " +
                    "Lower minValidCalipers or increase caliperCount.");
            return;
        }

        if (catalog.Type.Equals("measure.caliperArray", StringComparison.OrdinalIgnoreCase))
        {
            var count = RoundedIntParameter(node, catalog, "caliperCount", 9, 1, 101);
            var minimum = RoundedIntParameter(node, catalog, "minValidCalipers", 1, 1, 101);
            if (minimum > count)
                throw new InvalidOperationException(
                    $"Node '{node.Id}' requires {minimum} valid calipers, but caliperCount is {count}. " +
                    "Lower minValidCalipers or increase caliperCount.");
            return;
        }

        if (!catalog.Type.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase)) return;

        var mode = node.GetString("samplingMode", string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode))
            mode = node.Parameters?.ContainsKey("angleStepDeg") == true ? "step" : "count";

        var countAvailable = mode == "count"
            ? RoundedIntParameter(node, catalog, "caliperCount", 72, 3, 361)
            : CircleCaliperCountByStep(node, catalog);
        if (countAvailable < 3)
            throw new InvalidOperationException(
                $"Node '{node.Id}' arc and angleStepDeg generate only {countAvailable} radial caliper(s); circle fitting requires at least 3. " +
                "Widen the arc or reduce angleStepDeg.");

        var minimumValid = RoundedIntParameter(node, catalog, "minValidCalipers", 3, 3, 361);
        if (minimumValid > countAvailable)
            throw new InvalidOperationException(
                $"Node '{node.Id}' requires {minimumValid} valid calipers, but its arc and sampling settings generate only {countAvailable}. " +
                "Lower minValidCalipers, widen the arc, or increase sampling density.");
    }

    private static int CircleCaliperCountByStep(NodeDefinition node, NodeCatalogItem catalog)
    {
        var start = GetNumericParameter(node, catalog, "angleStartDeg");
        var end = GetNumericParameter(node, catalog, "angleEndDeg");
        var rawSpan = end - start;
        var span = rawSpan % 360;
        var fullCircle = Math.Abs(rawSpan) >= 1e-9 && Math.Abs(span) < 1e-9;
        if (Math.Abs(rawSpan) < 1e-9) return 1;
        if (fullCircle) span = 360;
        else if (span < 0) span += 360;

        var step = Math.Clamp(GetNumericParameter(node, catalog, "angleStepDeg"), 0.5, 45);
        var intervalCount = span / step;
        var nearestIntervalCount = (int)Math.Round(intervalCount);
        var reachesEnd = nearestIntervalCount > 0
            && Math.Abs(nearestIntervalCount * step - span) <= 1e-9;
        var count = fullCircle && reachesEnd
            ? nearestIntervalCount
            : (int)Math.Floor(intervalCount) + 1;
        var maximumCount = fullCircle ? 360 : 361;
        return Math.Min(count, maximumCount);
    }

    private static int RoundedIntParameter(NodeDefinition node, NodeCatalogItem catalog, string name, int fallback, int minimum, int maximum)
        => (int)Math.Clamp(Math.Floor(GetNumericParameter(node, catalog, name) + 0.5), minimum, maximum);

    private static double GetNumericParameter(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        var descriptor = catalog.Parameters.First(parameter =>
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var fallback = Convert.ToDouble(descriptor.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);
        return node.GetDouble(name, fallback);
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
