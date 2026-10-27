using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 测量与几何专项补全（node-gap-analysis.md 第八节）的行为覆盖。
///
/// 每个用例都用**可解析的合成几何**（直线 / 正弦 / 圆弧 / 已知半径圆环）构造输入，
/// 断言落在解析解附近而不是"只要不抛异常"——否则阈值调错也测不出来。
/// </summary>
public sealed class MeasurementGapNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    private static VisionPoint2D[] Straight(double length, int count = 2, double y = 0)
        => Enumerable.Range(0, count).Select(i => new VisionPoint2D(length * i / (count - 1), y)).ToArray();

    private static VisionPoint2D[] CirclePoints(double radius, double cx = 50, double cy = 50, int count = 96, double noise = 0)
        => Enumerable.Range(0, count).Select(i =>
        {
            var angle = 2 * Math.PI * i / count;
            var r = radius + (noise == 0 ? 0 : (i % 2 == 0 ? noise : -noise));
            return new VisionPoint2D(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
        }).ToArray();

    private static VisionPoint2D[] Arc(double radius, double startDeg, double sweepDeg, int count = 31)
        => Enumerable.Range(0, count).Select(i =>
        {
            var angle = (startDeg + sweepDeg * i / (count - 1)) * Math.PI / 180;
            return new VisionPoint2D(50 + radius * Math.Cos(angle), 50 + radius * Math.Sin(angle));
        }).ToArray();

    /// <summary>长波 + 短波叠加：λ=50 主波纹 + λ=2 细节，用于验证滤波把两者分开。</summary>
    private static VisionPoint2D[] MixedWave(double length, double step)
        => Enumerable.Range(0, (int)(length / step) + 1).Select(i =>
        {
            var x = i * step;
            return new VisionPoint2D(x, 5 * Math.Sin(2 * Math.PI * x / 50) + .5 * Math.Sin(2 * Math.PI * x / 2));
        }).ToArray();

    private static VisionPoint2D[] Sine(double amplitude, double wavelength, double length, double step)
        => Enumerable.Range(0, (int)(length / step) + 1)
            .Select(i => new VisionPoint2D(i * step, amplitude * Math.Sin(2 * Math.PI * i * step / wavelength))).ToArray();

    // ── 断链：轮廓 ⇄ 点集 ────────────────────────────────────────────
    [Fact]
    public async Task ToPoints_ArcLength_ResamplesEquidistantly_AndFeedsProfileNodes()
    {
        var result = await new ContourToPointsNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(Straight(100))]) }),
            Def("contour.toPoints", """{"resample":"arcLength","sampleStep":2,"closed":false}"""), CancellationToken.None);

        var points = result.Outputs["pointList"].Require<IReadOnlyList<VisionPoint2D>>("pointList");
        Assert.Equal(51, points.Count);
        Assert.Equal(100d, result.Outputs["length"].Require<double>("length"), 6);
        Assert.Equal(2d, result.Outputs["stepUsed"].Require<double>("stepUsed"), 6);
        Assert.Equal(2d, points[1].X - points[0].X, 6);

        // 点集必须能直接喂 GD&T 轮廓度节点，这正是断链修复要证明的事
        var profile = await new ProfileToleranceNode().ExecuteAsync(
            Ctx(new()
            {
                ["samples"] = VisionValue.PointList(points),
                ["nominalLine"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(100, 0)))
            }),
            Def("tolerance.profile"), CancellationToken.None);
        Assert.True(profile.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0d, profile.Outputs["profile"].Require<double>("profile"), 6);
    }

    [Fact]
    public async Task ToPoints_UniformAndEveryPoint_AndRejectsUnknownMode()
    {
        var contour = Contour(Straight(10, 11));
        var uniform = await new ContourToPointsNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([contour]) }),
            Def("contour.toPoints", """{"resample":"uniform","maxPoints":5,"closed":false}"""), CancellationToken.None);
        Assert.Equal(4L, uniform.Outputs["pointCount"].Require<long>("pointCount"));

        var every = await new ContourToPointsNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([contour]) }),
            Def("contour.toPoints", """{"resample":"everyPoint","closed":false}"""), CancellationToken.None);
        Assert.Equal(11L, every.Outputs["pointCount"].Require<long>("pointCount"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourToPointsNode().ExecuteAsync(
                Ctx(new() { ["contours"] = VisionValue.Contours([contour]) }),
                Def("contour.toPoints", """{"resample":"spiral"}"""), CancellationToken.None));

        var missing = await new ContourToPointsNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([contour]) }),
            Def("contour.toPoints", """{"index":3}"""), CancellationToken.None);
        Assert.False(missing.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task FromPoints_AcceptsPointListPort_WithoutJson()
    {
        var result = await new ContourFromPointsNode().ExecuteAsync(
            Ctx(new() { ["pointList"] = VisionValue.PointList([new(0, 0), new(10, 0), new(10, 10)]) }),
            Def("contour.fromPoints", """{"closed":false}"""), CancellationToken.None);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Equal(3, Assert.Single(contours).Points.Count);
        Assert.Equal(20d, Convert.ToDouble(result.Summary["perimeter"]), 1);
    }

    // ── 圆弧 ────────────────────────────────────────────────────────
    [Fact]
    public async Task ArcFit_QuarterArc_ReportsSweepAndArcLength()
    {
        var result = await new ArcFitNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(Arc(20, 0, 90))]) }),
            Def("measure.arcFit", """{"minCoverageDeg":20}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(20d, result.Outputs["radius"].Require<double>("radius"), 2);
        Assert.Equal(90d, result.Outputs["sweepDeg"].Require<double>("sweepDeg"), 1);
        Assert.Equal(31.4159, result.Outputs["arcLength"].Require<double>("arcLength"), 1);

        var gated = await new ArcFitNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(Arc(20, 0, 15))]) }),
            Def("measure.arcFit", """{"minCoverageDeg":60}"""), CancellationToken.None);
        Assert.False(gated.Outputs["found"].Require<bool>("found"));
        Assert.Contains("insufficient-arc-coverage", gated.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    // ── 极限圆 ──────────────────────────────────────────────────────
    [Fact]
    public async Task MzCircle_ZoneAndExtremalModes()
    {
        var points = CirclePoints(20, noise: 1);
        var mz = await new MinimumZoneCircleNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(points)]) }),
            Def("measure.mzCircle", """{"mode":"minZone","maxZoneWidth":2.5}"""), CancellationToken.None);
        Assert.True(mz.Outputs["found"].Require<bool>("found"));
        Assert.Equal(2d, mz.Outputs["zoneWidth"].Require<double>("zoneWidth"), .05);
        Assert.Equal(20d, mz.Outputs["radius"].Require<double>("radius"), .1);
        Assert.True(mz.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        var mcci = await new MinimumZoneCircleNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(points)]) }),
            Def("measure.mzCircle", """{"mode":"minimumCircumscribed"}"""), CancellationToken.None);
        Assert.Equal(21d, mcci.Outputs["radius"].Require<double>("radius"), .15);

        var mic = await new MinimumZoneCircleNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(points)]) }),
            Def("measure.mzCircle", """{"mode":"maximumInscribed","maxZoneWidth":1}"""), CancellationToken.None);
        Assert.Equal(19d, mic.Outputs["radius"].Require<double>("radius"), .15);
        Assert.False(mic.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
    }

    // ── 曲率 / 圆角 ─────────────────────────────────────────────────
    [Fact]
    public async Task Curvature_FindsFilletRadius_OnStraightArcStraightContour()
    {
        var points = new List<VisionPoint2D>();
        for (var x = 0.0; x <= 40; x += 1) points.Add(new(x, 0));
        for (var a = -90.0; a <= 0.001; a += 3) points.Add(new(40 + 5 * Math.Cos(a * Math.PI / 180), 5 + 5 * Math.Sin(a * Math.PI / 180)));
        for (var y = 6.0; y <= 40; y += 1) points.Add(new(45, y));

        var result = await new ContourCurvatureNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(points.ToArray())]) }),
            Def("measure.curvature", """{"fittingWindow":4,"cornerRadius":8,"closed":false}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.InRange(result.Outputs["minRadius"].Require<double>("minRadius"), 4.5, 6.5);
        Assert.True(result.Outputs["cornerCount"].Require<long>("cornerCount") >= 1);
        Assert.Contains("radius", result.Outputs["cornerJson"].Require<string>("cornerJson"));

        // 纯直线：局部圆拟合退化，节点必须报 found=false 而不是编一个半径
        var straight = await new ContourCurvatureNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(Straight(80, 40))]) }),
            Def("measure.curvature", """{"fittingWindow":4,"cornerRadius":8,"closed":false}"""), CancellationToken.None);
        Assert.False(straight.Outputs["found"].Require<bool>("found"));
    }

    // ── 滤波 / 粗糙度 ───────────────────────────────────────────────
    [Fact]
    public async Task ProfileFilter_SplitsWavinessFromResidual()
    {
        var result = await new ProfileFilterNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(MixedWave(100, .5))]) }),
            Def("measure.profileFilter", """{"cutoffWavelength":8,"sampleStep":0.5,"endEffect":"reflect"}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        var residual = result.Outputs["residual"].Require<IReadOnlyList<VisionPoint2D>>("residual");
        var waviness = result.Outputs["waviness"].Require<IReadOnlyList<VisionPoint2D>>("waviness");
        Assert.Equal(201, residual.Count);
        Assert.Equal(201, waviness.Count);
        // 幅值看标量端口：PointList 是像素坐标（主轴带 ~3° 倾角），直接量 Y 会混进线性趋势
        Assert.True(result.Outputs["wavinessPeak"].Require<double>("wavinessPeak") > 4, "waviness must keep the λ=50 component");
        Assert.True(result.Outputs["residualRms"].Require<double>("residualRms") > .2);

        // 端部效应：reflect 保留全长但两端被反射污染，discard 丢掉两端后残差幅值才干净
        var discard = await new ProfileFilterNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(MixedWave(100, .5))]) }),
            Def("measure.profileFilter", """{"cutoffWavelength":8,"sampleStep":0.5,"endEffect":"discard"}"""), CancellationToken.None);
        Assert.True(discard.Outputs["removedPointCount"].Require<long>("removedPointCount") > 0);
        Assert.InRange(discard.Outputs["residualPeak"].Require<double>("residualPeak"), .35, .75);  // 只剩 λ=2、幅值 0.5
    }

    [Fact]
    public async Task Roughness_SineAmplitude_MatchesAnalyticRa()
    {
        // 正弦 z=A·sin 的 Ra = 2A/π ≈ 0.6366A：振幅 2 → Ra≈1.273，Rmax≈4
        var result = await new ProfileRoughnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(Sine(2, 2, 40, .1))]) }),
            Def("measure.roughness", """{"cutoffWavelength":10,"sampleStep":0.1,"maxRa":1.5}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.InRange(result.Outputs["ra"].Require<double>("ra"), 1.1, 1.45);
        Assert.InRange(result.Outputs["rmax"].Require<double>("rmax"), 3.5, 4.5);
        Assert.InRange(result.Outputs["rz"].Require<double>("rz"), 3.0, 4.5);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(result.Outputs["zeroCrossings"].Require<long>("zeroCrossings") > 10);
    }

    // ── 轮廓间距 ────────────────────────────────────────────────────
    [Fact]
    public async Task ContourDistance_ParallelLines_ReportGap()
    {
        var result = await new ContourDistanceNode().ExecuteAsync(
            Ctx(new()
            {
                ["contours"] = VisionValue.Contours([Contour(Straight(100))]),
                ["referenceContours"] = VisionValue.Contours([Contour(Straight(100, 2, 10))])
            }),
            Def("measure.contourDistance", """{"mode":"gap","sampleStep":2,"maxSamples":200,"limit":12}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(10d, result.Outputs["distance"].Require<double>("distance"), .01);
        Assert.Equal(10d, result.Outputs["minDistance"].Require<double>("minDistance"), .01);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(10d, result.Outputs["closestPointB"].Require<VisionPoint2D>("closestPointB").Y, .01);
    }

    // ── 螺纹 / 齿轮 ─────────────────────────────────────────────────
    [Fact]
    public async Task Thread_SyntheticTriangle_RecoversPitchAndDiameters()
    {
        var points = new List<VisionPoint2D>();
        for (var i = 0; i <= 60; i++)
        {
            var phase = i % 10;                       // 齿距 10
            var z = phase <= 5 ? 5 - 0.3 * phase : 3.5 + 0.3 * (phase - 5);
            points.Add(new VisionPoint2D(i, z));
        }
        var result = await new ThreadMeasurementNode().ExecuteAsync(
            Ctx(new()
            {
                ["contours"] = VisionValue.Contours([Contour(points.ToArray())]),
                ["axis"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(60, 0)))
            }),
            Def("measure.thread", """{"polarity":"profile","minCrests":3,"sampleStep":0.5}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.InRange(result.Outputs["pitch"].Require<double>("pitch"), 9.5, 10.5);
        Assert.True(result.Outputs["crestCount"].Require<long>("crestCount") >= 5);
        Assert.Equal(10d, result.Outputs["majorDiameter"].Require<double>("majorDiameter"), .2);
        Assert.Equal(7d, result.Outputs["minorDiameter"].Require<double>("minorDiameter"), .4);
        Assert.InRange(result.Outputs["threadAngleDeg"].Require<double>("threadAngleDeg"), 20, 45);
    }

    [Fact]
    public async Task Gear_ClosedFormValues()
    {
        var result = await new SpurGearNode().ExecuteAsync(Ctx(new()),
            Def("measure.gear", """{"module":2,"teeth":20,"pressureAngleDeg":20}"""), CancellationToken.None);

        Assert.Equal(40d, result.Outputs["pitchDiameter"].Require<double>("pitchDiameter"), 6);
        Assert.Equal(40 * Math.Cos(20 * Math.PI / 180), result.Outputs["baseDiameter"].Require<double>("baseDiameter"), 4);
        Assert.Equal(44d, result.Outputs["outsideDiameter"].Require<double>("outsideDiameter"), 6);
        Assert.Equal(35d, result.Outputs["rootDiameter"].Require<double>("rootDiameter"), 6);
        Assert.Equal(3L, result.Outputs["spanTeeth"].Require<long>("spanTeeth"));
        Assert.Equal(3.456, result.Outputs["pinDiameter"].Require<double>("pinDiameter"), 3);
        Assert.True(result.Outputs["measurementOverPins"].Require<double>("measurementOverPins") > 43);
        Assert.True(result.Outputs["spanLength"].Require<double>("spanLength") > 0);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SpurGearNode().ExecuteAsync(Ctx(new()), Def("measure.gear", """{"teeth":1}"""), CancellationToken.None));
    }

    // ── 公差叠加 / 基准系 ───────────────────────────────────────────
    private const string Chain = """
        [{"name":"pin","nominal":10,"plus":0.02,"minus":0.02,"sign":1},
         {"name":"hole","nominal":10.05,"plus":0.03,"minus":0,"sign":-1}]
        """;

    private static string StackupJson(string method = "worstCase")
        => JsonSerializer.Serialize(new { contributionsJson = Chain, method, targetNominal = -0.05, tolerance = 0.1, unit = "mm" });

    [Fact]
    public async Task Stackup_WorstCaseAndRss()
    {
        var worst = await new ToleranceStackupNode().ExecuteAsync(Ctx(new()), Def("tolerance.stackup", StackupJson()), CancellationToken.None);
        Assert.Equal(-0.05, worst.Outputs["nominal"].Require<double>("nominal"), 6);
        Assert.Equal(-0.03, worst.Outputs["worstCaseMax"].Require<double>("worstCaseMax"), 6);
        Assert.Equal(-0.10, worst.Outputs["worstCaseMin"].Require<double>("worstCaseMin"), 6);
        Assert.True(worst.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Contains("pin", worst.Outputs["contributionsJson"].Require<string>("contributionsJson"));

        var rss = await new ToleranceStackupNode().ExecuteAsync(Ctx(new()), Def("tolerance.stackup", StackupJson("rss")), CancellationToken.None);
        var sigma = Math.Sqrt(Math.Pow(0.04 / 6, 2) + Math.Pow(0.03 / 6, 2));
        Assert.Equal(6 * sigma, rss.Outputs["rssTolerance"].Require<double>("rssTolerance"), 4);
        Assert.Equal(-0.05 + 3 * sigma, rss.Outputs["upperLimit"].Require<double>("upperLimit"), 4);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ToleranceStackupNode().ExecuteAsync(Ctx(new()), Def("tolerance.stackup", """{"contributionsJson":"[]"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Stackup_PortOverride_ReplacesFirstNominal()
    {
        var json = JsonSerializer.Serialize(new
        {
            contributionsJson = """[{"name":"pin","nominal":10,"plus":0.02,"minus":0.02,"sign":1}]"""
        });
        var result = await new ToleranceStackupNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Double(11) }), Def("tolerance.stackup", json), CancellationToken.None);
        Assert.Equal(11d, result.Outputs["nominal"].Require<double>("nominal"), 6);
    }

    [Fact]
    public async Task Drf_IntersectionOrigin_AndOrthogonalityGate()
    {
        var result = await new DatumReferenceFrameNode().ExecuteAsync(
            Ctx(new()
            {
                ["primary"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 0))),
                ["secondary"] = VisionValue.Line(new VisionLine2D(new(5, -5), new(5, 5)))
            }),
            Def("tolerance.drf", """{"maxOrthogonalityErrorDeg":0.1}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(5d, result.Outputs["origin"].Require<VisionPoint2D>("origin").X, 6);
        Assert.Equal(0d, result.Outputs["origin"].Require<VisionPoint2D>("origin").Y, 6);
        Assert.Equal(0d, result.Outputs["orthogonalityErrorDeg"].Require<double>("orthogonalityErrorDeg"), 3);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        var parallel = await new DatumReferenceFrameNode().ExecuteAsync(
            Ctx(new()
            {
                ["primary"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 0))),
                ["secondary"] = VisionValue.Line(new VisionLine2D(new(0, 5), new(10, 5)))
            }),
            Def("tolerance.drf"), CancellationToken.None);
        Assert.False(parallel.Outputs["found"].Require<bool>("found"));
        Assert.Contains("parallel-datums", parallel.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    // ── 线构造 ──────────────────────────────────────────────────────
    [Fact]
    public async Task ConstructLine_PerpendicularParallelMidlineBisector()
    {
        var baseLine = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 0)));

        var perpendicular = await new ConstructLineNode().ExecuteAsync(
            Ctx(new() { ["line"] = baseLine, ["point"] = VisionValue.Point(new(5, 5)) }),
            Def("geometry.lineConstruct", """{"operation":"perpendicular"}"""), CancellationToken.None);
        var p = perpendicular.Outputs["line"].Require<VisionLine2D>("line");
        Assert.Equal(5d, p.Start.X, 6);
        Assert.Equal(0d, p.Start.Y, 6);
        Assert.Equal(10d, p.Length, 6);

        var parallel = await new ConstructLineNode().ExecuteAsync(
            Ctx(new() { ["line"] = baseLine }),
            Def("geometry.lineConstruct", """{"operation":"parallel","offset":3}"""), CancellationToken.None);
        Assert.Equal(3d, parallel.Outputs["line"].Require<VisionLine2D>("line").Start.Y, 6);
        Assert.Equal(3d, parallel.Outputs["offset"].Require<double>("offset"), 6);

        var midline = await new ConstructLineNode().ExecuteAsync(
            Ctx(new()
            {
                ["line"] = baseLine,
                ["line2"] = VisionValue.Line(new VisionLine2D(new(0, 4), new(10, 4)))
            }),
            Def("geometry.lineConstruct", """{"operation":"midline"}"""), CancellationToken.None);
        Assert.Equal(2d, midline.Outputs["line"].Require<VisionLine2D>("line").Start.Y, 6);

        var bisector = await new ConstructLineNode().ExecuteAsync(
            Ctx(new()
            {
                ["line"] = baseLine,
                ["line2"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(0, 10)))
            }),
            Def("geometry.lineConstruct", """{"operation":"bisector"}"""), CancellationToken.None);
        Assert.Equal(45d, bisector.Outputs["angleDeg"].Require<double>("angleDeg"), 1);
    }

    [Fact]
    public async Task ConstructLine_TangentFromPoint_EmitsTwoLines()
    {
        var result = await new ConstructLineNode().ExecuteAsync(
            Ctx(new()
            {
                ["circle"] = VisionValue.Circle(new VisionCircle(0, 0, 5)),
                ["point"] = VisionValue.Point(new(10, 0))
            }),
            Def("geometry.lineConstruct", """{"operation":"tangent"}"""), CancellationToken.None);

        var first = result.Outputs["line"].Require<VisionLine2D>("line");
        var second = result.Outputs["line2"].Require<VisionLine2D>("line2");
        Assert.Equal(Math.Sqrt(75), first.Length, 6);
        Assert.Equal(Math.Sqrt(75), second.Length, 6);
        Assert.Equal(5d, Math.Sqrt(first.End.X * first.End.X + first.End.Y * first.End.Y), 6);
        Assert.NotEqual(first.End.Y, second.End.Y);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ConstructLineNode().ExecuteAsync(
                Ctx(new() { ["circle"] = VisionValue.Circle(new VisionCircle(0, 0, 5)), ["point"] = VisionValue.Point(new(1, 0)) }),
                Def("geometry.lineConstruct", """{"operation":"tangent"}"""), CancellationToken.None));
    }

    // ── 非刚体变换 ──────────────────────────────────────────────────
    [Fact]
    public async Task AffineTransform_AppliesScaleAndTranslation()
    {
        var result = await new AffineTransformNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.transformAffine", """{"scaleX":2,"scaleY":3,"translateX":10,"translateY":20,"sourceFrame":"Image","targetFrame":"World"}"""),
            CancellationToken.None);

        var transform = result.Outputs["transform"].Require<VisionTransform2D>("transform");
        var mapped = transform.Apply(new VisionPoint2D(1, 1));
        Assert.Equal(12d, mapped.X, 6);
        Assert.Equal(23d, mapped.Y, 6);
        Assert.Equal(6d, result.Outputs["determinant"].Require<double>("determinant"), 6);

        var rotated = await new AffineTransformNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.transformAffine", """{"rotationDeg":90}"""), CancellationToken.None);
        var q = rotated.Outputs["transform"].Require<VisionTransform2D>("transform").Apply(new VisionPoint2D(1, 0));
        Assert.Equal(0d, q.X, 6);
        Assert.Equal(1d, q.Y, 6);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new AffineTransformNode().ExecuteAsync(Ctx(new()), Def("coordinate.transformAffine", """{"scaleX":0}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ShearedTransform_KeepsDeterminantAtScaleProduct()
    {
        var result = await new AffineTransformNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.transformAffine", """{"scaleX":2,"scaleY":3,"shear":0.5}"""), CancellationToken.None);
        Assert.Equal(6d, result.Outputs["determinant"].Require<double>("determinant"), 6);
        var mapped = result.Outputs["transform"].Require<VisionTransform2D>("transform").Apply(new VisionPoint2D(0, 1));
        Assert.Equal(1.5, mapped.X, 6);
        Assert.Equal(3d, mapped.Y, 6);
    }
}
