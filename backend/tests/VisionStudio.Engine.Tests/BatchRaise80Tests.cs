using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral coverage for catalog descriptions, diagnostics and robustness gates.</summary>
public sealed class BatchRaise80Tests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    private static readonly string[] Band80Types =
    [
        "calibration.cameraIntrinsics", "calibration.handEye2D", "calibration.handEye3D", "calibration.dualCameraExtrinsics", "calibration.planar",
        "camera.syncCapture", "collection.detections.sort",
        "contour.extract",
        "coordinate.transformLine", "coordinate.distance", "coordinate.transformCompose", "coordinate.transformPoint",
        "coordinate.frameResolve", "coordinate.frameTree", "coordinate.transformCoordinatePoint", "coordinate.transformCoordinatePose",
        "coordinate.transformInverse", "coordinate.transformPoint3D", "coordinate.transformPose",
        "device.readTag", "device.writeVisionResult", "device.writeTag", "device.waitTag",
        "feature.edgeSubpixel", "feature.line",
        "flow.loopEnd", "flow.parallel", "flow.ifBoolean", "flow.join", "flow.delay", "flow.merge", "flow.result", "flow.try",
        "frameset.image",
        "geometry.angle",
        "image.acquire", "image.dynamicThreshold", "image.flatFieldCorrect", "image.illuminationNormalize", "image.sharpen",
        "inference.segmentation", "inference.instanceSegmentation", "inference.obb", "inference.pose",
        "logic.boolean",
        "measure.regionIntensity", "measure.caliper", "measure.caliperWidth", "measure.caliperArray", "measure.circleSpacing",
        "measure.parallelism", "measure.perpendicularity",
        "output.exportResult", "output.httpRequest", "output.saveImage",
        "region.connectedComponents", "region.fillHoles", "region.fromRoi",
        "robot.currentPose", "robot.executeTarget", "robot.guidance2d", "robot.j4TcpCompensation",
        "text.match", "text.regex", "text.validate",
        "value.number"
    ];

    [Fact]
    public void Catalog_Band80_ParametersDescribed_AndDiagnostics()
    {
        foreach (var type in Band80Types)
        {
            var item = BuiltInNodeCatalog.Items.Single(x => x.Type == type);
            Assert.True(item.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Description)),
                $"{type} params missing description: {string.Join(",", item.Parameters.Where(p => string.IsNullOrWhiteSpace(p.Description)).Select(p => p.Name))}");
            Assert.Contains(item.Outputs, o => o.Name == "diagnostics");
        }
    }

    [Fact]
    public async Task Preprocess_IlluminationAndSharpen_Gates()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(img, new Rect(8, 8, 12, 12), Scalar.All(200), -1);
        var illum = await new IlluminationNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.illuminationNormalize", """{"windowSize":15,"targetMean":128,"minBackground":1}"""), CancellationToken.None);
        Assert.Contains("image.illuminationNormalize", illum.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new IlluminationNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.illuminationNormalize", """{"targetMean":300}"""), CancellationToken.None));

        var sharp = await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.sharpen", """{"windowSize":5,"amount":1}"""), CancellationToken.None);
        Assert.Contains("image.sharpen", sharp.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.sharpen", """{"amount":99}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Region_ConnectedAndFillHoles_Diagnostics()
    {
        var region = Rect(4, 4, 20, 20);
        var cc = await new RegionConnectedComponentsNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.connectedComponents"), CancellationToken.None);
        Assert.Contains("region.connectedComponents", cc.Outputs["diagnostics"].Require<string>("diagnostics"));

        var fill = await new RegionFillHolesNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.fillHoles"), CancellationToken.None);
        Assert.Contains("region.fillHoles", fill.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ParallelismAndPerpendicularity_Gates()
    {
        var a = new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(40, 0));
        var b = new VisionLine2D(new VisionPoint2D(0, 5), new VisionPoint2D(40, 5.2));
        var par = await new ParallelismNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Line(a), ["b"] = VisionValue.Line(b) }),
            Def("measure.parallelism", """{"maxAngleErrorDeg":5}"""), CancellationToken.None);
        Assert.Contains("measure.parallelism", par.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ParallelismNode().ExecuteAsync(
                Ctx(new() { ["a"] = VisionValue.Line(a), ["b"] = VisionValue.Line(b) }),
                Def("measure.parallelism", """{"maxAngleErrorDeg":120}"""), CancellationToken.None));

        var c = new VisionLine2D(new VisionPoint2D(10, 0), new VisionPoint2D(10, 40));
        var perp = await new PerpendicularityNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Line(a), ["b"] = VisionValue.Line(c) }),
            Def("measure.perpendicularity", """{"maxAngleErrorDeg":5}"""), CancellationToken.None);
        Assert.Contains("measure.perpendicularity", perp.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ContourExtract_EmitsDiagnostics()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(8, 8, 24, 24), Scalar.All(255), -1);
        var result = await new ContourExtractNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("contour.extract", """{"mode":"external","binarize":"fixed","threshold":128,"minArea":10,"maxCount":10}"""), CancellationToken.None);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Contains("contour.extract", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ValueNumber_AndLogicBoolean_Depth()
    {
        var num = await new ValueNumberNode().ExecuteAsync(Ctx(new()), Def("value.number", """{"value":3.5}"""), CancellationToken.None);
        Assert.Equal(3.5, num.Outputs["value"].Require<double>("value"));
        Assert.Contains("value.number", num.Outputs["diagnostics"].Require<string>("diagnostics"));

        var logic = await new LogicBooleanNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Boolean(true), ["b"] = VisionValue.Boolean(false) }),
            Def("logic.boolean", """{"op":"and"}"""), CancellationToken.None);
        Assert.False(logic.Outputs["result"].Require<bool>("result"));
        Assert.Equal("and", logic.Outputs["operationApplied"].Require<string>("operationApplied"));
        Assert.Contains("logic.boolean", logic.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task TransformLine_AndDistance_Diagnostics()
    {
        var transform = VisionTransform2DMath.Rigid("A", "B", "mm", 5, 0, 0);
        var line = new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(10, 0));
        var xf = await new TransformLineNode().ExecuteAsync(
            Ctx(new() { ["line"] = VisionValue.Line(line), ["transform"] = VisionValue.Transform(transform) }),
            Def("coordinate.transformLine"), CancellationToken.None);
        Assert.Contains("coordinate.transformLine", xf.Outputs["diagnostics"].Require<string>("diagnostics"));

        var p1 = new VisionCoordinatePoint2D(0, 0, "A", "mm");
        var p2 = new VisionCoordinatePoint2D(3, 4, "A", "mm");
        var dist = await new CoordinateDistanceNode().ExecuteAsync(
            Ctx(new() { ["pointA"] = VisionValue.CoordinatePoint(p1), ["pointB"] = VisionValue.CoordinatePoint(p2) }),
            Def("coordinate.distance"), CancellationToken.None);
        Assert.Contains("coordinate.distance", dist.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Equal(5.0, dist.Outputs["distance"].Require<double>("distance"), 3);
    }

    [Fact]
    public void Inference_ExternalTrainKinds_IncludeSegmentationFamily()
    {
        Assert.True(Enum.IsDefined(typeof(VisionStudio.Engine.ExternalTrain.ExternalTrainKind), "Segmentation"));
        Assert.True(Enum.IsDefined(typeof(VisionStudio.Engine.ExternalTrain.ExternalTrainKind), "InstanceSegmentation"));
        Assert.True(Enum.IsDefined(typeof(VisionStudio.Engine.ExternalTrain.ExternalTrainKind), "Pose"));
        Assert.True(Enum.IsDefined(typeof(VisionStudio.Engine.ExternalTrain.ExternalTrainKind), "Obb"));
        foreach (var type in new[] { "inference.segmentation", "inference.instanceSegmentation", "inference.pose", "inference.obb" })
        {
            var item = BuiltInNodeCatalog.Items.Single(x => x.Type == type);
            Assert.Contains(item.Parameters, p => p.Name == "packagePath");
            Assert.Contains(item.Outputs, o => o.Name == "diagnostics");
        }
    }
}

