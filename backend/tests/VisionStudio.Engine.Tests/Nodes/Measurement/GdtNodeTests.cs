using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// GD&T 三件套（tolerance.datum / tolerance.position / tolerance.runout）行为契约：
/// 基准系折算、Ø 位置度与 MMC 补偿、径向/轴向跳动与覆盖角门限。
/// </summary>
public sealed class GdtNodeTests
{
    [Fact]
    public async Task Datum_ProjectsSecondaryOntoPrimaryAndReportsOffset()
    {
        // 基准 A：水平线 y=200；基准 B：点 (150,260) → 原点 (150,200)，偏置 +60（轴下方）。
        var inputs = new Dictionary<string, VisionValue>
        {
            ["primary"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(100, 200), new VisionPoint2D(300, 200))),
            ["secondary"] = VisionValue.Point(new VisionPoint2D(150, 260))
        };
        var result = await Run(new DatumFrameNode(), "tolerance.datum", inputs, """{"datumName":"A","unit":"mm"}""");

        var pose = result.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(150d, pose.X, 6);
        Assert.Equal(200d, pose.Y, 6);
        Assert.Equal(0d, pose.ThetaDeg, 6);
        Assert.Equal(60d, result.Outputs["secondaryOffset"].Require<double>("secondaryOffset"), 6);
        Assert.Equal(200d, result.Outputs["length"].Require<double>("length"), 6);
        Assert.Contains("\"name\":\"A\"", result.Outputs["datumJson"].Require<string>("datumJson"));
        Assert.Contains("mm", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task Datum_FallsBackToMidpointAndHonoursFlipAxis()
    {
        var midpoint = await Run(new DatumFrameNode(), "tolerance.datum", new Dictionary<string, VisionValue>
        {
            ["primary"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(100, 100)))
        }, "{}");
        var pose = midpoint.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(50d, pose.X, 6);
        Assert.Equal(50d, pose.Y, 6);
        Assert.Equal(45d, pose.ThetaDeg, 6);
        Assert.False(midpoint.Outputs.ContainsKey("secondaryOffset"));

        // flipAxis 把 +X 反过来：45° → -135°（归一化到 (-180,180]）。
        var flipped = await Run(new DatumFrameNode(), "tolerance.datum", new Dictionary<string, VisionValue>
        {
            ["primary"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(100, 100)))
        }, """{"flipAxis":true}""");
        Assert.Equal(-135d, flipped.Outputs["pose"].Require<VisionPose2D>("pose").ThetaDeg, 6);
    }

    [Fact]
    public async Task Datum_RejectsDegenerateLineAndBadUnit()
    {
        var zero = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new DatumFrameNode(), "tolerance.datum", new Dictionary<string, VisionValue>
            {
                ["primary"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(10, 10), new VisionPoint2D(10, 10)))
            }, "{}"));
        Assert.Contains("non-zero length", zero.Message);

        var unit = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new DatumFrameNode(), "tolerance.datum", new Dictionary<string, VisionValue>
            {
                ["primary"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(10, 0)))
            }, """{"unit":"millimetres-and-more"}"""));
        Assert.Contains("unit", unit.Message);
    }

    [Fact]
    public async Task Position_ReportsDiametralDeviationInDatumFrame()
    {
        // 基准在 (100,100) 无旋转；基本尺寸 (0,0)（相对基准原点），孔心 (103,104) → dx=3, dy=4，径向 5，Ø 偏差 10。
        var inputs = new Dictionary<string, VisionValue>
        {
            ["circle"] = VisionValue.Circle(new VisionCircle(103, 104, 5)),
            ["datum"] = VisionValue.Pose(new VisionPose2D(100, 100, 0))
        };
        var result = await Run(new PositionToleranceNode(), "tolerance.position", inputs,
            """{"diameterTolerance":12}""");

        Assert.Equal(3d, result.Outputs["deviationX"].Require<double>("deviationX"), 6);
        Assert.Equal(4d, result.Outputs["deviationY"].Require<double>("deviationY"), 6);
        Assert.Equal(10d, result.Outputs["diametralDeviation"].Require<double>("diametralDeviation"), 6);
        Assert.Equal(12d, result.Outputs["effectiveTolerance"].Require<double>("effectiveTolerance"), 6);
        Assert.Equal(10d / 12d, result.Outputs["utilization"].Require<double>("utilization"), 6);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(0d, result.Outputs["bonus"].Require<double>("bonus"), 6);
    }

    [Fact]
    public async Task Position_AppliesMmcBonusInTheRightDirection()
    {
        // Ø 偏差 10，公差 9 → 无补偿不合格；孔径 11.2 对 MMC 10 补 1.2 → 有效 10.2 合格。
        var inputs = new Dictionary<string, VisionValue>
        {
            ["point"] = VisionValue.Point(new VisionPoint2D(103, 104)),
            ["datum"] = VisionValue.Pose(new VisionPose2D(100, 100, 0)),
            ["size"] = VisionValue.Double(11.2)
        };
        const string json = """{"diameterTolerance":9,"mmcBonus":true,"mmcSize":10}""";

        var hole = await Run(new PositionToleranceNode(), "tolerance.position", Clone(inputs), json);
        Assert.Equal(1.2d, hole.Outputs["bonus"].Require<double>("bonus"), 6);
        Assert.Equal(10.2d, hole.Outputs["effectiveTolerance"].Require<double>("effectiveTolerance"), 6);
        Assert.True(hole.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        // 同一实测尺寸按"轴"解释：MMC 是最大直径，实际偏小 → 不给补偿，仍然不合格。
        var pin = await Run(new PositionToleranceNode(), "tolerance.position", Clone(inputs),
            """{"diameterTolerance":9,"mmcBonus":true,"mmcSize":10,"featureType":"pin"}""");
        Assert.Equal(0d, pin.Outputs["bonus"].Require<double>("bonus"), 6);
        Assert.False(pin.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
    }

    [Fact]
    public async Task Position_ChecksEachAxisInRectangularZone()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["point"] = VisionValue.Point(new VisionPoint2D(103, 104)),
            ["datum"] = VisionValue.Pose(new VisionPose2D(100, 100, 0))
        };
        var result = await Run(new PositionToleranceNode(), "tolerance.position", inputs,
            """{"zoneType":"rectangular","toleranceX":4,"toleranceY":3}""");

        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(4d / 3d, result.Outputs["utilization"].Require<double>("utilization"), 6);
        Assert.Equal(0d, result.Outputs["bonus"].Require<double>("bonus"), 6);
    }

    [Fact]
    public async Task Position_RequiresExactlyOneFeatureAndSizeForMmc()
    {
        var both = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new PositionToleranceNode(), "tolerance.position", new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.Point(new VisionPoint2D(1, 1)),
                ["circle"] = VisionValue.Circle(new VisionCircle(1, 1, 2))
            }, "{}"));
        Assert.Contains("exactly one", both.Message);

        var neither = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new PositionToleranceNode(), "tolerance.position", new Dictionary<string, VisionValue>(), "{}"));
        Assert.Contains("exactly one", neither.Message);

        var noSize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new PositionToleranceNode(), "tolerance.position", new Dictionary<string, VisionValue>
            {
                ["point"] = VisionValue.Point(new VisionPoint2D(1, 1))
            }, """{"mmcBonus":true,"mmcSize":10}"""));
        Assert.Contains("size", noSize.Message);

        var negativeRadius = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new PositionToleranceNode(), "tolerance.position", new Dictionary<string, VisionValue>
            {
                ["circle"] = VisionValue.Circle(new VisionCircle(1, 1, 0))
            }, "{}"));
        Assert.Contains("radius", negativeRadius.Message);
    }

    [Fact]
    public async Task Runout_MeasuresRadialFimAndCoverage()
    {
        var samples = CircleSamples(radius: 10, count: 180, oneOutlierAtDeg: 90, outlierRadius: 10.2, spanDeg: 360);
        var result = await Run(new RunoutToleranceNode(), "tolerance.runout", new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }, """{"mode":"radial","tolerance":0.5}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0.2d, result.Outputs["runout"].Require<double>("runout"), 3);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.InRange(result.Outputs["coverageDeg"].Require<double>("coverageDeg"), 356, 360);
        Assert.InRange(result.Outputs["highAngleDeg"].Require<double>("highAngleDeg"), 88, 92);
        Assert.InRange(result.Outputs["minValue"].Require<double>("minValue"), 9.99, 10.01);
    }

    [Fact]
    public async Task Runout_RejectsInsufficientCoverageAndSamples()
    {
        // 只测 0..90°：覆盖角约 90°，远低于 180° 门限 → found=false。
        var partial = CircleSamples(radius: 10, count: 90, oneOutlierAtDeg: -1, outlierRadius: 10, spanDeg: 90);
        var coverage = await Run(new RunoutToleranceNode(), "tolerance.runout", new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(partial),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }, """{"mode":"radial","minCoverageDeg":180}""");
        Assert.False(coverage.Outputs["found"].Require<bool>("found"));
        Assert.Contains("coverage", coverage.Outputs["diagnostics"].Require<string>("diagnostics"));

        var two = await Run(new RunoutToleranceNode(), "tolerance.runout", new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList([new VisionPoint2D(10, 0), new VisionPoint2D(0, 10)]),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }, "{}");
        Assert.False(two.Outputs["found"].Require<bool>("found"));
        Assert.Contains("at least 3", two.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task Runout_AxialModeUsesCoordinateAlongTheDatumAxis()
    {
        // 端面：沿基准轴方向 x 从 0 变到 0.3，径向变化不计入轴向跳动。
        var samples = new List<VisionPoint2D>();
        for (var index = 0; index < 40; index++)
        {
            var angle = index * Math.PI * 2 / 40;
            var x = index == 7 ? 0.3 : 0.0;
            samples.Add(new VisionPoint2D(x, 10 * Math.Sin(angle) + 5 * Math.Cos(angle)));
        }
        var result = await Run(new RunoutToleranceNode(), "tolerance.runout", new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["datum"] = VisionValue.Pose(new VisionPose2D(0, 0, 0))
        }, """{"mode":"axial","tolerance":0.2}""");

        Assert.Equal(0.3d, result.Outputs["runout"].Require<double>("runout"), 6);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.False(result.Outputs.ContainsKey("coverageDeg"));
        Assert.False(result.Outputs.ContainsKey("highAngleDeg"));
    }

    // ---------------------------------------------------------------- 辅助

    private static Dictionary<string, VisionValue> Clone(Dictionary<string, VisionValue> inputs)
        => new(inputs, StringComparer.OrdinalIgnoreCase);

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, Dictionary<string, VisionValue> inputs, string json)
    {
        var node = new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    /// <summary>生成一圈样本；oneOutlierAtDeg 指定角度用 outlierRadius 替换半径（-1 表示不放异常点）。</summary>
    private static List<VisionPoint2D> CircleSamples(
        double radius, int count, double oneOutlierAtDeg, double outlierRadius, double spanDeg)
    {
        var points = new List<VisionPoint2D>(count);
        for (var index = 0; index < count; index++)
        {
            var deg = spanDeg * index / (count - 1);
            var r = Math.Abs(deg - oneOutlierAtDeg) < 1e-9 || (oneOutlierAtDeg >= 0 && Math.Abs(deg - oneOutlierAtDeg) < spanDeg / count)
                ? outlierRadius
                : radius;
            var radians = deg * Math.PI / 180;
            points.Add(new VisionPoint2D(r * Math.Cos(radians), r * Math.Sin(radians)));
        }
        return points;
    }
}
