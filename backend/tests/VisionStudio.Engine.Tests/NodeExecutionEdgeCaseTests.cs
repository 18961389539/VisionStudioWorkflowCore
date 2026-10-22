using System.Collections;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class NodeExecutionEdgeCaseTests
{
    private static NodeDefinition Node(string type, object? parameters = null) => new("edge", type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters ?? new { })));
    private static NodeExecutionContext Inputs(params (string Name, VisionValue Value)[] values)
        => new(values.ToDictionary(value => value.Name, value => value.Value));

    [Theory]
    [InlineData("and", false)]
    [InlineData("and", true)]
    [InlineData("or", false)]
    [InlineData("or", true)]
    [InlineData("xor", false)]
    public async Task BinaryLogic_AlwaysRequiresTheSecondOperand(string op, bool a)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new LogicBooleanNode()
            .ExecuteAsync(Inputs(("a", VisionValue.Boolean(a))), Node("logic.boolean", new { op }), default));
        Assert.Contains("'b'", error.Message);
    }

    [Fact]
    public async Task UnaryLogic_DoesNotReadAnUnusedSecondOperand()
    {
        var result = await new LogicBooleanNode().ExecuteAsync(
            Inputs(("a", VisionValue.Boolean(true)), ("b", VisionValue.String("unused"))), Node("logic.boolean", new { op = "not" }), default);
        Assert.False(result.Outputs["result"].Require<bool>("result"));
    }

    [Fact]
    public async Task RegexValidation_StopsAfterTheFirstMatchAndIgnoresInactiveExpectedText()
    {
        var text = "A" + new string('a', 10_000) + "!";
        var result = await new TextValidationNode().ExecuteAsync(
            Inputs(("text", VisionValue.String(text)), ("expected", VisionValue.String(new string('x', 65_537)))),
            Node("text.validate", new { mode = "regex", pattern = "A|(a+)+$", fullMatch = false, timeoutMs = 100 }), default);

        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        Assert.Equal("A", result.Outputs["matchedText"].Require<string>("matchedText"));
        Assert.Equal("", result.Outputs["expected"].Require<string>("expected"));
    }

    [Fact]
    public async Task RegexMatchCounting_ReportsTimeoutsAfterTheFirstSuccessfulMatch()
    {
        var text = "A" + new string('a', 10_000) + "!";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new TextRegexNode().ExecuteAsync(
            Inputs(("text", VisionValue.String(text))),
            Node("text.regex", new { pattern = "A|(a+)+$", fullMatch = false, timeoutMs = 1 }), default));
        Assert.Contains("Regex validation exceeded 1 ms", error.Message);
    }

    [Theory]
    [InlineData("(?=A)", "AA", 2)]
    [InlineData("A+", "AA BB A", 2)]
    [InlineData("Z", "ABC", 0)]
    public async Task RegexMatchCounting_PreservesZeroWidthAndNoMatchBehavior(string pattern, string text, long expected)
    {
        var result = await new TextRegexNode().ExecuteAsync(Inputs(("text", VisionValue.String(text))),
            Node("text.regex", new { pattern, fullMatch = false, timeoutMs = 1000 }), default);
        Assert.Equal(expected, result.Outputs["matchCount"].Require<long>("matchCount"));
        Assert.Equal(expected > 0, result.Outputs["pass"].Require<bool>("pass"));
    }

    [Theory]
    [InlineData("sortBy")]
    [InlineData("sortBy2")]
    [InlineData("sortBy3")]
    public async Task EmptyDetections_StillRejectInvalidSortFields(string field)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new DetectionSortNode().ExecuteAsync(
            Inputs(("detections", VisionValue.Detections(Array.Empty<VisionDetection>()))),
            Node("collection.detections.sort", new Dictionary<string, object> { [field] = "invalid" }), default));
        Assert.Contains("sortBy", error.Message);
    }

    [Theory]
    [InlineData("collection.detections.filter")]
    [InlineData("collection.detections.sort")]
    [InlineData("collection.detections.at")]
    [InlineData("collection.detections.count")]
    [InlineData("flow.forEach")]
    public async Task DetectionNodes_ObserveCancellationDuringInputValidation(string type)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelDuringEnumeration(cancellation);
        IVisionNodeExecutor executor = type switch
        {
            "collection.detections.filter" => new DetectionFilterNode(),
            "collection.detections.sort" => new DetectionSortNode(),
            "collection.detections.at" => new DetectionAtIndexNode(),
            "collection.detections.count" => new DetectionCountNode(),
            _ => new DetectionForEachNode()
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executor.ExecuteAsync(
            Inputs(("detections", VisionValue.Detections(source))), Node(type), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(source.Reads < source.Count);
    }

    [Fact]
    public async Task DetectionFilter_ObservesCancellationAfterInputValidation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelDuringEnumeration(cancellation, cancelOnEnumeration: 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new DetectionFilterNode().ExecuteAsync(
            Inputs(("detections", VisionValue.Detections(source))), Node("collection.detections.filter"), cancellation.Token));
        Assert.True(source.Reads < source.Count * 2);
    }

    private sealed class CancelDuringEnumeration(CancellationTokenSource cancellation, int cancelOnEnumeration = 1) : IReadOnlyList<VisionDetection>
    {
        private int _enumerations;
        public int Count => 64;
        public int Reads { get; private set; }
        public VisionDetection this[int index] => new(index, index, 0, 10, 10, 100, 40, 0.8,
            new VisionCircle(index, 0, 5), new VisionRectangle2D(index, 0, 10, 10));
        public IEnumerator<VisionDetection> GetEnumerator()
        {
            var enumeration = ++_enumerations;
            for (var i = 0; i < Count; i++)
            {
                Reads++;
                if (enumeration == cancelOnEnumeration && i == 3) cancellation.Cancel();
                yield return this[i];
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
