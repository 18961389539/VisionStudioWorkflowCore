using System.Net;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Dedicated P0 regression coverage for Output node safety contracts.</summary>
public sealed class OutputP0SafetyTests
{
    [Fact]
    public async Task HttpPostTimeoutWithoutIdempotencyKeySendsOnceAndReportsSuppression()
    {
        var handler = new StubHandler(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var node = new HttpRequestNode(new HttpClient(handler));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(node, "output.httpRequest",
            new Dictionary<string, VisionValue>(), """{"url":"http://plant.local/write","method":"POST","retries":3,"retryWriteRequests":true,"timeoutMs":20}"""));

        Assert.Equal(1, handler.Requests);
        Assert.Contains("attempts=1", error.Message);
        var diagnostics = JsonDocument.Parse(Assert.IsType<string>(error.Data["diagnostics"]));
        Assert.Equal(1, diagnostics.RootElement.GetProperty("attemptCount").GetInt32());
        Assert.True(diagnostics.RootElement.GetProperty("retrySuppressed").GetBoolean());
        Assert.Equal("missing-idempotency-key", diagnostics.RootElement.GetProperty("retrySuppressedReason").GetString());
    }

    [Fact]
    public async Task HttpPatchIsSupportedButDoesNotRetryWithoutIdempotencyKey()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var node = new HttpRequestNode(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Run(node, "output.httpRequest",
            new Dictionary<string, VisionValue>(), """{"url":"http://plant.local/write","method":"PATCH","idempotencyKey":"operation-1","retries":3,"retryDelayMs":0}"""));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(1, Assert.IsType<int>(error.Data["attempts"]));
        var diagnostics = JsonDocument.Parse(Assert.IsType<string>(error.Data["diagnostics"]));
        Assert.Equal("write-retry-disabled", diagnostics.RootElement.GetProperty("retrySuppressedReason").GetString());
    }

    [Fact]
    public async Task NotifyRedactsUrlTokenSmtpPasswordAndCredentialShapedResponseValues()
    {
        const string response = """{"token":"value-secret","password":"smtp-secret","authorization":"Bearer bearer-secret","echo":"http://user:pass@plant.local/x"}""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        }));
        var result = await Run(new NotifyNode(new HttpClient(handler)), "output.notify",
            new Dictionary<string, VisionValue>
            {
                ["message"] = VisionValue.String("alarm"),
                ["value"] = VisionValue.String("""{"password":"value-secret"}""")
            }, """{"channel":"webhook","url":"http://plant.local/alarm?key=url-secret","smtpPassword":"smtp-secret"}""");

        var observable = result.Outputs["responseBody"].Require<string>("responseBody")
            + result.Outputs["diagnostics"].Require<string>("diagnostics");
        Assert.DoesNotContain("url-secret", observable, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-secret", observable, StringComparison.Ordinal);
        Assert.DoesNotContain("value-secret", observable, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", observable, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", observable, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", observable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyFailureDoesNotEchoCredentialBearingResponseOrUrl()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"token":"url-secret","password":"smtp-secret"}""")
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new NotifyNode(new HttpClient(handler)), "output.notify",
            new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("alarm") },
            """{"channel":"webhook","url":"http://plant.local/alarm?key=url-secret","smtpPassword":"smtp-secret","retries":3,"retryDelayMs":0}"""));

        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain("url-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-secret", error.Message, StringComparison.Ordinal);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyRejectsNonHttpProtocolBeforeSending()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new NotifyNode(new HttpClient(handler)), "output.notify",
            new Dictionary<string, VisionValue> { ["message"] = VisionValue.String("alarm") },
            """{"channel":"webhook","url":"ftp://plant.local/alarm"}"""));

        Assert.Contains("absolute http or https", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("..\\escape.png")]
    [InlineData("nested/../../escape.png")]
    public async Task SaveImageRejectsWiredParentTraversalBeforeCreatingDirectories(string wiredPath)
    {
        var root = NewTempDirectory();
        try
        {
            var node = new SaveImageNode();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunImage(node, root, wiredPath));
            Assert.Contains("..", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveImageRejectsWiredAbsolutePathBeforeWriting()
    {
        var root = NewTempDirectory();
        try
        {
            var outside = Path.Combine(root, "outside.png");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunImage(new SaveImageNode(), root, outside));
            Assert.Contains("相对路径", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outside));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveImageResolvesSafeWiredPathBeneathConfiguredArchiveRoot()
    {
        var root = NewTempDirectory();
        try
        {
            var result = await RunImage(new SaveImageNode(), root, "nested/actual.png");
            var expected = Path.Combine(root, "archive", "nested", "actual.png");
            Assert.Equal(expected, result.Outputs["path"].Require<string>("path"));
            Assert.True(File.Exists(expected));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveImageRejectsPartIdTraversalUsedByTemplate()
    {
        var root = NewTempDirectory();
        try
        {
            using var image = NewImage();
            var definition = Definition("output.saveImage", JsonSerializer.Serialize(new
            {
                path = Path.Combine(root, "archive", "base.png"),
                namingMode = "template",
                filenameTemplate = "{partId}"
            }));
            var context = ImageContext(image, new Dictionary<string, VisionValue>
            {
                ["partId"] = VisionValue.String("../escape")
            });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SaveImageNode().ExecuteAsync(context, definition, CancellationToken.None).AsTask());
            Assert.Contains("..", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SaveImageConflictFailureLeavesExistingBytesUntouched()
    {
        var root = NewTempDirectory();
        try
        {
            var configured = Path.Combine(root, "archive", "base.png");
            Directory.CreateDirectory(Path.GetDirectoryName(configured)!);
            await File.WriteAllBytesAsync(configured, [1, 2, 3, 4]);
            using var image = NewImage();
            var definition = Definition("output.saveImage", JsonSerializer.Serialize(new { path = configured, conflictPolicy = "fail" }));

            await Assert.ThrowsAsync<IOException>(() =>
                new SaveImageNode().ExecuteAsync(ImageContext(image), definition, CancellationToken.None).AsTask());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(configured));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<NodeExecutorResult> RunImage(SaveImageNode node, string root, string wiredPath)
    {
        using var image = NewImage();
        var definition = Definition("output.saveImage", JsonSerializer.Serialize(new { path = Path.Combine(root, "archive", "base.png") }));
        var context = ImageContext(image, new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(wiredPath) });
        return await node.ExecuteAsync(context, definition, CancellationToken.None);
    }

    private static Mat NewImage() => new(8, 8, MatType.CV_8UC1, Scalar.All(42));

    private static NodeExecutionContext ImageContext(Mat image, Dictionary<string, VisionValue>? inputs = null)
    {
        inputs ??= new Dictionary<string, VisionValue>();
        inputs["image"] = VisionValue.Image(VisionImage.Own(image.Clone()));
        return new NodeExecutionContext(inputs);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vision-output-p0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task<NodeExecutorResult> Run(IVisionNodeExecutor node, string type, Dictionary<string, VisionValue> inputs, string json)
        => node.ExecuteAsync(new NodeExecutionContext(inputs), Definition(type, json), CancellationToken.None).AsTask();

    private static NodeDefinition Definition(string type, string json)
        => new("node-1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return respond(cancellationToken);
        }
    }
}
