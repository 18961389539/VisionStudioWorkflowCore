using System.Text.Json;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 最佳拟合对齐与固定半径拟合（2026-10-28）。两个节点都是几何闭式解，
/// 所以用"已知变换能否原样恢复"做断言，而不是只看不抛异常。
/// </summary>
public sealed class BatchMeasurementAlignTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionPoint2D[] Arc(double cx, double cy, double r, double startDeg, double sweepDeg, int count)
    {
        var points = new VisionPoint2D[count];
        for (var index = 0; index < count; index++)
        {
            var degrees = startDeg + sweepDeg * index / (count - 1);
            var radians = degrees * Math.PI / 180.0;
            points[index] = new VisionPoint2D(cx + r * Math.Cos(radians), cy + r * Math.Sin(radians));
        }
        return points;
    }

    private static VisionPoint2D[] Transform(VisionPoint2D[] source, double rotationDeg, double tx, double ty, double scale = 1)
    {
        var radians = rotationDeg * Math.PI / 180.0;
        var cos = Math.Cos(radians) * scale;
        var sin = Math.Sin(radians) * scale;
        var result = new VisionPoint2D[source.Length];
        for (var index = 0; index < source.Length; index++)
            result[index] = new VisionPoint2D(cos * source[index].X - sin * source[index].Y + tx,
                                              sin * source[index].X + cos * source[index].Y + ty);
        return result;
    }

    // ---------------- 固定半径拟合 ----------------

    [Fact]
    public async Task FixedRadius_RecoversKnownCentreExactly()
    {
        var points = Arc(100, 80, 40, 20, 300, 60);
        var result = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Ctx(new() { ["points"] = VisionValue.PointList(points) }),
            Def("measure.fitCircleFixedRadius", """{"radius":40}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.InRange((double)result.Outputs["centerX"].Value!, 99.99, 100.01);
        Assert.InRange((double)result.Outputs["centerY"].Value!, 79.99, 80.01);
        Assert.InRange((double)result.Outputs["rmsError"].Value!, 0, 1e-3);
        Assert.Equal(40d, (double)result.Outputs["radius"].Value!, 6);
        var circle = (VisionCircle)result.Outputs["circle"].Value!;
        Assert.Equal(100d, circle.X, 2);
        Assert.Contains("measure.fitCircleFixedRadius", (string)result.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task FixedRadius_ResistsOutlierThatWouldDragAFreeFit()
    {
        var points = Arc(120, 60, 25, 0, 240, 48).ToList();
        // 一个明显偏外的离群点：自由拟合会被拉走，固定半径只能把它记成残差。
        points.Add(new VisionPoint2D(120 + 25 * 1.9, 60));

        var fixedRadius = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Ctx(new() { ["points"] = VisionValue.PointList(points) }),
            Def("measure.fitCircleFixedRadius", """{"radius":25}"""), CancellationToken.None);

        Assert.True((bool)fixedRadius.Outputs["found"].Value!);
        Assert.InRange((double)fixedRadius.Outputs["centerX"].Value!, 119, 121);
        Assert.InRange((double)fixedRadius.Outputs["centerY"].Value!, 59, 61);
        Assert.True((double)fixedRadius.Outputs["maxError"].Value! > 20);
    }

    [Fact]
    public async Task FixedRadius_GatesOnRmsAndRejectsTooFewPoints()
    {
        var noisy = Arc(50, 50, 20, 0, 270, 40)
            .Select((point, index) => index % 4 == 0 ? new VisionPoint2D(point.X + 1.5, point.Y) : point)
            .ToArray();
        var gated = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Ctx(new() { ["points"] = VisionValue.PointList(noisy) }),
            Def("measure.fitCircleFixedRadius", """{"radius":20,"maxRmsError":0.0001}"""), CancellationToken.None);
        Assert.False((bool)gated.Summary["withinTolerance"]!);

        var tooFew = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Ctx(new() { ["points"] = VisionValue.PointList([new VisionPoint2D(0, 0), new VisionPoint2D(1, 1)]) }),
            Def("measure.fitCircleFixedRadius", """{"radius":20}"""), CancellationToken.None);
        Assert.False((bool)tooFew.Outputs["found"].Value!);
    }

    // ---------------- 最佳拟合对齐 ----------------

    [Fact]
    public async Task BestFit_RecoversPureTranslation()
    {
        var reference = Arc(200, 150, 60, 15, 320, 80);
        var measured = Transform(reference, 0, 60, -25);

        var result = await new BestFitAlignNode().ExecuteAsync(
            Ctx(new() { ["measured"] = VisionValue.PointList(measured), ["reference"] = VisionValue.PointList(reference) }),
            Def("measure.bestFitAlign", """{"fitMode":"rigid","maxIterations":60}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.InRange((double)result.Outputs["rotationDeg"].Value!, -0.2, 0.2);
        Assert.InRange((double)result.Outputs["deltaX"].Value!, -60.5, -59.5);
        Assert.InRange((double)result.Outputs["deltaY"].Value!, 24.5, 25.5);
        Assert.InRange((double)result.Outputs["rmsError"].Value!, 0, 0.05);
        Assert.Equal(1d, (double)result.Outputs["scale"].Value!, 6);

        var aligned = (IReadOnlyList<VisionPoint2D>)result.Outputs["alignedPoints"].Value!;
        Assert.Equal(measured.Length, aligned.Count);
        Assert.InRange(aligned[0].X, reference[0].X - 0.1, reference[0].X + 0.1);
    }

    [Fact]
    public async Task BestFit_RecoversRotationAboutTheCentroid()
    {
        var reference = Arc(180, 120, 45, 40, 300, 72);
        double cx = 180, cy = 120;
        var rotated = Transform(reference.Select(p => new VisionPoint2D(p.X - cx, p.Y - cy)).ToArray(), 25, 0, 0)
            .Select(p => new VisionPoint2D(p.X + cx, p.Y + cy)).ToArray();

        var result = await new BestFitAlignNode().ExecuteAsync(
            Ctx(new() { ["measured"] = VisionValue.PointList(rotated), ["reference"] = VisionValue.PointList(reference) }),
            Def("measure.bestFitAlign", """{"fitMode":"rigid","maxIterations":80}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["found"].Value!);
        // 对齐后 measured 被转回 reference，所以解出的角度是 -25°。
        Assert.InRange((double)result.Outputs["rotationDeg"].Value!, -25.3, -24.7);
        Assert.InRange((double)result.Outputs["rmsError"].Value!, 0, 0.05);
    }

    [Fact]
    public async Task BestFit_SimilarityRecoversScaleAndRigidKeepsScaleAtOne()
    {
        var reference = Arc(160, 140, 50, 10, 320, 80);
        var scaled = Transform(reference.Select(p => new VisionPoint2D(p.X - 160, p.Y - 140)).ToArray(), 0, 0, 0, 1.15)
            .Select(p => new VisionPoint2D(p.X + 160, p.Y + 140)).ToArray();

        var similarity = await new BestFitAlignNode().ExecuteAsync(
            Ctx(new() { ["measured"] = VisionValue.PointList(scaled), ["reference"] = VisionValue.PointList(reference) }),
            Def("measure.bestFitAlign", """{"fitMode":"similarity","maxIterations":80}"""), CancellationToken.None);
        Assert.InRange((double)similarity.Outputs["scale"].Value!, 1 / 1.15 - 0.01, 1 / 1.15 + 0.01);
        Assert.InRange((double)similarity.Outputs["rmsError"].Value!, 0, 0.05);

        var rigid = await new BestFitAlignNode().ExecuteAsync(
            Ctx(new() { ["measured"] = VisionValue.PointList(scaled), ["reference"] = VisionValue.PointList(reference) }),
            Def("measure.bestFitAlign", """{"fitMode":"rigid","maxIterations":80}"""), CancellationToken.None);
        Assert.Equal(1d, (double)rigid.Outputs["scale"].Value!, 6);
        // 刚性模型吸收不了 15% 的尺度差，残差必然显著。
        Assert.True((double)rigid.Outputs["rmsError"].Value! > 0.5);
    }

    [Fact]
    public async Task BestFit_ReportsFoundFalseOnTooFewPoints()
    {
        var result = await new BestFitAlignNode().ExecuteAsync(
            Ctx(new()
            {
                ["measured"] = VisionValue.PointList([new VisionPoint2D(0, 0), new VisionPoint2D(1, 0)]),
                ["reference"] = VisionValue.PointList(Arc(0, 0, 10, 0, 300, 20))
            }),
            Def("measure.bestFitAlign"), CancellationToken.None);

        Assert.False((bool)result.Outputs["found"].Value!);
        Assert.Contains("at least 3 points", (string)result.Outputs["diagnostics"].Value!);
    }
}
