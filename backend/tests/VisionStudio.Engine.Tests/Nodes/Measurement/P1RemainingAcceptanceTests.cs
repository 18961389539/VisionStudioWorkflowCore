using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// docs/node-improvements/measurement 中剩余 P1 建议的独立回放验收。
/// 每条断言都落在新增的机器可读端口/原因码上，避免“没有抛异常就算通过”。
/// </summary>
public sealed class P1RemainingAcceptanceTests
{
    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor,
        Dictionary<string, VisionValue> inputs, object? parameters = null)
        => await executor.ExecuteAsync(new NodeExecutionContext(inputs),
            new("test", executor.Type, null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { }))),
            CancellationToken.None);

    private static VisionContour Contour(params VisionPoint2D[] points)
        => new(0, points, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));

    private static VisionPoint2D[] Arc(double radius, double startDeg, double sweepDeg, int count, double cx = 0, double cy = 0)
        => Enumerable.Range(0, count).Select(i =>
        {
            var angle = (startDeg + sweepDeg * i / (double)(count - 1)) * Math.PI / 180;
            return new VisionPoint2D(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }).ToArray();

    [Fact]
    public async Task Parallelism_SeparatesOrientationDriftFromGdTZoneAndSupportsZeroTolerance()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 0))),
            ["b"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 1)))
        };
        var zero = await Run(new ParallelismNode(), inputs,
            new { mode = "parallel", maxAngleErrorDeg = 0, toleranceEnabled = true });
        Assert.False(zero.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("endpointSpread", zero.Outputs["zoneWidthBasis"].Require<string>("zoneWidthBasis"));
        Assert.Equal(Math.Sqrt(101), zero.Outputs["evaluationLength"].Require<double>("evaluationLength"), 6);
        Assert.Equal(zero.Outputs["deviation"].Require<double>("deviation"),
            zero.Outputs["orientationDeviation"].Require<double>("orientationDeviation"), 9);

        var off = await Run(new ParallelismNode(), inputs, new { mode = "parallel", maxAngleErrorDeg = 0 });
        Assert.True(off.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.False(off.Outputs["toleranceEnabled"].Require<bool>("toleranceEnabled"));

        // 只改变评价长度，方向漂移与 zoneWidth 同步放大——正是 P1 要求显式声明的依赖。
        var longer = await Run(new ParallelismNode(), new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(10, 0))),
            ["b"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(20, 2)))
        }, new { mode = "parallel" });
        Assert.Equal(Math.Sqrt(404), longer.Outputs["evaluationLength"].Require<double>("evaluationLength"), 6);
        Assert.Equal(longer.Outputs["deviation"].Require<double>("deviation"),
            longer.Outputs["zoneWidth"].Require<double>("zoneWidth"), 9);
    }

    [Fact]
    public async Task Position_RejectsDiametralBonusOnRectangularZoneAndUndeclaredDisabledGate()
    {
        var inputs = new Dictionary<string, VisionValue> { ["point"] = VisionValue.Point(new VisionPoint2D(0, 0)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new PositionToleranceNode(), inputs,
            new { zoneType = "rectangular", mmcBonus = true, mmcSize = 5, toleranceX = 0.1, toleranceY = 0.1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new PositionToleranceNode(), inputs,
            new { zoneType = "rectangular", toleranceX = 0, toleranceY = 0 }));

        var measuredOnly = await Run(new PositionToleranceNode(), inputs,
            new { zoneType = "rectangular", toleranceX = 0, toleranceY = 0, measurementOnly = true });
        Assert.False(measuredOnly.Outputs["gateEnabled"].Require<bool>("gateEnabled"));
        Assert.Equal("none", measuredOnly.Outputs["activeAxes"].Require<string>("activeAxes"));
        Assert.False(measuredOnly.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("identity", measuredOnly.Outputs["datumSource"].Require<string>("datumSource"));
        Assert.False(measuredOnly.Outputs["datumCalibrated"].Require<bool>("datumCalibrated"));
    }

    [Fact]
    public async Task Drf_UnreliableIntersectionIsVisibleAsDatumValidFalse()
    {
        var result = await Run(new DatumReferenceFrameNode(), new Dictionary<string, VisionValue>
        {
            ["primary"] = VisionValue.Line(new VisionLine2D(new(0, 0), new(100, 1))),
            ["secondary"] = VisionValue.Line(new VisionLine2D(new(0, 10), new(100, 10.5)))
        }, new { maxConditionNumber = 1.001, maxIntersectionDistance = 0.5 });

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["conditionNumber"].Require<double>("conditionNumber") > 1);
        Assert.True(result.Outputs["intersectionDistanceToSegments"].Require<double>("intersectionDistanceToSegments") > 0);
        Assert.True(result.Outputs["originUnreliable"].Require<bool>("originUnreliable"));
        Assert.False(result.Outputs["datumValid"].Require<bool>("datumValid"));
    }

    [Fact]
    public async Task Runout_DeclaresPlanarProjectionBoundary()
    {
        var samples = Arc(20, 0, 350, 36);
        var result = await Run(new RunoutToleranceNode(), new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }, new { tolerance = 2 });
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("planar-projection", result.Outputs["measurementKind"].Require<string>("measurementKind"));
        Assert.Equal("XY", result.Outputs["projectionPlane"].Require<string>("projectionPlane"));
        Assert.False(result.Outputs["totalRunout3DSupported"].Require<bool>("totalRunout3DSupported"));
        Assert.Equal(0d, result.Outputs["datumAxisAngleDeg"].Require<double>("datumAxisAngleDeg"), 6);
    }

    [Fact]
    public async Task ArcFit_ShortArcObservabilityRejectsStableLookingRadius()
    {
        var shortArc = await Run(new ArcFitNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(Arc(100, 0, 6, 8))])
        }, new { minCoverageDeg = 0, maxConditionNumber = 100 });
        Assert.False(shortArc.Outputs["found"].Require<bool>("found"),
            shortArc.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("arc-observability-weak", shortArc.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(shortArc.Outputs["conditionNumber"].Require<double>("conditionNumber") > 100);
        Assert.True(shortArc.Outputs["radiusUncertainty"].Require<double>("radiusUncertainty") > 0);

        var full = await Run(new ArcFitNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(Arc(100, 0, 350, 60))])
        }, new { minCoverageDeg = 270, maxConditionNumber = 100 });
        Assert.True(full.Outputs["found"].Require<bool>("found"), full.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(full.Outputs["observabilityOk"].Require<bool>("observabilityOk"));
    }

    [Fact]
    public async Task Roundness_UnevenSamplingAndRobustDisagreementAreReported()
    {
        var clustered = await Run(new RoundnessNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(Arc(20, 0, 40, 40))])
        }, new { minCoverageDeg = 0, minPointsPerSector = 1 });
        Assert.False(clustered.Outputs["found"].Require<bool>("found"));
        Assert.Contains("uneven-angular-sampling", clustered.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(clustered.Outputs.ContainsKey("maxGapDeg"));

        var points = Arc(20, 0, 360, 60).ToList();
        points[0] = new VisionPoint2D(points[0].X * 2, points[0].Y * 2);
        var burr = await Run(new RoundnessNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(points.ToArray())])
        }, new { maxRobustShift = 0.01 });
        Assert.True(burr.Outputs["found"].Require<bool>("found"));
        Assert.False(burr.Outputs["robustAgreement"].Require<bool>("robustAgreement"));
        Assert.False(burr.Outputs["resultReleasable"].Require<bool>("resultReleasable"));
        Assert.True(burr.Outputs["robustCenterShift"].Require<double>("robustCenterShift") > 0);
    }

    [Fact]
    public async Task Straightness_DistinguishesMinimumZoneAndSuspectedDoubleEdge()
    {
        var zigzag = new[]
        {
            new VisionPoint2D(0, 1), new VisionPoint2D(1, -1), new VisionPoint2D(2, 1),
            new VisionPoint2D(3, -1), new VisionPoint2D(4, 1), new VisionPoint2D(5, -1), new VisionPoint2D(6, 1)
        };
        var inputs = new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours([Contour(zigzag)]) };
        var ls = await Run(new StraightnessNode(), inputs);
        Assert.True(ls.Outputs["found"].Require<bool>("found"));
        Assert.Equal("leastSquares", ls.Outputs["fitMethod"].Require<string>("fitMethod"));
        var zone = await Run(new StraightnessNode(), inputs, new { evaluationMode = "minimumZone" });
        Assert.Equal("minimumZone", zone.Outputs["fitMethod"].Require<string>("fitMethod"));
        Assert.True(zone.Outputs["minimumZoneDeviation"].Require<double>("minimumZoneDeviation")
                    <= ls.Outputs["deviation"].Require<double>("deviation") + 1e-9);

        // 开放的双边路径：先沿上边、再沿下边，保留两组相反侧残差但避免闭合零长度拟合。
        var band = Contour(
            new VisionPoint2D(0, 0), new VisionPoint2D(2, 0), new VisionPoint2D(4, 0),
            new VisionPoint2D(6, 0), new VisionPoint2D(8, 0), new VisionPoint2D(10, 0),
            new VisionPoint2D(11, 4), new VisionPoint2D(9, 4), new VisionPoint2D(7, 4),
            new VisionPoint2D(5, 4), new VisionPoint2D(3, 4), new VisionPoint2D(1, 4));
        var bandInputs = new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours([band]) };
        var rejected = await Run(new StraightnessNode(), bandInputs);
        Assert.False(rejected.Outputs["found"].Require<bool>("found"));
        Assert.Contains("suspected-closed-or-double-edge", rejected.Outputs["diagnostics"].Require<string>("diagnostics"));
        var declared = await Run(new StraightnessNode(), bandInputs, new { singleEdgeDeclared = true });
        Assert.True(declared.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task ContourDistance_ExposesBothDirectionsForAsymmetricContours()
    {
        var result = await Run(new ContourDistanceNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(new(5, 0), new(5, 5))]),
            ["referenceContours"] = VisionValue.Contours([Contour(new(0, 0), new(10, 0), new(10, 10))])
        }, new { closed = false, sampleStep = 1, direction = "symmetric", mode = "deviation" });

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("symmetric", result.Outputs["direction"].Require<string>("direction"));
        var forward = result.Outputs["directedAToB"].Require<double>("directedAToB");
        var backward = result.Outputs["directedBToA"].Require<double>("directedBToA");
        Assert.True(forward > 0 && backward > 0);
        Assert.Equal(Math.Max(forward, backward), result.Outputs["symmetricDistance"].Require<double>("symmetricDistance"), 9);
        Assert.True(result.Outputs.ContainsKey("symmetricGap"));
    }

    [Fact]
    public async Task Curvature_ArcLengthWindowModeReportsPhysicalSpan()
    {
        var points = Arc(50, 0, 180, 121);
        var result = await Run(new ContourCurvatureNode(), new Dictionary<string, VisionValue>
        {
            ["contours"] = VisionValue.Contours([Contour(points)])
        }, new { closed = false, fittingWindow = 5, windowLength = 30 });

        Assert.True(result.Outputs["found"].Require<bool>("found"), result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Equal("arcLength", result.Outputs["windowMode"].Require<string>("windowMode"));
        Assert.Equal(30d, result.Outputs["windowLength"].Require<double>("windowLength"), 6);
        Assert.InRange(result.Outputs["actualWindowLength"].Require<double>("actualWindowLength"), 25, 32);
        Assert.InRange(result.Outputs["windowLengthCv"].Require<double>("windowLengthCv"), -1, 0.05);
        Assert.True(result.Outputs["windowUniformityOk"].Require<bool>("windowUniformityOk"));
    }

    [Fact]
    public async Task Gear_UsesOneUnitContractAndRejectsImpossibleCombinations()
    {
        var valid = await Run(new SpurGearNode(), new(), new { module = 2, teeth = 20, unit = "mm" });
        Assert.True(valid.Outputs["found"].Require<bool>("found"));
        Assert.Equal("mm", valid.Outputs["unit"].Require<string>("unit"));
        Assert.True(valid.Outputs["dimensionalUseAllowed"].Require<bool>("dimensionalUseAllowed"));
        Assert.Equal("auto", valid.Outputs["pinDiameterSource"].Require<string>("pinDiameterSource"));

        var impossible = await Run(new SpurGearNode(), new(), new { module = 1, teeth = 3, profileShift = 2 });
        Assert.False(impossible.Outputs["found"].Require<bool>("found"));
        Assert.Contains("invalid-gear-combination", impossible.Outputs["diagnostics"].Require<string>("diagnostics"));

        var pixels = await Run(new SpurGearNode(), new(), new { module = 1, teeth = 20, unit = "px" });
        Assert.False(pixels.Outputs["dimensionalUseAllowed"].Require<bool>("dimensionalUseAllowed"));
    }

    [Fact]
    public async Task WallThickness_ReportsAnalyticExtremaAndSamplingApproximation()
    {
        var result = await Run(new WallThicknessNode(), new Dictionary<string, VisionValue>
        {
            ["inner"] = VisionValue.Circle(new VisionCircle(0, 0, 5)),
            ["outer"] = VisionValue.Circle(new VisionCircle(2, 0, 10))
        }, new { sampleCount = 8, startAngleDeg = 0 });

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("analytic-closed-form", result.Outputs["minMaxSource"].Require<string>("minMaxSource"));
        Assert.Equal("midpoint-uniform-angle", result.Outputs["integrationMethod"].Require<string>("integrationMethod"));
        Assert.Equal(45d, result.Outputs["angularStepDeg"].Require<double>("angularStepDeg"), 6);
        Assert.True(result.Outputs["meanIsApproximate"].Require<bool>("meanIsApproximate"));
        Assert.True(result.Outputs["meanApproximationError"].Require<double>("meanApproximationError") >= 0);
        Assert.Equal(3d, result.Outputs["minThickness"].Require<double>("minThickness"), 6);
        Assert.Equal(7d, result.Outputs["maxThickness"].Require<double>("maxThickness"), 6);
    }

    [Fact]
    public async Task BestFitAlign_SimilarityModeGatesScaleIndependentlyOfResidual()
    {
        var measured = Arc(20, 0, 270, 80, 100, 100).Select(p => new VisionPoint2D(p.X, p.Y)).ToArray();
        var reference = measured.Select(p => new VisionPoint2D(100 + (p.X - 100) * 1.05, 100 + (p.Y - 100) * 1.05)).ToArray();
        var inputs = new Dictionary<string, VisionValue>
        {
            ["measured"] = VisionValue.PointList(measured),
            ["reference"] = VisionValue.PointList(reference)
        };

        var accepted = await Run(new BestFitAlignNode(), inputs,
            new { fitMode = "similarity", autoInitialRotation = false });
        Assert.True(accepted.Outputs["found"].Require<bool>("found"));
        Assert.True(accepted.Outputs["scaleWithinTolerance"].Require<bool>("scaleWithinTolerance"));
        Assert.True(accepted.Outputs["scale"].Require<double>("scale") > 1.03);

        var rejected = await Run(new BestFitAlignNode(), inputs,
            new { fitMode = "similarity", autoInitialRotation = false, scaleTolerance = 0.001 });
        Assert.False(rejected.Outputs["found"].Require<bool>("found"));
        Assert.Equal("scale-outside-tolerance", rejected.Outputs["reason"].Require<string>("reason"));
        Assert.True(rejected.Outputs["scaleDeviation"].Require<double>("scaleDeviation") > 0.03);

        var rigid = await Run(new BestFitAlignNode(), inputs,
            new { fitMode = "rigid", autoInitialRotation = false });
        Assert.True(rigid.Outputs["rigidScaleEnforced"].Require<bool>("rigidScaleEnforced"));
    }
}
