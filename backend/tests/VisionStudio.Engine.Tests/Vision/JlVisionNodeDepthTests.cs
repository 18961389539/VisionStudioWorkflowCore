using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Depth acceptance for the generic JLVision operator nodes: each node must emit the debug/visual
/// outputs (overlay, plot/points, counters) that the catalog advertises, so the UI can show them.
/// </summary>
public sealed class JlVisionNodeDepthTests
{
    [Fact]
    public async Task MatrixOp_EmitsHeatmapPlotAndCounters()
    {
        var node = new NodeDefinition("n1", "jlvision.matrixOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"transpose","matrixA":"[[1,2],[3,4]]"}"""));
        var result = await new JLVisionMatrixNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.True(result.Outputs.ContainsKey("overlay"));
        Assert.True(result.Outputs.ContainsKey("plot"));
        Assert.Equal(2L, result.Outputs["rows"].Require<long>("rows"));
        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
    }

    [Fact]
    public async Task MatrixOp_Determinant_ScalarStillEmitsVisuals()
    {
        var node = new NodeDefinition("n1", "jlvision.matrixOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"determinant","matrixA":"[[4,1],[1,3]]"}"""));
        var result = await new JLVisionMatrixNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.Equal(11d, result.Outputs["value"].Require<double>("value"), 6);
        Assert.NotNull(result.Outputs["plot"].Require<IReadOnlyList<VisionContour>>("plot"));
    }

    [Fact]
    public async Task HomMat_EmitsOverlayAndSampledPoints()
    {
        var node = new NodeDefinition("n1", "jlvision.homMat", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"translate","tx":10,"ty":20}"""));
        var result = await new JLVisionHomMatNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.NotEmpty(result.Outputs["points"].Require<IReadOnlyList<VisionContour>>("points"));
        Assert.Equal(10d, result.Outputs["transform"].Require<VisionTransform2D>("transform").H02, 6);
    }

    [Fact]
    public async Task HomMat_TransPixel_StillEmitsSamplePoints()
    {
        var node = new NodeDefinition("n1", "jlvision.homMat", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"transPixel","tx":5,"ty":6,"px":1,"py":2}"""));
        var result = await new JLVisionHomMatNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.True(double.IsFinite(result.Outputs["value"].Require<double>("value")));
        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
    }

    [Fact]
    public async Task Geometry_EmitsOverlayPointsAndCount()
    {
        var node = new NodeDefinition("n1", "jlvision.geometry", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"distancePp","row1":0,"column1":0,"row2":30,"column2":40,"row3":0,"column3":40,"row4":30,"column4":0}"""));
        var result = await new JLVisionGeometryNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.NotEmpty(result.Outputs["inputPoints"].Require<IReadOnlyList<VisionContour>>("inputPoints"));
        Assert.True(result.Outputs["componentCount"].Require<long>("componentCount") >= 1);
    }

    [Fact]
    public async Task Geometry_Intersection_ExposesOverlayCanvas()
    {
        var node = new NodeDefinition("n1", "jlvision.geometry", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"operator":"intersectionLl","row1":0,"column1":0,"row2":30,"column2":40,"row3":0,"column3":40,"row4":30,"column4":0}"""));
        var result = await new JLVisionGeometryNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

        Assert.False(result.Outputs["overlay"].Require<VisionImage>("overlay").Mat.Empty());
    }

    [Fact]
    public async Task RegionOp_EmitsOverlayComponentsAndArea()
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue> { ["region"] = VisionValue.Region(region) };
            var node = new NodeDefinition("n1", "jlvision.regionOp", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"complement"}"""));
            var result = await new JLVisionRegionOpNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);

            Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
            Assert.True(result.Outputs["components"].Require<long>("components") >= 1);
            Assert.True(result.Outputs["area"].Require<long>("area") > 0);
        }
    }

    [Fact]
    public async Task Threshold_EmitsOverlayAndComponents()
    {
        using var gray = GrayImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(gray.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.threshold", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"threshold","min":0,"max":128}"""));
        var result = await new JLVisionThresholdNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.True(result.Outputs["components"].Require<long>("components") >= 1);
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task ContourOp_EmitsOverlayAndPointCount()
    {
        var inputs = new Dictionary<string, VisionValue> { ["contour"] = VisionValue.Contours([Square()]) };
        var node = new NodeDefinition("n1", "jlvision.contourOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"smoothContours","count":5}"""));
        var result = await new JLVisionContourOpNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.True(result.Outputs["pointCount"].Require<long>("pointCount") > 0);
    }

    [Fact]
    public async Task RegionFeature_EmitsOverlayAndFeatureKey()
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue> { ["region"] = VisionValue.Region(region) };
            var node = new NodeDefinition("n1", "jlvision.regionFeature", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"feature":"area"}"""));
            var result = await new JLVisionRegionFeatureNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);

            Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
            Assert.Equal("area", result.Outputs["feature"].Require<string>("feature"));
        }
    }

    [Fact]
    public async Task ContourFeature_EmitsOverlayAndContourEcho()
    {
        var inputs = new Dictionary<string, VisionValue> { ["contour"] = VisionValue.Contours([Square()]) };
        var node = new NodeDefinition("n1", "jlvision.contourFeature", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"feature":"circularity"}"""));
        var result = await new JLVisionContourFeatureNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.NotEmpty(result.Outputs["contour"].Require<IReadOnlyList<VisionContour>>("contour"));
        Assert.True(result.Outputs["pointCount"].Require<long>("pointCount") > 0);
    }

    [Fact]
    public async Task Measure1d_EmitsOverlayAndEdgeContours()
    {
        using var gray = GrayImage();
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(gray.Clone())) };
        var node = new NodeDefinition("n1", "jlvision.measure1d", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"shape":"rectangle","method":"measurePos","row":24,"column":24,"phi":0,"measureLength1":20,"measureLength2":2,"sigma":1,"threshold":20}"""));
        var result = await new JLVisionMeasure1dNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        Assert.NotNull(result.Outputs["overlay"].Require<VisionImage>("overlay"));
        Assert.NotNull(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
        foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
    }

    private static (VisionRegion Region, Mat Gray) BuildScene()
    {
        var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(10, 10, 24, 18), Scalar.All(255), -1);
        var bytes = new byte[64 * 64];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, bytes, 0, bytes.Length);
        mask.Dispose();
        var region = ImageDifferenceMetrics.RegionFromMask(bytes, 64, 64);

        var gray = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(gray, new Rect(10, 10, 24, 18), Scalar.All(210), -1);
        return (region, gray);
    }

    private static VisionContour Square(int x = 12, int y = 12, int size = 40)
    {
        var points = new List<VisionPoint2D>
        {
            new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size), new(x, y)
        };
        return new VisionContour(0, points, -1, false, size * size, 4 * size, 0.78,
            new VisionRectangle2D(x, y, size, size), new VisionCircle(x + size / 2d, y + size / 2d, size / 2d));
    }

    private static Mat GrayImage()
    {
        var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(image, new Rect(10, 12, 30, 18), Scalar.All(200), -1);
        return image;
    }
}
