using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral coverage for coordinate.point3D and roundness/straightness/wall-thickness measurement.</summary>
public sealed class BatchRaise70MeasureCoordTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
    private static VisionPoint2D[] CirclePoints(double r, int n = 48, double noise = 0)
        => Enumerable.Range(0, n).Select(i =>
        {
            var a = 360.0 * i / n * Math.PI / 180;
            var rr = r + (noise == 0 ? 0 : ((i % 2 == 0) ? noise : -noise));
            return new VisionPoint2D(50 + rr * Math.Cos(a), 50 + rr * Math.Sin(a));
        }).ToArray();

    [Fact]
    public async Task Point3D_FPorts_AzimuthElevation_AndRejectsHuge()
    {
        var result = await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.point3D", """{"x":3,"y":4,"z":12,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal(13d, result.Outputs["magnitude"].Require<double>("magnitude"), 9);
        Assert.Equal(5d, result.Outputs["xyMagnitude"].Require<double>("xyMagnitude"), 9);
        Assert.InRange(result.Outputs["azimuthDeg"].Require<double>("azimuthDeg"), 50, 60); // atan2(4,3)~53°
        Assert.InRange(result.Outputs["elevationDeg"].Require<double>("elevationDeg"), 65, 70); // atan2(12,5)~67°
        Assert.Contains("isOrigin", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Equal(false, result.Summary["isOrigin"]);
        Assert.Equal("Workpiece", result.Outputs["frame"].Require<string>("frame"));
        Assert.Equal("mm", result.Outputs["unit"].Require<string>("unit"));

        var origin = await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.point3D", """{"x":0,"y":0,"z":0}"""), CancellationToken.None);
        Assert.Equal(true, origin.Summary["isOrigin"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
                Def("coordinate.point3D", """{"x":1e12,"y":0,"z":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CoordinatePoint3DNode().ExecuteAsync(Ctx(new()),
                Def("coordinate.point3D", """{"frame":"   ","unit":"mm"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Roundness_FPorts_ToleranceAndCoverageGate()
    {
        var clean = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20))]) }),
            Def("measure.roundness", """{"maxDeviation":1,"minCoverageDeg":270}"""), CancellationToken.None);
        Assert.True(clean.Outputs["found"].Require<bool>("found"));
        Assert.True(clean.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(clean.Outputs["radius"].Require<double>("radius") > 15);
        Assert.True(clean.Outputs["coverageDeg"].Require<double>("coverageDeg") >= 270);
        Assert.True(clean.Outputs["outOfRoundPct"].Require<double>("outOfRoundPct") >= 0);
        Assert.True(clean.Outputs["relativeRms"].Require<double>("relativeRms") >= 0);
        Assert.Contains("peakOutward", clean.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(clean.Summary.ContainsKey("peakOutward"));
        Assert.True(clean.Summary.ContainsKey("peakInward"));

        var oval = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20, noise: 2))]) }),
            Def("measure.roundness", """{"maxDeviation":0.5}"""), CancellationToken.None);
        Assert.True(oval.Outputs["found"].Require<bool>("found"));
        Assert.False(oval.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(oval.Outputs["deviation"].Require<double>("deviation") > 0.5);

        var sparse = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20, n: 8))]) }),
            Def("measure.roundness", """{"minCoverageDeg":350}"""), CancellationToken.None);
        // 8 points over full circle still has high coverage; force fail with 3 points arc
        var arc = Enumerable.Range(0, 10).Select(i =>
        {
            var a = (i / 9.0) * 40 * Math.PI / 180; // 40° arc
            return new VisionPoint2D(50 + 20 * Math.Cos(a), 50 + 20 * Math.Sin(a));
        }).ToArray();
        var shortArc = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(arc)]) }),
            Def("measure.roundness", """{"minCoverageDeg":270}"""), CancellationToken.None);
        Assert.False(shortArc.Outputs["found"].Require<bool>("found"));
        Assert.Contains("insufficient-angular-coverage", shortArc.Outputs["diagnostics"].Require<string>("diagnostics"));
        _ = sparse; // keep compile if unused path
    }

    [Fact]
    public async Task Straightness_RelativeDeviation_AndTolerance()
    {
        var line = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(new(0, 0), new(10, 0), new(20, 0), new(30, 0), new(40, 0))]) }),
            Def("measure.straightness", """{"maxDeviation":0.5}"""), CancellationToken.None);
        Assert.True(line.Outputs["found"].Require<bool>("found"));
        Assert.True(line.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(line.Outputs["relativeDeviation"].Require<double>("relativeDeviation") < 0.01);
        Assert.True(Math.Abs(line.Outputs["angleDeg"].Require<double>("angleDeg")) < 1);
        Assert.Contains("peakPositive", line.Outputs["diagnostics"].Require<string>("diagnostics"));

        var bent = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(new(0, 0), new(10, 0), new(20, 3), new(30, 0), new(40, 0))]) }),
            Def("measure.straightness", """{"maxDeviation":0.5}"""), CancellationToken.None);
        Assert.True(bent.Outputs["found"].Require<bool>("found"));
        Assert.False(bent.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(bent.Outputs["deviation"].Require<double>("deviation") > 0.5);
        Assert.True(bent.Outputs["relativeDeviation"].Require<double>("relativeDeviation") > 0);

        var few = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(new(0, 0), new(1, 0))]) }),
            Def("measure.straightness"), CancellationToken.None);
        Assert.False(few.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task WallThickness_FPorts_ContainmentAndRadiusGate()
    {
        var ok = await new WallThicknessNode().ExecuteAsync(
            Ctx(new()
            {
                ["inner"] = VisionValue.Circle(new VisionCircle(50, 50, 20)),
                ["outer"] = VisionValue.Circle(new VisionCircle(50, 50, 30))
            }),
            Def("measure.wallThickness", """{"sampleCount":72,"nominalThickness":10,"tolerance":0.5}"""), CancellationToken.None);
        Assert.True(ok.Outputs["found"].Require<bool>("found"));
        Assert.True(ok.Outputs["contained"].Require<bool>("contained"));
        Assert.InRange(ok.Outputs["meanThickness"].Require<double>("meanThickness"), 9.5, 10.5);
        Assert.True(ok.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(ok.Outputs["uniformity"].Require<double>("uniformity") > 0.9);
        Assert.True(ok.Outputs["cv"].Require<double>("cv") >= 0);
        Assert.Contains("measure.wallThickness", ok.Outputs["diagnostics"].Require<string>("diagnostics"));

        var offset = await new WallThicknessNode().ExecuteAsync(
            Ctx(new()
            {
                ["inner"] = VisionValue.Circle(new VisionCircle(50, 50, 20)),
                ["outer"] = VisionValue.Circle(new VisionCircle(70, 50, 25)) // not contained
            }),
            Def("measure.wallThickness", """{"sampleCount":36}"""), CancellationToken.None);
        Assert.False(offset.Outputs["found"].Require<bool>("found"));
        Assert.False(offset.Outputs["contained"].Require<bool>("contained"));
        Assert.Contains("inner-disk-not-contained", offset.Outputs["diagnostics"].Require<string>("diagnostics"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new WallThicknessNode().ExecuteAsync(
                Ctx(new()
                {
                    ["inner"] = VisionValue.Circle(new VisionCircle(50, 50, 30)),
                    ["outer"] = VisionValue.Circle(new VisionCircle(50, 50, 20))
                }),
                Def("measure.wallThickness"), CancellationToken.None));
    }
}
