using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

public sealed class DeviceRobotP0SafetyTests
{
    [Fact]
    public async Task Udp_DiscardsStaleDatagramAndAcceptsOnlyCorrelatedReply()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)peer.Client.LocalEndPoint!;
        var responder = Task.Run(async () =>
        {
            var request = await peer.ReceiveAsync();
            await peer.SendAsync(Encoding.ASCII.GetBytes("OLD"), request.RemoteEndPoint);
            var sequence = Encoding.ASCII.GetString(request.Buffer)[4..];
            await peer.SendAsync(Encoding.ASCII.GetBytes("ACK" + sequence), request.RemoteEndPoint);
        });

        var result = await Run(new UdpMessageNode(), Def("device.udpMessage", new()
        {
            ["host"] = "127.0.0.1", ["port"] = endpoint.Port, ["payload"] = "PING",
            ["sequenceFormat"] = "ascii", ["timeoutMs"] = 2000
        }));
        await responder;

        Assert.Equal("ACK" + result.Outputs["sequence"].Require<long>("sequence"), result.Outputs["response"].Require<string>("response"));
        Assert.Equal("responseConfirmed", result.Outputs["requestState"].Require<string>("requestState"));
        Assert.False(result.Outputs["automaticRetry"].Require<bool>("automaticRetry"));
    }

    [Fact]
    public async Task Tcp_ReadTimeoutReportsSentUnconfirmedAndForbidsRetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var buffer = new byte[1024];
            await client.GetStream().ReadExactlyAsync(buffer.AsMemory(0, 1));
            await Task.Delay(1000);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(), Def("device.tcpRequest", new()
        {
            ["host"] = "127.0.0.1", ["port"] = port, ["payload"] = "FIRE", ["timeoutMs"] = 100
        })));
        await peer;

        Assert.Contains("stage=read", error.Message);
        Assert.Contains("requestState=sentUnconfirmed", error.Message);
        Assert.Contains("automaticRetry=forbidden", error.Message);
    }

    [Fact]
    public async Task WriteTag_MismatchedReadbackIsNotConfirmed()
    {
        var driver = new P0DeviceDriver([new("status", "Status", "s", DeviceTagDataType.Integer, true)]);
        await using var devices = new DeviceManager();
        devices.Register(driver);
        driver.ReadValue = 2L;

        var result = await Run(new DeviceWriteTagNode(devices), Def("device.writeTag", new()
        {
            ["deviceId"] = driver.Id, ["tagId"] = "status", ["fallbackValue"] = "1", ["valueType"] = "Integer"
        }));

        Assert.False(result.Outputs["confirmed"].Require<bool>("confirmed"));
        Assert.Equal("1", result.Outputs["expectedValue"].Require<string>("expectedValue"));
        Assert.Equal("2", result.Outputs["actualValue"].Require<string>("actualValue"));
    }

    [Fact]
    public async Task WriteVisionResult_SubstitutedCoordinateRequiresValidityTag()
    {
        var driver = P0DeviceDriver.Vision();
        await using var devices = new DeviceManager();
        devices.Register(driver);
        var node = VisionNode(driver.Id, ("missingCoordinatePolicy", "zero"), ("yValidTag", ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new DeviceWriteVisionResultNode(devices), node, coordinates: false));

        Assert.Contains("yValidTag", error.Message);
        Assert.Empty(driver.Writes);
    }

    [Fact]
    public async Task WriteVisionResult_PayloadFailureCompensatesWithoutReplayingPayload()
    {
        var driver = P0DeviceDriver.Vision();
        await using var devices = new DeviceManager();
        devices.Register(driver);
        driver.FailWrites = tag => tag == "resultY";
        var node = VisionNode(driver.Id, ("xValidTag", "validX"), ("yValidTag", "validY"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new DeviceWriteVisionResultNode(devices), node));

        Assert.Contains("Compensation:", error.Message);
        Assert.Contains("NOT retried", error.Message);
        Assert.Single(driver.Writes, write => write.TagId == "resultOk");
        Assert.Equal(false, driver.Writes.Last(write => write.TagId == "resultReady").Value);
        Assert.Equal(("validY", (object?)false), driver.Writes[^1]);
    }

    [Fact]
    public async Task Handshake_ExecuteTimeoutNeverResendsAndFinalStateIsReported()
    {
        await using var robots = new RobotManager();
        var adapter = new BusyRobotAdapter();
        robots.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 50, MaxRetries: 3, RetryDelayMs: 0, WaitForComplete: true, AutoAck: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => robots.ExecuteHandshakeAsync(
            adapter.Id, new VisionRobotTarget2D(1, 2, 0, "RobotBase", "mm", "Test", "Manual"), policy));

        Assert.Equal(1, adapter.SendCalls);
        Assert.Equal(1, adapter.MoveCalls);
        Assert.Contains("NOT resent", error.Message);
        Assert.Contains("FinalState=Ready", error.Message);
    }

    [Fact]
    public async Task Handshake_ErrorIsNotAcknowledgedOrRetried()
    {
        await using var robots = new RobotManager();
        var adapter = new BusyRobotAdapter { ErrorOnMove = true };
        robots.Register(adapter);
        var policy = new RobotCommandPolicy(TimeoutMs: 500, MaxRetries: 3, RetryDelayMs: 0, WaitForComplete: true, AutoAck: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => robots.ExecuteHandshakeAsync(
            adapter.Id, new VisionRobotTarget2D(1, 2, 0, "RobotBase", "mm", "Test", "Manual"), policy));

        Assert.Equal(0, adapter.AckCalls);
        Assert.Equal(1, adapter.MoveCalls);
        Assert.Contains("Error", error.Message);
    }

    [Fact]
    public async Task Guidance_RejectsFrameAndSourceMismatchBeforeTarget()
    {
        var pose = new VisionCoordinatePose2D(1, 2, 0, "RobotBase", "mm");
        var frameMismatch = Def("robot.guidance2d", new() { ["frame"] = "Camera" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RobotGuidance2DNode(), frameMismatch, pose));
        var sourceMismatch = Def("robot.guidance2d", new() { ["guidanceMode"] = "EyeInHand", ["poseSource"] = "EyeToHand" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RobotGuidance2DNode(), sourceMismatch, pose));
    }

    [Fact]
    public async Task J4Compensation_RejectsUnitFrameAndEccentricityViolations()
    {
        var target = new VisionRobotTarget2D(1, 2, 0, "RobotBase", "mm", "Test", "Manual");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new J4TcpCompensationNode(),
            Def("robot.j4TcpCompensation", new() { ["offsetUnit"] = "cm" }), target: target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new J4TcpCompensationNode(),
            Def("robot.j4TcpCompensation", new() { ["offsetFrame"] = "Tool" }), target: target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new J4TcpCompensationNode(),
            Def("robot.j4TcpCompensation", new() { ["tcpOffsetX"] = 10, ["maxEccentricity"] = 1 }), target: target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new J4TcpCompensationNode(),
            Def("robot.j4TcpCompensation", new() { ["maxAngleDeg"] = 180 }), target: target with { RDeg = 350 }));
    }

    [Fact]
    public void Catalog_DeclaresNewSafetyContracts()
    {
        foreach (var type in new[] { "device.serialPort", "device.tcpRequest", "device.udpMessage" })
        {
            var item = BuiltInNodeCatalog.Require(type);
            Assert.Contains(item.Outputs, output => output.Name == "requestState");
            Assert.Contains(item.Outputs, output => output.Name == "sequence");
            Assert.Contains(item.Parameters, parameter => parameter.Name == "sequenceFormat");
        }
        Assert.Contains(BuiltInNodeCatalog.Require("device.writeTag").Outputs, output => output.Name == "confirmed");
        Assert.Contains(BuiltInNodeCatalog.Require("robot.guidance2d").Parameters, parameter => parameter.Name == "poseSource");
        Assert.Contains(BuiltInNodeCatalog.Require("robot.j4TcpCompensation").Parameters, parameter => parameter.Name == "offsetUnit");
    }

    private static NodeDefinition VisionNode(string deviceId, params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, object>
        {
            ["deviceId"] = deviceId, ["rTag"] = "", ["xValidTag"] = "validX", ["yValidTag"] = "validY", ["missingCoordinatePolicy"] = "skip"
        };
        foreach (var (key, value) in overrides) values[key] = value;
        return Def("device.writeVisionResult", values);
    }

    private static NodeDefinition Def(string type, Dictionary<string, object> values)
        => new("p0-test", type, null, null, values.ToDictionary(pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, pair.Value.GetType())));

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, NodeDefinition node,
        VisionCoordinatePose2D? pose = null, VisionRobotTarget2D? target = null, bool coordinates = true)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["pass"] = VisionValue.Boolean(true)
        };
        if (coordinates)
        {
            inputs["x"] = VisionValue.Double(1);
            inputs["y"] = VisionValue.Double(2);
        }
        if (pose is { } p) inputs["pose"] = VisionValue.CoordinatePose(p);
        if (target is { } t) inputs["target"] = VisionValue.RobotTarget(t);
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private sealed class P0DeviceDriver : IDeviceDriver
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
        private DeviceConnectionState _state;
        public P0DeviceDriver(IReadOnlyList<DeviceTagDefinition> tags) => Tags = tags;
        public static P0DeviceDriver Vision() => new(
        [
            new("resultReady", "Ready", "0", DeviceTagDataType.Boolean, true),
            new("resultOk", "OK", "0", DeviceTagDataType.Boolean, true),
            new("resultX", "X", "0", DeviceTagDataType.Double, true),
            new("resultY", "Y", "0", DeviceTagDataType.Double, true),
            new("validX", "Valid X", "0", DeviceTagDataType.Boolean, true),
            new("validY", "Valid Y", "0", DeviceTagDataType.Boolean, true)
        ]);
        public string Id => "p0-device";
        public string Name => "P0 Device";
        public string Vendor => "Test";
        public string Model => "Test";
        public string Driver => "Test";
        public string Protocol => "Test";
        public string Endpoint => "Test";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities => new();
        public IReadOnlyList<DeviceTagDefinition> Tags { get; }
        public object? ReadValue { get; set; }
        public Func<string, bool>? FailWrites { get; set; }
        public List<(string TagId, object? Value)> Writes { get; } = [];
        public Task ConnectAsync(CancellationToken cancellationToken = default) { _state = DeviceConnectionState.Connected; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { _state = DeviceConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
        {
            var tag = Tags.Single(item => item.Id == tagId);
            var value = ReadValue ?? _values.GetValueOrDefault(tagId);
            return Task.FromResult(new DeviceTagSample(Id, tagId, tag.DataType, value, DeviceTagQuality.Good, DateTimeOffset.UtcNow));
        }
        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
        {
            if (FailWrites?.Invoke(tagId) == true) throw new InvalidOperationException("injected write failure");
            Writes.Add((tagId, value));
            _values[tagId] = value;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BusyRobotAdapter : IRobot2DAdapter
    {
        private long _commandId;
        private bool _busy;
        private bool _error;
        private RobotHandshakeSignals _signals = RobotHandshakeSignals.Empty;
        public string Id => "p0-robot";
        public string Name => "P0 Robot";
        public string Vendor => "Test";
        public string Model => "Test";
        public string Driver => "Test";
        public string BaseFrame => "RobotBase";
        public string Unit => "mm";
        public RobotCapabilities Capabilities => new();
        public int SendCalls { get; private set; }
        public int MoveCalls { get; private set; }
        public int AckCalls { get; private set; }
        public bool ErrorOnMove { get; init; }
        public RobotDescriptor Snapshot() => new(Id, Name, Vendor, Model, Driver, BaseFrame, Unit,
            RobotConnectionState.Connected, _error ? RobotHandshakeState.Faulted : _busy ? RobotHandshakeState.Executing : RobotHandshakeState.Ready,
            _signals, new VisionCoordinatePose2D(0, 0, 0, BaseFrame, Unit), null, _commandId, _busy, false,
            _error ? "injected error" : null, new RobotRuntimeSettings(), Capabilities, DateTimeOffset.UtcNow);
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        {
            SendCalls++;
            _commandId++;
            _signals = _signals with { TargetReady = true, CommandId = _commandId };
            return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, RobotHandshakeState.TargetAccepted, DateTimeOffset.UtcNow));
        }
        public Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
        {
            MoveCalls++;
            _busy = !ErrorOnMove;
            _error = ErrorOnMove;
            _signals = _signals with { Execute = true, Busy = _busy, Error = _error, ErrorCode = _error ? "injected" : null };
            return Task.FromResult(new RobotCommandReceipt(_commandId, Id, target, _error ? RobotHandshakeState.Faulted : RobotHandshakeState.Executing, DateTimeOffset.UtcNow));
        }
        public Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default) { AckCalls++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _busy = false;
            _signals = _signals with { Execute = false, Busy = false };
            return Task.CompletedTask;
        }
        public Task ResetFaultAsync(CancellationToken cancellationToken = default) { _error = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
