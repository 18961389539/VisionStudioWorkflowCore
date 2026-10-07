using VisionStudio.Engine.Provenance;
using System.Collections.Concurrent;

namespace VisionStudio.Engine.Device;

/// <summary>
/// In-memory PLC with Modbus-style addresses. It is a deterministic protocol simulator, not a Modbus TCP implementation.
/// Replace it with an NModbus/S7/OPC UA adapter without changing DeviceManager or workflow nodes.
/// </summary>
public sealed class VirtualModbusPlcDriver : IDeviceDriver, IHardwareProvenanceProvider
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceTagDefinition> _tags;
    private readonly object _gate = new();
    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _lastTrigger;
    private long _cycleCounter;
    private string? _error;

    public VirtualModbusPlcDriver(string id = "virtual-modbus-1", string name = "Virtual Modbus PLC")
    {
        Id = id;
        Name = name;
        Tags =
        [
            new("ready", "PLC Ready", "00001", DeviceTagDataType.Boolean, false, Description: "PLC side ready signal."),
            new("trigger", "Vision Trigger", "00002", DeviceTagDataType.Boolean, false, Description: "Simulated 250 ms trigger pulse every 3 seconds."),
            new("resultReady", "Result Ready", "00003", DeviceTagDataType.Boolean, true),
            new("resultOk", "Result OK", "00004", DeviceTagDataType.Boolean, true),
            new("hostHeartbeat", "Host Heartbeat", "00005", DeviceTagDataType.Boolean, true),
            new("deviceHeartbeat", "PLC Heartbeat", "00006", DeviceTagDataType.Boolean, false),
            new("resultX", "Result X", "40001", DeviceTagDataType.Double, true, "mm"),
            new("resultY", "Result Y", "40003", DeviceTagDataType.Double, true, "mm"),
            new("resultR", "Result R", "40005", DeviceTagDataType.Double, true, "deg"),
            new("cycleCounter", "Cycle Counter", "40007", DeviceTagDataType.Integer, false),
            new("recipe", "Recipe", "40009", DeviceTagDataType.Integer, true),
            new("statusText", "Status Text", "STR001", DeviceTagDataType.String, true)
        ];
        _tags = Tags.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in Tags)
            _values[tag.Id] = tag.DataType switch
            {
                DeviceTagDataType.Boolean => false,
                DeviceTagDataType.Integer => 0L,
                DeviceTagDataType.Double => 0d,
                DeviceTagDataType.String => string.Empty,
                _ => null
            };
        _values["ready"] = true;
        _values["statusText"] = "Virtual PLC Ready";
    }

    public string Id { get; }
    public string Name { get; }
    public string Vendor => "Virtual";
    public string Model => "Modbus PLC Simulator";
    public string Driver => "VirtualModbusMemory";
    public string Protocol => "Virtual/Modbus-style";
    public string Endpoint => $"memory://{Id}";
    public DeviceDriverCapabilities Capabilities { get; } = new(SupportsBatchRead: false, SupportsBatchWrite: false, SupportsDiagnostics: false, RealIo: false, SupportsString: true);
    public DeviceConnectionState ConnectionState { get { lock (_gate) return _state; } }
    public string? Error { get { lock (_gate) return _error; } }
    public IReadOnlyList<DeviceTagDefinition> Tags { get; }

    public HardwareProvenanceData GetHardwareProvenance() => new(
        Manufacturer: "VisionStudio",
        ProductName: Name,
        Model: Model,
        SerialNumber: Id,
        HardwareRevision: "sim-v1",
        FirmwareVersion: "simulated",
        SoftwareVersion: typeof(VirtualModbusPlcDriver).Assembly.GetName().Version?.ToString(),
        ControllerVersion: "Virtual Modbus Runtime",
        ProgramName: "Virtual PLC Cycle",
        ProgramHash: "virtual-modbus-cycle-v1",
        Attributes: new Dictionary<string, string> { ["simulation"] = "true", ["protocol"] = Protocol });

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _state = DeviceConnectionState.Connecting;
            _error = null;
        }
        await Task.Delay(60, cancellationToken);
        lock (_gate)
        {
            _startedAt = DateTimeOffset.UtcNow;
            _state = DeviceConnectionState.Connected;
            _error = null;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _state = DeviceConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var tag = RequireTag(tagId);
        UpdateDynamicSignals();
        _values.TryGetValue(tag.Id, out var value);
        return Task.FromResult(new DeviceTagSample(Id, tag.Id, tag.DataType, value, DeviceTagQuality.Good, DateTimeOffset.UtcNow));
    }

    public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var tag = RequireTag(tagId);
        if (!tag.Writable)
            throw new DeviceWriteNotAllowedException($"Tag '{tag.Id}' ({tag.Address}) is read-only.");
        _values[tag.Id] = DeviceValueConversion.ConvertTo(tag.DataType, value);
        return Task.CompletedTask;
    }

    private void UpdateDynamicSignals()
    {
        var elapsed = DateTimeOffset.UtcNow - _startedAt;
        var triggerWindow = elapsed.TotalMilliseconds % 3000;
        var trigger = triggerWindow < 250;
        var heartbeat = ((long)(elapsed.TotalMilliseconds / 500)) % 2 == 0;
        _values["trigger"] = trigger;
        _values["deviceHeartbeat"] = heartbeat;
        _values["ready"] = true;
        if (trigger && !_lastTrigger)
        {
            _cycleCounter++;
            _values["cycleCounter"] = _cycleCounter;
        }
        _lastTrigger = trigger;
    }

    private DeviceTagDefinition RequireTag(string tagId)
        => _tags.TryGetValue(tagId, out var tag)
            ? tag
            : throw new DeviceTagNotFoundException($"Device '{Id}' has no tag '{tagId}'.");

    private void EnsureConnected()
    {
        if (ConnectionState != DeviceConnectionState.Connected)
            throw new DeviceDisconnectedException($"Device '{Id}' is {ConnectionState}.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
