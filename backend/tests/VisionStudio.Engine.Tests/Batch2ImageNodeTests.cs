using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class Batch2ImageNodeTests
{
    [Fact]
    public async Task CropResizeRotateAndPerspective_ReturnOwnedImagesWithExpectedSizes()
    {
        using var source = new Mat(12, 16, MatType.CV_8UC1, Scalar.All(90));
        var crop = await RunImage(new ImageCropNode(), Image(source), """{"x":2,"y":3,"width":5,"height":4}""");
        using var cropImage = ImageFrom(crop);
        Assert.Equal((5, 4), (cropImage.Width, cropImage.Height));

        var resized = await RunImage(new ImageResizeNode(), Image(source), """{"width":8,"height":6,"interpolation":"nearest"}""");
        using var resizedImage = ImageFrom(resized);
        Assert.Equal((8, 6), (resizedImage.Width, resizedImage.Height));

        var rotated = await RunImage(new ImageRotateNode(), Image(source), """{"angleDeg":90,"expand":true}""");
        using var rotatedImage = ImageFrom(rotated);
        Assert.Equal((12, 16), (rotatedImage.Width, rotatedImage.Height));

        var corrected = await RunImage(new PerspectiveCorrectionNode(), Image(source), """{"x1":0,"y1":0,"x2":15,"y2":0,"x3":15,"y3":11,"x4":0,"y4":11,"width":16,"height":12}""");
        using var correctedImage = ImageFrom(corrected);
        Assert.Equal((16, 12), (correctedImage.Width, correctedImage.Height));
        Assert.Equal(90, correctedImage.Mat.At<byte>(5, 5));
    }

    [Fact]
    public async Task CropRejectsEmptyRoiAndPerspectiveRejectsDegenerateCorners()
    {
        using var source = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(1));
        var emptyCrop = await Assert.ThrowsAsync<InvalidOperationException>(() => RunImage(new ImageCropNode(), Image(source), """{"x":11,"y":0,"width":2,"height":2}"""));
        Assert.Contains("没有覆盖任何图像像素", emptyCrop.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunImage(new PerspectiveCorrectionNode(), Image(source), """{"x1":0,"y1":0,"x2":1,"y2":0,"x3":2,"y3":0,"x4":3,"y4":0,"width":8,"height":8}"""));
    }

    [Fact]
    public void RoiMaskRejectsUnsupportedRuntimeTypesInsteadOfReturningAnEmptyMask()
    {
        var roi = new VisionRoi((VisionRoiType)999);

        var error = Assert.Throws<InvalidOperationException>(() => roi.BuildMask(new Size(8, 8)));

        Assert.Contains("is not supported", error.Message);
    }

    [Theory]
    [InlineData("""{"roi":"Rectangle"}""", "must be an object or null")]
    [InlineData("""{"roi":[]}""", "must be an object or null")]
    [InlineData("""{"roi":{"type":"Ellipse","x":1,"y":1,"radius":2}}""", "known string type")]
    [InlineData("""{"roi":{"type":3,"x":1,"y":1,"radius":2}}""", "known string type")]
    [InlineData("""{"roi":{"type":"Rectangle","x":"bad","y":0,"width":4,"height":4}}""", "is malformed")]
    public async Task CropRejectsMalformedRoiInsteadOfSilentlyUsingTheFullImage(string json, string message)
    {
        using var source = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunImage(new ImageCropNode(), Image(source), json));

        Assert.Contains("ROI parameter 'roi'", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task CropKeepsExplicitNullRoiAsTheLegacyUnsetValue()
    {
        using var source = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(1));

        var result = await RunImage(new ImageCropNode(), Image(source), """{"roi":null,"x":2,"y":3,"width":4,"height":3}""");

        using var actual = ImageFrom(result);
        Assert.Equal((4, 3), (actual.Width, actual.Height));
        Assert.Equal(2, Convert.ToInt32(result.Summary["cropOriginX"]));
        Assert.Equal(3, Convert.ToInt32(result.Summary["cropOriginY"]));
    }

    [Fact]
    public async Task CropReportsAnEmptyInputImageClearly()
    {
        using var empty = new Mat();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunImage(new ImageCropNode(), Image(empty), """{"x":0,"y":0,"width":1,"height":1}"""));
        Assert.Contains("non-empty image", error.Message);
    }

    [Theory]
    [InlineData("""{"roi":{"type":"Rectangle","x":-2,"y":3,"width":7,"height":4}}""")]
    [InlineData("""{"roi":{"type":"Circle","x":1,"y":5,"radius":3}}""")]
    [InlineData("""{"roi":{"type":"Polygon","points":[{"x":-4,"y":2},{"x":5,"y":2},{"x":5,"y":8},{"x":-4,"y":8}]}}""")]
    [InlineData("""{"roi":{"type":"Brush","strokes":[{"mode":"Add","width":4,"points":[{"x":-2,"y":4},{"x":5,"y":4}]}]}}""")]
    public async Task CropClipsPartiallyVisibleRoiWithoutMovingItsGeometry(string json)
    {
        using var source = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(90));
        using var expectedRaster = Node("image.crop", json).GetRoi()!.BuildMask(source.Size());
        using var expectedBinary = new Mat();
        Cv2.Threshold(expectedRaster, expectedBinary, 1, 255, ThresholdTypes.Binary);
        using var selectedPixels = new Mat();
        Cv2.FindNonZero(expectedBinary, selectedPixels);
        var bounds = Cv2.BoundingRect(selectedPixels);
        using var expectedMask = new Mat(expectedBinary, bounds);
        using var sourceRegion = new Mat(source, bounds);
        using var expectedImage = new Mat(bounds.Size, source.Type(), Scalar.All(0));
        sourceRegion.CopyTo(expectedImage, expectedMask);

        var result = await RunImage(new ImageCropNode(), Image(source), json);
        using var actual = ImageFrom(result);
        using var actualMask = result.Outputs["roiMask"].Require<VisionImage>("roiMask");
        Assert.Equal((bounds.Width, bounds.Height), (actual.Width, actual.Height));
        Assert.Equal(bounds.X, Convert.ToInt32(result.Summary["cropOriginX"]));
        Assert.Equal(bounds.Y, Convert.ToInt32(result.Summary["cropOriginY"]));
        Assert.Equal(Cv2.CountNonZero(expectedBinary), Convert.ToInt32(result.Summary["selectedPixels"]));
        Assert.Equal(0d, Cv2.Norm(expectedMask, actualMask.Mat, NormTypes.INF));
        Assert.Equal(0d, Cv2.Norm(expectedImage, actual.Mat, NormTypes.INF));
    }

    [Theory]
    [InlineData("""{"roi":{"type":"Circle","x":20,"y":20,"radius":8}}""")]
    [InlineData("""{"roi":{"type":"Polygon","points":[{"x":5,"y":4},{"x":30,"y":8},{"x":14,"y":32}]}}""")]
    [InlineData("""{"roi":{"type":"Brush","strokes":[{"mode":"Add","width":8,"points":[{"x":8,"y":10},{"x":28,"y":26}]},{"mode":"Erase","width":4,"points":[{"x":18,"y":18}]}]}}""")]
    public async Task CropNonRectangularRoi_PreservesTightBoundsAndBinaryMask(string json)
    {
        using var source = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(90));
        using var raster = Node("image.crop", json).GetRoi()!.BuildMask(source.Size());
        using var binary = new Mat();
        Cv2.Threshold(raster, binary, 0, 255, ThresholdTypes.Binary);
        var bounds = Cv2.BoundingRect(binary);
        using var expectedMask = new Mat(binary, bounds);
        using var expectedSource = new Mat(source, bounds);
        using var expected = new Mat(bounds.Size, source.Type(), Scalar.All(0));
        expectedSource.CopyTo(expected, expectedMask);

        var result = await RunImage(new ImageCropNode(), Image(source), json);
        using var actual = ImageFrom(result);
        using var actualMask = result.Outputs["roiMask"].Require<VisionImage>("roiMask");
        Assert.Equal(bounds.X, Convert.ToInt32(result.Summary["x"]));
        Assert.Equal(bounds.Y, Convert.ToInt32(result.Summary["y"]));
        Assert.Equal(Cv2.CountNonZero(expectedMask), Convert.ToInt32(result.Summary["selectedPixels"]));
        var cropOrigin = result.Outputs["cropOrigin"].Require<VisionPoint2D>("cropOrigin");
        Assert.Equal(bounds.X, cropOrigin.X);
        Assert.Equal(bounds.Y, cropOrigin.Y);
        var cropTransform = result.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal(new VisionPoint2D(3, 4), cropTransform.Apply(new VisionPoint2D(bounds.X + 3, bounds.Y + 4)));
        Assert.Equal(0d, Cv2.Norm(expected, actual.Mat, NormTypes.INF));
        Assert.Equal(0d, Cv2.Norm(expectedMask, actualMask.Mat, NormTypes.INF));
    }

    [Fact]
    public async Task PointToLineAndCircleSpacing_ComputeSignedGeometry()
    {
        var point = new Dictionary<string, VisionValue> { ["point"] = VisionValue.Point(new(9, 4)), ["line"] = VisionValue.Line(new(new(0, 0), new(6, 0))) };
        var result = await new PointLineDistanceNode().ExecuteAsync(new(point), Node("measure.pointLineDistance", "{}"), default);
        Assert.Equal(4, result.Outputs["distance"].Require<double>("distance"));
        var segment = await new PointLineDistanceNode().ExecuteAsync(new(point), Node("measure.pointLineDistance", """{"segment":true}"""), default);
        Assert.Equal(5, segment.Outputs["distance"].Require<double>("distance"), 6);

        var circles = new Dictionary<string, VisionValue> { ["circleA"] = VisionValue.Circle(new(0, 0, 5)), ["circleB"] = VisionValue.Circle(new(8, 0, 5)) };
        var spacing = await new CircleSpacingNode().ExecuteAsync(new(circles), Node("measure.circleSpacing", "{}"), default);
        Assert.Equal(8, spacing.Outputs["centerDistance"].Require<double>("centerDistance"));
        Assert.Equal(-2, spacing.Outputs["gap"].Require<double>("gap"));
        Assert.Equal(VisionDataType.Measurement, spacing.Outputs["measurement"].Type);
    }

    [Fact]
    public async Task MultiPointFits_ProduceOrthogonalLineAndCircle()
    {
        var lineInputs = new Dictionary<string, VisionValue>
        {
            ["point1"] = VisionValue.Point(new(0, 1)), ["point2"] = VisionValue.Point(new(1, 3)), ["point3"] = VisionValue.Point(new(2, 5)), ["point4"] = VisionValue.Point(new(3, 7))
        };
        var fitLine = await new FitLineNode().ExecuteAsync(new(lineInputs), Node("measure.fitLine", "{}"), default);
        var line = fitLine.Outputs["line"].Require<VisionLine2D>("line");
        Assert.Equal(Math.Atan(2) * 180 / Math.PI, line.AngleDeg, 6);
        Assert.Equal(Math.Sqrt(45), line.Length, 5);

        var circleInputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 8; i++)
        {
            var a = Math.PI * 2 * i / 8;
            circleInputs[$"point{i + 1}"] = VisionValue.Point(new(10 + 5 * Math.Cos(a), -3 + 5 * Math.Sin(a)));
        }
        var fitCircle = await new FitCircleNode().ExecuteAsync(new(circleInputs), Node("measure.fitCircle", "{}"), default);
        var circle = fitCircle.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.Equal(10, circle.X, 7); Assert.Equal(-3, circle.Y, 7); Assert.Equal(5, circle.Radius, 7);
    }

    [Fact]
    public async Task FitCircleRejectsCollinearPointsAndCatalogDeclaresAllExecutors()
    {
        var points = new Dictionary<string, VisionValue> { ["point1"] = VisionValue.Point(new(0, 0)), ["point2"] = VisionValue.Point(new(1, 1)), ["point3"] = VisionValue.Point(new(2, 2)) };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new FitCircleNode().ExecuteAsync(new(points), Node("measure.fitCircle", "{}"), default));
        string[] expected = ["image.crop", "image.resize", "image.rotate", "image.perspective", "measure.pointLineDistance", "measure.circleSpacing", "measure.fitLine", "measure.fitCircle"];
        Assert.Equal(expected.Order(), Batch2ImageCatalog.Items.Select(x => x.Type).Order());
    }

    private static NodeDefinition Node(string type, string json) => new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static VisionValue Image(Mat source) => VisionValue.Image(VisionImage.Own(source.Clone()));
    private static VisionImage ImageFrom(NodeExecutorResult result) => (VisionImage)result.Outputs["image"].Require<IVisionImage>("image");
    private static async Task<NodeExecutorResult> RunImage(IVisionNodeExecutor executor, VisionValue image, string json)
    {
        using var owned = (VisionImage)image.Require<IVisionImage>("image");
        return await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = image }), Node(executor.Type, json), default);
    }
}
