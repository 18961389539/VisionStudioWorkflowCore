using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// P1 补全批次契约：defect.cluster / defect.classify / image.fuse / calibration.conveyor / tolerance.profile。
/// </summary>
public sealed class P1CompletionNodeTests
{
    // ------------------------------------------------------------ defect.cluster

    [Fact]
    public async Task Cluster_JoinsNearbyBoxesAndKeepsTheMergedExtent()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["detections"] = VisionValue.Detections([
                Detection(0, 0, 0, 10, 10, 100),
                Detection(1, 12, 0, 10, 10, 100),   // 与第 1 个间隔 2px
                Detection(2, 200, 200, 10, 10, 100) // 孤立
            ])
        };
        var result = await Run(new DefectClusterNode(), "defect.cluster", inputs, """{"linkDistancePx":5}""");

        var merged = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(1L, result.Outputs["mergedCount"].Require<long>("mergedCount"));
        Assert.Equal(2, merged.Count);
        var big = merged.OrderByDescending(d => d.Area).First();
        Assert.Equal(200d, big.Area, 6);
        Assert.Equal(0d, big.BoundingBox.X, 6);
        Assert.Equal(22d, big.BoundingBox.Width, 6);
    }

    [Fact]
    public async Task Cluster_WithZeroDistanceOnlyMergesOverlapsAndRejectsOversizedInput()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["detections"] = VisionValue.Detections([
                Detection(0, 0, 0, 10, 10, 100),
                Detection(1, 12, 0, 10, 10, 100)
            ])
        };
        var separate = await Run(new DefectClusterNode(), "defect.cluster", inputs, """{"linkDistancePx":0}""");
        Assert.Equal(2L, separate.Outputs["count"].Require<long>("count"));

        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new DefectClusterNode(), "defect.cluster", inputs, """{"maxDetections":1}"""));
        Assert.Contains("maxDetections", tooMany.Message);
    }

    // ------------------------------------------------------------ defect.classify

    [Fact]
    public async Task Classify_GradesByRuleOrderAndCountsNg()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["detections"] = VisionValue.Detections([
                Detection(0, 0, 0, 300, 10, 3000, circularity: 0.2),  // 长宽比 30 → 划痕
                Detection(1, 0, 0, 10, 10, 100, circularity: 0.9),    // 圆形小点 → 凹坑
                Detection(2, 0, 0, 100, 100, 6000, circularity: 0.5), // 大面积 → 脏污
                Detection(3, 0, 0, 20, 10, 60, circularity: 0.35)     // 不匹配任何规则
            ])
        };
        var result = await Run(new DefectClassifyNode(), "defect.classify", inputs, "{}");
        var graded = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");

        Assert.Equal(1, graded[0].ClassId);
        Assert.Equal(2, graded[1].ClassId);
        Assert.Equal(3, graded[2].ClassId);
        Assert.Equal(0, graded[3].ClassId);
        Assert.Equal(1L, result.Outputs["unclassifiedCount"].Require<long>("unclassifiedCount"));
        Assert.Equal(4L, result.Outputs["ngCount"].Require<long>("ngCount"));
        Assert.False(result.Outputs["ok"].Require<bool>("ok"));
        Assert.Contains("\"name\":\"scratch\"", result.Outputs["classCountsJson"].Require<string>("classCountsJson"));
    }

    [Fact]
    public async Task Classify_CanIgnoreUnclassifiedAndValidatesRules()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["detections"] = VisionValue.Detections([
                Detection(0, 0, 0, 300, 10, 3000, circularity: 0.2),
                Detection(1, 0, 0, 20, 10, 60, circularity: 0.35)
            ])
        };
        var relaxed = await Run(new DefectClassifyNode(), "defect.classify", inputs,
            """{"rejectUnclassified":false}""");
        Assert.Equal(1L, relaxed.Outputs["ngCount"].Require<long>("ngCount"));

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new DefectClassifyNode(), "defect.classify", inputs,
                """{"rulesJson":"[{\"name\":\"a\"},{\"name\":\"a\"}]"}"""));
        Assert.Contains("duplicated", duplicate.Message);

        var inverted = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new DefectClassifyNode(), "defect.classify", inputs,
                """{"rulesJson":"[{\"name\":\"a\",\"minArea\":100,\"maxArea\":10}]"}"""));
        Assert.Contains("minArea > maxArea", inverted.Message);
    }

    // ------------------------------------------------------------ image.fuse

    [Fact]
    public async Task Fuse_ComputesMeanMinMaxAndMedian()
    {
        using var first = Constant(120, 100);
        using var second = Constant(120, 200);
        var mean = await Run(new ImageFuseNode(), "image.fuse",
            Inputs(("image1", first), ("image2", second)), """{"mode":"mean"}""");
        using var meanImage = Result(mean);
        Assert.Equal(150, meanImage.Mat.At<byte>(0, 0));
        Assert.Equal(2L, mean.Outputs["inputCount"].Require<long>("inputCount"));

        var minimum = await Run(new ImageFuseNode(), "image.fuse",
            Inputs(("image1", first), ("image2", second)), """{"mode":"min"}""");
        using var minImage = Result(minimum);
        Assert.Equal(100, minImage.Mat.At<byte>(0, 0));

        var maximum = await Run(new ImageFuseNode(), "image.fuse",
            Inputs(("image1", first), ("image2", second)), """{"mode":"max"}""");
        using var maxImage = Result(maximum);
        Assert.Equal(200, maxImage.Mat.At<byte>(0, 0));
    }

    [Fact]
    public async Task Fuse_MedianRejectsASpeckPresentInOneFrameOnly()
    {
        using var clean1 = Constant(64, 100);
        using var clean2 = Constant(64, 100);
        using var specked = Constant(64, 100);
        specked.Set(10, 10, (byte)255); // 只有这一帧有亮点
        var median = await Run(new ImageFuseNode(), "image.fuse",
            Inputs(("image1", clean1), ("image2", specked), ("image3", clean2)), """{"mode":"median"}""");
        using var medianImage = Result(median);
        Assert.Equal(100, medianImage.Mat.At<byte>(10, 10));
    }

    [Fact]
    public async Task Fuse_RejectsMismatchedStacksAndMedianOnSixteenBit()
    {
        using var small = Constant(64, 100);
        using var large = Constant(128, 100);
        var size = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageFuseNode(), "image.fuse", Inputs(("image1", small), ("image2", large)), "{}"));
        Assert.Contains("same size", size.Message);

        var single = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageFuseNode(), "image.fuse", Inputs(("image1", small)), "{}"));
        Assert.Contains("at least two", single.Message);

        using var wide1 = new Mat(8, 8, MatType.CV_16UC1, Scalar.All(1000));
        using var wide2 = new Mat(8, 8, MatType.CV_16UC1, Scalar.All(2000));
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageFuseNode(), "image.fuse", Inputs(("image1", wide1), ("image2", wide2)),
                """{"mode":"median"}"""));
        Assert.Contains("8-bit", depth.Message);
    }

    // ------------------------------------------------------------ calibration.conveyor

    [Fact]
    public async Task Conveyor_ConvertsCountsAndCompensatesTriggerDelay()
    {
        var inputs = new Dictionary<string, VisionValue> { ["encoder"] = VisionValue.Double(2500) };
        var add = await Run(new ConveyorCalibrationNode(), "calibration.conveyor", inputs,
            """{"countsPerUnit":1000,"lineSpeedUnitPerSec":100,"triggerDelayMs":50}""");
        Assert.Equal(2.5d, add.Outputs["position"].Require<double>("position"), 9);
        Assert.Equal(5d, add.Outputs["triggerOffset"].Require<double>("triggerOffset"), 9);
        Assert.Equal(7.5d, add.Outputs["compensatedPosition"].Require<double>("compensatedPosition"), 9);
        Assert.Equal("mm", add.Outputs["unit"].Require<string>("unit"));

        var subtract = await Run(new ConveyorCalibrationNode(), "calibration.conveyor", inputs,
            """{"countsPerUnit":1000,"lineSpeedUnitPerSec":100,"triggerDelayMs":50,"compensationSign":"subtract"}""");
        Assert.Equal(-2.5d, subtract.Outputs["compensatedPosition"].Require<double>("compensatedPosition"), 9);
    }

    [Fact]
    public async Task Conveyor_RejectsZeroScale()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ConveyorCalibrationNode(), "calibration.conveyor",
                new Dictionary<string, VisionValue> { ["encoder"] = VisionValue.Double(10) },
                """{"countsPerUnit":0}"""));
        Assert.Contains("countsPerUnit", error.Message);
    }

    // ------------------------------------------------------------ tolerance.profile

    [Fact]
    public async Task Profile_BilateralReportsTheFullBandWidth()
    {
        var samples = new List<VisionPoint2D>();
        for (var index = 0; index < 11; index++)
            samples.Add(new VisionPoint2D(10 * index, 100 + (index % 2 == 0 ? 2 : -2)));

        var inputs = new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 100), new VisionPoint2D(100, 100)))
        };
        var result = await Run(new ProfileToleranceNode(), "tolerance.profile", inputs,
            """{"tolerance":5}""");

        Assert.Equal(4d, result.Outputs["profile"].Require<double>("profile"), 6);
        Assert.Equal(2d, result.Outputs["maxDeviation"].Require<double>("maxDeviation"), 6);
        Assert.Equal(-2d, result.Outputs["minDeviation"].Require<double>("minDeviation"), 6);
        Assert.Equal(2d, result.Outputs["rmsDeviation"].Require<double>("rmsDeviation"), 6);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal(0L, result.Outputs["crossedSampleCount"].Require<long>("crossedSampleCount"));
    }

    [Fact]
    public async Task Profile_UnilateralFlagsCrossedSamples()
    {
        var samples = new List<VisionPoint2D>
        {
            new(0, 101), new(10, 102), new(20, -1) // 第三点跑到材料内侧
        };
        var inputs = new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(100, 0)))
        };
        var result = await Run(new ProfileToleranceNode(), "tolerance.profile", inputs,
            """{"mode":"unilateralOutside","tolerance":5}""");

        Assert.Equal(1L, result.Outputs["crossedSampleCount"].Require<long>("crossedSampleCount"));
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        // 基准线在 y=0，三个样本偏差为 101 / 102 / -1：单侧带只取正值侧，跨侧的那一点另计。
        Assert.Equal(102d, result.Outputs["profile"].Require<double>("profile"), 6);
    }

    [Fact]
    public async Task Profile_SupportsCirclesAndRequiresExactlyOneNominal()
    {
        var samples = new List<VisionPoint2D>();
        for (var index = 0; index < 12; index++)
        {
            var angle = Math.PI * 2 * index / 12;
            var radius = index % 2 == 0 ? 10.5 : 9.5;
            samples.Add(new VisionPoint2D(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }
        var inputs = new Dictionary<string, VisionValue>
        {
            ["samples"] = VisionValue.PointList(samples),
            ["nominalCircle"] = VisionValue.Circle(new VisionCircle(0, 0, 10))
        };
        var result = await Run(new ProfileToleranceNode(), "tolerance.profile", inputs,
            """{"tolerance":1.2}""");
        Assert.Equal(1d, result.Outputs["profile"].Require<double>("profile"), 6);
        Assert.True(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        var both = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ProfileToleranceNode(), "tolerance.profile", new Dictionary<string, VisionValue>
            {
                ["samples"] = VisionValue.PointList(samples),
                ["nominalCircle"] = VisionValue.Circle(new VisionCircle(0, 0, 10)),
                ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 0), new VisionPoint2D(1, 0)))
            }, "{}"));
        Assert.Contains("exactly one nominal", both.Message);

        var none = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ProfileToleranceNode(), "tolerance.profile",
                new Dictionary<string, VisionValue> { ["samples"] = VisionValue.PointList(samples) }, "{}"));
        Assert.Contains("exactly one nominal", none.Message);
    }

    // ------------------------------------------------------------ 辅助

    private static VisionDetection Detection(
        int index, double x, double y, double width, double height, double area, double circularity = 0.5)
        => new(index, x + width / 2, y + height / 2, width, height, area,
            4 * (width + height), circularity,
            new VisionCircle(x + width / 2, y + height / 2, Math.Max(width, height) / 2),
            new VisionRectangle2D(x, y, width, height), 0.9, null);

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, Dictionary<string, VisionValue> inputs, string json)
    {
        var node = new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static Dictionary<string, VisionValue> Inputs(params (string Port, Mat Image)[] images)
    {
        var inputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (port, image) in images) inputs[port] = VisionValue.Image(VisionImage.Own(image.Clone()));
        return inputs;
    }

    private static VisionImage Result(NodeExecutorResult result)
        => result.Outputs["image"].Require<VisionImage>("image");

    private static Mat Constant(int size, byte value)
        => new(size, size, MatType.CV_8UC1, Scalar.All(value));
}
