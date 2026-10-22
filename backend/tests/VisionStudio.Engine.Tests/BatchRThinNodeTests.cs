using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchRThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionContour Contour(params VisionPoint2D[] pts)
        => new(0, pts, -1, false, 0, 0, 0, new(0, 0, 1, 1), new(0, 0, 1));
    private static VisionPoint2D[] CirclePoints(double r, double noise = 0, double arc = 360, int n = 36)
        => Enumerable.Range(0, n).Select(i =>
        {
            var a = Math.Min(360, Math.Max(1, arc)) * i / n * Math.PI / 180;
            var rr = r + (noise == 0 ? 0 : (i % 2 == 0 ? noise : -noise));
            return new VisionPoint2D(50 + rr * Math.Cos(a), 50 + rr * Math.Sin(a));
        }).ToArray();
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);
    private static VisionTransform2D Translation(double x, double y)
        => new("Image", "Image", "px", 1, 0, x, 0, 1, y, 0, 0, 1);

    [Fact]
    public async Task MedianAndFilter_ValidateAndDiagnose()
    {
        using var img = new Mat(24, 24, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Circle(img, new Point(12, 12), 3, Scalar.All(255), -1);
        var median = await new MedianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.blur.median", """{"kernelSize":5}"""), CancellationToken.None);
        Assert.Contains("image.blur.median", (string)median.Outputs["diagnostics"].Value!);

        var filter = await new ImageFilterNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.filter", """{"method":"median","kernelSize":5}"""), CancellationToken.None);
        Assert.Contains("image.filter", (string)filter.Outputs["diagnostics"].Value!);
        Assert.Equal("median", filter.Summary["method"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MedianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.blur.median", """{"kernelSize":2}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageFilterNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.filter", """{"method":"bilateral"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task FromThresholdAndTransform_TypedDiagnostics()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(img, new Rect(4, 4, 10, 10), Scalar.All(200), -1);
        var thr = await new RegionFromThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromThreshold", """{"minGray":100,"maxGray":255}"""), CancellationToken.None);
        Assert.True((bool)thr.Outputs["found"].Value!);
        Assert.Contains("region.fromThreshold", (string)thr.Outputs["diagnostics"].Value!);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new RegionFromThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("region.fromThreshold", """{"minGray":-1,"maxGray":255}"""), CancellationToken.None));

        var region = Rect(2, 2, 8, 8);
        var xf = await new RegionTransformNode().ExecuteAsync(
            Ctx(new() { ["region"] = VisionValue.Region(region), ["transform"] = VisionValue.Transform(Translation(1, 0)) }),
            Def("region.transform"), CancellationToken.None);
        Assert.Contains("region.transform", (string)xf.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public async Task MorphologyShapeAndHistogram_Harden()
    {
        using var img = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(img, new Rect(15, 15, 6, 6), Scalar.All(220), -1);
        var top = await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.topHat", """{"kernelWidth":9,"kernelHeight":9}"""), CancellationToken.None);
        Assert.Contains("image.topHat", (string)top.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageTopHatNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.topHat", """{"kernelWidth":99,"kernelHeight":99}"""), CancellationToken.None));

        var region = Rect(4, 4, 16, 12);
        var skel = await new RegionSkeletonNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.skeleton"), CancellationToken.None);
        Assert.Contains("region.skeleton", (string)skel.Outputs["diagnostics"].Value!);

        var hist = await new GrayHistogramNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.grayHistogram", """{"lowPercent":10,"highPercent":90}"""), CancellationToken.None);
        Assert.True((bool)hist.Outputs["found"].Value!);
        Assert.Contains("measure.grayHistogram", (string)hist.Outputs["diagnostics"].Value!);
        Assert.True(hist.Outputs.ContainsKey("mean"));
    }

    [Fact]
    public async Task RoundnessStraightness_ToleranceGate()
    {
        var round = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20))]) }),
            Def("measure.roundness", """{"maxDeviation":0.5}"""), CancellationToken.None);
        Assert.True((bool)round.Outputs["found"].Value!);
        Assert.True((bool)round.Outputs["withinTolerance"].Value!);

        var noisy = await new RoundnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(CirclePoints(20, noise: 2))]) }),
            Def("measure.roundness", """{"maxDeviation":0.01}"""), CancellationToken.None);
        Assert.True((bool)noisy.Outputs["found"].Value!);
        Assert.False((bool)noisy.Outputs["withinTolerance"].Value!);

        var straight = await new StraightnessNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours([Contour(
                new(0, 0), new(1, 0.01), new(2, -0.01), new(3, 0.01), new(4, 0))]) }),
            Def("measure.straightness", """{"maxDeviation":1}"""), CancellationToken.None);
        Assert.True((bool)straight.Outputs["found"].Value!);
        Assert.True((bool)straight.Outputs["withinTolerance"].Value!);
        Assert.True((double)straight.Outputs["length"].Value! > 0);
    }

    [Fact]
    public async Task Point3DMagnitude_And_ReadTagQualityGate()
    {
        var point = await new CoordinatePoint3DNode().ExecuteAsync(
            Ctx(new()), Def("coordinate.point3D", """{"x":3,"y":4,"z":12,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal(13d, (double)point.Outputs["magnitude"].Value!, 9);
        Assert.Contains("coordinate.point3D", (string)point.Outputs["diagnostics"].Value!);

        await using var manager = new DeviceManager();
        manager.Register(new FixedQualityDriver("batch-r-good", DeviceTagQuality.Good));
        manager.Register(new FixedQualityDriver("batch-r-bad", DeviceTagQuality.Bad));
        var node = new DeviceReadTagNode(manager);
        var good = await node.ExecuteAsync(Ctx(new()),
            Def("device.readTag", """{"deviceId":"batch-r-good","tagId":"trigger","requireGoodQuality":true}"""), CancellationToken.None);
        Assert.Contains("device.readTag", (string)good.Outputs["diagnostics"].Value!);
        Assert.Equal("Good", good.Outputs["quality"].Value);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(Ctx(new()),
                Def("device.readTag", """{"deviceId":"batch-r-bad","tagId":"trigger","requireGoodQuality":true}"""), CancellationToken.None));
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
