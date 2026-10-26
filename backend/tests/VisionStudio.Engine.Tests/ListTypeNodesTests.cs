using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 通用列表类型（NumberList / PointList）与配套节点的测试。
/// 覆盖类型兼容性、组装/提取、统计口径（样本/总体、CP/CPK）、点集拟合与退化输入。
/// </summary>
public sealed class ListTypeNodesTests
{
    [Fact]
    public void NumberAndPointListAreDistinctTypes()
    {
        // 列表类型只与自身或 Any 兼容：不引入隐式加宽，避免把单值误当列表。
        Assert.True(VisionNodeDispatcher.AreCompatible(VisionDataType.NumberList, VisionDataType.NumberList));
        Assert.False(VisionNodeDispatcher.AreCompatible(VisionDataType.NumberList, VisionDataType.Double));
        Assert.False(VisionNodeDispatcher.AreCompatible(VisionDataType.Double, VisionDataType.NumberList));
        Assert.False(VisionNodeDispatcher.AreCompatible(VisionDataType.NumberList, VisionDataType.PointList));
        // 既有规则不受影响。
        Assert.True(VisionNodeDispatcher.AreCompatible(VisionDataType.Integer, VisionDataType.Double));
    }

    [Fact]
    public async Task NumberList_CollectsConnectedInputsInPortOrder()
    {
        var result = await new ValueNumberListNode().ExecuteAsync(
            Ctx(("c", VisionValue.Double(3)), ("a", VisionValue.Double(1)), ("h", VisionValue.Double(8))),
            Def("value.numberList"), CancellationToken.None);

        Assert.Equal([1d, 3d, 8d], Numbers(result));
        Assert.Equal(3L, result.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task NumberList_RequiresAtLeastOneInputAndRejectsNonFinite()
    {
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ValueNumberListNode().ExecuteAsync(Ctx(), Def("value.numberList"), CancellationToken.None).AsTask());
        Assert.Contains("at least one connected", empty.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ValueNumberListNode().ExecuteAsync(Ctx(("a", VisionValue.Double(double.NaN))), Def("value.numberList"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DetectionNumbers_ExtractsColumnAndCountsMissingValues()
    {
        var detections = new[] { Detection(0, confidence: 0.9), Detection(1, confidence: null), Detection(2, confidence: 0.5) };
        var result = await new DetectionNumbersNode().ExecuteAsync(
            Ctx(("detections", VisionValue.Detections(detections))), Def("collection.detectionNumbers"), CancellationToken.None);

        Assert.Equal([0.9, 0.5], Numbers(result));
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        // confidence 缺失的那一条计入 skipped，而不是当 0 参与统计。
        Assert.Equal(1L, result.Outputs["skipped"].Require<long>("skipped"));
    }

    [Fact]
    public async Task DetectionNumbers_RejectsUnknownField()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DetectionNumbersNode().ExecuteAsync(
            Ctx(("detections", VisionValue.Detections([]))), Def("collection.detectionNumbers", ("field", "wat")), CancellationToken.None).AsTask());
        Assert.Contains("not supported", error.Message);
    }

    [Fact]
    public async Task Statistics_UsesDocumentedSampleSemantics()
    {
        var result = await new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([1d, 2d, 3d, 4d]))), Def("value.statistics"), CancellationToken.None);

        Assert.Equal(4L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(2.5, result.Outputs["mean"].Require<double>("mean"), 1e-12);
        // 样本口径 σ = sqrt(5/3)。
        Assert.Equal(Math.Sqrt(5d / 3d), result.Outputs["std"].Require<double>("std"), 1e-12);
        Assert.Equal(2.5, result.Outputs["median"].Require<double>("median"), 1e-12);
        Assert.Equal(1d, result.Outputs["min"].Require<double>("min"), 1e-12);
        Assert.Equal(4d, result.Outputs["max"].Require<double>("max"), 1e-12);
        Assert.Equal(3d, result.Outputs["range"].Require<double>("range"), 1e-12);
        Assert.Equal(10d, result.Outputs["sum"].Require<double>("sum"), 1e-12);
        // 未启用规格限时不输出 cp/cpk/pass，避免下游把"没有规格"误读为"合格"。
        Assert.False(result.Outputs.ContainsKey("cp"));
        Assert.False(result.Outputs.ContainsKey("pass"));
    }

    [Fact]
    public async Task Statistics_ComputesCpAndCpkWhenLimitsEnabled()
    {
        var sigma = Math.Sqrt(5d / 3d);
        var result = await new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([1d, 2d, 3d, 4d]))),
            Def("value.statistics", ("enableLimits", "true"), ("lsl", "1"), ("usl", "4")), CancellationToken.None);

        Assert.Equal(3d / (6d * sigma), result.Outputs["cp"].Require<double>("cp"), 1e-9);
        Assert.Equal(1.5d / (3d * sigma), result.Outputs["cpk"].Require<double>("cpk"), 1e-9);
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task Statistics_RejectsEmptyNonFiniteAndZeroVarianceWithLimits()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([]))), Def("value.statistics"), CancellationToken.None).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([1d, double.PositiveInfinity]))), Def("value.statistics"), CancellationToken.None).AsTask());

        // σ=0 时 CP/CPK 数学上为无穷大：明确失败而不是返回 Infinity。
        var zero = await Assert.ThrowsAsync<InvalidOperationException>(() => new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([2d, 2d, 2d]))),
            Def("value.statistics", ("enableLimits", "true"), ("lsl", "1"), ("usl", "3")), CancellationToken.None).AsTask());
        Assert.Contains("standard deviation is 0", zero.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new StatisticsNode().ExecuteAsync(
            Ctx(("numberList", VisionValue.NumberList([1d, 2d]))),
            Def("value.statistics", ("enableLimits", "true"), ("lsl", "3"), ("usl", "3")), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DetectionPoints_UsesDetectionCentres()
    {
        var result = await new DetectionPointsNode().ExecuteAsync(
            Ctx(("detections", VisionValue.Detections([Detection(0, x: 5, y: 7), Detection(1, x: 9, y: 11)]))),
            Def("collection.detectionPoints"), CancellationToken.None);

        var points = result.Outputs["pointList"].Require<IReadOnlyList<VisionPoint2D>>("pointList");
        Assert.Equal(2, points.Count);
        Assert.Equal(new VisionPoint2D(5, 7), points[0]);
        Assert.Equal(new VisionPoint2D(9, 11), points[1]);
    }

    [Fact]
    public async Task FitFromPoints_LineUsesTotalLeastSquares()
    {
        // 完全共线的点：斜率 2，残差应为 0，且方向对竖直方向的线也成立。
        var points = new List<VisionPoint2D> { new(0, 0), new(1, 2), new(2, 4), new(3, 6) };
        var result = await new FitFromPointsNode().ExecuteAsync(
            Ctx(("pointList", VisionValue.PointList(points))), Def("geometry.fitFromPoints"), CancellationToken.None);

        var line = result.Outputs["line"].Require<VisionLine2D>("line");
        Assert.Equal(Math.Atan2(2, 1) * 180d / Math.PI, line.AngleDeg, 1e-9);
        Assert.Equal(0d, result.Outputs["residualPx"].Require<double>("residualPx"), 1e-9);
        Assert.Equal(4L, result.Outputs["pointCount"].Require<long>("pointCount"));
    }

    [Fact]
    public async Task FitFromPoints_CircleRecoversKnownCentreAndRadius()
    {
        var centre = new VisionPoint2D(10, 20);
        const double radius = 5;
        var points = Enumerable.Range(0, 24)
            .Select(index => index * Math.PI * 2 / 24)
            .Select(angle => new VisionPoint2D(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)))
            .ToArray();

        var result = await new FitFromPointsNode().ExecuteAsync(
            Ctx(("pointList", VisionValue.PointList(points))), Def("geometry.fitFromPoints", ("operation", "circle")), CancellationToken.None);

        var circle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.Equal(centre.X, circle.X, 1e-6);
        Assert.Equal(centre.Y, circle.Y, 1e-6);
        Assert.Equal(radius, circle.Radius, 1e-6);
        Assert.Equal(0d, result.Outputs["residualPx"].Require<double>("residualPx"), 1e-6);
    }

    [Fact]
    public async Task FitFromPoints_RejectsDegenerateInputs()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FitFromPointsNode().ExecuteAsync(
            Ctx(("pointList", VisionValue.PointList([new VisionPoint2D(0, 0)]))), Def("geometry.fitFromPoints"), CancellationToken.None).AsTask());

        // 三点共线时圆拟合退化：明确失败，而不是给出任意圆心。
        var collinear = new List<VisionPoint2D> { new(0, 0), new(1, 1), new(2, 2) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FitFromPointsNode().ExecuteAsync(
            Ctx(("pointList", VisionValue.PointList(collinear))), Def("geometry.fitFromPoints", ("operation", "circle")), CancellationToken.None).AsTask());
        Assert.Contains("degenerate", error.Message);
    }

    [Fact]
    public void Catalog_DeclaresListTypesAndNodes()
    {
        Assert.Contains(BuiltInNodeCatalog.Require("value.numberList").Outputs, port => port.DataType == VisionDataType.NumberList);
        Assert.Contains(BuiltInNodeCatalog.Require("value.statistics").Inputs, port => port.DataType == VisionDataType.NumberList);
        Assert.Contains(BuiltInNodeCatalog.Require("collection.detectionPoints").Outputs, port => port.DataType == VisionDataType.PointList);
        Assert.Contains(BuiltInNodeCatalog.Require("geometry.fitFromPoints").Inputs, port => port.DataType == VisionDataType.PointList);
        Assert.Contains(BuiltInNodeCatalog.Require("collection.detectionNumbers").Outputs, port => port.DataType == VisionDataType.NumberList);
    }

    // ===== 夹具 =====

    private static IReadOnlyList<double> Numbers(NodeExecutorResult result)
        => result.Outputs["numberList"].Require<IReadOnlyList<double>>("numberList");

    private static VisionDetection Detection(int index, double x = 0, double y = 0, double? confidence = null)
        => new(index, x, y, 2, 2, 4, 8, 1, new VisionCircle(x, y, 1), new VisionRectangle2D(x, y, 2, 2), confidence, null);

    private static NodeExecutionContext Ctx(params (string Port, VisionValue Value)[] inputs)
        => new(inputs.ToDictionary(item => item.Port, item => item.Value, StringComparer.OrdinalIgnoreCase));

    private static NodeDefinition Def(string type, params (string Key, string Value)[] parameters)
    {
        var payload = parameters.ToDictionary(item => item.Key, item => (object)item.Value);
        return new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }
}
