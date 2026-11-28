using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// measurement 收尾批的契约验收：覆盖度门限、局部公差、极限圆收敛证据、
/// 圆心偏移的质量传递，以及色差阈值对区域语义的拆分。
/// </summary>
public sealed class MeasurementQualityBatchTests
{
    // ── measure.lineSpacing ─────────────────────────────────────────
    [Fact]
    public async Task LineSpacing_CoverageGate_RejectsSparseOverlap()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Line(new(new(0, 0), new(10, 0))),
            // B 只有 2px 长：11 条射线里只有 3 条真正打到它。
            ["b"] = VisionValue.Line(new(new(0, 5), new(2, 5)))
        };
        var result = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", """{"sampleCount":11,"minCoverageRatio":0.8}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("insufficient-coverage", result.Outputs["reason"].Require<string>("reason"));
        Assert.Equal(11L, result.Outputs["requestedSampleCount"].Require<long>("requestedSampleCount"));
        Assert.Equal(3L, result.Outputs["validSampleCount"].Require<long>("validSampleCount"));
        Assert.InRange(result.Outputs["coverageRatio"].Require<double>("coverageRatio"), 0.27, 0.28);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        using var doc = JsonDocument.Parse(result.Outputs["samplesJson"].Require<string>("samplesJson"));
        Assert.Equal(11, doc.RootElement.GetArrayLength());
        var skipped = doc.RootElement.EnumerateArray()
            .Count(item => item.GetProperty("SkipReason").GetString() == "outside-b-segment");
        Assert.Equal(8, skipped);
        Assert.All(doc.RootElement.EnumerateArray(), item => Assert.Equal(JsonValueKind.Number, item.GetProperty("Index").ValueKind));
    }

    [Fact]
    public async Task LineSpacing_AllSamplesMode_RejectsLocalSpike()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Line(new(new(0, 0), new(10, 0))),
            // B 微微倾斜：间距从 5.0 线性走到 5.3，均值恰好 5.15。
            ["b"] = VisionValue.Line(new(new(0, 5), new(10, 5.3)))
        };
        const string gate = """{"sampleCount":11,"nominalDistance":5.15,"tolerance":0.05""";
        var meanMode = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", gate + """}"""), CancellationToken.None);
        Assert.True(meanMode.Outputs["withinTolerance"].Require<bool>("withinTolerance"), "均值恰好命中名义值，mean 模式应通过");

        var allMode = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", gate + ""","toleranceMode":"allSamples"}"""), CancellationToken.None);
        Assert.False(allMode.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("sample-out-of-tolerance", allMode.Outputs["reason"].Require<string>("reason"));
        Assert.InRange(allMode.Outputs["maxAbsDeviation"].Require<double>("maxAbsDeviation"), 0.149, 0.151);
    }

    // ── measure.mzCircle ────────────────────────────────────────────
    [Fact]
    public async Task MzCircle_ReportsConvergenceEvidence_AndRefusesExhaustedSearch()
    {
        var points = CirclePoints(20, 1);
        var contours = VisionValue.Contours([Contour(points)]);

        var ok = await new MinimumZoneCircleNode().ExecuteAsync(Ctx(new() { ["contours"] = contours }),
            Def("measure.mzCircle", """{"mode":"minZone","maxZoneWidth":2.5}"""), CancellationToken.None);
        Assert.True(ok.Outputs["converged"].Require<bool>("converged"));
        Assert.Equal(5L, ok.Outputs["startCount"].Require<long>("startCount"));
        Assert.True(ok.Outputs["optimalityGap"].Require<double>("optimalityGap") < 0.01);
        Assert.True(ok.Outputs["found"].Require<bool>("found"));

        // 预算不足以把步长压到 precision：不得静默交出未优化的圆。
        var exhausted = await new MinimumZoneCircleNode().ExecuteAsync(Ctx(new() { ["contours"] = contours }),
            Def("measure.mzCircle", """{"mode":"minZone","precision":1e-6,"maxIterations":10}"""), CancellationToken.None);
        Assert.False(exhausted.Outputs["converged"].Require<bool>("converged"));
        Assert.False(exhausted.Outputs["found"].Require<bool>("found"));
        Assert.Equal("not-converged", exhausted.Outputs["reason"].Require<string>("reason"));
        Assert.True(exhausted.Outputs.ContainsKey("circle"));
    }

    [Fact]
    public async Task MzCircle_RadiusModesGateOnRadiusNotZoneWidth()
    {
        var contours = VisionValue.Contours([Contour(CirclePoints(20, 1))]);
        var result = await new MinimumZoneCircleNode().ExecuteAsync(Ctx(new() { ["contours"] = contours }),
            Def("measure.mzCircle", """{"mode":"minimumCircumscribed","nominalRadius":25,"radiusTolerance":1}"""), CancellationToken.None);

        Assert.Equal("radius", result.Outputs["gateMetric"].Require<string>("gateMetric"));
        Assert.InRange(result.Outputs["radius"].Require<double>("radius"), 20.5, 21.5);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("tolerance-exceeded", result.Outputs["reason"].Require<string>("reason"));

        var pass = await new MinimumZoneCircleNode().ExecuteAsync(Ctx(new() { ["contours"] = contours }),
            Def("measure.mzCircle", """{"mode":"minimumCircumscribed","nominalRadius":21,"radiusTolerance":0.5}"""), CancellationToken.None);
        Assert.Equal("radius", pass.Outputs["gateMetric"].Require<string>("gateMetric"));
        Assert.True(pass.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
    }

    // ── measure.concentricity ───────────────────────────────────────
    [Fact]
    public async Task Concentricity_PropagatesFitQuality_AndNamesOffendingCircle()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 10)),
            ["circleB"] = VisionValue.Circle(new VisionCircle(3, 4, 10))
        };
        var good = await new ConcentricityNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = inputs["circleA"],
            ["circleB"] = inputs["circleB"],
            ["qualityA"] = VisionValue.String("""{"fitError":0.02,"sampleCount":40,"coverageDeg":300}"""),
            ["qualityB"] = VisionValue.String("""{"rmsError":0.03,"inlierCount":36,"coverageDeg":280}""")
        }), Def("measure.concentricity", """{"maxCenterDistance":10,"requireQualityKnown":true,"maxFitError":0.05,"minCoverageDeg":200}"""), CancellationToken.None);
        Assert.True(good.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("known", good.Outputs["qualityStatusA"].Require<string>("qualityStatusA"));
        Assert.Equal(0.02, good.Outputs["fitErrorA"].Require<double>("fitErrorA"), 6);
        Assert.Equal(3d, good.Outputs["deltaX"].Require<double>("deltaX"), 9);
        Assert.Equal(4d, good.Outputs["deltaY"].Require<double>("deltaY"), 9);
        Assert.Equal(53.130102, good.Outputs["offsetAngleDeg"].Require<double>("offsetAngleDeg"), 3);

        var badFit = await new ConcentricityNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = inputs["circleA"],
            ["circleB"] = inputs["circleB"],
            ["qualityA"] = VisionValue.String("""{"fitError":0.02,"sampleCount":40,"coverageDeg":300}"""),
            ["qualityB"] = VisionValue.String("""{"fitError":0.4,"sampleCount":12,"coverageDeg":90}""")
        }), Def("measure.concentricity", """{"maxCenterDistance":10,"requireQualityKnown":true,"maxFitError":0.05}"""), CancellationToken.None);
        Assert.False(badFit.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("quality-insufficient:circleB", badFit.Outputs["reason"].Require<string>("reason"));
        Assert.Equal("quality-insufficient", badFit.Outputs["evaluationState"].Require<string>("evaluationState"));

        var missing = await new ConcentricityNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = inputs["circleA"],
            ["circleB"] = inputs["circleB"]
        }), Def("measure.concentricity", """{"maxCenterDistance":10,"requireQualityKnown":true}"""), CancellationToken.None);
        Assert.False(missing.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("quality-unknown:circleA", missing.Outputs["reason"].Require<string>("reason"));
        Assert.Equal("unknown", missing.Outputs["qualityStatusA"].Require<string>("qualityStatusA"));
    }

    [Fact]
    public async Task Concentricity_WithoutTolerance_DoesNotClaimQualityPass()
    {
        var result = await new ConcentricityNode().ExecuteAsync(Ctx(new()
        {
            ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 5)),
            ["circleB"] = VisionValue.Circle(new VisionCircle(3, 4, 5))
        }), Def("measure.concentricity"), CancellationToken.None);
        Assert.Equal("not-evaluated", result.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(5d, result.Outputs["centerDistance"].Require<double>("centerDistance"), 9);
    }

    // ── measure.colorDifference ─────────────────────────────────────
    [Fact]
    public async Task ColorDifference_ThresholdSeparatesDifferenceFromDefectArea()
    {
        using var image = VisionImage.Own(ColorPatch());
        var gated = await new ColorDifferenceNode().ExecuteAsync(Ctx(new() { ["image"] = VisionValue.Image(image.CloneOwned()) }),
            Def("measure.colorDifference", """{"referenceRed":0,"referenceGreen":0,"referenceBlue":0,"deltaEThreshold":50}"""), CancellationToken.None);
        using var _ = gated.Outputs["image"].Require<VisionImage>("image");

        var differenceArea = gated.Outputs["differenceArea"].Require<double>("differenceArea");
        var defectArea = gated.Outputs["defectArea"].Require<double>("defectArea");
        Assert.Equal(24 * 24, differenceArea, 0);
        Assert.InRange(defectArea, 16, 25);
        Assert.Equal(defectArea, gated.Outputs["area"].Require<double>("area"), 0);
        Assert.True(gated.Outputs["thresholdEnabled"].Require<bool>("thresholdEnabled"));
        Assert.False(gated.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("fail", gated.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.True(gated.Outputs["p50DeltaE"].Require<double>("p50DeltaE") < 15);
        Assert.True(gated.Outputs["p99DeltaE"].Require<double>("p99DeltaE") > 50);

        var ungated = await new ColorDifferenceNode().ExecuteAsync(Ctx(new() { ["image"] = VisionValue.Image(image.CloneOwned()) }),
            Def("measure.colorDifference", """{"referenceRed":0,"referenceGreen":0,"referenceBlue":0}"""), CancellationToken.None);
        using var __ = ungated.Outputs["image"].Require<VisionImage>("image");
        Assert.False(ungated.Outputs["thresholdEnabled"].Require<bool>("thresholdEnabled"));
        Assert.Equal("not-evaluated", ungated.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.Equal(ungated.Outputs["differenceArea"].Require<double>("differenceArea"), ungated.Outputs["area"].Require<double>("area"), 0);
    }

    // ── helpers ─────────────────────────────────────────────────────
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static NodeDefinition Def(string type, string? json = null) => new(type, type, null, null,
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static VisionContour Contour(IReadOnlyList<VisionPoint2D> points)
        => new(1, points, -1, true, 1200, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    private static List<VisionPoint2D> CirclePoints(double radius, double noise)
    {
        var points = new List<VisionPoint2D>();
        for (var i = 0; i < 120; i++)
        {
            var angle = i * Math.PI * 2 / 120;
            var r = radius + noise * Math.Sin(i * 0.7);
            points.Add(new(100 + r * Math.Cos(angle), 100 + r * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>24×24 的黑色底 + 4×4 白色块：白色块 ΔE 远超阈值，底色只有很小的非零差异。</summary>
    private static Mat ColorPatch()
    {
        var mat = new Mat(24, 24, MatType.CV_8UC3, new Scalar(10, 10, 10));
        using var block = new Mat(mat, new Rect(2, 2, 4, 4));
        block.SetTo(new Scalar(255, 255, 255));
        return mat;
    }
}
