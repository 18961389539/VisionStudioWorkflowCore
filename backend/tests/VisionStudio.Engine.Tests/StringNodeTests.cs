using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// value.string（字符串加工）行为测试：覆盖模板、拼接、数字互转、子串、裁剪、替换、补齐与失败路径。
/// 约定：非法**配置**必须抛错；越界的**运行时**子串必须裁剪并置 truncated=true。
/// </summary>
public sealed class StringNodeTests
{
    /// <summary>用字典构造节点参数，避免在测试里手拼 JSON 字符串。</summary>
    private static NodeDefinition Def(string op, params (string Key, object Value)[] parameters)
    {
        var payload = new Dictionary<string, object> { ["op"] = op };
        foreach (var (key, value) in parameters) payload[key] = value;
        return new NodeDefinition("n1", "value.string", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }

    private static NodeExecutionContext Ctx(params (string Port, VisionValue Value)[] inputs)
        => new(inputs.ToDictionary(item => item.Port, item => item.Value, StringComparer.OrdinalIgnoreCase));

    private static VisionValue Str(string value) => VisionValue.String(value);

    private static async Task<NodeExecutorResult> Run(NodeDefinition definition, NodeExecutionContext context)
        => await new ValueStringNode().ExecuteAsync(context, definition, CancellationToken.None);

    private static string Text(NodeExecutorResult result) => result.Outputs["textValue"].Require<string>("textValue");

    [Fact]
    public async Task Template_ResolvesNamedTokensAliasesAndEscapes()
    {
        var result = await Run(
            Def("template", ("template", "SN:{a}-{{OK}}-{1}-{value}")),
            Ctx(("a", Str("A100")), ("b", Str("12")), ("value", VisionValue.Double(1.5))));

        Assert.Equal("SN:A100-{OK}-12-1.5", Text(result));
        Assert.Equal(19L, result.Outputs["charCount"].Require<long>("charCount"));
        Assert.False(result.Outputs["truncated"].Require<bool>("truncated"));
    }

    [Fact]
    public async Task Template_UnconnectedPartsBecomeEmptyAndUnknownTokensFail()
    {
        var ok = await Run(Def("template", ("template", "{a}{c}")), Ctx(("a", Str("x"))));
        Assert.Equal("x", Text(ok));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("template", ("template", "{nope}")), Ctx(("a", Str("x")))));

        // 引用 {value} 但未接线时必须报错，否则会静默产出缺字段的上报串。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("template", ("template", "{value}")), Ctx()));
    }

    [Fact]
    public async Task Join_SkipsEmptyParts()
    {
        var result = await Run(
            Def("join", ("separator", "-")),
            Ctx(("a", Str("SN")), ("c", Str("OK"))));

        Assert.Equal("SN-OK", Text(result));
    }

    [Fact]
    public async Task NumberToString_UsesInvariantFormatAndRejectsBadFormat()
    {
        var result = await Run(Def("numbertostring", ("format", "F3")), Ctx(("value", VisionValue.Double(2.5))));
        Assert.Equal("2.500", Text(result));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("numbertostring", ("format", "F99999999999999999999")), Ctx(("value", VisionValue.Double(2.5)))));
    }

    [Fact]
    public async Task ToNumber_ParsesTrimmedTextAndEmitsOnlyThenNumberPort()
    {
        var parsed = await Run(Def("tonumber"), Ctx(("a", Str("  -42.5 "))));
        Assert.Equal(-42.5, parsed.Outputs["numberValue"].Require<double>("numberValue"));
        Assert.Equal("  -42.5 ", Text(parsed));

        // 其它运算不应输出 numberValue（0 会被误读成"转换结果是 0"）。
        var joined = await Run(Def("join"), Ctx(("a", Str("x"))));
        Assert.False(joined.Outputs.ContainsKey("numberValue"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Def("tonumber"), Ctx(("a", Str("OK")))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Def("tonumber"), Ctx(("a", Str("   ")))));
    }

    [Fact]
    public async Task Substring_ClampsOutOfRangeAndFlagsTruncated()
    {
        var inside = await Run(Def("substring", ("startIndex", 2), ("takeLength", 3)), Ctx(("a", Str("ABCDEFG"))));
        Assert.Equal("CDE", Text(inside));
        Assert.False(inside.Outputs["truncated"].Require<bool>("truncated"));

        var remainder = await Run(Def("substring", ("startIndex", 4), ("takeLength", 0)), Ctx(("a", Str("ABCDEFG"))));
        Assert.Equal("EFG", Text(remainder));

        var clamped = await Run(Def("substring", ("startIndex", 5), ("takeLength", 99)), Ctx(("a", Str("ABC"))));
        Assert.Equal(string.Empty, Text(clamped));
        Assert.True(clamped.Outputs["truncated"].Require<bool>("truncated"));
    }

    [Fact]
    public async Task TrimReplacePadUpperCaseAndLower_FollowDocumentedSemantics()
    {
        var trimmed = await Run(Def("trim", ("trimSide", "start")), Ctx(("a", Str("  ab  "))));
        Assert.Equal("ab  ", Text(trimmed));

        var replacedOnce = await Run(
            Def("replace", ("find", "xx"), ("replaceWith", "y"), ("replaceAll", false)),
            Ctx(("a", Str("xx-xx"))));
        Assert.Equal("y-xx", Text(replacedOnce));

        // 正则模式 + 忽略大小写；replaceWith 按字面量写入，不做 $1 反向引用展开。
        var regexReplaced = await Run(
            Def("replace", ("find", "a+"), ("replaceWith", "$1"), ("useRegex", true), ("ignoreCase", true)),
            Ctx(("a", Str("AA-b"))));
        Assert.Equal("$1-b", Text(regexReplaced));

        var padded = await Run(Def("pad", ("padWidth", 5), ("padChar", "0"), ("padSide", "left")), Ctx(("a", Str("42"))));
        Assert.Equal("00042", Text(padded));

        var notTruncated = await Run(Def("pad", ("padWidth", 2), ("padChar", "0")), Ctx(("a", Str("1234"))));
        Assert.Equal("1234", Text(notTruncated));

        Assert.Equal("AB", Text(await Run(Def("upper"), Ctx(("a", Str("ab"))))));
        Assert.Equal("ab", Text(await Run(Def("lower"), Ctx(("a", Str("AB"))))));
        Assert.Equal(3L, (await Run(Def("length"), Ctx(("a", Str("abc"))))).Outputs["charCount"].Require<long>("charCount"));
    }

    [Fact]
    public async Task InvalidConfigurationFailsExplicitly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("replace", ("find", string.Empty)), Ctx(("a", Str("x")))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("pad", ("padWidth", 5), ("padChar", "00")), Ctx(("a", Str("x")))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("nonsense"), Ctx(("a", Str("x")))));
    }

    [Fact]
    public async Task MaxLength_IsEnforcedInsteadOfSilentTruncation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Def("pad", ("padWidth", 32), ("padChar", "x"), ("maxLength", 16)), Ctx(("a", Str("a")))));
    }

    [Fact]
    public void Catalog_DeclaresStringNodeWithMatchingPorts()
    {
        var item = BuiltInNodeCatalog.Items.Single(entry => entry.Type == "value.string");
        Assert.Equal("Value", item.Category);
        Assert.Contains(item.Inputs, port => port.Name == "a" && port.DataType == VisionDataType.String);
        Assert.Contains(item.Outputs, port => port.Name == "textValue" && port.DataType == VisionDataType.String);
        Assert.Contains(item.Outputs, port => port.Name == "numberValue" && !port.Required);
    }
}
