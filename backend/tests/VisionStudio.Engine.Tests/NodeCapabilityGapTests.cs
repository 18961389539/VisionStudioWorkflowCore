using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class NodeCapabilityGapTests
{
    [Fact]
    public async Task TemplateMatch_ReturnsEverySeparatedCopy_AndKeepsTheBestSingleResult()
    {
        using var template = Template();
        using var scene = new Mat(220, 240, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 30);
        Paste(scene, template, 140, 150);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minCenterDistance":20,"minScore":0.8}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(2, detections.Count);
        Assert.Contains(detections, detection => Math.Abs(detection.X - 20) < 3 && Math.Abs(detection.Y - 30) < 3);
        Assert.Contains(detections, detection => Math.Abs(detection.X - 140) < 3 && Math.Abs(detection.Y - 150) < 3);
        Assert.Equal(1d, result.Outputs["scale"].Require<double>("scale"), 3);
        Assert.True(result.Outputs.ContainsKey("x"));
    }

    [Fact]
    public async Task TemplateMatch_SearchRoi_KeepsOnlyTheCopyInsideTheRegion()
    {
        using var template = Template();
        using var scene = new Mat(220, 240, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 30);
        Paste(scene, template, 140, 150);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minScore":0.8,"searchRoi":{"type":"Rectangle","x":10,"y":20,"width":60,"height":60}}""");

        Assert.Equal(1L, result.Outputs["count"].Require<long>("count"));
        Assert.InRange(result.Outputs["x"].Require<double>("x"), 17, 23);
        Assert.InRange(result.Outputs["y"].Require<double>("y"), 27, 33);
    }

    [Fact]
    public async Task TemplateMatch_DiscreteScale_FindsAResampledCopy()
    {
        using var template = Template();
        using var scaled = new Mat();
        Cv2.Resize(template, scaled, new Size(26, 26), 0, 0, InterpolationFlags.Area);
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, scaled, 40, 50);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"scaleMinPercent":70,"scaleMaxPercent":90,"scaleStepPercent":10,"minScore":0.6,"angleRangeDeg":0}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0.8d, result.Outputs["scale"].Require<double>("scale"), 3);
        Assert.Equal("discrete-resampled-ncc", result.Summary["scaleSearch"]);
        Assert.InRange(result.Outputs["x"].Require<double>("x"), 36, 44);
    }

    [Fact]
    public async Task TemplateMatch_FullMaskStillMatches_AndTinyMaskIsRejected()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 30, 40);
        var masked = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"templateRoi":{"type":"Rectangle","x":0,"y":0,"width":32,"height":32}}""");
        Assert.True(masked.Outputs["found"].Require<bool>("found"));
        Assert.Equal(true, masked.Summary["templateMask"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Run(new TemplateMatchNode(), scene, template,
            """{"templateRoi":{"type":"Rectangle","x":0,"y":0,"width":2,"height":2}}"""));
        Assert.Contains("16 pixels", error.Message);
    }

    [Fact]
    public async Task TemplateMatch_OversizedTemplateStillFailsAtUnitScale()
    {
        using var template = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(255));
        using var scene = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template, """{"minScore":0.5}"""));
        Assert.Contains("larger than the scene", error.Message);
    }

    [Fact]
    public async Task WaitTag_TimeoutCanFailOrContinue_AndEdgesNeedAPriorLevel()
    {
        await using var manager = new DeviceManager();
        var rising = new ScriptedDriver("gap-rising", index => Sample("gap-rising", index switch
        {
            0 => false,
            _ => true
        }));
        manager.Register(rising);
        var node = new DeviceWaitTagNode(manager);
        var rose = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-rising","tagId":"trigger","triggerMode":"Rising","timeoutMs":400,"pollMs":10,"stableMs":0}"""),
            CancellationToken.None);
        Assert.True(rose.Outputs["matched"].Require<bool>("matched"));

        var level = new ScriptedDriver("gap-level", _ => Sample("gap-level", false));
        manager.Register(level);
        var failed = await Assert.ThrowsAsync<TimeoutException>(async () => await node.ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-level","tagId":"trigger","timeoutMs":50,"pollMs":10}"""),
            CancellationToken.None));
        Assert.Contains("Timed out waiting for device 'gap-level' tag 'trigger'", failed.Message);

        var continued = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-level","tagId":"trigger","timeoutMs":50,"pollMs":10,"onTimeout":"Continue"}"""),
            CancellationToken.None);
        Assert.False(continued.Outputs["matched"].Require<bool>("matched"));
        Assert.True(continued.Outputs.ContainsKey("waitMs"));
    }

    [Fact]
    public async Task WaitTag_DebounceRejectsAShortBlip_AndBadQualityDoesNotMatch()
    {
        await using var manager = new DeviceManager();
        var blip = new ScriptedDriver("gap-blip", index => Sample("gap-blip", index == 1 || index >= 4));
        manager.Register(blip);
        var node = new DeviceWaitTagNode(manager);
        var held = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-blip","tagId":"trigger","triggerMode":"Rising","stableMs":40,"timeoutMs":500,"pollMs":10}"""),
            CancellationToken.None);
        Assert.True(held.Outputs["matched"].Require<bool>("matched"));
        Assert.True(held.Outputs["waitMs"].Require<double>("waitMs") >= 40);

        var bad = new ScriptedDriver("gap-bad", index => Sample("gap-bad", true, index == 0 ? DeviceTagQuality.Bad : DeviceTagQuality.Good));
        manager.Register(bad);
        var accepted = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-bad","tagId":"trigger","qualityMode":"Good","timeoutMs":300,"pollMs":10}"""),
            CancellationToken.None);
        Assert.True(accepted.Outputs["matched"].Require<bool>("matched"));
        Assert.Equal("Good", accepted.Outputs["quality"].Require<string>("quality"));

        var onlyBad = new ScriptedDriver("gap-only-bad", _ => Sample("gap-only-bad", true, DeviceTagQuality.Bad));
        manager.Register(onlyBad);
        var missed = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-only-bad","tagId":"trigger","qualityMode":"Good","onTimeout":"Continue","timeoutMs":60,"pollMs":10}"""),
            CancellationToken.None);
        Assert.False(missed.Outputs["matched"].Require<bool>("matched"));
    }

    [Fact]
    public async Task WaitTag_ContinueWithoutASample_ReportsTimeoutQuality()
    {
        await using var manager = new DeviceManager();
        manager.Register(new BlockingDriver("gap-block"));
        var result = await new DeviceWaitTagNode(manager).ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("device.waitTag", """{"deviceId":"gap-block","tagId":"trigger","onTimeout":"Continue","timeoutMs":50,"pollMs":10}"""),
            CancellationToken.None);
        Assert.False(result.Outputs["matched"].Require<bool>("matched"));
        Assert.Equal("Timeout", result.Outputs["quality"].Require<string>("quality"));
    }

    [Fact]
    public async Task CoordinatePointAndPose_AcceptDynamicInputs_AndRejectBadFrames()
    {
        var point = await new CoordinatePointNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["x"] = VisionValue.Double(12.5), ["y"] = VisionValue.Integer(4) }),
            Node("coordinate.point", """{"x":1,"y":2,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        var read = point.Outputs["point"].Require<VisionCoordinatePoint2D>("point");
        Assert.Equal(12.5, read.X, 6);
        Assert.Equal(4, read.Y, 6);

        var pose = await new CoordinatePoseNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["thetaDeg"] = VisionValue.Double(270) }),
            Node("coordinate.pose", """{"x":3,"y":4,"thetaDeg":10,"frame":"RobotBase","unit":"MM"}"""), CancellationToken.None);
        var posed = pose.Outputs["pose"].Require<VisionCoordinatePose2D>("pose");
        Assert.Equal(-90, posed.ThetaDeg, 6);
        Assert.Equal("mm", posed.Unit);
        Assert.Equal(3, posed.X, 6);

        var infinite = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new CoordinatePointNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["x"] = VisionValue.Double(double.NaN) }),
            Node("coordinate.point", """{"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None));
        Assert.Contains("finite", infinite.Message);

        var empty = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new CoordinatePoseNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("coordinate.pose", """{"frame":"  ","unit":"mm"}"""), CancellationToken.None));
        Assert.Contains("non-empty", empty.Message);

        var unit = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new CoordinatePointNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("coordinate.point", """{"frame":"Workpiece","unit":"inch"}"""), CancellationToken.None));
        Assert.Contains("mm, cm, m", unit.Message);

        var tree = new VisionFrameTree2D([VisionTransform2DMath.Rigid("Workpiece", "RobotBase", "mm", 1, 2, 3)]);
        var known = await new CoordinatePointNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["tree"] = VisionValue.FrameTree(tree) }),
            Node("coordinate.point", """{"frame":"robotbase","unit":"cm"}"""), CancellationToken.None);
        Assert.Equal("robotbase", known.Outputs["point"].Require<VisionCoordinatePoint2D>("point").Frame);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new CoordinatePoseNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["tree"] = VisionValue.FrameTree(tree) }),
            Node("coordinate.pose", """{"frame":"Camera","unit":"mm"}"""), CancellationToken.None));
        Assert.Contains("Camera", unknown.Message);
        Assert.Contains("RobotBase", unknown.Message);
    }

    [Fact]
    public async Task ArithmeticAndBoolean_RejectUnknownOps_AndSupportExtendedMath()
    {
        var numbers = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Double(9), ["b"] = VisionValue.Double(2) };
        Assert.Equal(81d, (await RunValue(new ValueArithmeticNode(), numbers, """{"op":"pow"}""")).Outputs["result"].Require<double>("result"), 6);
        Assert.Equal(3d, (await RunValue(new ValueArithmeticNode(), new() { ["a"] = VisionValue.Double(9) }, """{"op":"sqrt"}""")).Outputs["result"].Require<double>("result"), 6);
        Assert.Equal(4d, (await RunValue(new ValueArithmeticNode(), new() { ["a"] = VisionValue.Double(-4) }, """{"op":"abs"}""")).Outputs["result"].Require<double>("result"), 6);
        Assert.Equal(2d, (await RunValue(new ValueArithmeticNode(), numbers, """{"op":"min"}""")).Outputs["result"].Require<double>("result"), 6);
        Assert.Equal(9d, (await RunValue(new ValueArithmeticNode(), numbers, """{"op":"max"}""")).Outputs["result"].Require<double>("result"), 6);
        Assert.Equal(1d, (await RunValue(new ValueArithmeticNode(), new() { ["a"] = VisionValue.Double(5) }, """{"op":"clamp","clampMin":0,"clampMax":1}""")).Outputs["result"].Require<double>("result"), 6);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RunValue(new ValueArithmeticNode(), numbers, """{"op":"mod"}"""));
        Assert.Contains("not supported", unknown.Message);
        var negative = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RunValue(new ValueArithmeticNode(), new() { ["a"] = VisionValue.Double(-1) }, """{"op":"sqrt"}"""));
        Assert.Contains("sqrt", negative.Message);

        var logic = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new LogicBooleanNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["a"] = VisionValue.Boolean(true), ["b"] = VisionValue.Boolean(true) }),
            Node("logic.boolean", """{"op":"nand"}"""), CancellationToken.None));
        Assert.Contains("not supported", logic.Message);
    }

    [Fact]
    public async Task IfCondition_UsesToleranceRangeAndPreviousHysteresis()
    {
        var near = await new IfConditionNode().ExecuteAsync(Number(10.0000000005),
            Node("flow.if", """{"operator":"==","threshold":10,"tolerance":1e-6}"""), CancellationToken.None);
        Assert.True(near.Outputs["condition"].Require<bool>("condition"));

        var inside = await new IfConditionNode().ExecuteAsync(Number(4),
            Node("flow.if", """{"mode":"range","rangeMin":1,"rangeMax":5}"""), CancellationToken.None);
        Assert.True(inside.Outputs["condition"].Require<bool>("condition"));
        var reversed = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new IfConditionNode().ExecuteAsync(Number(4),
            Node("flow.if", """{"mode":"range","rangeMin":5,"rangeMax":1}"""), CancellationToken.None));
        Assert.Contains("greater than rangeMax", reversed.Message);

        var held = await new IfConditionNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(9), ["previous"] = VisionValue.Boolean(true) }),
            Node("flow.if", """{"operator":">","threshold":10,"hysteresis":2}"""), CancellationToken.None);
        Assert.True(held.Outputs["condition"].Require<bool>("condition"));
        Assert.Equal(true, held.Summary["hysteresisApplied"]);

        var cold = await new IfConditionNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(11), ["previous"] = VisionValue.Boolean(false) }),
            Node("flow.if", """{"operator":">","threshold":10,"hysteresis":2}"""), CancellationToken.None);
        Assert.False(cold.Outputs["condition"].Require<bool>("condition"));
        Assert.Equal(true, cold.Summary["hysteresisApplied"]);
    }

    [Fact]
    public async Task DetectionSort_UsesSecondaryKeysConfidenceAndTopN()
    {
        IReadOnlyList<VisionDetection> source =
        [
            Detection(10, 1, confidence: null, classId: 2),
            Detection(30, 5, confidence: 0.2, classId: 1),
            Detection(30, 2, confidence: 0.9, classId: 1),
            Detection(20, 3, confidence: 0.4, classId: null)
        ];
        var result = await new DetectionSortNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(source) }),
            Node("collection.detections.sort", """{"sortBy":"area","descending":true,"sortBy2":"x","descending2":false,"topN":3}"""),
            CancellationToken.None);
        var sorted = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal([2d, 5d, 3d], sorted.Select(item => item.X));
        Assert.Equal([0, 1, 2], sorted.Select(item => item.Index));

        var byConfidence = await new DetectionSortNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(source) }),
            Node("collection.detections.sort", """{"sortBy":"confidence","descending":false}"""),
            CancellationToken.None);
        var confidences = byConfidence.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal([0.2d, 0.4d, 0.9d], confidences.Take(3).Select(item => item.Confidence));
        Assert.Null(confidences[^1].Confidence);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new DetectionSortNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["detections"] = VisionValue.Detections(source) }),
            Node("collection.detections.sort", """{"sortBy":"angle"}"""), CancellationToken.None));
        Assert.Contains("sortBy", unknown.Message);
    }

    private static async Task<NodeExecutorResult> Run(TemplateMatchNode node, Mat scene, Mat template, string json)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(scene.Clone())),
            ["template"] = VisionValue.Image(VisionImage.Own(template.Clone()))
        };
        try
        {
            return await node.ExecuteAsync(new NodeExecutionContext(inputs), Node(node.Type, json), CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    private static Task<NodeExecutorResult> RunValue(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string json)
        => executor.ExecuteAsync(new NodeExecutionContext(inputs), Node(executor.Type, json), CancellationToken.None).AsTask();

    private static NodeExecutionContext Number(double value)
        => new(new Dictionary<string, VisionValue> { ["value"] = VisionValue.Double(value) });

    private static NodeDefinition Node(string type, string json)
        => new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static Mat Template()
    {
        var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(template, new Rect(6, 8, 20, 8), Scalar.All(230), -1);
        Cv2.Circle(template, new Point(11, 24), 3, Scalar.All(200), -1);
        return template;
    }

    private static void Paste(Mat scene, Mat patch, int x, int y)
    {
        using var area = new Mat(scene, new Rect(x, y, patch.Width, patch.Height));
        patch.CopyTo(area);
    }

    private static VisionDetection Detection(double area, double x, double? confidence, int? classId)
        => new(0, x, 0, 10, 8, area, 40, 0.5, new VisionCircle(x + 5, 4, 5), new VisionRectangle2D(x, 0, 10, 8), confidence, classId);

    private static DeviceTagSample Sample(string deviceId, bool value, DeviceTagQuality quality = DeviceTagQuality.Good)
        => new(deviceId, "trigger", DeviceTagDataType.Boolean, value, quality, DateTimeOffset.UtcNow);

    private sealed class ScriptedDriver(string id, Func<int, DeviceTagSample> next) : IDeviceDriver
    {
        private int _reads;
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
        public string Id { get; } = id;
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Script";
        public string Driver => "ScriptedDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://script";
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
        {
            if (_state != DeviceConnectionState.Connected) throw new DeviceDisconnectedException("Disconnected");
            return Task.FromResult(next(Interlocked.Increment(ref _reads) - 1));
        }
        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingDriver(string id) : IDeviceDriver
    {
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
        public string Id { get; } = id;
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Block";
        public string Driver => "BlockingDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://block";
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
            => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<DeviceTagSample>(_ => throw new InvalidOperationException("unreachable"), cancellationToken);
        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
