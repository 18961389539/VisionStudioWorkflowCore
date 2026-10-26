using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 自定义协议通信（device.tcpRequest / device.udpMessage / device.serialPort）测试。
///
/// TCP/UDP 用本机回环真实收发（不是 mock 传输层），覆盖终止符、固定长度、超时、超限、
/// 正则校验与"必须匹配"策略；串口因 CI 无硬件只验证配置校验与错误归类。
/// </summary>
public sealed class CustomProtocolNodeTests
{
    [Fact]
    public async Task Tcp_SendsTerminatedRequestAndStripsReplyTerminator()
    {
        await using var server = new LoopbackTcpServer(reply: Encoding.UTF8.GetBytes("PONG\r\n"));
        var result = await Run(new TcpRequestNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = server.Port, ["payload"] = "PING", ["sendTerminator"] = "crlf", ["expectPattern"] = "^PONG$", ["requireMatch"] = true }));

        Assert.Equal("PONG", result.Outputs["response"].Require<string>("response"));
        Assert.Equal(4L, result.Outputs["bytesRead"].Require<long>("bytesRead"));
        Assert.True(result.Outputs["matched"].Require<bool>("matched"));
        // 请求按配置带上了 CRLF。
        Assert.Equal("PING\r\n", Encoding.UTF8.GetString(server.LastRequest!));
    }

    [Fact]
    public async Task Tcp_SupportsHexPayloadAndFixedLengthReply()
    {
        await using var server = new LoopbackTcpServer(reply: [0x02, 0x41, 0x03]);
        var result = await Run(new TcpRequestNode(),
            Def(new()
            {
                ["host"] = "127.0.0.1", ["port"] = server.Port,
                ["payload"] = "02 31 03", ["payloadEncoding"] = "hex",
                ["readMode"] = "fixedLength", ["readLength"] = 3
            }));

        Assert.Equal(new byte[] { 0x02, 0x31, 0x03 }, server.LastRequest);
        Assert.Equal("02 41 03", result.Outputs["responseHex"].Require<string>("responseHex"));
    }

    [Fact]
    public async Task Tcp_FailsWithActionableMessageWhenPeerDoesNotReply()
    {
        await using var server = new LoopbackTcpServer(reply: null); // 接受连接但永不回复
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = server.Port, ["payload"] = "PING", ["timeoutMs"] = 300 })));

        Assert.Contains("timed out after 300 ms", error.Message);
        // 超时后必须提示"请求可能已到达对端"，避免上层自动重发造成重复动作。
        Assert.Contains("may already have reached", error.Message);
    }

    [Fact]
    public async Task Tcp_RejectsOversizedReply()
    {
        await using var server = new LoopbackTcpServer(reply: Encoding.UTF8.GetBytes(new string('x', 64) + "\r\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = server.Port, ["payload"] = "PING", ["maxResponseBytes"] = 16 })));

        Assert.Contains("maxResponseBytes", error.Message);
    }

    [Fact]
    public async Task Tcp_RequireMatchTurnsMismatchIntoFailure()
    {
        // 回环服务器一次只接受一条连接，因此每次请求都用独立实例。
        await using var softServer = new LoopbackTcpServer(reply: Encoding.UTF8.GetBytes("ERR\r\n"));
        // 默认只置 matched=false，不打断流程。
        var soft = await Run(new TcpRequestNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = softServer.Port, ["payload"] = "PING", ["expectPattern"] = "^OK$" }));
        Assert.False(soft.Outputs["matched"].Require<bool>("matched"));

        await using var strictServer = new LoopbackTcpServer(reply: Encoding.UTF8.GetBytes("ERR\r\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = strictServer.Port, ["payload"] = "PING", ["expectPattern"] = "^OK$", ["requireMatch"] = true })));
        Assert.Contains("did not match expectPattern", error.Message);
    }

    [Fact]
    public async Task Tcp_RejectsInvalidConfigurationBeforeTouchingNetwork()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["port"] = 0, ["payload"] = "X" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["port"] = 5000, ["readMode"] = "fixedLength", ["readLength"] = 0, ["payload"] = "X" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new TcpRequestNode(),
            Def(new() { ["port"] = 5000, ["payload"] = "0D0", ["payloadEncoding"] = "hex" })));
    }

    [Fact]
    public async Task Udp_SendsDatagramAndReadsReply()
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var responderTask = Task.Run(async () =>
        {
            var received = await responder.ReceiveAsync();
            await responder.SendAsync(Encoding.UTF8.GetBytes("ACK"), received.RemoteEndPoint);
        });

        var result = await Run(new UdpMessageNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = port, ["payload"] = "HELLO" }));
        await responderTask;

        Assert.Equal("ACK", result.Outputs["response"].Require<string>("response"));
        Assert.True(result.Outputs["matched"].Require<bool>("matched"));
    }

    [Fact]
    public async Task Udp_CanSendWithoutWaitingForReply()
    {
        using var sink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sink.Client.LocalEndPoint!).Port;

        var result = await Run(new UdpMessageNode(),
            Def(new() { ["host"] = "127.0.0.1", ["port"] = port, ["payload"] = "FIRE", ["expectReply"] = false }));

        Assert.Equal(0L, result.Outputs["bytesRead"].Require<long>("bytesRead"));
        var received = await sink.ReceiveAsync();
        Assert.Equal("FIRE", Encoding.UTF8.GetString(received.Buffer));
    }

    [Fact]
    public async Task Serial_ValidatesConfigurationWithoutHardware()
    {
        // CI 没有串口硬件：只验证配置校验与错误归类，不做真实 I/O。
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SerialRequestNode(),
            Def(new() { ["portName"] = "  ", ["payload"] = "X" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SerialRequestNode(),
            Def(new() { ["portName"] = "COM1", ["payload"] = "X", ["parity"] = "wat" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SerialRequestNode(),
            Def(new() { ["portName"] = "COM1", ["payload"] = "X", ["readMode"] = "fixedLength", ["readLength"] = 0 })));
    }

    [Fact]
    public void Catalog_DeclaresProtocolNodesAsDeviceActions()
    {
        foreach (var type in new[] { "device.tcpRequest", "device.udpMessage", "device.serialPort" })
        {
            var item = BuiltInNodeCatalog.Require(type);
            var action = Assert.Single(item.Actions!);
            Assert.Equal(NodeActionKind.DeviceWrite, action.Kind);
            var resource = Assert.Single(item.Resources!);
            Assert.Equal("device", resource.Kind);
            Assert.Equal("deviceId", resource.Parameter);
        }
    }

    // ===== 夹具 =====

    private sealed class LoopbackTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly byte[]? _reply;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public byte[]? LastRequest { get; private set; }

        public LoopbackTcpServer(byte[]? reply)
        {
            _reply = reply;
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buffer = new byte[1024];
                    var read = await stream.ReadAsync(buffer);
                    LastRequest = buffer[..read];
                    if (_reply is null) { await Task.Delay(Timeout.Infinite, _stop.Token); }
                    await stream.WriteAsync(_reply);
                    await stream.FlushAsync();
                    await Task.Delay(200);
                }
                catch (Exception) { /* 测试结束时监听被关闭属于正常路径 */ }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            _stop.Dispose();
        }
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, NodeDefinition node)
        => await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);

    private static NodeDefinition Def(Dictionary<string, object> parameters)
    {
        var json = JsonSerializer.Serialize(parameters);
        return new NodeDefinition("n1", "test", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    }
}
