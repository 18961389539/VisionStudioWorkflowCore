using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 四个特征检测节点（2026-10-28）。用合成图做几何断言：角点数量与位置、椭圆中心与半轴、
/// 脊线长度与宽度、Gabor 主导方向。只看"不抛异常"是抓不到方向算错这类问题的。
/// </summary>
public sealed class BatchFeatureDetectorTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task Corner_FindsRectangleCornersAndSuppressesNeighbours()
    {
        using var image = new Mat(120, 160, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(image, new Rect(40, 30, 60, 40), Scalar.All(230), -1);

        var result = await new FeatureCornerNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.corner", """{"method":"shi-tomasi","maxCorners":50,"qualityLevel":0.05,"minDistance":8}"""),
            CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        var points = (IReadOnlyList<VisionPoint2D>)result.Outputs["points"].Value!;
        Assert.InRange(points.Count, 4, 12);
        // 四个角附近都必须有点，且不会在同一个角上挤两个。
        foreach (var corner in new[] { (40d, 30d), (99d, 30d), (40d, 69d), (99d, 69d) })
            Assert.Contains(points, point => Math.Abs(point.X - corner.Item1) < 3 && Math.Abs(point.Y - corner.Item2) < 3);
        Assert.Contains("feature.corner", (string)result.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task Corner_HarrisWithSubpixelRefinementAndInvalidInput()
    {
        using var sharp = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(sharp, new Rect(20, 20, 30, 30), Scalar.All(200), -1);
        // 硬阶跃上 cornerSubPix 本来就收敛到整数坐标，先模糊一次才谈得上亚像素。
        using var image = new Mat();
        Cv2.GaussianBlur(sharp, image, new Size(5, 5), 1.5);

        var harris = await new FeatureCornerNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.corner", """{"method":"harris","qualityLevel":0.05,"maxCorners":100,"minDistance":5,"subpixel":true}"""),
            CancellationToken.None);
        Assert.True((long)harris.Outputs["count"].Value! > 0, (string)harris.Outputs["diagnostics"].Value!);
        var refined = (IReadOnlyList<VisionPoint2D>)harris.Outputs["points"].Value!;
        // 精修只允许把角点挪动不到 1 像素，且必须仍贴近真实角。
        foreach (var point in refined)
        {
            var nearestCorner = new[] { (20d, 20d), (49d, 20d), (20d, 49d), (49d, 49d) }
                .Min(corner => Math.Sqrt(Math.Pow(point.X - corner.Item1, 2) + Math.Pow(point.Y - corner.Item2, 2)));
            Assert.True(nearestCorner < 1.5, $"corner drifted to ({point.X},{point.Y}), {nearestCorner:0.00}px from the true corner");
        }

        using var empty = new Mat();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FeatureCornerNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(empty) }), Def("feature.corner"), CancellationToken.None));
    }

    [Fact]
    public async Task Ellipse_RecoversCentreAndSemiAxes()
    {
        using var image = new Mat(160, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(image, new Point(100, 80), new Size(60, 30), 0, 0, 360, Scalar.All(255), 2);

        var result = await new FeatureEllipseNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.ellipse", """{"minArea":1000,"minAspectRatio":0.3,"maxAspectRatio":1,"minCoverageDeg":300,"maxRmsError":3}"""),
            CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!, (string)result.Outputs["diagnostics"].Value!);
        // 内外两条边缘拟合出同一个椭圆，节点会去重成 1 个。
        Assert.Equal(1L, (long)result.Outputs["count"].Value!);
        var center = (VisionPoint2D)result.Outputs["center"].Value!;
        Assert.InRange(center.X, 99, 101);
        Assert.InRange(center.Y, 79, 81);
        Assert.InRange((double)result.Outputs["majorAxis"].Value!, 118, 122);
        Assert.InRange((double)result.Outputs["minorAxis"].Value!, 58, 62);
        Assert.Contains("feature.ellipse", (string)result.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task Ellipse_AspectFilterRejectsElongatedShape()
    {
        using var image = new Mat(160, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(image, new Point(100, 80), new Size(60, 30), 0, 0, 360, Scalar.All(255), 2);

        // maxAspectRatio=1 只接受长短轴相等的圆，长椭圆必须被拒。
        var strict = await new FeatureEllipseNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.ellipse", """{"minArea":100,"minAspectRatio":0.99,"maxAspectRatio":1,"minCoverageDeg":300}"""),
            CancellationToken.None);
        Assert.False((bool)strict.Outputs["found"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FeatureEllipseNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(image) }),
                Def("feature.ellipse", """{"minAspectRatio":0.8,"maxAspectRatio":0.3}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Ridge_ExtractsCentreLineOfAThinBrightLine()
    {
        using var image = new Mat(120, 200, MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(image, new Rect(20, 58, 160, 4), Scalar.All(240), -1);

        var result = await new FeatureRidgeNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.ridge", """{"sigma":1.5,"low":5,"high":10,"polarity":"light","lineModel":"bar-shaped","extractWidth":true,"minLength":20}"""),
            CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.True((long)result.Outputs["count"].Value! >= 1);
        Assert.True((double)result.Outputs["totalLength"].Value! > 100);
        var points = (IReadOnlyList<VisionPoint2D>)result.Outputs["points"].Value!;
        Assert.True(points.Count > 50);
        // 中心线应贴着 y≈59..60，而不是跑到线的上下边缘。
        Assert.InRange(points.Average(point => point.Y), 58, 62);
        Assert.Contains("feature.ridge", (string)result.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task GaborBank_DetectsDominantOrientationOfVerticalStripes()
    {
        using var image = new Mat(120, 160, MatType.CV_8UC1, Scalar.All(0));
        for (var y = 0; y < image.Rows; y++)
        for (var x = 0; x < image.Cols; x++)
            image.Set(y, x, (byte)Math.Clamp(128 + 100 * Math.Sin(2 * Math.PI * x / 8.0), 0, 255));

        var result = await new FeatureGaborBankNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.gaborBank", """{"wavelength":8,"orientations":6,"sigmaRatio":0.56,"gamma":0.5,"energyMode":"sum","directionMap":false}"""),
            CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.Equal(6L, (long)result.Outputs["count"].Value!);
        // 灰度沿 x 变化，主导方向为 0 度（6 个方向里 0/30/.../150）。
        Assert.InRange((double)result.Outputs["dominantDirectionDeg"].Value!, -0.01, 0.01);
        Assert.True((double)result.Outputs["anisotropy"].Value! > 1.2);
        Assert.True((double)result.Outputs["maxEnergy"].Value! > 100);
    }

    [Fact]
    public async Task GaborBank_RejectsWavelengthLargerThanHalfImage()
    {
        using var image = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(50));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FeatureGaborBankNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(image) }),
                Def("feature.gaborBank", """{"wavelength":400}"""), CancellationToken.None));
    }
}
