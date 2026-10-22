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
    private sealed record CoordinateFrame(string Key, string Label, bool IsTraceable = true);
    private sealed record TransformFrames(
        CoordinateFrame? Source,
        CoordinateFrame? Target,
        IReadOnlyList<CoordinateFrame> MappedFrames);

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
        var context = new CompileContext(workflow.Edges);

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
    private void ValidatePlaneMapInputs(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        foreach (var node in workflow.Nodes.Where(n => n.Type == "jlvision.worldPlane" && n.GetString("operator", "image") == "applyMap"))
        {
            var bindings = new[] { "mapRows", "mapColumns", "mapInfo" }.Select(port => FindInputEdge(workflow.Edges, node.Id, port)).ToArray();
            if (bindings.Any(edge => edge is null))
                throw new InvalidOperationException($"Node '{node.Id}': 应用映射需配套连接 mapRows/mapColumns/mapInfo，请重新生成并连接映射。");
            var producerId = bindings[0]!.SourceNodeId;
            if (bindings.Any(edge => !edge!.SourceNodeId.Equals(producerId, StringComparison.OrdinalIgnoreCase) || edge.SourcePort != edge.TargetPort) ||
                !byId.TryGetValue(producerId, out var producer) || producer.Type != "jlvision.worldPlane" || producer.GetString("operator", "image") != "map")
                throw new InvalidOperationException($"Node '{node.Id}': 映射行图、列图和描述必须来自同一个映射生成节点的对应端口。");
            var original = ResolveImageInputFrame(producer.Id, "image", workflow, byId);
            var actual = ResolveImageInputFrame(node.Id, "image", workflow, byId);
            if (original is null || actual is null || !original.Key.Equals(actual.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Node '{node.Id}': 原图坐标系与映射生成时不一致；请使用同一相机图像来源，或在图像尺寸/坐标变化后重新生成映射。");
        }
    }

    private void ValidateMeasurementImageFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        var measurementTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "feature.edgeSubpixel", "measure.caliper", "measure.caliperWidth", "measure.caliperArray", "measure.caliperLine", "measure.caliperCircle"
        };

        foreach (var node in workflow.Nodes.Where(item => measurementTypes.Contains(item.Type)))
        {
            var transformEdge = FindInputEdge(workflow.Edges, node.Id, "transform");
            var imageEdge = FindInputEdge(workflow.Edges, node.Id, "image");
            if (transformEdge is null || imageEdge is null) continue;

            var transformFrames = ResolveTransformFrames(transformEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var imageFrame = ResolveImageFrame(imageEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var targetFrame = transformFrames?.Target;
            if (targetFrame is null || imageFrame is null
                || !targetFrame.IsTraceable || !imageFrame.IsTraceable
                || string.Equals(targetFrame.Key, imageFrame.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            throw new InvalidOperationException(
                $"Node '{node.Id}' samples image frame '{imageFrame.Label}', but its transform targets '{targetFrame.Label}'. " +
                "Connect an image from the transform's target frame, or use a transform composed for the measurement image.");
        }
    }

    private void ValidateImageWarpFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        foreach (var node in workflow.Nodes.Where(item => item.Type.Equals("image.warpAffine", StringComparison.OrdinalIgnoreCase)))
        {
            var transformEdge = FindInputEdge(workflow.Edges, node.Id, "transform");
            var imageEdge = FindInputEdge(workflow.Edges, node.Id, "image");
            if (transformEdge is null || imageEdge is null) continue;

            var transform = ResolveTransformFrames(transformEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var imageFrame = ResolveImageFrame(imageEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var sourceFrame = transform?.Source;
            if (sourceFrame is null || imageFrame is null
                || !sourceFrame.IsTraceable || !imageFrame.IsTraceable
                || string.Equals(sourceFrame.Key, imageFrame.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            throw new InvalidOperationException(
                $"Node '{node.Id}' warps image frame '{imageFrame.Label}', but its transform expects source frame '{sourceFrame.Label}'. " +
                "Connect the image from the transform's source frame, or use a transform computed for the input image.");
        }
    }

    private TransformFrames? ResolveTransformFrames(
        string nodeId,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var type = node.Type;
        var catalog = registry.Require(type).Catalog;
        var frameRule = NodeFrameSemantics.Resolve(node, catalog);
        if (frameRule?.Transform is { } framePolicy && framePolicy != NodeTransformFramePolicy.None)
        {
            var source = ResolveImageInputFrame(nodeId, frameRule.InputPort, workflow, byId);
            switch (framePolicy)
            {
                case NodeTransformFramePolicy.Spatial:
                {
                    var key = NodeFrameSemantics.ImageFrameKey(
                        nodeId,
                        frameRule,
                        port => ResolveImageInputFrame(nodeId, port, workflow, byId)?.Key,
                        port => ResolveTransformTargetFrame(nodeId, port, workflow, byId)?.Key);
                    var target = key is null
                        ? null
                        : new CoordinateFrame(key, NodeFrameSemantics.FrameLabel(nodeId, frameRule, "spatial frame"));
                    return new TransformFrames(source, target, target is null ? [] : [target]);
                }
                case NodeTransformFramePolicy.RoiReference:
                {
                    var target = ResolveImageInputFrame(nodeId, frameRule.InputPort, workflow, byId);
                    return new TransformFrames(
                        new CoordinateFrame($"roi:{nodeId}:reference", $"{nodeId} reference image", IsTraceable: false),
                        target,
                        target is null ? [] : [target]);
                }
                case NodeTransformFramePolicy.ReferenceInput:
                {
                    var target = ResolveImageInputFrame(nodeId, frameRule.ReferencePort ?? "reference", workflow, byId);
                    return new TransformFrames(source, target, target is null ? [] : [target]);
                }
            }
        }
        if (type.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
        {
            var templateEdge = FindInputEdge(workflow.Edges, nodeId, "template");
            var imageInput = FindInputEdge(workflow.Edges, nodeId, "image");
            var source = templateEdge is null
                ? new CoordinateFrame($"match:{nodeId}:template", $"{nodeId} trained template", IsTraceable: false)
                : ResolveImageFrame(templateEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var target = imageInput is null ? null : ResolveImageFrame(imageInput.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            return new TransformFrames(source, target, target is null ? [] : [target]);
        }
        if (type.Equals("coordinate.transformCompose", StringComparison.OrdinalIgnoreCase))
        {
            var firstEdge = FindInputEdge(workflow.Edges, nodeId, "transformA");
            var secondEdge = FindInputEdge(workflow.Edges, nodeId, "transformB");
            var first = firstEdge is null ? null : ResolveTransformFrames(firstEdge.SourceNodeId, workflow, byId,
                new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
            var second = secondEdge is null ? null : ResolveTransformFrames(secondEdge.SourceNodeId, workflow, byId,
                new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
            if (first?.Target is { IsTraceable: true } firstTarget
                && second?.Source is { IsTraceable: true } secondSource
                && !string.Equals(firstTarget.Key, secondSource.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Node '{nodeId}' cannot compose transforms: '{firstTarget.Label}' does not match '{secondSource.Label}'. " +
                    "Connect transforms whose intermediate image frames come from the same image path.");
            var mappedFrames = (first?.MappedFrames ?? [])
                .Concat(second?.MappedFrames ?? [])
                .GroupBy(frame => frame.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            return new TransformFrames(first?.Source, second?.Target, mappedFrames);
        }
        if (type.Equals("coordinate.transformInverse", StringComparison.OrdinalIgnoreCase))
        {
            var input = FindInputEdge(workflow.Edges, nodeId, "transform");
            var source = input is null ? null : ResolveTransformFrames(input.SourceNodeId, workflow, byId, visited);
            if (source is null) return null;
            var path = (source.Source is null ? source.MappedFrames : [source.Source, .. source.MappedFrames]).ToArray();
            return new TransformFrames(source.Target, source.Source, path.Reverse().Skip(1).ToArray());
        }
        return null;
    }

    /// <summary>
    /// Connected line and circle geometry must match the sampled image frame, or the source frame
    /// of an explicit transform. Pixel coordinates from a cropped branch are otherwise plausible
    /// values that silently point at a different location.
    /// </summary>
    private void ValidateMeasurementGeometryFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        var measurementTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "feature.edgeSubpixel", "measure.caliper", "measure.caliperWidth", "measure.caliperArray", "measure.caliperLine", "measure.caliperCircle"
        };

        foreach (var node in workflow.Nodes.Where(item => measurementTypes.Contains(item.Type)))
        {
            var transformEdge = FindInputEdge(workflow.Edges, node.Id, "transform");
            var transform = transformEdge is null ? null
                : ResolveTransformFrames(transformEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            if (transformEdge is not null && transform is null) continue;
            var imageFrame = transformEdge is null
                ? ResolveImageInputFrame(node.Id, "image", workflow, byId)
                : null;

            foreach (var inputPort in new[] { "searchLine", "circle" })
            {
                var geometryEdge = FindInputEdge(workflow.Edges, node.Id, inputPort);
                if (geometryEdge is null) continue;
                var geometryFrame = ResolveGeometryFrame(geometryEdge.SourceNodeId, geometryEdge.SourcePort,
                    workflow, byId, new(StringComparer.OrdinalIgnoreCase));
                if (geometryFrame is null || !geometryFrame.IsTraceable) continue;

                if (transform is null)
                {
                    if (imageFrame is { IsTraceable: true }
                        && !string.Equals(geometryFrame.Key, imageFrame.Key, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Node '{node.Id}' input '{inputPort}' geometry is in frame '{geometryFrame.Label}', but its measurement image is in frame '{imageFrame.Label}'. " +
                            "Connect geometry and image from the same pixel frame, or provide a transform for reference-frame geometry.");
                    continue;
                }

                if (transform.Source is { IsTraceable: true } sourceFrame
                    && string.Equals(geometryFrame.Key, sourceFrame.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (transform.MappedFrames.FirstOrDefault(frame =>
                        string.Equals(frame.Key, geometryFrame.Key, StringComparison.OrdinalIgnoreCase)) is { } appliedFrame)
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' input '{inputPort}' geometry from '{geometryEdge.SourceNodeId}' is already in transform frame '{appliedFrame.Label}'. " +
                        "Remove the transform input or connect geometry from its source frame.");

                if (transform.Source is { IsTraceable: true } expectedSource)
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' input '{inputPort}' geometry is in frame '{geometryFrame.Label}', but the transform expects source frame '{expectedSource.Label}'. " +
                        "Connect geometry from the transform's source frame or choose a matching transform.");
            }
        }
    }

    /// <summary>
    /// ROI values have image-pixel coordinates too. A croppedRoi connected to a different crop
    /// or to the original image can otherwise mask the wrong pixels without any runtime error.
    /// </summary>
    private void ValidateRoiFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        foreach (var edge in workflow.Edges.Where(item => !WorkflowGraph.IsControlEdge(item)))
        {
            if (!byId.TryGetValue(edge.TargetNodeId, out var targetNode)) continue;
            var targetCatalog = registry.Require(targetNode.Type).Catalog;
            var targetPort = targetCatalog.Inputs.FirstOrDefault(port =>
                port.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase));
            if (targetPort?.DataType != VisionDataType.Roi) continue;

            var imagePort = RoiImageInputPort(targetNode.Type, targetPort.Name);
            if (imagePort is null) continue;
            var roiFrame = ResolveRoiFrame(edge.SourceNodeId, edge.SourcePort, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
            var imageFrame = ResolveImageInputFrame(targetNode.Id, imagePort, workflow, byId);
            if (roiFrame is not { IsTraceable: true } || imageFrame is not { IsTraceable: true }
                || string.Equals(roiFrame.Key, imageFrame.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            throw new InvalidOperationException(
                $"Node '{targetNode.Id}' ROI input '{targetPort.Name}' is in frame '{roiFrame.Label}', but its '{imagePort}' image is in frame '{imageFrame.Label}'. " +
                "Connect an ROI from the same image frame; for a crop, use the croppedRoi output from the crop that supplies the image.");
        }
    }

    private static string? RoiImageInputPort(string nodeType, string roiPort)
    {
        if (nodeType.Equals("image.crop", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("region.fromRoi", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("feature.edgeSubpixel", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("measure.caliper", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("measure.caliperWidth", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("measure.caliperArray", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("measure.caliperLine", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase))
            return "image";
        if (nodeType.Equals("roi.transform", StringComparison.OrdinalIgnoreCase)) return "debugImage";
        if (nodeType.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
            return roiPort.Equals("searchRoi", StringComparison.OrdinalIgnoreCase) ? "image"
                : roiPort.Equals("roi", StringComparison.OrdinalIgnoreCase) || roiPort.Equals("clutterRoi", StringComparison.OrdinalIgnoreCase)
                    ? "template" : null;
        return null;
    }

    private CoordinateFrame? ResolveRoiFrame(
        string nodeId,
        string outputPort,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        if (node.Type.Equals("image.crop", StringComparison.OrdinalIgnoreCase)
            && outputPort.Equals("croppedRoi", StringComparison.OrdinalIgnoreCase))
            return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
        if (node.Type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase)
            && outputPort.Equals("roi", StringComparison.OrdinalIgnoreCase))
            return ResolveImageInputFrame(nodeId, "debugImage", workflow, byId);
        return null;
    }

    private CoordinateFrame? ResolveGeometryFrame(
        string nodeId,
        string outputPort,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var type = node.Type;
        if (type.Equals("image.crop", StringComparison.OrdinalIgnoreCase))
            return outputPort.Equals("cropOrigin", StringComparison.OrdinalIgnoreCase)
                ? ResolveImageInputFrame(nodeId, "image", workflow, byId)
                : null;
        if (type.Equals("image.resize", StringComparison.OrdinalIgnoreCase)
            || type.Equals("image.rotate", StringComparison.OrdinalIgnoreCase)
            || type.Equals("image.perspective", StringComparison.OrdinalIgnoreCase))
            return null;

        if (type.Equals("roi.transform", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "roi", "rectangle", "circle", "center", "pose"))
            return ResolveImageInputFrame(nodeId, "debugImage", workflow, byId);

        if (type.Equals("match.shape", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "center", "rectangle", "pose"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        if (type.Equals("jlvision.featureExtract", StringComparison.OrdinalIgnoreCase))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        if (type.Equals("measure.fitRectangle", StringComparison.OrdinalIgnoreCase))
        {
            var input = FindInputEdge(workflow.Edges, nodeId, "contours");
            return input is null ? null : ResolveContourFrame(input.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
        }
        if (type.Equals("geometry.intersections", StringComparison.OrdinalIgnoreCase))
        {
            var first = node.GetString("operator", "lineCircle") == "circles" ? "circle" : "line";
            var input = FindInputEdge(workflow.Edges, nodeId, first);
            return input is null ? null : ResolveGeometryFrame(input.SourceNodeId, input.SourcePort, workflow, byId, visited);
        }

        if (type.Equals("feature.line", StringComparison.OrdinalIgnoreCase) && IsOneOf(outputPort, "line"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("feature.circle", StringComparison.OrdinalIgnoreCase) && IsOneOf(outputPort, "circle", "center"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        if (type.Equals("feature.edgeSubpixel", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "point", "searchLine"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("measure.caliper", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "point", "searchLine"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("measure.caliperLine", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "point", "line", "searchLine"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "circle", "center"))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        if (type.Equals("collection.detections.at", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "center", "circle", "rectangle"))
            return ResolveDetectionInputFrame(nodeId, workflow, byId, visited);
        if (type.Equals("flow.forEach", StringComparison.OrdinalIgnoreCase)
            && IsOneOf(outputPort, "center", "circle", "rectangle"))
            return ResolveDetectionInputFrame(nodeId, workflow, byId, visited);

        if (type.Equals("measure.fitLine", StringComparison.OrdinalIgnoreCase) && IsOneOf(outputPort, "line")
            || type.Equals("measure.fitCircle", StringComparison.OrdinalIgnoreCase) && IsOneOf(outputPort, "circle", "center"))
            return ResolveFittedPointFrame(nodeId, workflow, byId, visited);

        var catalog = registry.Require(type).Catalog;
        var output = catalog.Outputs.FirstOrDefault(port => port.Name.Equals(outputPort, StringComparison.OrdinalIgnoreCase));
        // Image-producing measurement nodes emit geometry in the same frame as their image
        // output. Resolve that output path so detections and blob circles are checked too.
        var isImageGeometry = output?.DataType is VisionDataType.Line2D or VisionDataType.Circle
            or VisionDataType.Point2D or VisionDataType.Rectangle2D or VisionDataType.Pose2D;
        if (isImageGeometry && catalog.Outputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image))
            return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));

        return null;
    }

    private CoordinateFrame? ResolveContourFrame(string nodeId, WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId, HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        if (node.Type == "jlvision.worldPlane") return new($"world:{nodeId}:plane:{node.GetString("unit", "mm")}", $"{nodeId}.contours (world plane)");
        if (node.Type == "jlvision.dxf" && node.GetString("operator", "read") == "read") return new($"cad:{nodeId}:{node.GetString("unit", "unspecified")}", $"{nodeId}.contours (CAD coordinates)");
        var catalog = registry.Require(node.Type).Catalog;
        if (catalog.Inputs.Any(p => p.Name == "image")) return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        var input = workflow.Edges.FirstOrDefault(e => e.TargetNodeId == nodeId && !WorkflowGraph.IsControlEdge(e) && catalog.Inputs.Any(p => p.Name == e.TargetPort && p.DataType == VisionDataType.Contour));
        return input is null ? null : ResolveContourFrame(input.SourceNodeId, workflow, byId, visited);
    }

    private CoordinateFrame? ResolveDetectionInputFrame(
        string nodeId,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        var edge = FindInputEdge(workflow.Edges, nodeId, "detections");
        return edge is null ? null : ResolveDetectionFrame(edge.SourceNodeId, workflow, byId, visited);
    }

    private CoordinateFrame? ResolveDetectionFrame(
        string nodeId,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var type = node.Type;
        if (type.Equals("jlvision.featureExtract", StringComparison.OrdinalIgnoreCase))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("measure.blobs", StringComparison.OrdinalIgnoreCase)
            || type.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);
        if (type.Equals("collection.detections.filter", StringComparison.OrdinalIgnoreCase)
            || type.Equals("collection.detections.sort", StringComparison.OrdinalIgnoreCase)
            || type.Equals("flow.forEach", StringComparison.OrdinalIgnoreCase))
            return ResolveDetectionInputFrame(nodeId, workflow, byId, visited);
        if (type.Equals("region.toDetections", StringComparison.OrdinalIgnoreCase))
        {
            var region = FindInputEdge(workflow.Edges, nodeId, "region");
            return region is null ? null : ResolveRegionFrame(region.SourceNodeId, region.SourcePort, workflow, byId, visited);
        }
        return null;
    }

    private CoordinateFrame? ResolveRegionFrame(
        string nodeId,
        string outputPort,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var type = node.Type;
        var catalog = registry.Require(type).Catalog;
        if (!catalog.Outputs.Any(port => port.Name.Equals(outputPort, StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Region))
            return null;

        if (type.Equals("image.warpAffine", StringComparison.OrdinalIgnoreCase))
            return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
        if (type.Equals("region.transform", StringComparison.OrdinalIgnoreCase))
        {
            var transformEdge = FindInputEdge(workflow.Edges, nodeId, "transform");
            return transformEdge is null ? null
                : ResolveTransformFrames(transformEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase))?.Target;
        }

        var regionInputs = workflow.Edges.Where(edge => edge.TargetNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
                && !WorkflowGraph.IsControlEdge(edge)
                && catalog.Inputs.Any(port => port.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase)
                    && port.DataType == VisionDataType.Region))
            .ToArray();
        if (regionInputs.Length > 0)
        {
            var frames = regionInputs.Select(edge => ResolveRegionFrame(edge.SourceNodeId, edge.SourcePort,
                workflow, byId, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase))).ToArray();
            if (frames.Any(frame => frame is not { IsTraceable: true })) return null;
            var distinctFrames = frames.Cast<CoordinateFrame>()
                .GroupBy(frame => frame.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (distinctFrames.Length > 1)
                throw new InvalidOperationException(
                    $"Node '{nodeId}' cannot combine regions from different image frames ('{distinctFrames[0].Label}' and '{distinctFrames[1].Label}'). " +
                    "Connect regions from the same image frame.");
            return distinctFrames[0];
        }

        if (type.Equals("region.fromRoi", StringComparison.OrdinalIgnoreCase))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        if (catalog.Outputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image)
            && catalog.Inputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image))
            return ResolveImageInputFrame(nodeId, "image", workflow, byId);

        return null;
    }

    private CoordinateFrame? ResolveFittedPointFrame(
        string nodeId,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        var pointEdges = workflow.Edges.Where(edge => edge.TargetNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
                && !WorkflowGraph.IsControlEdge(edge)
                && registry.Require(byId[nodeId].Type).Catalog.Inputs.Any(port => port.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase)
                    && port.DataType == VisionDataType.Point2D))
            .ToArray();
        if (pointEdges.Length == 0) return null;

        var pointFrames = pointEdges.Select(edge => ResolveGeometryFrame(edge.SourceNodeId, edge.SourcePort,
            workflow, byId, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (pointFrames.Any(frame => frame is null || !frame.IsTraceable)) return null;
        var distinctFrames = pointFrames.Cast<CoordinateFrame>()
            .GroupBy(frame => frame.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (distinctFrames.Length > 1)
            throw new InvalidOperationException(
                $"Node '{nodeId}' cannot fit points from different image frames ('{distinctFrames[0].Label}' and '{distinctFrames[1].Label}'). " +
                "Connect points from the same image frame.");
        return distinctFrames[0];
    }

    private CoordinateFrame? ResolveImageInputFrame(
        string nodeId,
        string inputPort,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        var edge = FindInputEdge(workflow.Edges, nodeId, inputPort);
        return edge is null
            ? null
            : ResolveImageFrame(edge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsOneOf(string value, params string[] expected)
        => expected.Any(item => value.Equals(item, StringComparison.OrdinalIgnoreCase));

    private CoordinateFrame? ResolveImageFrame(
        string nodeId,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var type = node.Type;
        var catalog = registry.Require(type).Catalog;
        var rule = NodeFrameSemantics.Resolve(node, catalog);
        if (rule is not null)
        {
            switch (rule.Policy)
            {
                case NodeImageFramePolicy.PreserveInput:
                    return ResolveImageInputFrame(nodeId, rule.InputPort, workflow, byId)
                        ?? new CoordinateFrame($"image:{nodeId}:source", $"{nodeId}.image");
                case NodeImageFramePolicy.NewSpatialFrame:
                    return new CoordinateFrame($"image:{nodeId}:spatial", NodeFrameSemantics.FrameLabel(nodeId, rule, "spatial frame"));
                case NodeImageFramePolicy.NewFixedFrame:
                    return new CoordinateFrame(
                        $"image:{nodeId}:{rule.FrameSuffix ?? "output"}",
                        NodeFrameSemantics.FrameLabel(nodeId, rule, rule.FrameSuffix ?? "output"));
                case NodeImageFramePolicy.TransformTarget:
                {
                    var target = ResolveTransformTargetFrame(nodeId, rule.TransformPort ?? "transform", workflow, byId);
                    return target ?? new CoordinateFrame($"image:{nodeId}:spatial", NodeFrameSemantics.FrameLabel(nodeId, rule, "spatial frame"));
                }
                case NodeImageFramePolicy.InputFrame:
                    return ResolveImageInputFrame(nodeId, rule.InputPort, workflow, byId);
            }
        }

        // Undeclared nodes (plugins that did not declare frames) keep the previous default:
        // image outputs inherit the connected image input, or get a private source frame.
        if (!catalog.Outputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image))
            return null;
        if (catalog.Inputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image))
        {
            var imageInput = FindInputEdge(workflow.Edges, nodeId, "image");
            return imageInput is null
                ? null
                : ResolveImageFrame(imageInput.SourceNodeId, workflow, byId, visited);
        }

        return new CoordinateFrame($"image:{nodeId}:source", $"{nodeId}.image");
    }

    private CoordinateFrame? ResolveTransformTargetFrame(
        string nodeId,
        string port,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        var edge = FindInputEdge(workflow.Edges, nodeId, port);
        return edge is null
            ? null
            : ResolveTransformFrames(edge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase))?.Target;
    }

    private static EdgeDefinition? FindInputEdge(IReadOnlyList<EdgeDefinition> edges, string nodeId, string port)
        => edges.FirstOrDefault(edge => edge.TargetNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
            && edge.TargetPort.Equals(port, StringComparison.OrdinalIgnoreCase)
            && !WorkflowGraph.IsControlEdge(edge));

    /// <summary>
    /// Validates catalog-declared parameters before compilation so bad recipes are rejected instead of
    /// silently falling back to defaults inside the JsonParameters getters. Parameters absent from the
    /// node use their declared defaults; undeclared keys (for example the ROI payload consumed by
    /// VisionRoiParameterExtensions) are intentionally ignored.
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
        if (catalog.Type == "device.writeTag" && parameterName is "valueType" or "fallbackValue"
            && connectedInputPorts.Contains("value")) return true;
        if (catalog.Type is "coordinate.point" or "coordinate.point3D" or "coordinate.pose"
            && parameterName is "x" or "y" or "z" or "thetaDeg"
            && connectedInputPorts.Contains(parameterName)
            && catalog.Inputs.Any(input => input.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase)
                && input.DataType == VisionDataType.Double)) return true;
        if (catalog.Type is "text.match" or "text.validate" && parameterName is "text" or "expected"
            && connectedInputPorts.Contains(parameterName)) return true;
        if (catalog.Type == "jlvision.matrixOp" && parameterName is "matrixA" or "matrixB" && connectedInputPorts.Contains(parameterName))
            return true;
        if (catalog.Type == "output.saveImage" && parameterName is "path" or "partId" && connectedInputPorts.Contains(parameterName)) return true;
        if (catalog.Type == "output.httpRequest" &&
            (parameterName is "url" or "body" or "idempotencyKey" && connectedInputPorts.Contains(parameterName)
                || parameterName is "body" or "contentType" && connectedInputPorts.Contains("value"))) return true;
        var hasConnectedSearchLine = connectedInputPorts.Contains("searchLine")
            && catalog.Inputs.Any(input => input.Name.Equals("searchLine", StringComparison.OrdinalIgnoreCase)
                && input.DataType == VisionDataType.Line2D);
        if (hasConnectedSearchLine
            && (catalog.Type.Equals("feature.edgeSubpixel", StringComparison.OrdinalIgnoreCase)
                || catalog.Type.Equals("measure.caliper", StringComparison.OrdinalIgnoreCase)
                || catalog.Type.Equals("measure.caliperWidth", StringComparison.OrdinalIgnoreCase)
                || catalog.Type.Equals("measure.caliperArray", StringComparison.OrdinalIgnoreCase)
                || catalog.Type.Equals("measure.caliperLine", StringComparison.OrdinalIgnoreCase))
            && parameterName is "startX" or "startY" or "endX" or "endY")
            return true;

        var hasConnectedCircle = connectedInputPorts.Contains("circle")
            && catalog.Inputs.Any(input => input.Name.Equals("circle", StringComparison.OrdinalIgnoreCase)
                && input.DataType == VisionDataType.Circle);
        return hasConnectedCircle
            && catalog.Type.Equals("measure.caliperCircle", StringComparison.OrdinalIgnoreCase)
            && parameterName is "centerX" or "centerY" or "radius";
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
            var actual = node.Parameters is not null && node.Parameters.TryGetValue(controller.Name, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : controller.DefaultValue?.ToString();
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
        CompileContext context,
        string? loopNodeId = null)
    {
        var steps = new List<Dictionary<string, object?>>();
        var items = sequence.Items;

        for (var i = 0; i < items.Count;)
        {
            if (loopNodeId is null && items[i] is StructuredNodeIr firstNode && IsPipelineEligible(firstNode.Node))
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
                    steps.Add(CreateVisionStep(node.Node.Id, nextStepId, loopNodeId));
                    break;

                case StructuredIfIr conditional:
                    steps.AddRange(CompileIf(conditional, nextStepId, context, loopNodeId));
                    break;

                case StructuredSwitchIr branch:
                    steps.AddRange(CompileSwitch(branch, nextStepId, context, loopNodeId));
                    break;

                case StructuredTryIr attempt:
                    ValidateTryBody(attempt, context.Edges);
                    steps.AddRange(CompileTry(attempt, nextStepId, context, loopNodeId));
                    break;

                case StructuredForEachIr loop:
                    steps.AddRange(CompileForEach(loop, nextStepId, context, loopNodeId));
                    break;

                case StructuredParallelIr parallel:
                    steps.AddRange(CompileParallel(parallel, nextStepId, context, loopNodeId));
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
        CompileContext context,
        string? loopNodeId = null)
    {
        var node = conditional.Node;
        var truePrimitiveId = $"{node.Id}.__if_true";
        var falsePrimitiveId = $"{node.Id}.__if_false";
        var conditionExpression = $"data.BranchConditions[{JsonSerializer.Serialize(node.Id)}]";

        var marker = CreateVisionStep(node.Id, truePrimitiveId, loopNodeId);
        var truePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = truePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"data.Halted == false && {conditionExpression}" },
            ["Do"] = new object[] { CompileSequence(conditional.TrueBranch, continuationStepId: null, context, loopNodeId).ToArray() },
            ["NextStepId"] = falsePrimitiveId,
            ["ErrorBehavior"] = "Terminate"
        };
        var falsePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = falsePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"data.Halted == false && {conditionExpression} == false" },
            ["Do"] = new object[] { CompileSequence(conditional.FalseBranch, continuationStepId: null, context, loopNodeId).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            falsePrimitive["NextStepId"] = continuationStepId;

        return [marker, truePrimitive, falsePrimitive];
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileSwitch(
        StructuredSwitchIr branch,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var steps = new List<Dictionary<string, object?>>();
        var firstPrimitiveId = branch.Branches.Count == 0 ? continuationStepId : $"{branch.Node.Id}.__switch_{branch.Branches[0].Port}";
        steps.Add(CreateVisionStep(branch.Node.Id, firstPrimitiveId, loopNodeId));
        for (var index = 0; index < branch.Branches.Count; index++)
        {
            var item = branch.Branches[index];
            var primitiveId = $"{branch.Node.Id}.__switch_{item.Port}";
            var condition = $"data.Halted == false && data.SwitchSelections[{JsonSerializer.Serialize(branch.Node.Id)}] == {JsonSerializer.Serialize(item.Port)}";
            var primitive = new Dictionary<string, object?>
            {
                ["Id"] = primitiveId,
                ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
                ["Inputs"] = new Dictionary<string, string> { ["Condition"] = condition },
                ["Do"] = new object[] { CompileSequence(item.Sequence, continuationStepId: null, context, loopNodeId).ToArray() },
                ["ErrorBehavior"] = "Terminate"
            };
            if (index == branch.Branches.Count - 1 && continuationStepId is not null) primitive["NextStepId"] = continuationStepId;
            else if (index + 1 < branch.Branches.Count) primitive["NextStepId"] = $"{branch.Node.Id}.__switch_{branch.Branches[index + 1].Port}";
            steps.Add(primitive);
        }
        return steps;
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileTry(
        StructuredTryIr attempt,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var successId = $"{attempt.Node.Id}.__try_success";
        var errorId = $"{attempt.Node.Id}.__try_error";
        var successCondition = $"data.TrySucceeded[{JsonSerializer.Serialize(attempt.Node.Id)}]";
        var marker = CreateVisionStep(attempt.Node.Id, successId, loopNodeId);
        var success = new Dictionary<string, object?>
        {
            ["Id"] = successId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = successCondition },
            ["Do"] = new object[] { CompileSequence(attempt.Success, continuationStepId: null, context, loopNodeId).ToArray() },
            ["NextStepId"] = errorId,
            ["ErrorBehavior"] = "Terminate"
        };
        var error = new Dictionary<string, object?>
        {
            ["Id"] = errorId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"{successCondition} == false" },
            ["Do"] = new object[] { CompileSequence(attempt.Error, continuationStepId: null, context, loopNodeId).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null) error["NextStepId"] = continuationStepId;
        return [marker, success, error];
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileForEach(
        StructuredForEachIr loop,
        string? continuationStepId,
        CompileContext context,
        string? enclosingLoopNodeId)
    {
        var primitiveId = $"{loop.Node.Id}.__foreach";
        var marker = CreateVisionStep(loop.Node.Id, primitiveId, enclosingLoopNodeId);
        var foreachPrimitive = new Dictionary<string, object?>
        {
            ["Id"] = primitiveId,
            ["StepType"] = "WorkflowCore.Primitives.Foreach, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string>
            {
                ["Collection"] = $"data.ForEachCollections[{JsonSerializer.Serialize(loop.Node.Id)}]",
                ["RunParallel"] = "false"
            },
            ["Do"] = new object[] { CompileSequence(loop.Body, continuationStepId: null, context, loop.Node.Id).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            foreachPrimitive["NextStepId"] = continuationStepId;
        return [marker, foreachPrimitive];
    }

    private void ValidateTryBody(StructuredTryIr attempt, IReadOnlyList<EdgeDefinition> edges)
    {
        if (attempt.Body.Items.Any(item => item is not StructuredNodeIr))
            throw new InvalidOperationException($"Try '{attempt.Node.Id}' body must be a linear sequence of pure compute nodes; nested control regions are not supported.");
        var bodyIds = attempt.Body.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validConsumers = bodyIds.Concat(attempt.Success.NodeIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidEdge = edges.FirstOrDefault(edge => !WorkflowGraph.IsControlEdge(edge) &&
            bodyIds.Contains(edge.SourceNodeId) && !validConsumers.Contains(edge.TargetNodeId));
        if (invalidEdge is not null)
            throw new InvalidOperationException($"Try '{attempt.Node.Id}' body output '{invalidEdge.SourceNodeId}.{invalidEdge.SourcePort}' cannot flow to '{invalidEdge.TargetNodeId}.{invalidEdge.TargetPort}'; body values may only be consumed inside the body or success route, never error/outside routes.");
        foreach (var bodyNode in attempt.Body.Items.Cast<StructuredNodeIr>().Select(x => x.Node))
        {
            var catalog = registry.Require(bodyNode.Type).Catalog;
            if (!string.Equals(catalog.PluginId, "builtin", StringComparison.OrdinalIgnoreCase) ||
                VisionExecutionPolicy.Resolve(catalog) != VisionExecutionMode.Pipeline ||
                catalog.Capabilities?.Deterministic == false || catalog.Actions is not { Count: 0 })
                throw new InvalidOperationException($"Try '{attempt.Node.Id}' cannot retry body node '{bodyNode.Id}' ({bodyNode.Type}); automatic retry requires a deterministic built-in Pipeline node with an explicit empty Actions declaration.");
        }
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileParallel(
        StructuredParallelIr parallel,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        ValidateParallelCapabilities(parallel.Branch1, parallel.Node.Id, "branch1");
        ValidateParallelCapabilities(parallel.Branch2, parallel.Node.Id, "branch2");

        var sequenceId = $"{parallel.Node.Id}.__parallel";
        // V0.64: 两条分支若全部由可融合节点组成，则合并为一个流水线段，由 VisionPipelineExecutor
        // 按分支分组并发执行（此前用 Sequence 顺序执行两条分支，时间线上永远不会重叠）。
        // 含非融合节点（flow.result、设备节点等）的分支仍回退为顺序 Sequence，语义保守。
        if (loopNodeId is null && TryCollectFusedBranchNodes(parallel.Branch1, out var branch1Nodes) &&
            TryCollectFusedBranchNodes(parallel.Branch2, out var branch2Nodes))
        {
            var fusedNodes = branch1Nodes.Concat(branch2Nodes).ToArray();
            var fusedMarker = CreateVisionStep(parallel.Node.Id, sequenceId, loopNodeId);
            return [fusedMarker, CreatePipelineStep(fusedNodes, continuationStepId, context, stepIdOverride: sequenceId)];
        }

        var marker = CreateVisionStep(parallel.Node.Id, sequenceId, loopNodeId);
        var sequence = new Dictionary<string, object?>
        {
            ["Id"] = sequenceId,
            ["StepType"] = "WorkflowCore.Primitives.Sequence, WorkflowCore",
            ["Do"] = new object[]
            {
                CompileSequence(parallel.Branch1, continuationStepId: null, context, loopNodeId).ToArray(),
                CompileSequence(parallel.Branch2, continuationStepId: null, context, loopNodeId).ToArray()
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
                case StructuredSwitchIr branch:
                    yield return branch.Node;
                    foreach (var child in branch.Branches)
                        foreach (var node in EnumerateNodes(child.Sequence)) yield return node;
                    break;
                case StructuredTryIr attempt:
                    yield return attempt.Node;
                    foreach (var child in EnumerateNodes(attempt.Body)) yield return child;
                    foreach (var child in EnumerateNodes(attempt.Success)) yield return child;
                    foreach (var child in EnumerateNodes(attempt.Error)) yield return child;
                    break;
                case StructuredForEachIr loop:
                    yield return loop.Node;
                    foreach (var child in EnumerateNodes(loop.Body)) yield return child;
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
        StructuredSwitchIr branch => branch.Node.Id,
        StructuredTryIr attempt => attempt.Node.Id,
        StructuredForEachIr loop => loop.Node.Id,
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

        public CompileContext(IReadOnlyList<EdgeDefinition> edges) => Edges = edges;
        public IReadOnlyList<EdgeDefinition> Edges { get; }

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
