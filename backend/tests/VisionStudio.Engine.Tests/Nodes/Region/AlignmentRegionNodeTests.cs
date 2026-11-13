using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class AlignmentRegionNodeTests : IDisposable
{
    private readonly List<IDisposable> _outputs = [];
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); }
    private async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, object? parameters = null, CancellationToken token = default)
    {
        var node = new NodeDefinition("test", executor.Type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { })));
        var result = await executor.ExecuteAsync(new(inputs), node, token);
        _outputs.AddRange(result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>());
        return result;
    }
    private static VisionRegion Rect(int x, int y, int w, int h, int width = 64, int height = 64) => new(width, height,
        [new(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static VisionRegion Region(NodeExecutorResult r, string port = "region") => r.Outputs[port].Require<VisionRegion>(port);
    private static Dictionary<string, VisionValue> Input(VisionRegion r) => new() { ["region"] = VisionValue.Region(r) };
    private static VisionTransform2D Translation(double x, double y) => new("a", "b", "px", 1, 0, x, 0, 1, y, 0, 0, 1);

    [Theory]
    [InlineData("erode", 9)] [InlineData("dilate", 49)] [InlineData("open", 25)] [InlineData("close", 25)]
    public async Task RegionMorphologyUsesPixelSets(string operation, double area)
    {
        var source = Rect(10, 10, 5, 5);
        var result = await Run(new RegionMorphologyNode(), Input(source), new { operation, kernelWidth = 3, kernelHeight = 3 });
        Assert.Equal(area, result.Outputs["area"].Require<double>("area"));
        Assert.Equal(25, source.Components[0].Runs.Sum(r => r.EndColumn - r.StartColumn + 1));
    }

    [Fact]
    public async Task MorphologyClipsBoundsAndUnionsOnlyWhenRequested()
    {
        var source = new VisionRegion(64, 64, [Rect(0, 0, 3, 3).Components[0], Rect(4, 0, 3, 3).Components[0]]);
        var perObject = await Run(new RegionMorphologyNode(), Input(source), new { operation = "dilate" });
        Assert.Equal(2, Region(perObject).Components.Count);
        Assert.All(Region(perObject).Components.SelectMany(c => c.Runs), r => { Assert.True(r.Row >= 0); Assert.True(r.StartColumn >= 0); });
        var united = await Run(new RegionMorphologyNode(), Input(source), new { operation = "close", unionFirst = true });
        var split = await Run(new RegionConnectedComponentsNode(), Input(Region(united)));
        Assert.Single(Region(split).Components);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionMorphologyNode(), Input(source), new { kernelWidth = 2 }));
    }

    [Fact]
    public async Task PropertiesKeepHolesPixelAreaCentroidAndPerObjectOrder()
    {
        var runs = new List<VisionRegionRun>();
        for (var y = 10; y < 20; y++)
            if (y is >= 13 and <= 16) { runs.Add(new(y, 10, 12)); runs.Add(new(y, 17, 19)); }
            else runs.Add(new(y, 10, 19));
        var region = new VisionRegion(64, 64, [new(runs), Rect(30, 30, 3, 3).Components[0]]);
        var properties = await Run(new RegionPropertiesNode(), Input(region));
        Assert.Equal(84d, properties.Outputs["area"].Require<double>("area"));
        Assert.Equal(new VisionPoint2D(14.5, 14.5), properties.Outputs["center"].Require<VisionPoint2D>("center"));
        var json = JsonDocument.Parse(properties.Outputs["propertiesJson"].Require<string>("propertiesJson"));
        Assert.Equal(2, json.RootElement.GetArrayLength()); Assert.Equal(9, json.RootElement[1].GetProperty("Area").GetDouble());
        var detections = (await Run(new RegionToDetectionsNode(), Input(region))).Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(84d, detections[0].Area); Assert.Equal(0, detections[0].Index); Assert.Equal(1, detections[1].Index);
        Assert.Equal(14.5, detections[0].X); Assert.Equal(31, detections[1].X);
        var absent = await Run(new RegionPropertiesNode(), Input(region), new { index = 2 });
        Assert.False(absent.Outputs["found"].Require<bool>("found")); Assert.False(absent.Outputs.ContainsKey("area"));
    }

    [Theory]
    [InlineData("region.morphology")] [InlineData("region.properties")] [InlineData("region.toDetections")] [InlineData("region.transform")]
    public async Task EmptyRegionsHaveNormalEmptyResults(string type)
    {
        IVisionNodeExecutor executor = type switch { "region.morphology" => new RegionMorphologyNode(), "region.properties" => new RegionPropertiesNode(),
            "region.toDetections" => new RegionToDetectionsNode(), _ => new RegionTransformNode() };
        var input = Input(new(64, 64, [])); input["transform"] = VisionValue.Transform(Translation(2, 3));
        var result = await Run(executor, input);
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.Equal(0L, result.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task AsymmetricRegionUsesCentroidAcrossDetectionAndCollectionConsumers()
    {
        var source = new VisionRegion(32, 32, [new([new(5, 5, 8), new(6, 5, 5), new(7, 5, 5)])]);
        var converted = await Run(new RegionToDetectionsNode(), Input(source));
        var detection = Assert.Single(converted.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections"));
        Assert.Equal(6d, detection.X); Assert.Equal(5.5, detection.Y);
        Assert.Equal(detection.X, detection.Circle.X); Assert.Equal(detection.Y, detection.Circle.Y);
        var selected = await Run(new DetectionAtIndexNode(), new() { ["detections"] = converted.Outputs["detections"] });
        Assert.Equal(new VisionPoint2D(6, 5.5), selected.Outputs["center"].Require<VisionPoint2D>("center"));
    }

    [Fact]
    public async Task BilinearWarpReportsOwnedImageAndExcludesUnsupportedBorder()
    {
        using var image = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(200)));
        var result = await Run(new ImageWarpAffineNode(), new() { ["image"] = VisionValue.Image(image), ["transform"] = VisionValue.Transform(Translation(4, -2)) });
        var valid = Region(result, "validRegion");
        Assert.Equal(5, valid.Components[0].Runs.Min(r => r.StartColumn));
        Assert.DoesNotContain(valid.Components.SelectMany(c => c.Runs), r => r.Row >= 13);
        var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, output.Mat.At<byte>(5, 0)); Assert.Equal(200, output.Mat.At<byte>(5, 6));
        image.Mat.SetTo(Scalar.All(99)); Assert.Equal(200, output.Mat.At<byte>(5, 6));
    }

    [Fact]
    public async Task BoardViewAppendRejectsMismatchedSizeAndPreservesPreviousViewsWhenAbsent()
    {
        using var image = VisionImage.Own(new Mat(100, 100, MatType.CV_8UC1, Scalar.All(255)));
        var previous = new CameraCalibrationRequest(100, 100, [new([[0, 0, 0]], [[10, 10]])]);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["calibrationDataJson"] = VisionValue.String(JsonSerializer.Serialize(previous)) };
        var result = await Run(new CalibrationDetectBoardNode(), inputs);
        Assert.Single(JsonSerializer.Deserialize<CameraCalibrationRequest>(result.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"))!.Views);
        inputs["calibrationDataJson"] = VisionValue.String(JsonSerializer.Serialize(previous with { ImageWidth = 200 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new CalibrationDetectBoardNode(), inputs));
    }

    [Theory]
    [InlineData(1, 0, 4, 0, 1, -3)]
    [InlineData(0, -1, 63, 1, 0, 0)]
    public async Task ImageAndRegionAffineSharePixelCentreConvention(double a, double b, double tx, double c, double d, double ty)
    {
        var source = Rect(10, 15, 12, 8);
        var mask = await Run(new RegionMorphologyNode(), Input(source), new { kernelWidth = 1, kernelHeight = 1 });
        var t = new VisionTransform2D("a", "b", "px", a, b, tx, c, d, ty, 0, 0, 1);
        var region = await Run(new RegionTransformNode(), new() { ["region"] = VisionValue.Region(source), ["transform"] = VisionValue.Transform(t) });
        var image = await Run(new ImageWarpAffineNode(), new() { ["image"] = mask.Outputs["image"], ["transform"] = VisionValue.Transform(t) }, new { interpolation = "nearest" });
        var expected = region.Outputs["image"].Require<VisionImage>("image").Mat;
        var actual = image.Outputs["image"].Require<VisionImage>("image").Mat;
        Assert.Equal(0, Cv2.Norm(expected, actual, NormTypes.INF));
        var point = t.Apply(new(10, 15)); Assert.Equal(255, actual.At<byte>((int)point.Y, (int)point.X));
    }

    [Fact]
    public async Task TransformClipsRemovesEmptyObjectsAndRejectsNonpixelOrProjectiveMatrices()
    {
        var source = Rect(0, 0, 5, 5);
        var clipped = await Run(new RegionTransformNode(), new() { ["region"] = VisionValue.Region(source), ["transform"] = VisionValue.Transform(Translation(-3, 0)) });
        Assert.Equal(10d, clipped.Outputs["area"].Require<double>("area"));
        var removed = await Run(new RegionTransformNode(), new() { ["region"] = VisionValue.Region(source), ["transform"] = VisionValue.Transform(Translation(100, 0)) });
        Assert.Empty(Region(removed).Components);
        foreach (var t in new[] { Translation(0, 0) with { TargetUnit = "mm" }, Translation(0, 0) with { H20 = .01 }, Translation(0, 0) with { H00 = 0 }, Translation(0, 0) with { H02 = double.NaN } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RegionTransformNode(), new() { ["region"] = VisionValue.Region(source), ["transform"] = VisionValue.Transform(t) }));
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(-12)]
    public async Task RegistrationRecoversKnownTranslationAndRotation(double angle)
    {
        using var reference = VisionImage.Own(new Mat(180, 240, MatType.CV_8UC1, Scalar.All(25)));
        var rng = new Random(1234);
        for (var i = 0; i < 35; i++) Cv2.Circle(reference.Mat, new(rng.Next(65, 160), rng.Next(55, 120)), rng.Next(2, 7), Scalar.All(rng.Next(50, 255)), -1);
        using var matrix = Cv2.GetRotationMatrix2D(new Point2f(119.5f, 89.5f), angle, 1);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + 13); matrix.Set(1, 2, matrix.At<double>(1, 2) - 7);
        using var current = VisionImage.Own(new Mat()); Cv2.WarpAffine(reference.Mat, current.Mat, matrix, reference.Mat.Size());
        var registration = await Run(new ImageRegisterNode(), new() { ["image"] = VisionValue.Image(current), ["reference"] = VisionValue.Image(reference) },
            new { x = 50, y = 40, width = 130, height = 100, angleRangeDeg = 20, minScore = .65 });
        Assert.True(registration.Outputs["found"].Require<bool>("found"));
        var transform = registration.Outputs["transform"].Require<VisionTransform2D>("transform");
        foreach (var p in new[] { new VisionPoint2D(80, 60), new VisionPoint2D(140, 100) })
        {
            var moved = new VisionPoint2D(matrix.At<double>(0, 0) * p.X + matrix.At<double>(0, 1) * p.Y + matrix.At<double>(0, 2), matrix.At<double>(1, 0) * p.X + matrix.At<double>(1, 1) * p.Y + matrix.At<double>(1, 2));
            var recovered = transform.Apply(moved); Assert.InRange(Math.Abs(recovered.X - p.X), 0, .6); Assert.InRange(Math.Abs(recovered.Y - p.Y), 0, .6);
        }
        var warped = await Run(new ImageWarpAffineNode(), new() { ["image"] = VisionValue.Image(current), ["transform"] = registration.Outputs["transform"] });
        Assert.NotEmpty(Region(warped, "validRegion").Components);
        current.Dispose(); Assert.False(warped.Outputs["image"].Require<VisionImage>("image").Mat.Empty());
    }

    [Fact]
    public async Task TexturelessRegistrationOmitsTransformAndDoesNotFabricateIdentity()
    {
        using var image = VisionImage.Own(new Mat(64, 64, MatType.CV_8UC1, Scalar.All(50)));
        var result = await Run(new ImageRegisterNode(), new() { ["image"] = VisionValue.Image(image), ["reference"] = VisionValue.Image(image) });
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("transform"));
    }

    [Fact]
    public async Task LineSpacingMeasuresTaperAndFiniteOverlap()
    {
        var inputs = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Line(new(new(0, 0), new(10, 0))), ["b"] = VisionValue.Line(new(new(0, 5), new(10, 15))) };
        var result = await Run(new LineSpacingNode(), inputs, new { sampleCount = 3 });
        Assert.Equal(10d, result.Outputs["distance"].Require<double>("distance")); Assert.Equal(5d, result.Outputs["min"].Require<double>("min")); Assert.Equal(15d, result.Outputs["max"].Require<double>("max"));
        inputs["b"] = VisionValue.Line(new(new(20, 5), new(30, 5)));
        result = await Run(new LineSpacingNode(), inputs);
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("distance"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new LineSpacingNode(), inputs, new { direction = "angle", angleDeg = 0 }));
    }

    [Theory]
    [InlineData("chessboard")] [InlineData("circles")]
    public async Task BoardDetectionExtractsAllOrderedCorrespondences(string pattern)
    {
        using var image = VisionImage.Own(new Mat(300, 360, MatType.CV_8UC1, Scalar.All(255)));
        if (pattern == "chessboard")
        {
            for (var y = 0; y < 7; y++) for (var x = 0; x < 8; x++) if ((x + y) % 2 == 0) Cv2.Rectangle(image.Mat, new Rect(40 + x * 30, 40 + y * 30, 30, 30), Scalar.All(0), -1);
        }
        else
        {
            for (var y = 0; y < 6; y++) for (var x = 0; x < 7; x++) Cv2.Circle(image.Mat, new(70 + x * 30, 70 + y * 30), 7, Scalar.All(0), -1);
        }
        var result = await Run(new CalibrationDetectBoardNode(), new() { ["image"] = VisionValue.Image(image) }, new { pattern, columns = 7, rows = 6, spacingMm = 5 });
        Assert.True(result.Outputs["found"].Require<bool>("found"), JsonSerializer.Serialize(result.Summary)); Assert.Equal(42L, result.Outputs["count"].Require<long>("count"));
        var view = JsonSerializer.Deserialize<CameraCalibrationView>(result.Outputs["viewJson"].Require<string>("viewJson"))!;
        Assert.Equal(new double[] { 30, 25, 0 }, view.ObjectPoints[^1]);
        Assert.InRange(view.ImagePoints[0][0], 69, 71); Assert.InRange(view.ImagePoints[0][1], 69, 71);
        Assert.InRange(view.ImagePoints[^1][0], 249, 251); Assert.InRange(view.ImagePoints[^1][1], 219, 221);
        var next = await Run(new CalibrationDetectBoardNode(), new() { ["image"] = VisionValue.Image(image), ["calibrationDataJson"] = result.Outputs["calibrationDataJson"] }, new { pattern });
        var request = JsonSerializer.Deserialize<CameraCalibrationRequest>(next.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"))!;
        Assert.Equal(2, request.Views.Count);
    }

    [Fact]
    public async Task MissingBoardReturnsNoViewAndPreservesAccumulatedViews()
    {
        using var image = VisionImage.Own(new Mat(100, 100, MatType.CV_8UC1, Scalar.All(255)));
        var result = await Run(new CalibrationDetectBoardNode(), new() { ["image"] = VisionValue.Image(image) });
        Assert.False(result.Outputs["found"].Require<bool>("found")); Assert.False(result.Outputs.ContainsKey("viewJson"));
        Assert.Empty(JsonSerializer.Deserialize<CameraCalibrationRequest>(result.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"))!.Views);
    }

    [Fact]
    public async Task TiltedCircleViewsFeedIntrinsicCalibrationThroughConnectedPort()
    {
        VisionValue? accumulated = null;
        for (var v = 0; v < 8; v++)
        {
            using var image = VisionImage.Own(new Mat(480, 640, MatType.CV_8UC1, Scalar.All(255)));
            var rx = (v % 3 - 1) * .2; var ry = (v % 4 - 1.5) * .17; var rz = (v % 2 == 0 ? 1 : -1) * .07 * (v + 1);
            for (var row = 0; row < 6; row++) for (var col = 0; col < 8; col++)
            {
                var x = col * 22d; var y = row * 22d;
                var cx = Math.Cos(rx); var sx = Math.Sin(rx); var cy = Math.Cos(ry); var sy = Math.Sin(ry); var cz = Math.Cos(rz); var sz = Math.Sin(rz);
                var qx = cz * cy * x + (cz * sy * sx - sz * cx) * y - 90 + v * 13;
                var qy = sz * cy * x + (sz * sy * sx + cz * cx) * y - 55 + (v % 4) * 12;
                var qz = -sy * x + cy * sx * y + 650 + v * 13;
                Cv2.Circle(image.Mat, new((int)Math.Round((600 * qx / qz + 320) * 256), (int)Math.Round((605 * qy / qz + 240) * 256)), 5 * 256, Scalar.All(0), -1, LineTypes.AntiAlias, 8);
            }
            var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) };
            if (accumulated is not null) inputs["calibrationDataJson"] = accumulated;
            var result = await Run(new CalibrationDetectBoardNode(), inputs, new { pattern = "circles", rows = 6, columns = 8, spacingMm = 22, maxGridError = 2 });
            Assert.True(result.Outputs["found"].Require<bool>("found"), $"view {v}: {JsonSerializer.Serialize(result.Summary)}");
            accumulated = result.Outputs["calibrationDataJson"];
        }
        // Invalid parameter JSON demonstrates that the connected port has precedence.
        var fit = await Run(new CameraIntrinsicCalibrationNode(), new() { ["calibrationDataJson"] = accumulated! }, new { calibrationDataJson = "invalid" });
        var model = JsonSerializer.Deserialize<CameraCalibrationModel>(fit.Outputs["calibrationJson"].Require<string>("calibrationJson"))!;
        Assert.InRange(model.Rms, 0, .5); Assert.InRange(model.CameraMatrix[0][0], 550, 650); Assert.InRange(model.CameraMatrix[1][1], 550, 660);
    }

    [Fact]
    public async Task PerspectiveChessboardIsOrderedAndIncompleteGridIsRejected()
    {
        using var board = new Mat(300, 360, MatType.CV_8UC1, Scalar.All(255));
        for (var y = 0; y < 7; y++) for (var x = 0; x < 8; x++) if ((x + y) % 2 == 0) Cv2.Rectangle(board, new Rect(40 + x * 30, 40 + y * 30, 30, 30), Scalar.All(0), -1);
        Point2f[] from = [new(40, 40), new(280, 40), new(280, 250), new(40, 250)];
        Point2f[] to = [new(60, 50), new(300, 90), new(280, 260), new(50, 250)];
        using var matrix = Cv2.GetPerspectiveTransform(from, to);
        using var image = VisionImage.Own(new Mat()); Cv2.WarpPerspective(board, image.Mat, matrix, board.Size(), borderValue: Scalar.All(255));
        var result = await Run(new CalibrationDetectBoardNode(), new() { ["image"] = VisionValue.Image(image) });
        Assert.True(result.Outputs["found"].Require<bool>("found"), JsonSerializer.Serialize(result.Summary));
        // Remove an interior intersection rather than just changing expected grid dimensions.
        var view = JsonSerializer.Deserialize<CameraCalibrationView>(result.Outputs["viewJson"].Require<string>("viewJson"))!;
        var p = view.ImagePoints[20]; Cv2.Circle(image.Mat, new((int)p[0], (int)p[1]), 9, Scalar.All(255), -1);
        result = await Run(new CalibrationDetectBoardNode(), new() { ["image"] = VisionValue.Image(image) });
        Assert.False(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task EveryNewExecutorHonoursPreCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        IVisionNodeExecutor[] executors = [new ImageRegisterNode(), new ImageWarpAffineNode(), new RegionMorphologyNode(), new RegionPropertiesNode(), new RegionToDetectionsNode(), new RegionTransformNode(), new LineSpacingNode(), new CalibrationDetectBoardNode()];
        foreach (var executor in executors) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(executor, new(), token: cancellation.Token));
    }
}
