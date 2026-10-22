using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStudio.Engine.Robot;

public sealed record TcpRobotRequest(
    string Operation,
    VisionRobotTarget2D? Target = null,
    RobotRuntimeSettings? Settings = null,
    long CommandId = 0,
    int TimeoutMs = 5000);

public sealed record TcpRobotResponse(
    bool Ok,
    string? Error,
    RobotDescriptor Robot,
    RobotCommandReceipt? Receipt = null);

/// <summary>
/// Localhost newline-delimited JSON robot controller simulator. This intentionally uses only .NET sockets so
/// a future real ABB TCP protocol adapter can be developed behind the same IRobot2DAdapter boundary.
/// </summary>
public sealed class TcpRobotSimulatorServer : IAsyncDisposable
{
    private int _port;
    private readonly VirtualAbbRobotAdapter _robot = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TcpRobotSimulatorServer(int port = 40501) => _port = port;
    public int Port => _port;
    public bool Running => _listener is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        if (_port == 0) _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) { client?.Dispose(); break; }
            catch { client?.Dispose(); if (!ct.IsCancellationRequested) await Task.Delay(50, ct); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) return;
            var request = JsonSerializer.Deserialize<TcpRobotRequest>(line, _json)
                ?? throw new InvalidOperationException("Invalid TCP robot request.");
            var response = await ExecuteAsync(request, ct);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
        }
        catch (Exception ex)
        {
            var response = new TcpRobotResponse(false, ex.Message, _robot.Snapshot());
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
        }
        finally { client.Dispose(); }
    }

    private async Task<TcpRobotResponse> ExecuteAsync(TcpRobotRequest request, CancellationToken ct)
    {
        RobotCommandReceipt? receipt = null;
        switch (request.Operation.ToUpperInvariant())
        {
            case "CONNECT": await _robot.ConnectAsync(ct); break;
            case "DISCONNECT": await _robot.DisconnectAsync(ct); break;
            case "SNAPSHOT": break;
            case "SETTINGS": await _robot.ApplySettingsAsync(request.Settings ?? new RobotRuntimeSettings(), ct); break;
            case "SEND_TARGET": receipt = await _robot.SendTargetAsync(request.Target ?? throw new InvalidOperationException("Target is required."), ct); break;
            case "MOVE_TARGET":
                receipt = await _robot.MoveToAsync(request.Target ?? throw new InvalidOperationException("Target is required."), ct);
                break;
            case "ACK": await _robot.AcknowledgeAsync(request.CommandId, ct); break;
            case "STOP": await _robot.StopAsync(ct); break;
            case "RESET": await _robot.ResetFaultAsync(ct); break;
            default: throw new InvalidOperationException($"Unknown TCP simulator operation '{request.Operation}'.");
        }
        return new TcpRobotResponse(true, null, _robot.Snapshot(), receipt);
    }


    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        await _robot.DisposeAsync();
        _cts?.Dispose();
        _listener = null;
    }
}

/// <summary>
/// Real TCP client adapter talking to TcpRobotSimulatorServer. One JSON request/response per connection keeps the
/// MVP protocol inspectable with netcat/Wireshark and avoids hidden in-process shortcuts.
/// </summary>
public sealed class TcpJsonRobotAdapter : IRobot2DAdapter
{
    private readonly object _gate = new();
    private readonly string _host;
    private int _port;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private RobotDescriptor _snapshot;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public TcpJsonRobotAdapter(string id, string name, string host, int port)
    {
        Id = id;
        Name = name;
        _host = host;
        _port = port;
        _snapshot = new RobotDescriptor(
            Id, Name, "ABB", "TCP JSON Simulator", "tcp-json", "RobotBase", "mm",
            RobotConnectionState.Disconnected, RobotHandshakeState.Disconnected, RobotHandshakeSignals.Empty,
            new VisionCoordinatePose2D(520, 240, 28, "RobotBase", "mm"), null, 0, false, false, null,
            new RobotRuntimeSettings(), Capabilities, DateTimeOffset.UtcNow);
    }

    public string Id { get; }
    public string Name { get; }
    public string Vendor => "ABB";
    public string Model => "TCP JSON Simulator";
    public string Driver => "tcp-json";
    public string BaseFrame => "RobotBase";
    public string Unit => "mm";
    public RobotCapabilities Capabilities { get; } = new(MaxLinearSpeedMmPerSec: 1200, MaxAngularSpeedDegPerSec: 540);

    public RobotDescriptor Snapshot() { lock (_gate) return _snapshot; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
        => Update(await RequestAsync(new TcpRobotRequest("CONNECT"), cancellationToken));

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopPollingAsync();
        Update(await RequestAsync(new TcpRobotRequest("DISCONNECT"), cancellationToken));
    }

    public async Task ApplySettingsAsync(RobotRuntimeSettings settings, CancellationToken cancellationToken = default)
        => Update(await RequestAsync(new TcpRobotRequest("SETTINGS", Settings: settings), cancellationToken));

    public async Task<RobotCommandReceipt> SendTargetAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(new TcpRobotRequest("SEND_TARGET", Target: target), cancellationToken);
        Update(response);
        return response.Receipt ?? throw new InvalidOperationException("TCP simulator did not return a command receipt.");
    }

    public async Task<RobotCommandReceipt> MoveToAsync(VisionRobotTarget2D target, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(new TcpRobotRequest("MOVE_TARGET", Target: target, TimeoutMs: 30000), cancellationToken);
        Update(response);
        var receipt = response.Receipt ?? throw new InvalidOperationException("TCP simulator did not return a command receipt.");
        StartPolling(receipt.CommandId);
        return receipt;
    }

    public async Task AcknowledgeAsync(long commandId, CancellationToken cancellationToken = default)
    {
        await StopPollingAsync();
        Update(await RequestAsync(new TcpRobotRequest("ACK", CommandId: commandId), cancellationToken));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await StopPollingAsync();
        Update(await RequestAsync(new TcpRobotRequest("STOP"), cancellationToken));
    }

    public async Task ResetFaultAsync(CancellationToken cancellationToken = default)
        => Update(await RequestAsync(new TcpRobotRequest("RESET"), cancellationToken));

    private void StartPolling(long commandId)
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _pollTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var response = await RequestAsync(new TcpRobotRequest("SNAPSHOT"), ct);
                    Update(response);
                    var snapshot = Snapshot();
                    if (snapshot.LastCommandId == commandId && (snapshot.Handshake.Complete || snapshot.Handshake.Error))
                        break;
                    await Task.Delay(25, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                // Connection errors surface on the next explicit command; polling is diagnostic only.
            }
        }, CancellationToken.None);
    }

    private async Task StopPollingAsync()
    {
        var cts = _pollCts;
        var task = _pollTask;
        _pollCts = null;
        _pollTask = null;
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
        cts.Dispose();
    }

    private async Task<TcpRobotResponse> RequestAsync(TcpRobotRequest request, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);
        await using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, _json));
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException("TCP robot simulator closed the connection without a response.");
        var response = JsonSerializer.Deserialize<TcpRobotResponse>(line, _json)
            ?? throw new IOException("TCP robot simulator returned invalid JSON.");
        if (!response.Ok) throw new InvalidOperationException(response.Error ?? "TCP robot simulator error.");
        return response;
    }

    private void Update(TcpRobotResponse response)
    {
        var robot = response.Robot;
        lock (_gate)
        {
            _snapshot = robot with
            {
                Id = Id,
                Name = Name,
                Vendor = Vendor,
                Model = Model,
                Driver = Driver
            };
        }
    }

    public async ValueTask DisposeAsync() => await StopPollingAsync();
}
