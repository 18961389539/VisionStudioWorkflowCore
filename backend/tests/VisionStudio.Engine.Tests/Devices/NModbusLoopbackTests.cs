using System.Net;
using System.Net.Sockets;
using NModbus;
using VisionStudio.Engine.Device;

namespace VisionStudio.Engine.Tests;

public sealed class NModbusLoopbackTests
{
    [Theory]
    [InlineData(Modbus32BitOrder.ABCD)]
    [InlineData(Modbus32BitOrder.CDAB)]
    [InlineData(Modbus32BitOrder.BADC)]
    [InlineData(Modbus32BitOrder.DCBA)]
    public async Task RealNModbusDriver_BatchRoundTripsTypedValues(Modbus32BitOrder order)
    {
        await using var loopback = await ModbusLoopbackServer.StartAsync();
        var tags = new[]
        {
            new DeviceTagDefinition("coil", "Coil", "C:0", DeviceTagDataType.Boolean, Writable: true),
            new DeviceTagDefinition("count", "Count", "HR:0", DeviceTagDataType.Integer, Writable: true),
            new DeviceTagDefinition("position", "Position", "HR:2", DeviceTagDataType.Double, Writable: true, Unit: "mm")
        };
        await using var driver = new NModbusTcpDeviceDriver(new NModbusTcpDeviceOptions(
            "loopback", "Loopback", "127.0.0.1", loopback.Port, 1, 2000, order, tags));

        await driver.ConnectAsync();
        await driver.WriteManyAsync(new Dictionary<string, object?>
        {
            ["coil"] = true,
            ["count"] = 123456L,
            ["position"] = 12.5d
        });
        var samples = await driver.ReadManyAsync(["coil", "count", "position"]);
        var byId = samples.ToDictionary(x => x.TagId, StringComparer.OrdinalIgnoreCase);

        Assert.True(byId["coil"].AsBoolean());
        Assert.Equal(123456L, byId["count"].AsInteger());
        Assert.InRange(Math.Abs(byId["position"].AsDouble() - 12.5), 0, 0.001);
        Assert.True(driver.ProtocolDiagnostics.BatchReads >= 1);
        Assert.True(driver.ProtocolDiagnostics.BatchWrites >= 1);
    }

    [Fact]
    public async Task SilentPeer_TimesOutWithinBudget_AndResetsConnection()
    {
        // R02（2026-10-12 复审）：对端接受连接但从不回复时，NModbus 异步操作必须在配置预算内结束——
        // 旧实现只设置 TcpClient.ReceiveTimeout（对异步操作无效），读取会无限挂起并持续占用 IO 门。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        using var acceptCts = new CancellationTokenSource();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!acceptCts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    lock (accepted) accepted.Add(client); // 保持连接打开、永不回复。
                }
            }
            catch { /* 取消/关闭 */ }
        });

        try
        {
            var tags = new[] { new DeviceTagDefinition("count", "Count", "HR:0", DeviceTagDataType.Integer) };
            await using var driver = new NModbusTcpDeviceDriver(new NModbusTcpDeviceOptions(
                "silent", "Silent", "127.0.0.1", port, 1, 200, Modbus32BitOrder.ABCD, tags));
            await driver.ConnectAsync();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<DeviceProtocolTimeoutException>(() => driver.ReadAsync("count"));
            watch.Stop();

            // 预算内结束（宽松上限），并且连接已被隔离。
            Assert.True(watch.ElapsedMilliseconds < 5_000, $"read must end within the configured budget, took {watch.ElapsedMilliseconds} ms");
            Assert.Equal(DeviceConnectionState.Faulted, driver.ConnectionState);

            // 隔离后的下一次读取快速失败（不再挂起），必须显式重连。
            var secondWatch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<DeviceDisconnectedException>(() => driver.ReadAsync("count"));
            secondWatch.Stop();
            Assert.True(secondWatch.ElapsedMilliseconds < 1_000, "an isolated connection must fail fast until an explicit reconnect");
        }
        finally
        {
            acceptCts.Cancel();
            listener.Stop();
            lock (accepted) foreach (var client in accepted) client.Dispose();
            try { await acceptLoop; } catch { }
        }
    }

    private sealed class ModbusLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _listenTask;

        private ModbusLoopbackServer(TcpListener listener, CancellationTokenSource cts, Task listenTask)
        {
            _listener = listener;
            _cts = cts;
            _listenTask = listenTask;
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public static Task<ModbusLoopbackServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var factory = new ModbusFactory();
            var network = factory.CreateSlaveNetwork(listener);
            network.AddSlave(factory.CreateSlave(1));
            var cts = new CancellationTokenSource();
            var task = network.ListenAsync(cts.Token);
            return Task.FromResult(new ModbusLoopbackServer(listener, cts, task));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _listenTask; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
            _cts.Dispose();
        }
    }
}
