using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 分割·轮廓·特征补全批（node-gap-analysis.md 第十节）的行为覆盖。
///
/// 这批节点做的是数据形态转换，所以用例重点是**坐标语义与往返一致性**：
/// ROI 的锚点、轮廓光栅化的面积、region→mask 的环回、凸性缺陷的深度。
/// </summary>
public sealed class RegionGapNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    private static VisionDetection Detection(double x, double y, double w, double h, double radius = 0)
        => new(0, x, y, w, h, w * h, 2 * (w + h), .8, new VisionCircle(x, y, radius),
            new VisionRectangle2D(x - w / 2, y - h / 2, w, h));

    // ── Roi 生成 ────────────────────────────────────────────────────
    [Fact]
    public async Task RoiFromGeometry_BuildsRectangleCircleAndPolygon()
    {
        var rectangle = await new RoiFromGeometryNode().ExecuteAsync(Ctx(new()),
            Def("roi.fromGeometry", """{"shape":"rectangle","x":10,"y":20,"width":30,"height":40}"""), CancellationToken.None);
        var roi = rectangle.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Rectangle, roi.Type);
        Assert.Equal(10d, roi.X!.Value, 6);
        Assert.Equal(40d, roi.Height!.Value, 6);

        // 旋转矩形必须取轴对齐外接框（ROI 没有角度概念，宁可放大也不漏检）
        var rotated = await new RoiFromGeometryNode().ExecuteAsync(
            Ctx(new() { ["rectangle"] = VisionValue.Rectangle(new VisionRectangle2D(50, 50, 20, 20, 90)) }),
            Def("roi.fromGeometry", """{"shape":"rectangle","rectangleAnchor":"center"}"""), CancellationToken.None);
        var rotatedRoi = rotated.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(40d, rotatedRoi.X!.Value, 3);
        Assert.InRange(rotatedRoi.Width!.Value, 18, 22);

        var circle = await new RoiFromGeometryNode().ExecuteAsync(Ctx(new()),
            Def("roi.fromGeometry", """{"shape":"circle","centerX":60,"centerY":70,"radius":15,"expand":5}"""), CancellationToken.None);
        var circleRoi = circle.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(20d, circleRoi.Radius!.Value, 6);

        var polygon = await new RoiFromGeometryNode().ExecuteAsync(
            Ctx(new() { ["pointList"] = VisionValue.PointList([new(0, 0), new(10, 0), new(10, 10)]) }),
            Def("roi.fromGeometry", """{"shape":"polygon"}"""), CancellationToken.None);
        Assert.Equal(3L, polygon.Outputs["pointCount"].Require<long>("pointCount"));
        Assert.Equal(3, polygon.Outputs["roi"].Require<VisionRoi>("roi").Points!.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new RoiFromGeometryNode().ExecuteAsync(Ctx(new()),
                Def("roi.fromGeometry", """{"shape":"rectangle","width":0}"""), CancellationToken.None));
    }

    [Fact]
    public async Task RoiFromDetection_CentresOnTheDetectionAndHandlesMissingIndex()
    {
        var detections = VisionValue.Detections([Detection(50, 60, 20, 10, radius: 5)]);
        var box = await new RoiFromDetectionNode().ExecuteAsync(
            Ctx(new() { ["detections"] = detections }),
            Def("roi.fromDetection", """{"index":0,"shape":"rectangle","expand":2}"""), CancellationToken.None);
        var roi = box.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(38d, roi.X!.Value, 6);   // 50 - (20+4)/2
        Assert.Equal(24d, roi.Width!.Value, 6);

        var circle = await new RoiFromDetectionNode().ExecuteAsync(
            Ctx(new() { ["detections"] = detections }),
            Def("roi.fromDetection", """{"shape":"circle"}"""), CancellationToken.None);
        Assert.Equal(5d, circle.Outputs["radius"].Require<double>("radius"), 6);

        var missing = await new RoiFromDetectionNode().ExecuteAsync(
            Ctx(new() { ["detections"] = detections }),
            Def("roi.fromDetection", """{"index":3}"""), CancellationToken.None);
        Assert.False(missing.Outputs.ContainsKey("roi"));
        Assert.Equal(false, missing.Summary["found"]);
    }

    // ── 轮廓 → 区域 → 掩膜 往返 ─────────────────────────────────────
    private static VisionContour Square(double left, double top, double right, double bottom)
        => Contour(new(left, top), new(right, top), new(right, bottom), new(left, bottom));

    [Fact]
    public async Task RegionFromContour_FillsPolygon_AndReachesArea()
    {
        var result = await new RegionFromContourNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Square(10, 10, 30, 30)]) }),
            Def("region.fromContour", """{"index":0,"closed":true}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        // 21×21 像素的实心方块（FillPoly 含边界）
        Assert.InRange(result.Outputs["area"].Require<long>("area"), 380, 460);
        var region = result.Outputs["region"].Require<VisionRegion>("region");
        Assert.Equal(31, region.Width);
        Assert.Single(region.Components);

        // 接了图像就以图像为画布，坐标不再被裁到包围盒
        using var canvas = VisionImage.Own(new OpenCvSharp.Mat(100, 100, OpenCvSharp.MatType.CV_8UC1));
        var full = await new RegionFromContourNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Square(10, 10, 30, 30)]), ["image"] = VisionValue.Image(canvas) }),
            Def("region.fromContour"), CancellationToken.None);
        Assert.Equal(100, full.Outputs["region"].Require<VisionRegion>("region").Width);
    }

    [Fact]
    public async Task RegionToMask_RoundTripsAreaExactly()
    {
        var region = await new RegionFromContourNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Square(10, 10, 30, 30)]) }),
            Def("region.fromContour"), CancellationToken.None);
        var mask = await new RegionToMaskNode().ExecuteAsync(
            Ctx(new() { ["region"] = region.Outputs["region"] }),
            Def("region.toMask", """{"foreground":255,"background":0}"""), CancellationToken.None);

        var image = mask.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(region.Outputs["area"].Require<long>("area"), mask.Outputs["area"].Require<long>("area"));
        Assert.Equal(region.Outputs["area"].Require<long>("area"), OpenCvSharp.Cv2.CountNonZero(image.Mat));

        // 掩膜可以再回灌成区域，两跳之后面积不变
        var back = await new RegionFromMaskNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(image.Mat.Clone())) }),
            Def("region.fromMask"), CancellationToken.None);
        Assert.Equal(region.Outputs["area"].Require<long>("area"), back.Outputs["area"].Require<long>("area"));
    }

    // ── 凸性缺陷 ────────────────────────────────────────────────────
    [Fact]
    public async Task Convexity_ConvexSquareHasNoDefect()
    {
        var result = await new ContourConvexityNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Square(0, 0, 40, 40)]) }),
            Def("contour.convexity", """{"closed":true,"minDefectDepth":0.5}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1d, result.Outputs["convexity"].Require<double>("convexity"), 3);
        Assert.Equal(0L, result.Outputs["defectCount"].Require<long>("defectCount"));
        Assert.True(result.Outputs["maxDefectDepth"].Require<double>("maxDefectDepth") < .5);
    }

    [Fact]
    public async Task Convexity_FindsNotchDepthAndCountsOneDefect()
    {
        // 上边缘挖一个深 10 的三角缺口：(15,0)→(20,10)→(25,0)
        var notched = Contour(new(0, 0), new(15, 0), new(20, 10), new(25, 0), new(40, 0),
            new(40, 40), new(0, 40));
        var result = await new ContourConvexityNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([notched]) }),
            Def("contour.convexity", """{"minDefectDepth":0.5,"maxDefectDepth":8}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.InRange(result.Outputs["maxDefectDepth"].Require<double>("maxDefectDepth"), 9.5, 10.5);
        Assert.Equal(1L, result.Outputs["defectCount"].Require<long>("defectCount"));
        // 面积 1600 - 三角 50 = 1550，凸包 1600
        Assert.InRange(result.Outputs["area"].Require<double>("area"), 1540, 1560);
        Assert.InRange(result.Outputs["convexity"].Require<double>("convexity"), .95, .975);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));   // 10 > 8
        Assert.Contains("depth", result.Outputs["defectsJson"].Require<string>("defectsJson"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourConvexityNode().ExecuteAsync(
                Ctx(new() { ["contours"] = VisionValue.Contours([notched]) }),
                Def("contour.convexity", """{"closed":false}"""), CancellationToken.None));
    }
}
