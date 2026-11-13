using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class NodeContractHardeningTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));
    private static VisionValue Contours(params VisionContour[] contours) => VisionValue.Contours(contours);
    private static Task<InvalidOperationException> Throws(Func<ValueTask<NodeExecutorResult>> action)
        => Assert.ThrowsAsync<InvalidOperationException>(async () => await action());

    private static VisionContour Triangle(double x = 0, double y = 0) => new(
        0, [new VisionPoint2D(x, y), new VisionPoint2D(x + 10, y), new VisionPoint2D(x + 10, y + 10)],
        -1, false, 50, 0, 0, new VisionRectangle2D(x, y, 10, 10), new VisionCircle(x + 5, y + 5, 5));

    private static VisionRegion RectRegion(int canvas, int x, int y, int width, int height) => new(
        canvas, canvas, [new VisionRegionComponent([new VisionRegionRun(y, x, x + width - 1)])]);

    [Fact]
    public async Task JlVisionMeasure1d_DoesNotDisposeTheCallerImage()
    {
        using var image = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(120)));
        var result = await new JLVisionMeasure1dNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(image) }),
            Def("jlvision.measure1d", """{"shape":"rectangle","method":"measurePos","row":16,"column":16,"measureLength1":10,"measureLength2":2}"""),
            CancellationToken.None);
        Assert.False(image.Mat.Empty());
        Assert.Equal(32, image.Mat.Width);
        Assert.Equal(120, image.Mat.At<byte>(0, 0));
        Assert.False(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task JlVisionMeasure1d_RejectsOutOfRangeSigma()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(100));
        var error = await Throws(() => new JLVisionMeasure1dNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }),
            Def("jlvision.measure1d", """{"sigma":0.1}"""), CancellationToken.None));
        Assert.Contains("sigma", error.Message);
    }

    [Fact]
    public async Task JlVisionThreshold_RejectsAConnectedButEmptySecondImage()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(100));
        var error = await Throws(() => new JLVisionThresholdNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source), ["image2"] = VisionValue.Image(VisionImage.Own(new Mat())) }),
            Def("jlvision.threshold", """{"operator":"dynThreshold"}"""), CancellationToken.None));
        Assert.Contains("non-empty image2", error.Message);
    }

    [Fact]
    public async Task JlVisionContourOp_RejectsAnEmptySecondContourList()
    {
        var error = await Throws(() => new JLVisionContourOpNode().ExecuteAsync(
            Ctx(new() { ["contour"] = Contours(Triangle()), ["contour2"] = Contours() }),
            Def("jlvision.contourOp", """{"operator":"union2ClosedContours"}"""), CancellationToken.None));
        Assert.Contains("contour2", error.Message);
    }

    [Fact]
    public async Task Perspective_UsesTheSelectedInterpolationAndRejectsUnknownValues()
    {
        using var source = new Mat(24, 24, MatType.CV_8UC1, Scalar.All(90));
        var nearest = await new PerspectiveCorrectionNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }),
            Def("image.perspective", """{"x1":0,"y1":0,"x2":23,"y2":0,"x3":23,"y3":23,"x4":0,"y4":23,"width":12,"height":12,"interpolation":"nearest"}"""),
            CancellationToken.None);
        Assert.Equal("nearest", nearest.Summary["interpolation"]);
        (nearest.Outputs["image"].Value as IDisposable)?.Dispose();

        await Throws(() => new PerspectiveCorrectionNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }),
            Def("image.perspective", """{"interpolation":"lanczos"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ColorRange_HonorsFailPolicyOnAnEmptySelection()
    {
        using var black = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(0));
        var error = await Throws(() => new RegionFromColorRangeNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(black) }),
            Def("region.fromColorRange", """{"space":"rgb","min1":0.9,"max1":1,"min2":0.9,"max2":1,"min3":0.9,"max3":1,"emptyPolicy":"fail"}"""),
            CancellationToken.None));
        Assert.Contains("emptyPolicy=fail", error.Message);
    }

    [Fact]
    public async Task ContrastNormalize_HonorsFailPolicyOnAUniformImage()
    {
        using var uniform = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(77));
        var error = await Throws(() => new ContrastNormalizeNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(uniform) }),
            Def("image.contrastNormalize", """{"emptyPolicy":"fail"}"""), CancellationToken.None));
        Assert.Contains("zero contrast", error.Message);
    }

    [Fact]
    public async Task ReduceDomain_HonorsFailPolicyOnAnEmptyRegion()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(10));
        var empty = new VisionRegion(16, 16, Array.Empty<VisionRegionComponent>());
        var error = await Throws(() => new ReduceDomainNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(source), ["region"] = VisionValue.Region(empty) }),
            Def("image.reduceDomain", """{"emptyPolicy":"fail"}"""), CancellationToken.None));
        Assert.Contains("emptyPolicy=fail", error.Message);
    }

    [Fact]
    public async Task RegionFromRoi_InvertComplementsTheRoiInsideTheCanvas()
    {
        var roi = new VisionRoi(VisionRoiType.Rectangle, X: 2, Y: 2, Width: 4, Height: 4);
        var plain = await new RegionFromRoiNode().ExecuteAsync(
            Ctx(new() { ["roi"] = VisionValue.Roi(roi) }),
            Def("region.fromRoi", """{"canvasWidth":10,"canvasHeight":10,"invert":false}"""), CancellationToken.None);
        var inverted = await new RegionFromRoiNode().ExecuteAsync(
            Ctx(new() { ["roi"] = VisionValue.Roi(roi) }),
            Def("region.fromRoi", """{"canvasWidth":10,"canvasHeight":10,"invert":true}"""), CancellationToken.None);
        Assert.Equal(16d, plain.Outputs["area"].Require<double>("area"), 3);
        Assert.Equal(84d, inverted.Outputs["area"].Require<double>("area"), 3);
    }

    [Fact]
    public async Task RegionToDetections_AppliesTheSelectedOrderBeforeTheCap()
    {
        var region = new VisionRegion(64, 64,
        [
            new VisionRegionComponent([new VisionRegionRun(0, 0, 1), new VisionRegionRun(1, 0, 1)]),
            new VisionRegionComponent([new VisionRegionRun(30, 30, 33), new VisionRegionRun(31, 30, 33), new VisionRegionRun(32, 30, 33)])
        ]);
        var byArea = await new RegionToDetectionsNode().ExecuteAsync(
            Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.toDetections", """{"sortBy":"area"}"""), CancellationToken.None);
        var byX = await new RegionToDetectionsNode().ExecuteAsync(
            Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.toDetections", """{"sortBy":"x"}"""), CancellationToken.None);
        var areaDetections = byArea.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        var xDetections = byX.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.True(areaDetections[0].Area > areaDetections[1].Area);
        Assert.True(xDetections[0].X < xDetections[1].X);
    }

    [Fact]
    public async Task CircleSpacing_EmitsTheDeclaredOverlapFlag()
    {
        var overlap = await new CircleSpacingNode().ExecuteAsync(
            Ctx(new()
            {
                ["circleA"] = VisionValue.Circle(new VisionCircle(10, 10, 5)),
                ["circleB"] = VisionValue.Circle(new VisionCircle(18, 10, 5))
            }),
            Def("measure.circleSpacing"), CancellationToken.None);
        Assert.True(overlap.Outputs["overlapping"].Require<bool>("overlapping"));

        var separate = await new CircleSpacingNode().ExecuteAsync(
            Ctx(new()
            {
                ["circleA"] = VisionValue.Circle(new VisionCircle(10, 10, 2)),
                ["circleB"] = VisionValue.Circle(new VisionCircle(30, 10, 2))
            }),
            Def("measure.circleSpacing"), CancellationToken.None);
        Assert.False(separate.Outputs["overlapping"].Require<bool>("overlapping"));
    }

    [Fact]
    public async Task CoordinateDistance_SupportsManhattanMetric()
    {
        var a = new VisionCoordinatePoint2D(0, 0, "Workpiece", "mm");
        var b = new VisionCoordinatePoint2D(3, 4, "Workpiece", "mm");
        var result = await new CoordinateDistanceNode().ExecuteAsync(
            Ctx(new() { ["pointA"] = VisionValue.CoordinatePoint(a), ["pointB"] = VisionValue.CoordinatePoint(b) }),
            Def("coordinate.distance", """{"metric":"manhattan"}"""), CancellationToken.None);
        Assert.Equal(7d, result.Outputs["distance"].Require<double>("distance"), 6);
    }

    [Fact]
    public async Task CoordinateTransformLine_EnforcesMinLength()
    {
        var line = new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(3, 4));
        var transform = VisionTransform2DMath.Rigid("A", "B", "mm", 0, 0, 0);
        var error = await Throws(() => new TransformLineNode().ExecuteAsync(
            Ctx(new() { ["line"] = VisionValue.Line(line), ["transform"] = VisionValue.Transform(transform) }),
            Def("coordinate.transformLine", """{"minLength":6}"""), CancellationToken.None));
        Assert.Contains("minLength", error.Message);
    }

    [Fact]
    public async Task TextRegex_AppliesSinglelineAndEmptyInputPolicies()
    {
        var match = await new TextRegexNode().ExecuteAsync(
            Ctx(new() { ["text"] = VisionValue.String("A\nB") }),
            Def("text.regex", """{"pattern":"A.B","fullMatch":true,"singleline":true,"emptyInputPolicy":"nomatch"}"""),
            CancellationToken.None);
        Assert.True(match.Outputs["pass"].Require<bool>("pass"));

        var empty = await new TextRegexNode().ExecuteAsync(
            Ctx(new() { ["text"] = VisionValue.String("") }),
            Def("text.regex", """{"pattern":"A","emptyInputPolicy":"nomatch"}"""), CancellationToken.None);
        Assert.False(empty.Outputs["pass"].Require<bool>("pass"));

        await Throws(() => new TextRegexNode().ExecuteAsync(
            Ctx(new() { ["text"] = VisionValue.String("") }),
            Def("text.regex", """{"pattern":"A","emptyInputPolicy":"fail"}"""), CancellationToken.None));
    }

    [Theory]
    [InlineData("inference.obb")]
    [InlineData("inference.pose")]
    [InlineData("inference.instanceSegmentation")]
    public async Task InferenceGuardNodes_RejectCropAndReportMissingModels(string type)
    {
        using var source = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(0));
        IVisionNodeExecutor executor = type switch
        {
            "inference.obb" => new OnnxOrientedDetectionNode(),
            "inference.pose" => new OnnxPoseNode(),
            _ => new OnnxInstanceSegmentationNode()
        };
        var crop = await Throws(() => executor.ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }), Def(type, """{"crop":true}"""), CancellationToken.None));
        Assert.Contains("crop=true", crop.Message);

        var missing = await Throws(() => executor.ExecuteAsync(
            Ctx(new() { ["image"] = Img(source) }), Def(type), CancellationToken.None));
        Assert.Contains("requires modelPath", missing.Message);
    }
}
