using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// measurement 契约批次：卡尺歧义/裁剪、斑点截断对账、点线距离拆分、圆间距相切公差。
/// </summary>
public sealed class MeasurementContractBatchTests
{
    [Fact]
    public async Task Caliper_AmbiguityMarginRefusesToPickBetweenEquallyStrongEdges()
    {
        using var image = MakeDoubleEdge();
        var relaxed = await RunCaliper(image, """{"startX":2,"startY":32,"endX":90,"endY":32,"polarity":"any","edgeThreshold":10,"minContrast":10}""");
        Assert.True(relaxed.Outputs["found"].Require<bool>("found"));
        Assert.Equal("ok", relaxed.Outputs["reason"].Require<string>("reason"));
        Assert.Equal(1d, relaxed.Outputs["clippedLengthRatio"].Require<double>("clippedLengthRatio"), 6);

        var strict = await RunCaliper(image, """{"startX":2,"startY":32,"endX":90,"endY":32,"polarity":"any","edgeThreshold":10,"minContrast":10,"ambiguityMargin":20}""");
        Assert.False(strict.Outputs["found"].Require<bool>("found"));
        Assert.Equal("ambiguous-edge", strict.Outputs["reason"].Require<string>("reason"));
        Assert.True(strict.Outputs["ambiguous"].Require<bool>("ambiguous"));
        // 不给出唯一测量点，避免下游误用。
        Assert.False(strict.Outputs.ContainsKey("point"));
        Assert.True(strict.Outputs["candidateCount"].Require<long>("candidateCount") >= 2);
        Assert.InRange(strict.Outputs["strengthMargin"].Require<double>("strengthMargin"), 0, 20);
    }

    [Fact]
    public async Task Caliper_ReportsRequestedAndEffectiveSearchLineWhenClipped()
    {
        using var image = VisionImage.Own(new Mat(64, 64, MatType.CV_8UC1, Scalar.All(20)));
        var result = await RunCaliper(image, """{"startX":-64,"startY":32,"endX":32,"endY":32,"polarity":"any","edgeThreshold":5}""");
        var requested = result.Outputs["requestedSearchLine"].Require<VisionLine2D>("requestedSearchLine");
        var effective = result.Outputs["effectiveSearchLine"].Require<VisionLine2D>("effectiveSearchLine");
        Assert.Equal(-64, requested.Start.X, 6);
        Assert.InRange(effective.Start.X, -1e-6, 1e-6);
        Assert.InRange(result.Outputs["clippedLengthRatio"].Require<double>("clippedLengthRatio"), 0.1, 0.6);
    }

    [Fact]
    public async Task Blobs_TruncationAccountsForCandidateAndEmittedArea()
    {
        using var image = MakeBlobGrid(columns: 5, rows: 5);
        var result = await new MultiBlobNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }),
            Definition("measure.blobs", """{"minArea":1,"maxCount":5}"""), CancellationToken.None);
        Assert.True(result.Outputs["truncated"].Require<bool>("truncated"));
        Assert.False(result.Outputs["resultComplete"].Require<bool>("resultComplete"));
        Assert.Equal(5L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(25L, result.Outputs["inputCount"].Require<long>("inputCount"));
        Assert.Equal(0L, result.Outputs["filteredOutCount"].Require<long>("filteredOutCount"));
        // 汇总面积只覆盖输出斑点，必须小于候选面积。
        Assert.True(result.Outputs["emittedArea"].Require<double>("emittedArea")
            < result.Outputs["candidateArea"].Require<double>("candidateArea"));
        Assert.Equal("largestArea", result.Outputs["selectionRule"].Require<string>("selectionRule"));
        Assert.Equal(0L, result.Outputs["selectedIndex"].Require<long>("selectedIndex"));
    }

    [Fact]
    public async Task PointLine_KeepsInfiniteAndSegmentDistancesSeparate()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["point"] = VisionValue.Point(new VisionPoint2D(20, 5)),
            ["line"] = VisionValue.Line(new(new VisionPoint2D(0, 0), new VisionPoint2D(10, 0)))
        };
        var segment = await new PointLineDistanceNode().ExecuteAsync(
            new NodeExecutionContext(inputs), Definition("measure.pointLineDistance", """{"segment":true}"""), CancellationToken.None);

        Assert.Equal("segment", segment.Outputs["referenceKind"].Require<string>("referenceKind"));
        Assert.Equal(5d, segment.Outputs["lineDistance"].Require<double>("lineDistance"), 6);
        Assert.Equal(11.180339887, segment.Outputs["distance"].Require<double>("distance"), 4);
        Assert.Equal(segment.Outputs["distance"].Require<double>("distance"),
            segment.Outputs["endpointDistance"].Require<double>("endpointDistance"), 6);
        Assert.True(segment.Outputs["projectionClamped"].Require<bool>("projectionClamped"));
        // 投影落在延长线上时，有符号线段距离没有定义。
        Assert.False(segment.Outputs.ContainsKey("signedSegmentDistance"));
        Assert.Equal("not-evaluated", segment.Outputs["evaluationState"].Require<string>("evaluationState"));

        var gated = await new PointLineDistanceNode().ExecuteAsync(
            new NodeExecutionContext(inputs),
            Definition("measure.pointLineDistance", """{"segment":true,"requireWithinSegment":true}"""), CancellationToken.None);
        Assert.False(gated.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("outside-segment", gated.Outputs["reason"].Require<string>("reason"));
        Assert.Equal("fail", gated.Outputs["evaluationState"].Require<string>("evaluationState"));

        var distanceGate = await new PointLineDistanceNode().ExecuteAsync(
            new NodeExecutionContext(inputs),
            Definition("measure.pointLineDistance", """{"segment":true,"maxDistance":5}"""), CancellationToken.None);
        Assert.Equal("distance-exceeded", distanceGate.Outputs["reason"].Require<string>("reason"));
        Assert.False(distanceGate.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
    }

    [Fact]
    public async Task CircleSpacing_UsesExplicitTangencyTolerance()
    {
        // 圆心距比半径和少 5e-7 px：默认 1e-6 公差下必须判相切，而不是 overlapping。
        var nearTangent = await new CircleSpacingNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 10)),
                ["circleB"] = VisionValue.Circle(new VisionCircle(19.9999995, 0, 10))
            }), Definition("measure.circleSpacing", "{}"), CancellationToken.None);
        Assert.Equal("tangent", nearTangent.Outputs["relation"].Require<string>("relation"));
        Assert.False(nearTangent.Outputs["gapValid"].Require<bool>("gapValid"));

        // 收紧公差后同一几何回到 overlapping，说明容差确实参与分类。
        var strict = await new CircleSpacingNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 10)),
                ["circleB"] = VisionValue.Circle(new VisionCircle(19.9999995, 0, 10))
            }), Definition("measure.circleSpacing", """{"relationTolerancePx":1e-9}"""), CancellationToken.None);
        Assert.Equal("overlapping", strict.Outputs["relation"].Require<string>("relation"));

        var contained = await new CircleSpacingNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["circleA"] = VisionValue.Circle(new VisionCircle(0, 0, 50)),
                ["circleB"] = VisionValue.Circle(new VisionCircle(5, 0, 10))
            }), Definition("measure.circleSpacing", "{}"), CancellationToken.None);
        Assert.Equal("contained", contained.Outputs["relation"].Require<string>("relation"));
        Assert.False(contained.Outputs["gapValid"].Require<bool>("gapValid"));
    }

    private static async Task<NodeExecutorResult> RunCaliper(VisionImage image, string json)
        => await new CaliperNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }),
            Definition("measure.caliper", json), CancellationToken.None);

    private static VisionImage MakeDoubleEdge()
    {
        var mat = new Mat(64, 96, MatType.CV_8UC1, Scalar.All(20));
        // 两条上升沿相距 6px、幅值相同，中间留一段暗区把它们分开。
        using (var first = new Mat(mat, new Rect(30, 0, 3, 64))) first.SetTo(Scalar.All(220));
        using (var second = new Mat(mat, new Rect(36, 0, 20, 64))) second.SetTo(Scalar.All(220));
        return VisionImage.Own(mat);
    }

    private static VisionImage MakeBlobGrid(int columns, int rows)
    {
        var mat = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            Cv2.Rectangle(mat, new Rect(6 + column * 28, 6 + row * 28, 8, 8), Scalar.All(255), -1);
        return VisionImage.Own(mat);
    }

    private static NodeDefinition Definition(string type, string json)
        => new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
}
