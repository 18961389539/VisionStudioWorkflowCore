using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class MetrologyResultNodeTests
{
    private static NodeDefinition Def(string json = "{}")
        => new("test", "measure.metrologyResult", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(IReadOnlyList<VisionMetrologyResult> results)
        => new(new Dictionary<string, VisionValue> { ["results"] = VisionValue.MetrologyResults(results) });

    private static VisionMetrologyResult[] Results() =>
    [
        new(0, "hole", "circle", true, new VisionPoint2D(10, 20), new VisionCircle(10, 20, 5),
            Pose: new VisionPose2D(10, 20, 0), ResidualPx: 0.25),
        new(1, "failed", "line", false),
        new(2, "slot", "rectangle", true, new VisionPoint2D(30, 40),
            Rectangle: new VisionRectangle2D(30, 40, 20, 10, 15), Pose: new VisionPose2D(30, 40, 15), ResidualPx: 0.1)
    ];

    [Fact]
    public async Task SelectByName_ReturnsGeometryAndDiagnostics()
    {
        var result = await new MetrologyResultNode().ExecuteAsync(
            Ctx(Results()), Def("""{"name":"hole"}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("hole", result.Outputs["name"].Require<string>("name"));
        Assert.Equal("circle", result.Outputs["kind"].Require<string>("kind"));
        Assert.Equal(5d, result.Outputs["radius"].Require<double>("radius"));
        Assert.Equal(3L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(0.25d, result.Outputs["residual"].Require<double>("residual"));
        var diag = result.Outputs["diagnostics"].Require<string>("diagnostics");
        Assert.Contains("hole", diag);
        Assert.Contains("circle", diag);
    }

    [Fact]
    public async Task SelectByIndex_ReturnsFailedObjectAsNotFound()
    {
        var result = await new MetrologyResultNode().ExecuteAsync(
            Ctx(Results()), Def("""{"index":1}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("failed", result.Outputs["name"].Require<string>("name"));
        Assert.Equal("line", result.Outputs["kind"].Require<string>("kind"));
        Assert.Equal(3L, result.Outputs["count"].Require<long>("count"));
        Assert.False(result.Outputs.ContainsKey("circle"));
        Assert.False(result.Outputs.ContainsKey("line"));
        Assert.Contains("\"selector\":\"index\"", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task MissingSelection_ReturnsNotFoundWithoutGeometry()
    {
        var result = await new MetrologyResultNode().ExecuteAsync(
            Ctx(Results()), Def("""{"name":"nope"}"""), CancellationToken.None);

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("nope", result.Outputs["name"].Require<string>("name"));
        Assert.Equal("", result.Outputs["kind"].Require<string>("kind"));
        Assert.False(result.Outputs.ContainsKey("center"));
        Assert.Contains("\"selectedIndex\":-1", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task NameSelection_IgnoresIndexAndStillSelectsByName()
    {
        var result = await new MetrologyResultNode().ExecuteAsync(
            Ctx(Results()), Def("""{"name":"slot","index":999}"""), CancellationToken.None);

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("slot", result.Outputs["name"].Require<string>("name"));
        Assert.Equal(20d, result.Outputs["rectangle"].Require<VisionRectangle2D>("rectangle").Width);
    }

    [Fact]
    public async Task DuplicateNames_ThrowClearError()
    {
        VisionMetrologyResult[] duplicate =
        [
            new(0, "same", "circle", true, Center: new VisionPoint2D(1, 1), Circle: new VisionCircle(1, 1, 1)),
            new(1, "same", "circle", true, Center: new VisionPoint2D(2, 2), Circle: new VisionCircle(2, 2, 1))
        ];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(
                Ctx(duplicate), Def("""{"name":"same"}"""), CancellationToken.None));
        Assert.Contains("not unique", error.Message);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-1)]
    [InlineData(512)]
    public async Task InvalidIndex_Throws(double index)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(
                Ctx(Results()), Def($$"""{"index":{{index}}}"""), CancellationToken.None));
    }

    [Fact]
    public async Task OverlongName_Throws()
    {
        var name = new string('x', 129);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(
                Ctx(Results()), Def(JsonSerializer.Serialize(new { name })), CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateObjectIndices_AreRejectedInsteadOfSelectingTheFirst()
    {
        VisionMetrologyResult[] results = [new(0, "first", "circle", false), new(0, "second", "circle", false)];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx(results), Def(), default));
        Assert.Contains("index 0 is not unique", error.Message);
    }

    [Theory]
    [InlineData("center")]
    [InlineData("radius")]
    [InlineData("zeroRadius")]
    [InlineData("line")]
    [InlineData("rectangle")]
    [InlineData("pose")]
    [InlineData("residual")]
    [InlineData("negativeResidual")]
    public async Task SuccessfulSelection_RejectsInvalidGeometryAndResiduals(string field)
    {
        var item = new VisionMetrologyResult(0, "invalid", "circle", true, Center: new(10, 20));
        item = field switch
        {
            "center" => item with { Center = new(double.NaN, 20) },
            "radius" => item with { Circle = new(10, 20, double.PositiveInfinity) },
            "zeroRadius" => item with { Circle = new(10, 20, 0) },
            "line" => item with { Line = new(new(1, 1), new(1, 1)) },
            "rectangle" => item with { Rectangle = new(10, 20, -1, 10) },
            "pose" => item with { Pose = new(10, 20, double.NaN) },
            "residual" => item with { ResidualPx = double.NaN },
            _ => item with { ResidualPx = -1 }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx([item]), Def(), default));
        Assert.Contains("Metrology result 'invalid'", error.Message);
    }

    [Fact]
    public async Task FailedSelection_DoesNotExposeOrValidateStaleGeometry()
    {
        VisionMetrologyResult[] results = [new(0, "failed", "circle", false, Circle: new(0, 0, double.NaN))];
        var result = await new MetrologyResultNode().ExecuteAsync(Ctx(results), Def(), default);
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.False(result.Outputs.ContainsKey("circle"));
        Assert.False(result.Outputs.ContainsKey("radius"));
    }

    [Fact]
    public async Task IndexSelection_StillAllowsDuplicateNamesWithDistinctIndices()
    {
        VisionMetrologyResult[] results = [new(0, "same", "circle", false), new(1, "same", "line", false)];
        var result = await new MetrologyResultNode().ExecuteAsync(Ctx(results), Def("""{"index":1}"""), default);
        Assert.Equal("line", result.Outputs["kind"].Require<string>("kind"));
    }

    [Fact]
    public async Task InvalidLists_ReportClearErrors()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx([null!]), Def(), default));
        Assert.Contains("null object", missing.Message);
        var overBudget = Enumerable.Range(0, 513).Select(i => new VisionMetrologyResult(i, $"object{i}", "line", false)).ToArray();
        var budget = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx(overBudget), Def(), default));
        Assert.Contains("at most 512", budget.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(512)]
    public async Task InvalidObjectIndex_IsRejected(int index)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx([new(index, "invalid", "line", false)]), Def(), default));
        Assert.Contains("must be in 0..511", error.Message);
    }

    [Fact]
    public async Task Selection_ObservesCancellationDuringTraversal()
    {
        using var cancellation = new CancellationTokenSource();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new MetrologyResultNode().ExecuteAsync(Ctx(new CancelDuringRead(cancellation)), Def(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed class CancelDuringRead(CancellationTokenSource cancellation) : IReadOnlyList<VisionMetrologyResult>
    {
        public int Count => 2;
        public VisionMetrologyResult this[int index] => new(index, $"object{index}", "line", false);
        public IEnumerator<VisionMetrologyResult> GetEnumerator()
        {
            yield return this[0];
            cancellation.Cancel();
            yield return this[1];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
