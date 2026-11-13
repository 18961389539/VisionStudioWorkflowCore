using System.Net;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchMThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task FlatField_EmitsDiagnosticsAndRejectsEmptyOrDepthMismatch()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(60));
        using var dark = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(10));
        using var bright = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(110));
        var ok = await new FlatFieldCorrectNode().ExecuteAsync(Ctx(new()
        {
            ["image"] = Img(source), ["dark"] = Img(dark), ["bright"] = Img(bright)
        }), Def("image.flatFieldCorrect"), CancellationToken.None);
        Assert.Contains("image.flatFieldCorrect", (string)ok.Outputs["diagnostics"].Value!);
        Assert.True((double)ok.Outputs["gainMean"].Value! > 0);
        Assert.True(ok.Outputs.ContainsKey("correctedMean"));

        using var empty = new Mat(0, 0, MatType.CV_8UC1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FlatFieldCorrectNode().ExecuteAsync(Ctx(new()
            {
                ["image"] = Img(empty), ["dark"] = Img(dark), ["bright"] = Img(bright)
            }), Def("image.flatFieldCorrect"), CancellationToken.None));

        using var depth = new Mat(16, 16, MatType.CV_16UC1, Scalar.All(1000));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FlatFieldCorrectNode().ExecuteAsync(Ctx(new()
            {
                ["image"] = Img(source), ["dark"] = Img(dark), ["bright"] = VisionValue.Image(VisionImage.Own(depth.Clone()))
            }), Def("image.flatFieldCorrect"), CancellationToken.None));
    }

    [Fact]
    public async Task SaveImage_ValidatesExtensionAndEmitsDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-batchm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var source = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(42));
            var path = Path.Combine(dir, "out.png");
            var result = await new SaveImageNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
                Def("output.saveImage", JsonSerializer.Serialize(new { path })), CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.Contains("output.saveImage", (string)result.Outputs["diagnostics"].Value!);
            Assert.Contains(".png", (string)result.Outputs["diagnostics"].Value!);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new SaveImageNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
                    Def("output.saveImage", JsonSerializer.Serialize(new { path = Path.Combine(dir, "x.xyz") })), CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TextRegex_EmitsGroupCountDiagnosticsAndRejectsEmptyPattern()
    {
        var result = await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("LOT-20261018-A01") }),
            Def("text.regex", """{"pattern":"LOT-(?<date>[0-9]{8})-(?<batch>[A-Z][0-9]{2})"}"""), CancellationToken.None);
        Assert.True((bool)result.Outputs["pass"].Value!);
        Assert.True((long)result.Outputs["groupCount"].Value! >= 2);
        Assert.Contains("text.validate", (string)result.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("ABC") }),
                Def("text.regex", """{"pattern":"   "}"""), CancellationToken.None));
    }

    [Fact]
    public async Task SyncCapture_ValidatesParamsPartialCaptureAndToleranceGate()
    {
        using var image = VisionImage.Own(new Mat(4, 4, MatType.CV_8UC1));
        using var set = new VisionFrameSet(
            "sync-1", "device-ptp", 8.25, false,
            new Dictionary<string, IVisionImage> { ["a"] = image },
            new Dictionary<string, VisionFrameSetFrameInfo> { ["a"] = new("a", 7, DateTimeOffset.UtcNow, 100, 9, "Mono8") });
        var executor = new SynchronizedCaptureNode(new FakeSync(set));
        var ok = await executor.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1"}"""), CancellationToken.None);
        Assert.Contains("camera.syncCapture", (string)ok.Outputs["diagnostics"].Value!);
        Assert.False((bool)ok.Outputs["withinTolerance"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1","requireWithinTolerance":true}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1","minCameraCount":2}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"  "}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1","frameTimeoutMs":1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ContourProperties_ExposesScalarPortsAndRejectsBadSelect()
    {
        using var image = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(10, 10, 40, 30), Scalar.All(255), -1);
        var extracted = await new ContourExtractNode().ExecuteAsync(Ctx(new() { ["image"] = Img(image) }),
            Def("contour.extract", """{"mode":"external"}"""), CancellationToken.None);
        var contours = extracted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var result = await new ContourPropertiesNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
            Def("contour.properties", """{"selectBy":"largest"}"""), CancellationToken.None);
        Assert.True(result.Outputs.ContainsKey("solidity"));
        Assert.True(result.Outputs.ContainsKey("aspectRatio"));
        Assert.Contains("contour.properties", (string)result.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourPropertiesNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
                Def("contour.properties", """{"selectBy":"bogus"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourPropertiesNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
                Def("contour.properties", """{"selectBy":"index","index":99}"""), CancellationToken.None));
    }

    [Fact]
    public async Task LineSpacing_ToleranceGateOverlaysAndDiagnostics()
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Line(new(new(0, 0), new(10, 0))),
            ["b"] = VisionValue.Line(new(new(0, 5), new(10, 5)))
        };
        var result = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", """{"sampleCount":3,"nominalDistance":5,"tolerance":0.5}"""), CancellationToken.None);
        Assert.True((bool)result.Outputs["withinTolerance"].Value!);
        Assert.Equal(5d, (double)result.Outputs["distance"].Value!, 6);
        Assert.Contains("measure.lineSpacing", (string)result.Outputs["diagnostics"].Value!);
        Assert.NotNull(result.Overlays); Assert.True(result.Overlays.Count >= 2);

        var fail = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", """{"sampleCount":3,"nominalDistance":5,"tolerance":0.1}"""), CancellationToken.None);
        // mean is exactly 5 so 0.1 still passes — use tighter nominal miss
        fail = await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
            Def("measure.lineSpacing", """{"sampleCount":3,"nominalDistance":8,"tolerance":0.5}"""), CancellationToken.None);
        Assert.False((bool)fail.Outputs["withinTolerance"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new LineSpacingNode().ExecuteAsync(Ctx(inputs),
                Def("measure.lineSpacing", """{"nominalDistance":5,"tolerance":0}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ExportAndHttp_DiagnosticsRetriesAndPayloadGate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-batchm-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "r.json");
            var exp = await new ExportResultNode().ExecuteAsync(Ctx(new() { ["value"] = VisionValue.Integer(42) }),
                Def("output.exportResult", JsonSerializer.Serialize(new { path, format = "json" })), CancellationToken.None);
            Assert.Contains("output.exportResult", (string)exp.Outputs["diagnostics"].Value!);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new ExportResultNode().ExecuteAsync(Ctx(new() { ["value"] = VisionValue.String(new string('x', 200)) }),
                    Def("output.exportResult", JsonSerializer.Serialize(new { path = Path.Combine(dir, "big.json"), format = "json", maxBytes = 64 })), CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }

        var attempts = 0;
        var flaky = new HttpRequestNode(new HttpClient(new StubHandler((_, _) =>
        {
            attempts++;
            if (attempts < 3) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("recovered") });
        })));
        var http = await flaky.ExecuteAsync(Ctx(new()), Def("output.httpRequest", """{"url":"https://localhost/","retries":3}"""), CancellationToken.None);
        Assert.Equal("recovered", http.Outputs["body"].Value);
        Assert.Equal(3L, (long)http.Outputs["attempts"].Value!);
        Assert.Contains("output.httpRequest", (string)http.Outputs["diagnostics"].Value!);
    }

    private sealed class FakeSync(VisionFrameSet frameSet) : ISynchronizedFrameSetService
    {
        public Task<VisionFrameSet> CaptureAsync(SynchronizedFrameSetCaptureRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(frameSet);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

