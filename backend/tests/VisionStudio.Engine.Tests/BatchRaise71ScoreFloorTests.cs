using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Raise exact score-71 nodes: F-port exercise, diagnostics richness, robustness gates, catalog pdesc.
/// </summary>
public sealed class BatchRaise71ScoreFloorTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public async Task ColorConvert_EmitsChannelsAndDiagnostics()
    {
        using var color = new Mat(16, 16, MatType.CV_8UC3, new Scalar(40, 80, 160));
        var result = await new ImageColorConvertNode().ExecuteAsync(Ctx(new() { ["image"] = Img(color) }),
            Def("image.colorConvert", """{"space":"hsv"}"""), CancellationToken.None);
        Assert.True(result.Outputs.ContainsKey("channel1"));
        Assert.True(result.Outputs.ContainsKey("channel2"));
        Assert.True(result.Outputs.ContainsKey("channel3"));
        Assert.Contains("image.colorConvert", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("H", result.Outputs["channelNames"].Require<string>("channelNames"));
        Assert.Contains("\"space\":\"hsv\"", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageColorConvertNode().ExecuteAsync(Ctx(new() { ["image"] = Img(color) }),
                Def("image.colorConvert", """{"space":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ContrastIllumSharpen_EmitIntensityPorts()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(img, new Rect(12, 12, 20, 20), Scalar.All(200), -1);

        var contrast = await new ContrastNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.contrastNormalize", """{"tailPercent":0,"outputMin":0,"outputMax":255}"""), CancellationToken.None);
        Assert.Contains("image.contrastNormalize", contrast.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(contrast.Outputs.ContainsKey("meanIntensity"));
        Assert.True(contrast.Outputs.ContainsKey("inputMin"));
        Assert.True(contrast.Outputs.ContainsKey("inputMax"));

        var illum = await new IlluminationNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.illuminationNormalize", """{"windowSize":15,"targetMean":128,"minBackground":1}"""), CancellationToken.None);
        Assert.Contains("image.illuminationNormalize", illum.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(illum.Outputs["meanIntensity"].Require<double>("meanIntensity") > 0);
        Assert.True(illum.Outputs.ContainsKey("minIntensity"));
        Assert.True(illum.Outputs.ContainsKey("maxIntensity"));

        var sharp = await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.sharpen", """{"windowSize":5,"amount":1.5}"""), CancellationToken.None);
        Assert.Contains("image.sharpen", sharp.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(sharp.Outputs.ContainsKey("meanIntensity"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.sharpen", """{"amount":99}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Difference_EmitsDiagnosticsAndSimilarity()
    {
        using var a = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(50));
        using var b = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(50));
        Cv2.Rectangle(b, new Rect(8, 8, 8, 8), Scalar.All(200), -1);
        var result = await new ImageDifferenceNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(a), ["reference"] = Img(b) }),
            Def("image.difference", """{"mode":"absolute","scale":1,"threshold":20}"""), CancellationToken.None);
        Assert.Contains("image.difference", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(result.Outputs["mean"].Require<double>("mean") >= 0);
        Assert.True(result.Outputs["similarity"].Require<double>("similarity") < 1.0);
        Assert.True(result.Outputs.ContainsKey("aboveThresholdFraction"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageDifferenceNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(a), ["reference"] = Img(b) }),
                Def("image.difference", """{"mode":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ReduceDomain_FoundAndDiagnostics()
    {
        using var img = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(120));
        var region = Rect(5, 5, 20, 15, 40);
        var result = await new ReduceDomainNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["region"] = VisionValue.Region(region) }),
            Def("image.reduceDomain"), CancellationToken.None);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["area"].Require<double>("area") > 0);
        Assert.Contains("image.reduceDomain", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(result.Outputs.ContainsKey("domainImage"));

        var empty = await new ReduceDomainNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["region"] = VisionValue.Region(new VisionRegion(40, 40, Array.Empty<VisionRegionComponent>())) }),
            Def("image.reduceDomain", """{"emptyPolicy":"passthrough"}"""), CancellationToken.None);
        Assert.False(empty.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task Threshold_ManualAdaptiveOtsu_EnrichPorts()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(8, 8, 12, 12), Scalar.All(220), -1);

        var manual = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":128,"invert":false}"""), CancellationToken.None);
        Assert.Equal(128d, manual.Outputs["threshold"].Require<double>("threshold"));
        Assert.Contains("image.threshold", manual.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(manual.Outputs["nonZeroCount"].Require<long>("nonZeroCount") > 0);
        Assert.True(manual.Outputs.ContainsKey("meanIntensity"));

        var otsu = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"otsu"}"""), CancellationToken.None);
        Assert.Contains("\"mode\":\"otsu\"", otsu.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(otsu.Outputs.ContainsKey("meanIntensity"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.threshold", """{"mode":"bogus"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ContourFromPoints_AreaPerimeterDiagnostics()
    {
        var result = await new ContourFromPointsNode().ExecuteAsync(Ctx(new()),
            Def("contour.fromPoints", """{"pointsJson":"[[10,10],[40,10],[40,40],[10,40]]","closed":true}"""), CancellationToken.None);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1L, result.Outputs["count"].Require<long>("count"));
        Assert.Contains("contour.fromPoints", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(Convert.ToDouble(result.Summary["area"]) > 0);
        Assert.True(Convert.ToDouble(result.Summary["perimeter"]) > 0);

        var miss = await new ContourFromPointsNode().ExecuteAsync(Ctx(new()),
            Def("contour.fromPoints", """{"pointsJson":"[[1,1]]","closed":true}"""), CancellationToken.None);
        Assert.False(miss.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task ContourDeviation_WithinToleranceGate()
    {
        var square = await new ContourFromPointsNode().ExecuteAsync(Ctx(new()),
            Def("contour.fromPoints", """{"pointsJson":"[[0,0],[30,0],[30,30],[0,30]]","closed":true}"""), CancellationToken.None);
        var shifted = await new ContourFromPointsNode().ExecuteAsync(Ctx(new()),
            Def("contour.fromPoints", """{"pointsJson":"[[2,0],[32,0],[32,30],[2,30]]","closed":true}"""), CancellationToken.None);
        var ok = await new ContourDeviationNode().ExecuteAsync(
            Ctx(new() { ["contours"] = square.Outputs["contours"], ["referenceContours"] = square.Outputs["contours"] }),
            Def("measure.contourDeviation", """{"sampleStep":2,"maxSamples":500,"maxDeviation":1,"symmetric":true}"""), CancellationToken.None);
        Assert.True(ok.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Contains("withinTolerance", ok.Outputs["diagnostics"].Require<string>("diagnostics"));

        var shiftedResult = await new ContourDeviationNode().ExecuteAsync(
            Ctx(new() { ["contours"] = square.Outputs["contours"], ["referenceContours"] = shifted.Outputs["contours"] }),
            Def("measure.contourDeviation", """{"sampleStep":2,"maxSamples":500,"maxDeviation":0.5,"symmetric":true}"""), CancellationToken.None);
        Assert.False(shiftedResult.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.True(shiftedResult.Outputs["deviation"].Require<double>("deviation") > 0.5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourDeviationNode().ExecuteAsync(
                Ctx(new() { ["contours"] = square.Outputs["contours"], ["referenceContours"] = square.Outputs["contours"] }),
                Def("measure.contourDeviation", """{"maxDeviation":-1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task RegionBooleanFilterMorphFromRange_EnrichDiagnostics()
    {
        var a = Rect(4, 4, 20, 20, 64);
        var b = Rect(14, 14, 20, 20, 64);
        var union = await new RegionBooleanNode().ExecuteAsync(
            Ctx(new() { ["a"] = VisionValue.Region(a), ["b"] = VisionValue.Region(b) }),
            Def("region.boolean", """{"operation":"union"}"""), CancellationToken.None);
        Assert.Contains("region.boolean", union.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("\"operation\":\"union\"", union.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(union.Outputs["found"].Require<bool>("found"));

        var filtered = await new RegionFilterNode().ExecuteAsync(
            Ctx(new() { ["region"] = VisionValue.Region(a) }),
            Def("region.filter", """{"feature":"area","min":10,"max":99999}"""), CancellationToken.None);
        Assert.Contains("region.filter", filtered.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("\"feature\":\"area\"", filtered.Outputs["diagnostics"].Require<string>("diagnostics"));

        var morph = await new RegionMorphologyNode().ExecuteAsync(
            Ctx(new() { ["region"] = VisionValue.Region(a) }),
            Def("region.morphology", """{"operation":"open","kernelWidth":3,"kernelHeight":3}"""), CancellationToken.None);
        Assert.Contains("region.morphology", morph.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("\"operation\":\"open\"", morph.Outputs["diagnostics"].Require<string>("diagnostics"));

        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(img, new Rect(10, 10, 20, 20), Scalar.All(200), -1);
        var range = await new RegionFromRangeNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":150,"maxGray":255}"""), CancellationToken.None);
        Assert.Contains("region.fromRange", range.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Contains("\"mode\":\"gray\"", range.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(range.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task GrayHistogram_PercentilePorts()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(80));
        Cv2.Rectangle(img, new Rect(4, 4, 10, 10), Scalar.All(200), -1);
        var hist = await new GrayHistogramNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.grayHistogram", """{"lowPercent":10,"highPercent":90}"""), CancellationToken.None);
        Assert.True(hist.Outputs["found"].Require<bool>("found"));
        Assert.Contains("measure.grayHistogram", hist.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(hist.Outputs["mean"].Require<double>("mean") > 0);
        Assert.True(hist.Outputs["percentileLow"].Require<double>("percentileLow") <= hist.Outputs["percentileHigh"].Require<double>("percentileHigh"));
    }

    [Fact]
    public async Task TextRegex_DiagnosticsAndEmptyReject()
    {
        var pass = await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("ABC-12") }),
            Def("text.regex", """{"pattern":"^[A-Z]+-\\d+$","fullMatch":true}"""), CancellationToken.None);
        Assert.True(pass.Outputs["pass"].Require<bool>("pass"));
        Assert.Contains("text.regex", pass.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("x") }),
                Def("text.regex", """{"pattern":""}"""), CancellationToken.None));
    }

    [Fact]
    public async Task DeviceReadTag_DiagnosticsAndQualityGate()
    {
        await using var manager = new DeviceManager();
        manager.Register(new FixedQualityDriver("raise71-good", DeviceTagQuality.Good));
        manager.Register(new FixedQualityDriver("raise71-bad", DeviceTagQuality.Bad));
        var node = new DeviceReadTagNode(manager);
        var good = await node.ExecuteAsync(Ctx(new()),
            Def("device.readTag", """{"deviceId":"raise71-good","tagId":"trigger","requireGoodQuality":true}"""), CancellationToken.None);
        Assert.Contains("device.readTag", good.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.Equal("Good", good.Outputs["quality"].Require<string>("quality"));
        Assert.True(good.Outputs.ContainsKey("boolValue"));
        Assert.True(good.Outputs.ContainsKey("numberValue"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(Ctx(new()),
                Def("device.readTag", """{"deviceId":"raise71-bad","tagId":"trigger","requireGoodQuality":true}"""), CancellationToken.None));
    }

    [Fact]
    public async Task RobotCurrentPose_EmitsDiagnostics()
    {
        await using var manager = new RobotManager();
        manager.Register(new VirtualAbbRobotAdapter());
        var node = new RobotCurrentPoseNode(manager);
        var result = await node.ExecuteAsync(Ctx(new()), Def("robot.currentPose", """{"robotId":"virtual-abb-1"}"""), CancellationToken.None);
        Assert.Contains("robot.currentPose", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(result.Outputs.ContainsKey("pose"));
        Assert.True(result.Outputs.ContainsKey("connection"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(Ctx(new()), Def("robot.currentPose", """{"robotId":"  "}"""), CancellationToken.None));
    }

    private sealed class FixedQualityDriver(string id, DeviceTagQuality quality) : IDeviceDriver
    {
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
        public string Id { get; } = id;
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Fixed";
        public string Driver => "FixedQualityDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://fixed";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities { get; } = new();
        public IReadOnlyList<DeviceTagDefinition> Tags { get; } = [];
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _state = DeviceConnectionState.Connected;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _state = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }
        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceTagSample(Id, tagId, DeviceTagDataType.Boolean, true, quality, DateTimeOffset.UtcNow));
        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
