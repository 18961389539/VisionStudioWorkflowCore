using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests.Nodes;

public sealed class ValueP0ImprovementTests
{
    [Theory]
    [InlineData("divide", -1d, 0d)]
    [InlineData("sqrt", -1d, 0d)]
    [InlineData("pow", -4d, 0.5d)]
    [InlineData("pow", 0d, -1d)]
    public async Task Arithmetic_DomainErrorsIgnoreZeroPolicy(string op, double a, double b)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ValueArithmeticNode(),
            new() { ["a"] = VisionValue.Double(a), ["b"] = VisionValue.Double(b) },
            new() { ["op"] = op, ["onNonFinite"] = "zero" }));
        Assert.Contains("errorCode=invalidInput", error.Message);
        Assert.Contains("value.arithmetic", error.Message);
    }

    [Fact]
    public async Task Arithmetic_OverflowUsesZeroPolicyAndErrorCode()
    {
        var result = await Run(new ValueArithmeticNode(),
            new() { ["a"] = VisionValue.Double(double.MaxValue), ["b"] = VisionValue.Double(2) },
            new() { ["op"] = "pow", ["onNonFinite"] = "zero" });
        Assert.Equal(0d, result.Outputs["result"].Require<double>("result"));
        Assert.False(result.Outputs["isFinite"].Require<bool>("isFinite"));
        Assert.Equal("resultOverflow", result.Outputs["errorCode"].Require<string>("errorCode"));
        Assert.Contains("pow", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Range_NonFiniteInputCannotBecomeInvertedPass(double value)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ValueRangeNode(),
            new() { ["value"] = VisionValue.Double(value) }, new() { ["invert"] = true }));
        Assert.Contains("valid=false", error.Message);
    }

    [Fact]
    public async Task Range_FiniteInputEmitsValid()
    {
        var result = await Run(new ValueRangeNode(), new() { ["value"] = VisionValue.Double(5) }, new() { ["min"] = 0, ["max"] = 10 });
        Assert.True(result.Outputs["valid"].Require<bool>("valid"));
    }

    [Fact]
    public async Task String_SubstringAndPadUseCodePointsWithoutSplittingSurrogates()
    {
        var substring = await Run(new ValueStringNode(), new() { ["a"] = VisionValue.String("A😀B") },
            new() { ["op"] = "substring", ["startIndex"] = 1, ["takeLength"] = 1 });
        Assert.Equal("😀", substring.Outputs["textValue"].Require<string>("textValue"));
        Assert.Equal(2L, substring.Outputs["charCount"].Require<long>("charCount"));
        Assert.Equal(1L, substring.Outputs["codePointCount"].Require<long>("codePointCount"));

        var padded = await Run(new ValueStringNode(), new() { ["a"] = VisionValue.String("😀") },
            new() { ["op"] = "pad", ["padWidth"] = 3, ["padChar"] = "🚀" });
        Assert.Equal("🚀🚀😀", padded.Outputs["textValue"].Require<string>("textValue"));
        Assert.Equal(3L, padded.Outputs["codePointCount"].Require<long>("codePointCount"));
    }

    [Fact]
    public async Task JsonParse_RejectsExcessiveDepthAndDuplicatesDuringParsing()
    {
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new JsonParseNode(),
            new() { ["json"] = VisionValue.String("""{"a":{"b":1}}""") }, new() { ["maxDepth"] = 1 }));
        Assert.Contains("errorCode=maxDepthExceeded", depth.Message);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new JsonParseNode(),
            new() { ["json"] = VisionValue.String("""{"a":1,"a":2}""") }, new() { ["duplicatePropertyPolicy"] = "fail" }));
        Assert.Contains("errorCode=duplicateKey", duplicate.Message);
    }

    [Theory]
    [InlineData("first", 1d)]
    [InlineData("last", 2d)]
    public async Task JsonParse_AppliesDuplicateSelectionPolicy(string policy, double expected)
    {
        var result = await Run(new JsonParseNode(), new() { ["json"] = VisionValue.String("""{"a":1,"a":2}""") },
            new() { ["path"] = "a", ["duplicatePropertyPolicy"] = policy });
        Assert.Equal(expected, result.Outputs["number"].Require<double>("number"));
        Assert.Equal("none", result.Outputs["errorCode"].Require<string>("errorCode"));
    }

    private static Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, Dictionary<string, object> parameters)
        => executor.ExecuteAsync(new NodeExecutionContext(inputs), Definition(parameters), CancellationToken.None).AsTask();

    private static NodeDefinition Definition(Dictionary<string, object> parameters)
        => new("n", "test", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters))!);
}
