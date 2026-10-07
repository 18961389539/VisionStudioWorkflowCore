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
