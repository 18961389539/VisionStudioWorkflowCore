using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

public sealed partial class VisionWorkflowCompiler
{
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

    /// <summary>
    /// 需要"几何/图像必须与 transform 帧一致"校验的节点：带 transform + image 输入，且图像本身不是
    /// transform 结果。image.warpAffine 声明 TransformTarget（输出图像 = 变换结果），输入图像在
    /// transform 的 source 帧，由 ValidateImageWarpFrames 单独校验。规则来自目录声明，不再维护
    /// 六个测量类型的硬编码名单——新增测量节点自动受保护。
    /// </summary>
    private bool SamplesGeometryAgainstTransformedImage(NodeDefinition node)
    {
        var catalog = registry.Require(node.Type).Catalog;
        if (!catalog.Inputs.Any(port => port.Name.Equals("transform", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Transform2D))
            return false;
        if (!catalog.Inputs.Any(port => port.Name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Image))
            return false;
        return NodeFrameSemantics.Resolve(node, catalog)?.Policy != NodeImageFramePolicy.TransformTarget;
    }

    private void ValidateMeasurementImageFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        foreach (var node in workflow.Nodes.Where(SamplesGeometryAgainstTransformedImage))
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

        if (catalog.TransformFrames is { } transformDeclaration)
        {
            switch (transformDeclaration.Policy)
            {
                case NodeTransformFramePolicy.TemplateToImage:
                {
                    var templateEdge = FindInputEdge(workflow.Edges, nodeId, transformDeclaration.TemplatePort);
                    var imageEdge = FindInputEdge(workflow.Edges, nodeId, transformDeclaration.ImagePort);
                    var source = templateEdge is null
                        ? new CoordinateFrame($"match:{nodeId}:template", $"{nodeId} trained template", IsTraceable: false)
                        : ResolveImageFrame(templateEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
                    var target = imageEdge is null
                        ? null
                        : ResolveImageFrame(imageEdge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
                    return new TransformFrames(source, target, target is null ? [] : [target]);
                }
                case NodeTransformFramePolicy.Compose:
                {
                    TransformFrames? ResolveComposePort(string port)
                    {
                        var edge = FindInputEdge(workflow.Edges, nodeId, port);
                        return edge is null
                            ? null
                            : ResolveTransformFrames(edge.SourceNodeId, workflow, byId,
                                new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
                    }

                    // 与运行时 ComposeTransformNode 保持一致：innerOuter 执行 Compose(B, A)，
                    // 帧链方向随之反过来（B.Target 必须衔接 A.Source）。
                    var innerOuter = node.GetString("order", "outerInner").Trim()
                        .Equals("innerOuter", StringComparison.OrdinalIgnoreCase);
                    var firstPort = innerOuter ? transformDeclaration.SecondaryPort ?? string.Empty : transformDeclaration.PrimaryPort;
                    var secondPort = innerOuter ? transformDeclaration.PrimaryPort : transformDeclaration.SecondaryPort ?? string.Empty;
                    var first = ResolveComposePort(firstPort);
                    var second = ResolveComposePort(secondPort);
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
                case NodeTransformFramePolicy.Invert:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, transformDeclaration.PrimaryPort);
                    var source = edge is null ? null : ResolveTransformFrames(edge.SourceNodeId, workflow, byId, visited);
                    if (source is null) return null;
                    var path = (source.Source is null ? source.MappedFrames : [source.Source, .. source.MappedFrames]).ToArray();
                    return new TransformFrames(source.Target, source.Source, path.Reverse().Skip(1).ToArray());
                }
            }
        }

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
        return null;
    }

    /// <summary>
    /// Connected line and circle geometry must match the sampled image frame, or the source frame
    /// of an explicit transform. Pixel coordinates from a cropped branch are otherwise plausible
    /// values that silently point at a different location.
    /// </summary>
    private void ValidateMeasurementGeometryFrames(WorkflowDefinition workflow, IReadOnlyDictionary<string, NodeDefinition> byId)
    {
        foreach (var node in workflow.Nodes.Where(SamplesGeometryAgainstTransformedImage))
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

            var imagePort = RoiImageInputPort(targetCatalog, targetPort.Name);
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

    private static string? RoiImageInputPort(NodeCatalogItem catalog, string roiPort)
        => catalog.RoiImageBindings?
            .FirstOrDefault(binding => binding.RoiPort.Equals(roiPort, StringComparison.OrdinalIgnoreCase))
            ?.ImagePort;

    private CoordinateFrame? ResolveRoiFrame(
        string nodeId,
        string outputPort,
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, NodeDefinition> byId,
        HashSet<string> visited)
    {
        if (!visited.Add(nodeId) || !byId.TryGetValue(nodeId, out var node)) return null;
        var catalog = registry.Require(node.Type).Catalog;
        var rule = NodeFrameSemantics.ResolveOutput(node, catalog, outputPort);
        if (rule?.Source == NodeOutputFrameSource.ImageOutput)
            return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
        if (rule?.Source == NodeOutputFrameSource.ImageInput)
            return ResolveImageInputFrame(nodeId, rule.InputPort ?? "image", workflow, byId);
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
        var catalog = registry.Require(type).Catalog;
        var rule = NodeFrameSemantics.ResolveOutput(node, catalog, outputPort);
        if (rule is not null)
        {
            switch (rule.Source)
            {
                case NodeOutputFrameSource.ImageInput:
                    return ResolveImageInputFrame(nodeId, rule.InputPort ?? "image", workflow, byId);
                case NodeOutputFrameSource.ImageOutput:
                    return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
                case NodeOutputFrameSource.GeometryInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "line");
                    return edge is null
                        ? null
                        : ResolveGeometryFrame(edge.SourceNodeId, edge.SourcePort, workflow, byId, visited);
                }
                case NodeOutputFrameSource.DetectionInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "detections");
                    return edge is null ? null : ResolveDetectionFrame(edge.SourceNodeId, workflow, byId, visited);
                }
                case NodeOutputFrameSource.RegionInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "region");
                    return edge is null
                        ? null
                        : ResolveRegionFrame(edge.SourceNodeId, edge.SourcePort, workflow, byId, visited);
                }
                case NodeOutputFrameSource.ContourInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "contours");
                    return edge is null
                        ? null
                        : ResolveContourFrame(edge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
                }
                case NodeOutputFrameSource.FittedPoints:
                    return ResolveFittedPointFrame(nodeId, workflow, byId, visited);
            }
        }

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
        var catalog = registry.Require(type).Catalog;
        var rule = NodeFrameSemantics.ResolveOutput(node, catalog, "detections");
        if (rule is not null)
        {
            switch (rule.Source)
            {
                case NodeOutputFrameSource.ImageInput:
                    return ResolveImageInputFrame(nodeId, rule.InputPort ?? "image", workflow, byId);
                case NodeOutputFrameSource.DetectionInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "detections");
                    return edge is null ? null : ResolveDetectionFrame(edge.SourceNodeId, workflow, byId, visited);
                }
                case NodeOutputFrameSource.RegionInput:
                {
                    var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "region");
                    return edge is null
                        ? null
                        : ResolveRegionFrame(edge.SourceNodeId, edge.SourcePort, workflow, byId, visited);
                }
            }
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

        var rule = NodeFrameSemantics.ResolveOutput(node, catalog, outputPort);
        if (rule?.Source == NodeOutputFrameSource.ImageOutputFrame)
            return ResolveImageFrame(nodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase));
        if (rule?.Source == NodeOutputFrameSource.OutputTransformTarget)
        {
            var edge = FindInputEdge(workflow.Edges, nodeId, rule.InputPort ?? "transform");
            return edge is null ? null
                : ResolveTransformFrames(edge.SourceNodeId, workflow, byId, new(StringComparer.OrdinalIgnoreCase))?.Target;
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

        // region.fromRoi 由 OutputFrames 声明覆盖（ImageInput → image）。

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
}
