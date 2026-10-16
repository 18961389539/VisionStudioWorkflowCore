using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class RoiCorrectionSafetyTests : IDisposable
{
    private readonly List<IDisposable> _outputs = [];
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); }

    private async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string parameters = "{}")
    {
        var result = await executor.ExecuteAsync(new(inputs), new NodeDefinition("test", executor.Type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters)), default);
        _outputs.AddRange(result.Outputs.Values.Select(value => value.Value).OfType<IDisposable>());
        return result;
    }

    [Fact]
    public void RectangleMaskIntersectsOriginalBoundsInsteadOfMovingOrEnlargingTheRegion()
    {
        using var partial = new VisionRoi(VisionRoiType.Rectangle, X: -20, Y: 5, Width: 50, Height: 10).BuildMask(new Size(100, 50));
        Assert.Equal(300, Cv2.CountNonZero(partial));
        Assert.Equal(255, partial.At<byte>(5, 29));
        Assert.Equal(0, partial.At<byte>(5, 30));
        using var outside = new VisionRoi(VisionRoiType.Rectangle, X: 110, Y: 5, Width: 50, Height: 10).BuildMask(new Size(100, 50));
        Assert.Equal(0, Cv2.CountNonZero(outside));
        using var fractional = new VisionRoi(VisionRoiType.Rectangle, X: -1.2, Y: 2.2, Width: 3.4, Height: 2.4).BuildMask(new Size(10, 10));
        Assert.Equal(6, Cv2.CountNonZero(fractional));
    }

    [Fact]
    public void CirclePolygonAndBrushKeepTheirOffCanvasGeometry()
    {
        using var circle = new VisionRoi(VisionRoiType.Circle, X: -4, Y: 10, Radius: 6).BuildMask(new Size(30, 30));
        Assert.NotEqual(0, circle.At<byte>(10, 1));
        Assert.Equal(0, circle.At<byte>(10, 5));
        using var outsideCircle = new VisionRoi(VisionRoiType.Circle, X: -20, Y: 10, Radius: 6).BuildMask(new Size(30, 30));
        Assert.Equal(0, Cv2.CountNonZero(outsideCircle));
        using var polygon = new VisionRoi(VisionRoiType.Polygon, Points: [new(-10, 0), new(10, 20), new(-10, 20)]).BuildMask(new Size(30, 30));
        Assert.Equal(0, polygon.At<byte>(2, 0)); // The original diagonal reaches x=0 only near y=10.
        Assert.NotEqual(0, polygon.At<byte>(18, 0));
        using var outsideBrush = new VisionRoi(VisionRoiType.Brush, Strokes: [new(VisionRoiStrokeMode.Add, 4, [new(-10, 4), new(-10, 20)])]).BuildMask(new Size(30, 30));
        Assert.Equal(0, Cv2.CountNonZero(outsideBrush));
        using var eraser = new VisionRoi(VisionRoiType.Brush, Strokes: [
            new(VisionRoiStrokeMode.Add, 4, [new(0, 4), new(0, 20)]),
            new(VisionRoiStrokeMode.Erase, 4, [new(-10, 4), new(-10, 20)])]).BuildMask(new Size(30, 30));
        Assert.Equal(255, eraser.At<byte>(12, 0));
    }

    [Fact]
    public async Task CaptureReferenceFreezesPixelsAndConnectedPoseWithoutRequiringAnRoi()
    {
        using var image = VisionImage.Own(new Mat(24, 32, MatType.CV_8UC1, Scalar.All(17)));
        image.Mat.Set(8, 9, (byte)221);
        var result = await Run(new RoiTransformNode(), new()
        {
            ["debugImage"] = VisionValue.Image(image), ["currentPose"] = VisionValue.Pose(new(12.125, 8.25, 37.5))
        }, """{"captureReference":true,"currentX":999}""");
        Assert.True((bool)result.Summary["referenceCaptured"]!);
        Assert.Equal(12.125, result.Summary["referenceX"]);
        Assert.Equal(37.5, result.Summary["referenceThetaDeg"]);
        using var saved = Cv2.ImDecode(Convert.FromBase64String((string)result.Summary["referenceImage"]!), ImreadModes.Unchanged);
        Assert.Equal(image.Mat.Size(), saved.Size());
        Assert.Equal(0d, Cv2.Norm(image.Mat, saved));
        image.Mat.SetTo(Scalar.All(0));
        Assert.Equal(221, saved.At<byte>(8, 9));
        Assert.Equal(221, result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(8, 9));
        Assert.False(result.Outputs.ContainsKey("roi"));
    }

    [Fact]
    public async Task CaptureRejectsMissingImageAndInvalidPoseAndCorrectionRejectsAChangedResolution()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), [], """{"captureReference":true}"""));
        using var image = VisionImage.Own(new Mat(24, 32, MatType.CV_8UC1, Scalar.All(0)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), new()
        {
            ["debugImage"] = VisionValue.Image(image), ["currentPose"] = VisionValue.Pose(new(double.NaN, 0, 0))
        }, """{"captureReference":true}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), new() { ["debugImage"] = VisionValue.Image(image) },
            """{"referenceImage":"saved","referenceWidth":64,"referenceHeight":48,"roi":{"type":"Circle","x":10,"y":10,"radius":4}}"""));
        Assert.Contains("dimensions", error.Message);
    }

    [Fact]
    public async Task CorrectionRejectsCoordinatesOutsideTheRoiRasterizationRange()
    {
        var inputRoiError = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), [],
            """{"roi":{"type":"Circle","x":10000001,"y":10,"radius":4}}"""));
        Assert.Contains("10000000", inputRoiError.Message);

        var poseError = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), new()
        {
            ["currentPose"] = VisionValue.Pose(new(10_000_001, 0, 0))
        }, """{"roi":{"type":"Rectangle","x":2,"y":3,"width":4,"height":5}}"""));
        Assert.Contains("10000000", poseError.Message);

        var correctedRoiError = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), [],
            """{"roi":{"type":"Rectangle","x":9999998,"y":3,"width":2,"height":5},"currentX":100}"""));
        Assert.Contains("10000000", correctedRoiError.Message);
    }

    [Fact]
    public async Task CorrectionRejectsNullPolygonVerticesAsInvalidGeometry()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RoiTransformNode(), [],
            """{"roi":{"type":"Polygon","points":[{"x":1,"y":1},null,{"x":8,"y":1}]}}"""));

        Assert.Contains("at least three finite points", error.Message);
    }

    [Theory]
    [InlineData("measure.caliperLine", false)]
    [InlineData("measure.caliperLine", true)]
    [InlineData("feature.edgeSubpixel", false)]
    [InlineData("measure.caliper", false)]
    public async Task MeasurementSearchAndSamplingLayoutFollowTheSameCorrectionAsTheRoi(string type, bool connectedLine)
    {
        using var image = VisionImage.Own(new Mat(220, 220, MatType.CV_8UC1, Scalar.All(0)));
        using (var lower = new Mat(image.Mat, new Rect(0, 110, 220, 110))) lower.SetTo(Scalar.All(255));
        var correction = await Run(new RoiTransformNode(), [],
            """{"roi":{"type":"Rectangle","x":20,"y":40,"width":80,"height":40},"baseX":60,"baseY":60,"currentX":120,"currentY":110,"currentThetaDeg":90}""");
        IVisionNodeExecutor executor = type switch
        {
            "feature.edgeSubpixel" => new EdgeSubpixelNode(), "measure.caliper" => new CaliperNode(), _ => new CaliperLineNode()
        };
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["transform"] = correction.Outputs["transform"] };
        if (type != "measure.caliper") inputs["roi"] = correction.Outputs["roi"];
        if (connectedLine) inputs["searchLine"] = VisionValue.Line(new(new(30, 60), new(90, 60)));
        var result = await Run(executor, inputs,
            """{"startX":30,"startY":60,"endX":90,"endY":60,"edgeLength":30,"sampleWidth":3,"minContrast":8}""");
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 119, 121);
        Assert.InRange(point.Y, 108, 112);
        var search = result.Outputs["searchLine"].Require<VisionLine2D>("searchLine");
        Assert.Equal(search.Start.X, search.End.X, 6);
        if (type == "measure.caliperLine") Assert.True(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task CircleMeasurementFollowsArcDirectionAsWellAsCenter()
    {
        using var image = VisionImage.Own(new Mat(220, 220, MatType.CV_8UC1, Scalar.All(0)));
        Cv2.Ellipse(image.Mat, new Point(120, 110), new Size(30, 30), 0, 270, 360, Scalar.All(255), 2);
        var correction = await Run(new RoiTransformNode(), [],
            """{"roi":{"type":"Circle","x":60,"y":60,"radius":30},"baseX":60,"baseY":60,"currentX":120,"currentY":110,"currentThetaDeg":90}""");
        var result = await Run(new CaliperCircleNode(), new()
        {
            ["image"] = VisionValue.Image(image), ["transform"] = correction.Outputs["transform"],
            ["circle"] = VisionValue.Circle(new(60, 60, 30))
        }, """{"angleStartDeg":0,"angleEndDeg":90,"angleStepDeg":5,"radialBand":6,"sampleWidth":1,"minContrast":8}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(Convert.ToInt32(result.Summary["edgePoints"]) >= 10);
        var circle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(circle.X, 117, 123); Assert.InRange(circle.Y, 107, 113);
        Assert.InRange(circle.Radius, 27, 33);
    }

    [Fact]
    public async Task MeasurementRejectsScaleAndWorldUnitTransformsRatherThanMisinterpretingGeometry()
    {
        using var image = VisionImage.Own(new Mat(20, 20, MatType.CV_8UC1, Scalar.All(0)));
        foreach (var transform in new[]
        {
            new VisionTransform2D("base", "current", "px", 2, 0, 0, 0, 2, 0, 0, 0, 1),
            new VisionTransform2D("base", "current", "mm", 1, 0, 0, 0, 1, 0, 0, 0, 1)
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new CaliperLineNode(), new()
            {
                ["image"] = VisionValue.Image(image), ["transform"] = VisionValue.Transform(transform)
            }));
    }
}
