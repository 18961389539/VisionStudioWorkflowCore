using System.Net;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class Batch2UtilityNodeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(40)]
    public async Task Delay_CompletesAndHonorsCancellation(int delayMs)
    {
        var node = new DelayNode();
        var result = await Run(node, new(), JsonSerializer.Serialize(new { delayMs }));
        // elapsedMs 报告实测耗时：必须覆盖请求值，同时不能被当成请求值原样回显。
        var elapsed = (double)result.Outputs["elapsedMs"].Value!;
        Assert.True(elapsed >= delayMs, $"elapsedMs {elapsed} should cover the requested {delayMs}ms");
        Assert.True(elapsed < 5000, $"elapsedMs {elapsed} should stay near the requested delay");
        Assert.Equal(delayMs, (int)result.Summary["requestedMs"]!);
        Assert.Equal(Math.Round(elapsed, 3), (double)result.Summary["elapsedMs"]!);
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await node.ExecuteAsync(new(new Dictionary<string, VisionValue>()), Definition(node.Type, "{\"delayMs\":600000}"), cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(node, new(), "{\"delayMs\":600001}"));
    }

    [Fact]
    public async Task BooleanIfAndSwitch_ReturnDecisionAndRejectMalformedCases()
    {
        var ifNode = new BooleanIfConditionNode();
        var ifResult = await Run(ifNode, new() { ["condition"] = VisionValue.Boolean(false) }, "{}");
        Assert.False((bool)ifResult.Outputs["condition"].Value!);
        var switchNode = new SwitchNode();
        var selected = await Run(switchNode, new() { ["value"] = VisionValue.String("NG") }, "{\"casesJson\":\"[\\\"OK\\\",\\\"NG\\\"]\"}");
        Assert.Equal("case2", selected.Outputs["selected"].Value);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(switchNode, new() { ["value"] = VisionValue.String("x") }, "{\"casesJson\":\"bad\"}"));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"A\",null]")]
    [InlineData("[\"A\",\"A\"]")]
    [InlineData("[\"A\",\" \"]")]
    public async Task Switch_RejectsExplicitInvalidCasesInsteadOfUsingDefaults(string cases)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(new SwitchNode(),
            new() { ["value"] = VisionValue.String("OK") }, JsonSerializer.Serialize(new { casesJson = cases })));
    }

    [Theory]
    [InlineData("A", "case1")]
    [InlineData("a", "case2")]
    [InlineData(" A", "default")]
    public async Task Switch_PreservesCaseSensitiveUntrimmedLabelMatching(string value, string expectedPort)
    {
        var result = await Run(new SwitchNode(), new() { ["value"] = VisionValue.String(value) },
            JsonSerializer.Serialize(new { casesJson = "[\"A\",\"a\"]" }));
        Assert.Equal(expectedPort, result.Outputs["selected"].Value);
    }

    [Fact]
    public async Task SaveImage_EncodesRealImageAndRejectsUnsupportedExtension()
    {
        var dir = TempDir();
        using var source = new Mat(12, 14, MatType.CV_8UC1, Scalar.All(123));
        var node = new SaveImageNode();
        var path = Path.Combine(dir, "nested", "frame.png");
        var result = await Run(node, new() { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) }, JsonSerializer.Serialize(new { path }));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal(new FileInfo(path).Length, (long)result.Outputs["bytesWritten"].Value!);
        await Assert.ThrowsAsync<IOException>(async () => await Run(node, new() { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) }, JsonSerializer.Serialize(new { path })));
        await Assert.ThrowsAnyAsync<Exception>(async () => await Run(node, new() { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) }, JsonSerializer.Serialize(new { path = Path.Combine(dir, "bad.ext") })));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExportResult_WritesTypedValue(string format)
    {
        var dir = TempDir(); var path = Path.Combine(dir, "nested", "result." + format);
        var result = await Run(new ExportResultNode(), new() { ["value"] = VisionValue.String("a,\"b\"") }, JsonSerializer.Serialize(new { path, format }));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("String", text);
        Assert.Contains("a,", text);
        Assert.True((long)result.Outputs["bytesWritten"].Value! > 0);
        await Assert.ThrowsAsync<IOException>(async () => await Run(new ExportResultNode(), new() { ["value"] = VisionValue.String("second") }, JsonSerializer.Serialize(new { path, format })));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HttpRequest_HandlesSuccessNonSuccessTimeoutAndCallerCancellation()
    {
        var success = new HttpRequestNode(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }))));
        var result = await Run(success, new(), "{\"url\":\"https://localhost/\"}");
        Assert.Equal("ok", result.Outputs["body"].Value);

        var rejected = new HttpRequestNode(new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () => await Run(rejected, new(), "{\"url\":\"https://localhost/\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);

        var delayed = new HttpRequestNode(new HttpClient(new StubHandler(async (_, token) => { await Task.Delay(3000, token); return new HttpResponseMessage(HttpStatusCode.OK); })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Run(delayed, new(), "{\"url\":\"https://localhost/\",\"timeoutMs\":20}"));
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delayed.ExecuteAsync(new(new Dictionary<string, VisionValue>()), Definition(delayed.Type, "{\"url\":\"https://localhost/\",\"timeoutMs\":10000}"), cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(success, new(), "{\"url\":\"file:///tmp/x\"}"));
    }

    [Fact]
    public async Task HttpRequest_EnforcesResponseBudgetAndRespectsCharset()
    {
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("检测完成", System.Text.Encoding.Unicode) })));
        var executor = new HttpRequestNode(client);
        var result = await Run(executor, new(), "{\"url\":\"https://localhost/\",\"maxResponseBytes\":100}");
        Assert.Equal("检测完成", result.Outputs["body"].Value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(executor, new(), "{\"url\":\"https://localhost/\",\"maxResponseBytes\":3}"));
        Assert.Contains("maxResponseBytes=3", error.Message);
    }

    [Fact]
    public void Catalog_DeclaresWorkflowCoreOnlyIoAndNoHardwareActions()
    {
        Assert.Equal(10, Batch2UtilityCatalog.Items.Count);
        Assert.All(Batch2UtilityCatalog.Items, item => Assert.Empty(item.Actions!));
        Assert.All(Batch2UtilityCatalog.Items, item => Assert.Equal(VisionExecutionMode.WorkflowCore, item.Capabilities!.ExecutionMode));
        Assert.False(Batch2UtilityCatalog.Items.Single(x => x.Type == "output.saveImage").Capabilities!.SupportsRunNode);
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string json)
    {
        try { return await executor.ExecuteAsync(new NodeExecutionContext(inputs), Definition(executor.Type, json), CancellationToken.None); }
        finally { foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose(); }
    }

    private static NodeDefinition Definition(string type, string json) => new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static string TempDir() { var path = Path.Combine(Path.GetTempPath(), "vs-batch2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
