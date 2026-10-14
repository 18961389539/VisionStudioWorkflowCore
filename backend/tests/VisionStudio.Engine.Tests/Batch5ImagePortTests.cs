using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch5ImagePortTests
{
    [Fact]
    public void NewPayloadTypes_PreserveExistingPluginEnumValues()
    {
        Assert.Equal(14, (int)VisionDataType.DetectionList);
        Assert.Equal(15, (int)VisionDataType.Transform2D);
        Assert.Equal(22, (int)VisionDataType.Measurement);
    }

    [Fact]
    public async Task RoiAndContourProperties_RemainStructuredInExecutionReports()
    {
        var roi = new NodeDefinition("roi", "roi.transform", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"roi":{"type":"Rectangle","x":10,"y":10,"width":20,"height":15}}"""));
        var props = new NodeDefinition("properties", "contour.properties", null, null, null);
        var contour = new VisionContour(0, [new(10, 10), new(30, 10), new(30, 25), new(10, 25)],
            -1, false, 300, 70, 0.7, new(10, 10, 21, 16), new(20, 17.5, 13));
        using var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require(roi.Type), new RoiTransformNode(), "builtin");
        registry.Register(BuiltInNodeCatalog.Require(props.Type), new ContourPropertiesNode(), "builtin");
        using var data = new VisionWorkflowData
        {
            Definition = new("snapshots", "snapshots", [roi, props],
                [new("contours", "source", "contours", props.Id, "contours", "data")])
        };
        data.OutputsByNode["source"] = new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours([contour]) };
        var runtime = new VisionNodeRuntime(new VisionNodeDispatcher(registry));
        await runtime.ExecuteAsync(data, roi.Id, default);
        await runtime.ExecuteAsync(data, props.Id, default);
        var reports = data.SnapshotReports();
        Assert.IsType<VisionRoi>(reports.Single(report => report.NodeId == roi.Id).Outputs!["roi"].Value);
        Assert.IsAssignableFrom<IReadOnlyList<VisionContourProperties>>(reports.Single(report => report.NodeId == props.Id).Outputs!["properties"].Value);
    }

    [Theory]
    [InlineData("contour.extract", false)]
    [InlineData("contour.extract", true)]
    [InlineData("feature.edgeSubpixel", false)]
    [InlineData("feature.edgeSubpixel", true)]
    [InlineData("measure.caliperLine", false)]
    [InlineData("measure.caliperLine", true)]
    [InlineData("measure.caliperCircle", false)]
    [InlineData("measure.caliperCircle", true)]
    [InlineData("match.shape", false)]
    [InlineData("match.shape", true)]
    public async Task DeclaredImageOutput_CanFeedDownstreamNode_AndDoesNotOwnInput(string type, bool blank)
    {
        using var mat = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        if (!blank) Cv2.Circle(mat, new Point(120, 120), 50, Scalar.All(255), -1);
        using var input = VisionImage.Own(mat.Clone());
        IVisionNodeExecutor executor = type switch
        {
            "contour.extract" => new ContourExtractNode(),
            "feature.edgeSubpixel" => new EdgeSubpixelNode(),
            "measure.caliperLine" => new CaliperLineNode(),
            "measure.caliperCircle" => new CaliperCircleNode(),
            _ => new ShapeMatchNode()
        };
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"startX":20,"startY":120,"endX":220,"endY":120,"centerX":120,"centerY":120,"radius":50,"radialBand":10,"minArea":100}""");
        var node = new NodeDefinition("tool", type, null, null, parameters);
        var target = new NodeDefinition("next", "image.threshold", null, null, null);
        using var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require(type), executor, "builtin");
        registry.Register(BuiltInNodeCatalog.Require(target.Type), new ThresholdNode(), "builtin");
        var dispatcher = new VisionNodeDispatcher(registry);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) };
        if (type == "match.shape") inputs["template"] = VisionValue.Image(input);
        var result = await dispatcher.ExecuteAsync(node, inputs, null, default);
        using var output = Assert.IsType<VisionImage>(result.Outputs["image"].Value);
        Assert.Equal((240, 240), (output.Width, output.Height));
        Assert.Equal(0, Cv2.Norm(mat, input.Mat));
        if (blank) Assert.False(result.Outputs["found"].Require<bool>("found"));

        using var data = new VisionWorkflowData
        {
            Definition = new("image-chain", "image-chain", [node, target],
                [new("image-edge", node.Id, "image", target.Id, "image", "data")])
        };
        data.OutputsByNode[node.Id] = result.Outputs;
        var next = await dispatcher.ExecuteAsync(target, dispatcher.ResolveInputs(data, target), null, default);
        using var thresholded = Assert.IsType<VisionImage>(next.Outputs["image"].Value);
        Assert.Equal((240, 240), (thresholded.Width, thresholded.Height));
        output.Dispose();
        Assert.False(input.Mat.Empty());
        Assert.Equal(blank ? 0 : 255, input.Mat.At<byte>(120, 120));
    }
}
