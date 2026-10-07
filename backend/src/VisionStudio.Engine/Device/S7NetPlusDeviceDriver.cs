using System.Net.Sockets;
using S7.Net;
using S7.Net.Types;

namespace VisionStudio.Engine.Device;

public sealed record S7NetPlusDeviceOptions(
    string Id,
    string Name,
    string Host,
    string CpuType = "S71500",
    int Port = 102,
    short Rack = 0,
    short Slot = 0,
    int TimeoutMs = 3000,
    IReadOnlyList<DeviceTagDefinition>? Tags = null);

/// <summary>
/// Real Siemens S7 adapter using S7.Net Plus. Address examples:
/// DB10.DBX0.0, DB10.DBW2, DB10.DBD4, M10.0, MW20, MD24.
/// V0.19 intentionally keeps STRING out of the generic scalar mapper; add a dedicated
/// string codec when a project defines its concrete S7 STRING layout.
/// </summary>
public sealed class S7NetPlusDeviceDriver : IDeviceDriver, IDeviceBatchDriver, IDeviceDiagnosticsProvider
{
    private readonly S7NetPlusDeviceOptions _options;
    private readonly Dictionary<string, DeviceTagDefinition> _tags;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly ProtocolDiagnosticsCounter _diagnostics = new();
    private Plc? _plc;
    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    private string? _error;

    public S7NetPlusDeviceDriver(S7NetPlusDeviceOptions options)
    {
        _options = options with
        {
            Port = Math.Clamp(options.Port, 1, 65535),
            TimeoutMs = Math.Clamp(options.TimeoutMs, 100, 60000),
            Tags = options.Tags ?? Array.Empty<DeviceTagDefinition>()
        };
        if (!Enum.TryParse<CpuType>(_options.CpuType, ignoreCase: true, out _))
            throw new DeviceAddressException($"Unknown S7 CPU type '{_options.CpuType}'. Example: S71200 or S71500.");
        _tags = _options.Tags!.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in _tags.Values) ValidateDefinition(tag);
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public string Vendor => "S7.Net Plus";
    public string Model => _options.CpuType;
    public string Driver => "S7NetPlusDeviceDriver";
    public string Protocol => "Siemens S7";
    public string Endpoint => $"s7://{_options.Host}:{_options.Port} rack={_options.Rack} slot={_options.Slot}";
    public DeviceConnectionState ConnectionState { get { lock (_stateGate) return _state; } }
    public string? Error { get { lock (_stateGate) return _error; } }
    public DeviceDriverCapabilities Capabilities { get; } = new(true, true, true, true, false);
    public IReadOnlyList<DeviceTagDefinition> Tags => _options.Tags!;
    public DeviceProtocolDiagnostics ProtocolDiagnostics => _diagnostics.Snapshot();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionState == DeviceConnectionState.Connected && _plc?.IsConnected == true) return;
        SetState(DeviceConnectionState.Connecting, null);
        try
        {
            if (!Enum.TryParse<CpuType>(_options.CpuType, true, out var cpu))
                throw new DeviceAddressException($"Unknown S7 CPU type '{_options.CpuType}'.");
            var plc = new Plc(cpu, _options.Host, _options.Port, _options.Rack, _options.Slot)
            {
                ReadTimeout = _options.TimeoutMs,
                WriteTimeout = _options.TimeoutMs
            };
            await plc.OpenAsync(cancellationToken);
            lock (_stateGate)
            {
                _plc?.Close();
                _plc = plc;
                _state = DeviceConnectionState.Connected;
                _error = null;
            }
        }
        catch (Exception ex)
        {
            SetState(DeviceConnectionState.Faulted, ex.Message);
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            _plc?.Close();
            _plc = null;
            _state = DeviceConnectionState.Disconnected;
            _error = null;
        }
        return Task.CompletedTask;
    }

    public async Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
    {
        var tag = RequireTag(tagId);
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            // S7.Net Plus cancellation closes the socket during in-flight I/O. We therefore
            // honor cancellation before protocol I/O but let the queued request finish normally.
            cancellationToken.ThrowIfCancellationRequested();
            return await _diagnostics.TrackAsync(async () =>
            {
                var raw = await RequirePlc().ReadAsync(tag.Address, CancellationToken.None);
                return Sample(tag, ConvertReadValue(tag, raw));
            });
        }
        catch (Exception ex)
        {
            SetProtocolError(ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    public async Task<IReadOnlyList<DeviceTagSample>> ReadManyAsync(IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default)
    {
        var tags = tagIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(RequireTag).ToArray();
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();
            return await _diagnostics.TrackAsync(async () =>
            {
                var output = new List<DeviceTagSample>(tags.Length);
                // S7 PDUs have strict request/response size limits. Small chunks avoid depending
                // on a controller-specific negotiated PDU size while still reducing round trips.
                foreach (var chunk in tags.Chunk(8))
                {
                    var items = chunk.Select(t => DataItem.FromAddress(t.Address)).ToList();
                    await RequirePlc().ReadMultipleVarsAsync(items, CancellationToken.None);
                    for (var i = 0; i < chunk.Length; i++)
                        output.Add(Sample(chunk[i], ConvertReadValue(chunk[i], items[i].Value)));
                }
                return (IReadOnlyList<DeviceTagSample>)output;
            }, batchRead: true);
        }
        catch (Exception ex)
        {
            SetProtocolError(ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    public async Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default)
    {
        var tag = RequireTag(tagId);
        if (!tag.Writable) throw new DeviceWriteNotAllowedException($"Tag '{tag.Id}' is read-only.");
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();
            var converted = ConvertWriteValue(tag, value);
            await _diagnostics.TrackAsync(() => RequirePlc().WriteAsync(tag.Address, converted, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SetProtocolError(ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    public async Task WriteManyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        var items = values.Select(kv => (Tag: RequireTag(kv.Key), Value: kv.Value)).ToArray();
        foreach (var item in items)
            if (!item.Tag.Writable) throw new DeviceWriteNotAllowedException($"Tag '{item.Tag.Id}' is read-only.");

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();
            await _diagnostics.TrackAsync(async () =>
            {
                foreach (var chunk in items.Chunk(6))
                {
                    var dataItems = chunk.Select(x => DataItem.FromAddressAndValue(x.Tag.Address, ConvertWriteValue(x.Tag, x.Value))).ToArray();
                    await RequirePlc().WriteAsync(dataItems);
                }
            }, batchWrite: true);
        }
        catch (Exception ex)
        {
            SetProtocolError(ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    private static object? ConvertReadValue(DeviceTagDefinition tag, object? raw)
    {
        if (raw is null) return null;
        if (tag.DataType == DeviceTagDataType.Boolean)
            return raw is bool b ? b : Convert.ToUInt64(raw) != 0;

        if (tag.DataType == DeviceTagDataType.Integer)
        {
            if (IsDword(tag.Address))
                return raw switch
                {
                    uint u => (long)unchecked((int)u),
                    int i => (long)i,
                    _ => Convert.ToInt64(raw)
                };
            if (IsWord(tag.Address))
                return raw switch
                {
                    ushort u => (long)unchecked((short)u),
                    short s => (long)s,
                    _ => Convert.ToInt64(raw)
                };
            return Convert.ToInt64(raw);
        }

        if (tag.DataType == DeviceTagDataType.Double)
        {
            if (!IsDword(tag.Address))
                return Convert.ToDouble(raw);
            return raw switch
            {
                uint u => (double)BitConverter.Int32BitsToSingle(unchecked((int)u)),
                int i => (double)BitConverter.Int32BitsToSingle(i),
                float f => (double)f,
                double d => d,
                _ => Convert.ToDouble(raw)
            };
        }

        if (tag.DataType == DeviceTagDataType.String)
            throw new DeviceAddressException($"S7 STRING tag '{tag.Id}' requires a project-specific codec and is not enabled in V0.19.");
        return raw;
    }

    private static object ConvertWriteValue(DeviceTagDefinition tag, object? value)
    {
        var converted = DeviceValueConversion.ConvertTo(tag.DataType, value);
        return tag.DataType switch
        {
            DeviceTagDataType.Boolean => (bool)converted!,
            DeviceTagDataType.Integer when IsByte(tag.Address) => checked((byte)(long)converted!),
            DeviceTagDataType.Integer when IsWord(tag.Address) => checked((short)(long)converted!),
            DeviceTagDataType.Integer when IsDword(tag.Address) => checked((int)(long)converted!),
            DeviceTagDataType.Integer => checked((int)(long)converted!),
            DeviceTagDataType.Double when IsDword(tag.Address) => checked((float)(double)converted!),
            DeviceTagDataType.Double => throw new DeviceAddressException($"S7 floating point tag '{tag.Id}' must map to a DWord address such as DB1.DBD4 or MD20."),
            DeviceTagDataType.String => throw new DeviceAddressException($"S7 STRING tag '{tag.Id}' requires a project-specific codec and is not enabled in V0.19."),
            _ => converted ?? 0
        };
    }

    private static void ValidateDefinition(DeviceTagDefinition tag)
    {
        try { _ = DataItem.FromAddress(tag.Address); }
        catch (Exception ex) { throw new DeviceAddressException($"Invalid S7 address '{tag.Address}' for tag '{tag.Id}': {ex.Message}"); }
        if (tag.DataType == DeviceTagDataType.String)
            throw new DeviceAddressException($"String tag '{tag.Id}' is not supported by S7NetPlusDeviceDriver V0.19.");
        if (tag.DataType == DeviceTagDataType.Double && !IsDword(tag.Address))
            throw new DeviceAddressException($"Double tag '{tag.Id}' must use a DWord S7 address (DBD/MD/ID/QD). ");
    }

    private static bool IsByte(string a) => ContainsToken(a, "DBB") || StartsAny(a, "MB", "IB", "EB", "QB", "AB", "OB");
    private static bool IsWord(string a) => ContainsToken(a, "DBW") || StartsAny(a, "MW", "IW", "EW", "QW", "AW", "OW");
    private static bool IsDword(string a) => ContainsToken(a, "DBD") || StartsAny(a, "MD", "ID", "ED", "QD", "AD", "OD");
    private static bool ContainsToken(string value, string token) => value.Contains(token, StringComparison.OrdinalIgnoreCase);
    private static bool StartsAny(string value, params string[] prefixes) => prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private DeviceTagDefinition RequireTag(string id)
        => _tags.TryGetValue(id, out var tag) ? tag : throw new DeviceTagNotFoundException($"Device '{Id}' has no tag '{id}'.");

    private Plc RequirePlc() => _plc ?? throw new DeviceDisconnectedException($"Device '{Id}' is not connected.");

    private void EnsureConnected()
    {
        if (ConnectionState != DeviceConnectionState.Connected || _plc?.IsConnected != true)
            throw new DeviceDisconnectedException($"Device '{Id}' is {ConnectionState}.");
    }

    private DeviceTagSample Sample(DeviceTagDefinition tag, object? value)
        => new(Id, tag.Id, tag.DataType, value, DeviceTagQuality.Good, DateTimeOffset.UtcNow);

    private void SetProtocolError(Exception ex)
    {
        if (ex is SocketException or IOException or ObjectDisposedException or PlcException)
            SetState(DeviceConnectionState.Faulted, ex.Message);
        else lock (_stateGate) _error = ex.Message;
    }

    private void SetState(DeviceConnectionState state, string? error)
    {
        lock (_stateGate) { _state = state; _error = error; }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ioGate.Dispose();
    }
}
