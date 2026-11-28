using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class MeasurementP0SafetyTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("p0", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    [Fact]
    public async Task CircleSpacing_ContainedDisk_HasNoPhysicalGap()
    {
        var result = await new CircleSpacingNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 10)),
            ["circleB"] = VisionValue.Circle(new VisionCircle(1, 0, 2))
        }), Def("measure.circleSpacing"), CancellationToken.None);

        Assert.Equal("contained", result.Outputs["relation"].Require<string>("relation"));
        Assert.False(result.Outputs["gapValid"].Require<bool>("gapValid"));
        Assert.True(double.IsNaN(result.Outputs["gap"].Require<double>("gap")));
        Assert.True(result.Outputs["disksIntersect"].Require<bool>("disksIntersect"));
        Assert.False(result.Outputs["boundariesIntersect"].Require<bool>("boundariesIntersect"));
    }

    [Fact]
    public async Task ContourDistance_UsesExactProjectionOntoSegment()
    {
        VisionContour Contour(params VisionPoint2D[] points)
            => new(0, points, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
        var result = await new ContourDistanceNode().ExecuteAsync(Ctx(new()
        {
            ["contours"] = VisionValue.Contours([Contour(new VisionPoint2D(5, 1), new VisionPoint2D(5, 2))]),
            ["referenceContours"] = VisionValue.Contours([Contour(new VisionPoint2D(0, 0), new VisionPoint2D(10, 0))])
        }), Def("measure.contourDistance", """{"sampleStep":100,"closed":false}"""), CancellationToken.None);

        Assert.Equal(1d, result.Outputs["minDistance"].Require<double>("minDistance"), 9);
        Assert.Equal(5d, result.Outputs["closestPointB"].Require<VisionPoint2D>("closestPointB").X, 9);
        Assert.Equal(50d, result.Outputs["minSamplingErrorBoundPx"].Require<double>("minSamplingErrorBoundPx"), 9);
    }

    [Fact]
    public async Task BestFitAlign_WithoutConvergence_IsNotAccepted()
    {
        var measured = Enumerable.Range(0, 20).Select(i => new VisionPoint2D(i * 5, 0)).ToArray();
        // 水平线无法用一次刚性 ICP 变成对角线；相同点集则本来就会收敛，不能用于验证拒绝未收敛结果。
        var reference = Enumerable.Range(0, 20).Select(i => new VisionPoint2D(i * 5, i * 5)).ToArray();
        var result = await new BestFitAlignNode().ExecuteAsync(Ctx(new()
        {
            ["measured"] = VisionValue.PointList(measured),
            ["reference"] = VisionValue.PointList(reference)
        }), Def("measure.bestFitAlign", """{"maxIterations":1,"tolerancePx":1e-12,"autoInitialRotation":false}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.False(result.Outputs["converged"].Require<bool>("converged"));
    }

    [Fact]
    public async Task Concentricity_DisabledTolerance_IsNotEvaluation()
    {
        var result = await new ConcentricityNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 5)),
            ["circleB"] = VisionValue.Circle(new VisionCircle(3, 4, 2))
        }), Def("measure.concentricity"), CancellationToken.None);

        Assert.Equal(5d, result.Outputs["centerDistance"].Require<double>("centerDistance"), 9);
        Assert.False(result.Outputs["toleranceEnabled"].Require<bool>("toleranceEnabled"));
        Assert.Equal("not-evaluated", result.Outputs["evaluationState"].Require<string>("evaluationState"));
    }

    [Fact]
    public async Task Curvature_WithoutLimits_IsNotEvaluated()
    {
        var points = Enumerable.Range(0, 40).Select(i =>
        {
            var angle = 2 * Math.PI * i / 40;
            return new VisionPoint2D(20 * Math.Cos(angle), 20 * Math.Sin(angle));
        }).ToArray();
        VisionContour contour = new(0, points, -1, false, 0, 0, 0, new(0, 0, 40, 40), new(0, 0, 20));
        var result = await new ContourCurvatureNode().ExecuteAsync(Ctx(new()
        {
            ["contours"] = VisionValue.Contours([contour])
        }), Def("measure.curvature", """{"fittingWindow":2,"closed":true}"""), CancellationToken.None);

        Assert.False(result.Outputs["toleranceEnabled"].Require<bool>("toleranceEnabled"));
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("not-evaluated", result.Outputs["evaluationState"].Require<string>("evaluationState"));
    }

    [Fact]
    public async Task WallThickness_AnalyticMinimumCanRejectGoodMean()
    {
        var result = await new WallThicknessNode().ExecuteAsync(Ctx(new()
        {
            ["inner"] = VisionValue.Circle(new VisionCircle(2, 0, 5)),
            ["outer"] = VisionValue.Circle(new VisionCircle(0, 0, 10))
        }), Def("measure.wallThickness", """{"sampleCount":64,"minThicknessLimit":4}"""), CancellationToken.None);

        Assert.Equal(3d, result.Outputs["minThickness"].Require<double>("minThickness"), 9);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
    }

    [Fact]
    public async Task Runout_DisabledToleranceAndFailedCoverage_NeverPass()
    {
        var samples = Enumerable.Range(0, 5).Select(i => new VisionPoint2D(10 * Math.Cos(i * Math.PI / 4), 10 * Math.Sin(i * Math.PI / 4))).ToArray();
        var context = Ctx(new()
        {
            ["samples"] = VisionValue.PointList(samples),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        });
        var coverage = await new RunoutToleranceNode().ExecuteAsync(context,
            Def("tolerance.runout", """{"minCoverageDeg":270}"""), CancellationToken.None);
        Assert.False(coverage.Outputs["found"].Require<bool>("found"));
        Assert.False(coverage.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        var full = Enumerable.Range(0, 72).Select(i => new VisionPoint2D(10 * Math.Cos(i * Math.PI / 36), 10 * Math.Sin(i * Math.PI / 36))).ToArray();
        var disabled = await new RunoutToleranceNode().ExecuteAsync(Ctx(new()
        {
            ["samples"] = VisionValue.PointList(full), ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }), Def("tolerance.runout", """{"tolerance":0}"""), CancellationToken.None);
        Assert.True(disabled.Outputs["found"].Require<bool>("found"));
        Assert.False(disabled.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("not-evaluated", disabled.Outputs["evaluationState"].Require<string>("evaluationState"));
    }
}
