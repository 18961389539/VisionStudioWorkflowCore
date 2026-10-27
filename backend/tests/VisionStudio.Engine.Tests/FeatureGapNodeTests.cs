using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 行业件补全批（第十节 P2）的行为覆盖：超像素要真的切成多块且像素不丢，
/// 轮廓匹配要在旋转/缩放后仍然高分、换一个形状就掉分。
/// </summary>
public sealed class FeatureGapNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    /// <summary>不对称的"房子"轮廓：旋转后必须仍然认得出来（正方形有 90° 对称，测不出旋转）。</summary>
    private static VisionPoint2D[] House() =>
        [new(0, 0), new(40, 0), new(40, 30), new(20, 50), new(0, 30)];

    private static VisionPoint2D[] Transform(VisionPoint2D[] points, double degrees, double scale, double cx, double cy)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var centerX = points.Average(p => p.X); var centerY = points.Average(p => p.Y);
        return points.Select(p =>
        {
            var dx = (p.X - centerX) * scale; var dy = (p.Y - centerY) * scale;
            return new VisionPoint2D(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }).ToArray();
    }

    // ── 超像素 ──────────────────────────────────────────────────────
    [Fact]
    public async Task Superpixel_SplitsTextureAndKeepsEveryPixel()
    {
        using var source = new Mat(128, 128, MatType.CV_8UC3, Scalar.All(40));
        Cv2.Rectangle(source, new Rect(64, 0, 64, 128), new Scalar(210, 210, 210), -1);
        using var vision = VisionImage.Own(source.Clone());

        var result = await new SuperpixelNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("feature.superpixel", """{"regionSize":16,"ruler":10,"iterations":5,"enforceConnectivity":true}"""),
            CancellationToken.None);

        var count = result.Outputs["count"].Require<long>("count");
        Assert.True(count >= 4, $"expected several superpixels, got {count}");
        Assert.Equal(128L * 128, Convert.ToInt64(result.Summary["pixelCount"]));
        var region = result.Outputs["region"].Require<VisionRegion>("region");
        Assert.Equal(128, region.Width);
        Assert.Equal((int)count, region.Components.Count);
        Assert.Equal(128, result.Outputs["labels"].Require<VisionImage>("labels").Mat.Rows);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SuperpixelNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(new Mat(16, 16, MatType.CV_16UC1, Scalar.All(100)))) }),
                Def("feature.superpixel"), CancellationToken.None));
    }

    // ── 轮廓形状匹配 ────────────────────────────────────────────────
    [Fact]
    public async Task ContourMatch_RecoversRotationAndScale()
    {
        var template = Contour(House());
        var scene = Contour(Transform(House(), 30, 1.5, 300, 200));

        var result = await new ContourMatchXldNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([scene]), ["templateContours"] = VisionValue.Contours([template]) }),
            Def("contour.matchXld", """{"samples":360,"minScore":0.8,"allowRotation":true}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["score"].Require<double>("score") > .95);
        Assert.InRange(Math.Abs(result.Outputs["rotationDeg"].Require<double>("rotationDeg")), 29, 31);
        Assert.Equal(1.5, result.Outputs["scale"].Require<double>("scale"), 2);
        // 质心取等弧长质心，与"顶点平均"差 1 px 量级，属正常
        Assert.InRange(result.Outputs["center"].Require<VisionPoint2D>("center").X, 298, 303);
    }

    [Fact]
    public async Task ContourMatch_ScoresLowForADifferentShape()
    {
        var template = Contour(House());
        var other = Contour(new(0, 0), new(60, 0), new(60, 8), new(0, 8));   // 细长条，半径签名完全不同

        var result = await new ContourMatchXldNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([other]), ["templateContours"] = VisionValue.Contours([template]) }),
            Def("contour.matchXld", """{"samples":360,"minScore":0.9}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["score"].Require<double>("score") < .9);
    }
}
