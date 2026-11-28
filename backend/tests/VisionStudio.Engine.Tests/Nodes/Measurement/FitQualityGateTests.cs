using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// measure.fitCircle / fitLine / fitCircleFixedRadius 的质量门契约：
/// 支撑比例、全点残差、收敛门与径向逐点门都要给出可复核的结构化结果，而不是抛异常。
/// </summary>
public sealed class FitQualityGateTests
{
    [Fact]
    public async Task FitCircle_SupportRatioGateRejectsFewPointsFakingAHighConfidenceCircle()
    {
        // 9 个落在圆周上的点 + 3 个远离的离群点：内点 RMS 很小，但支撑比例只有 75%。
        var inputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 9; i++)
        {
            var angle = i * 2 * Math.PI / 9;
            inputs[$"point{i + 1}"] = VisionValue.Point(new(100 + 40 * Math.Cos(angle), 100 + 40 * Math.Sin(angle)));
        }
        for (var i = 0; i < 3; i++) inputs[$"point{10 + i}"] = VisionValue.Point(new(10 + i * 5, 10 + i * 7));

        var result = await new FitCircleNode().ExecuteAsync(
            Context(inputs), Definition("measure.fitCircle", """{"minInlierRatio":0.8}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"),
            string.Join(";", result.Outputs.Select(kv => $"{kv.Key}={kv.Value.Value}")));
        Assert.Equal("insufficient-inlier-ratio", result.Outputs["reason"].Require<string>("reason"));
        Assert.Equal("fail", result.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.Equal(12L, result.Outputs["pointCount"].Require<long>("pointCount"));
        Assert.InRange(result.Outputs["inlierRatio"].Require<double>("inlierRatio"), 0.7, 0.78);
        // 全点 RMS 必须明显大于内点 RMS，否则“少数点撑起高置信”无法被看见。
        Assert.True(result.Outputs["allPointRmsError"].Require<double>("allPointRmsError")
            > result.Outputs["rmsError"].Require<double>("rmsError") * 10);
        Assert.True(result.Outputs.ContainsKey("circle"));
    }

    [Fact]
    public async Task FitCircle_RmsGateReturnsStructuredFailureWithEvidence()
    {
        var inputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            // 每点半径都不同（±0.5 的正弦抖动），避免子集恰好落在某个圆上而绕过 RMS 门。
            var radius = 30 + 0.5 * Math.Sin(i * 1.7);
            inputs[$"point{i + 1}"] = VisionValue.Point(new(60 + radius * Math.Cos(angle), 60 + radius * Math.Sin(angle)));
        }

        var result = await new FitCircleNode().ExecuteAsync(
            Context(inputs), Definition("measure.fitCircle", """{"maxRmsError":0.01}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"),
            string.Join(";", result.Outputs.Select(kv => $"{kv.Key}={kv.Value.Value}")));
        Assert.Equal("rms-exceeded", result.Outputs["reason"].Require<string>("reason"));
        Assert.Equal(8L, result.Outputs["pointCount"].Require<long>("pointCount"));
        Assert.True(result.Outputs["inlierCount"].Require<long>("inlierCount") > 0);
        Assert.True(result.Outputs["maxError"].Require<double>("maxError") > 0);
        Assert.True(result.Outputs.ContainsKey("radius"));
    }

    [Fact]
    public async Task FitCircle_GatesOffIsNotEvaluatedNotPass()
    {
        var inputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 6; i++)
        {
            var angle = i * Math.PI / 3;
            inputs[$"point{i + 1}"] = VisionValue.Point(new(40 + 25 * Math.Cos(angle), 40 + 25 * Math.Sin(angle)));
        }

        var result = await new FitCircleNode().ExecuteAsync(
            Context(inputs), Definition("measure.fitCircle", "{}"), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("not-evaluated", result.Outputs["evaluationState"].Require<string>("evaluationState"));
    }

    [Fact]
    public async Task FitLine_ReversedInputKeepsGeometryAndExposesPolarity()
    {
        var forward = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 6; i++) forward[$"point{i + 1}"] = VisionValue.Point(new(10 + i * 20, 30 + i * 10));
        var reversed = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 6; i++) reversed[$"point{i + 1}"] = VisionValue.Point(new(110 - i * 20, 80 - i * 10));

        var a = await new FitLineNode().ExecuteAsync(Context(forward), Definition("measure.fitLine", "{}"), CancellationToken.None);
        var b = await new FitLineNode().ExecuteAsync(Context(reversed), Definition("measure.fitLine", "{}"), CancellationToken.None);

        Assert.Equal(a.Outputs["length"].Require<double>("length"), b.Outputs["length"].Require<double>("length"), 3);
        Assert.Equal(a.Outputs["angleMod180"].Require<double>("angleMod180"), b.Outputs["angleMod180"].Require<double>("angleMod180"), 3);
        Assert.Equal(a.Outputs["directionSign"].Require<long>("directionSign"), -b.Outputs["directionSign"].Require<long>("directionSign"));
        Assert.True(b.Outputs["polarityKnown"].Require<bool>("polarityKnown"));
    }

    [Fact]
    public async Task FixedRadius_NonConvergenceBlocksFound()
    {
        // 只给四分之一圆弧：质心明显偏离圆心，Späth 一步迭代不可能收敛。
        var points = Enumerable.Range(0, 12)
            .Select(i =>
            {
                var angle = i * (Math.PI / 2) / 11;
                return new VisionPoint2D(100 + 50 * Math.Cos(angle), 100 + 50 * Math.Sin(angle));
            })
            .ToArray();

        var result = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Context(new() { ["points"] = VisionValue.PointList(points) }),
            Definition("measure.fitCircleFixedRadius", """{"radius":50,"maxIterations":1}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("not-converged", result.Outputs["reason"].Require<string>("reason"));
        Assert.False(result.Outputs["converged"].Require<bool>("converged"));
        // 仍要交出历史最优圆心供复检。
        Assert.True(result.Outputs.ContainsKey("circle"));
    }

    [Fact]
    public async Task FixedRadius_RadialGateSplitsTooCloseAndTooFar()
    {
        var points = new List<VisionPoint2D>();
        for (var i = 0; i < 24; i++)
        {
            var angle = i * 2 * Math.PI / 24;
            var radius = i == 5 ? 55 : i == 9 ? 45 : 50; // 一个偏远 5px、一个偏近 5px
            points.Add(new VisionPoint2D(200 + radius * Math.Cos(angle), 200 + radius * Math.Sin(angle)));
        }

        var result = await new FixedRadiusCircleFitNode().ExecuteAsync(
            Context(new() { ["points"] = VisionValue.PointList(points.ToArray()) }),
            Definition("measure.fitCircleFixedRadius", """{"radius":50,"maxRadialError":2}"""), CancellationToken.None);

        Assert.True(result.Outputs["converged"].Require<bool>("converged"));
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("radial-error-exceeded", result.Outputs["reason"].Require<string>("reason"));
        Assert.Equal(1L, result.Outputs["tooCloseCount"].Require<long>("tooCloseCount"));
        Assert.Equal(1L, result.Outputs["tooFarCount"].Require<long>("tooFarCount"));
        Assert.InRange(result.Outputs["maxRadialError"].Require<double>("maxRadialError"), 4.5, 5.5);
    }

    [Fact]
    public async Task FitEllipse_QualityGateAndSelectionAreObservable()
    {
        var points = new List<VisionPoint2D>();
        for (var i = 0; i < 40; i++)
        {
            var angle = i * 2 * Math.PI / 40;
            points.Add(new VisionPoint2D(200 + 60 * Math.Cos(angle), 150 + 30 * Math.Sin(angle)));
        }
        points.Add(new VisionPoint2D(260, 40)); // 混入邻边
        var contour = new VisionContour(1, points, -1, false, 4000, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
        var other = new VisionContour(2, [new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0.5, 0.5)], -1, false, 3000, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

        var result = await new FitEllipseNode().ExecuteAsync(
            Context(new() { ["contours"] = VisionValue.Contours([other, contour]) }),
            Definition("measure.fitEllipse", """{"selectBy":"largest","maxRadialError":3}"""), CancellationToken.None);

        Assert.Equal(1L, result.Outputs["selectedIndex"].Require<long>("selectedIndex"));
        Assert.Equal(2L, result.Outputs["candidateCount"].Require<long>("candidateCount"));
        Assert.Equal("contour-area", result.Outputs["selectionMetric"].Require<string>("selectionMetric"));
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("radial-error-exceeded", result.Outputs["reason"].Require<string>("reason"));
        Assert.True(result.Outputs["maxRadialError"].Require<double>("maxRadialError") > 3);
        Assert.InRange(result.Outputs["semiMajor"].Require<double>("semiMajor"), 55, 75);
    }

    private static NodeExecutionContext Context(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static NodeDefinition Definition(string type, string json)
        => new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
}
