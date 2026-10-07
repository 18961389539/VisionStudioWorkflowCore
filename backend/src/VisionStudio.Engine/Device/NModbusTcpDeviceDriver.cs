using System.Buffers.Binary;
using System.Net.Sockets;
using NModbus;
using NModbus.Device;

namespace VisionStudio.Engine.Device;

public enum Modbus32BitOrder
{
    ABCD,
    CDAB,
    BADC,
    DCBA
}

public sealed record NModbusTcpDeviceOptions(
    string Id,
    string Name,
    string Host,
    int Port = 502,
    byte UnitId = 1,
    int TimeoutMs = 3000,
    Modbus32BitOrder RegisterOrder = Modbus32BitOrder.ABCD,
    IReadOnlyList<DeviceTagDefinition>? Tags = null);

public enum ModbusArea
{
    Coil,
    DiscreteInput,
    HoldingRegister,
    InputRegister
}

public readonly record struct ModbusAddress(ModbusArea Area, ushort Address)
{
    public static ModbusAddress Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DeviceAddressException("Modbus address is empty. Use C:0, DI:0, HR:0 or IR:0 (0-based).");
        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !ushort.TryParse(parts[1], out var address))
            throw new DeviceAddressException($"Invalid Modbus address '{text}'. Expected C:0, DI:0, HR:0 or IR:0.");
        var area = parts[0].ToUpperInvariant() switch
        {
            "C" or "COIL" or "COILS" => ModbusArea.Coil,
            "DI" or "DISCRETE" or "DISCRETEINPUT" => ModbusArea.DiscreteInput,
            "HR" or "HOLDING" or "HOLDINGREGISTER" => ModbusArea.HoldingRegister,
            "IR" or "INPUT" or "INPUTREGISTER" => ModbusArea.InputRegister,
            _ => throw new DeviceAddressException($"Unknown Modbus area '{parts[0]}'.")
        };
        return new ModbusAddress(area, address);
    }
}

/// <summary>
/// Real Modbus TCP client built on NModbus. Addresses are deliberately protocol-specific
/// only inside the driver; Workflow still refers to stable tag IDs.
/// V0.19 scalar mapping: Boolean = coil/discrete, Integer = signed Int32 (2 registers),
/// Double = IEEE754 Float32 (2 registers). String is intentionally rejected.
/// </summary>
public sealed class NModbusTcpDeviceDriver : IDeviceDriver, IDeviceBatchDriver, IDeviceDiagnosticsProvider
{
    private readonly NModbusTcpDeviceOptions _options;
    private readonly Dictionary<string, DeviceTagDefinition> _tags;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly ProtocolDiagnosticsCounter _diagnostics = new();
    private TcpClient? _client;
    private IModbusMaster? _master;
    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
    private string? _error;

    public NModbusTcpDeviceDriver(NModbusTcpDeviceOptions options)
    {
        _options = options with
        {
            Port = Math.Clamp(options.Port, 1, 65535),
            TimeoutMs = Math.Clamp(options.TimeoutMs, 100, 60000),
            Tags = options.Tags ?? Array.Empty<DeviceTagDefinition>()
        };
        _tags = _options.Tags!.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in _tags.Values) ValidateDefinition(tag);
    }

    public string Id => _options.Id;
    public string Name => _options.Name;
    public string Vendor => "NModbus";
    public string Model => "Modbus TCP Client";
    public string Driver => "NModbusTcpDeviceDriver";
    public string Protocol => "Modbus TCP";
    public string Endpoint => $"tcp://{_options.Host}:{_options.Port} unit={_options.UnitId} order={_options.RegisterOrder}";
    public DeviceConnectionState ConnectionState { get { lock (_stateGate) return _state; } }
    public string? Error { get { lock (_stateGate) return _error; } }
    public DeviceDriverCapabilities Capabilities { get; } = new(true, true, true, true, false);
    public IReadOnlyList<DeviceTagDefinition> Tags => _options.Tags!;
    public DeviceProtocolDiagnostics ProtocolDiagnostics => _diagnostics.Snapshot();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionState == DeviceConnectionState.Connected) return;
        SetState(DeviceConnectionState.Connecting, null);
        try
        {
            var client = new TcpClient { ReceiveTimeout = _options.TimeoutMs, SendTimeout = _options.TimeoutMs, NoDelay = true };
            await client.ConnectAsync(_options.Host, _options.Port, cancellationToken);
            var master = new ModbusFactory().CreateMaster(client);
            lock (_stateGate)
            {
                _client?.Dispose();
                _master?.Dispose();
                _client = client;
                _master = master;
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
            _master?.Dispose();
            _client?.Dispose();
            _master = null;
            _client = null;
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
            return await _diagnostics.TrackAsync(async () =>
            {
                var value = await ReadValueAsync(tag, cancellationToken);
                return Sample(tag, value);
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
            return await _diagnostics.TrackAsync(async () =>
            {
                var output = new Dictionary<string, DeviceTagSample>(StringComparer.OrdinalIgnoreCase);
                foreach (var areaGroup in tags.GroupBy(t => ModbusAddress.Parse(t.Address).Area))
                {
                    foreach (var window in BuildReadWindows(areaGroup.ToArray()))
                        await ReadWindowAsync(window, output, cancellationToken);
                }
                return (IReadOnlyList<DeviceTagSample>)tags.Select(t => output[t.Id]).ToArray();
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
            await _diagnostics.TrackAsync(() => WriteValueAsync(tag, value, cancellationToken));
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
        var items = values.Select(kv => (Tag: RequireTag(kv.Key), kv.Value)).ToArray();
        foreach (var item in items)
            if (!item.Tag.Writable) throw new DeviceWriteNotAllowedException($"Tag '{item.Tag.Id}' is read-only.");

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            await _diagnostics.TrackAsync(async () =>
            {
                await WriteBatchValuesAsync(items, cancellationToken);
            }, batchWrite: true);
        }
        catch (Exception ex)
        {
            SetProtocolError(ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    private async Task WriteBatchValuesAsync((DeviceTagDefinition Tag, object? Value)[] items, CancellationToken ct)
    {
        var master = RequireMaster();
        var mapped = items.Select(x => (x.Tag, x.Value, Point: ModbusAddress.Parse(x.Tag.Address)))
            .OrderBy(x => x.Point.Area).ThenBy(x => x.Point.Address).ToArray();

        foreach (var areaGroup in mapped.GroupBy(x => x.Point.Area))
        {
            if (areaGroup.Key == ModbusArea.Coil)
            {
                var sorted = areaGroup.OrderBy(x => x.Point.Address).ToArray();
                var group = new List<(DeviceTagDefinition Tag, object? Value, ModbusAddress Point)>();
                foreach (var item in sorted)
                {
                    if (group.Count > 0 && item.Point.Address != group[^1].Point.Address + 1)
                    {
                        await FlushCoilsAsync(group);
                        group.Clear();
                    }
                    group.Add(item);
                }
                if (group.Count > 0) await FlushCoilsAsync(group);
                continue;
            }

            if (areaGroup.Key != ModbusArea.HoldingRegister)
                throw new DeviceWriteNotAllowedException($"Modbus area {areaGroup.Key} is read-only.");

            var registers = areaGroup.OrderBy(x => x.Point.Address).ToArray();
            var run = new List<(DeviceTagDefinition Tag, object? Value, ModbusAddress Point)>();
            var expectedNext = -1;
            foreach (var item in registers)
            {
                if (run.Count > 0 && item.Point.Address != expectedNext)
                {
                    await FlushRegistersAsync(run);
                    run.Clear();
                }
                run.Add(item);
                expectedNext = item.Point.Address + RegisterCount(item.Tag);
            }
            if (run.Count > 0) await FlushRegistersAsync(run);
        }

        async Task FlushCoilsAsync(List<(DeviceTagDefinition Tag, object? Value, ModbusAddress Point)> group)
        {
            foreach (var item in group)
                if (item.Tag.DataType != DeviceTagDataType.Boolean)
                    throw new DeviceAddressException($"Coil tag '{item.Tag.Id}' must be Boolean.");
            var values = group.Select(x => (bool)DeviceValueConversion.ConvertTo(DeviceTagDataType.Boolean, x.Value)!).ToArray();
            if (values.Length == 1)
                await master.WriteSingleCoilAsync(_options.UnitId, group[0].Point.Address, values[0]);
            else
                await master.WriteMultipleCoilsAsync(_options.UnitId, group[0].Point.Address, values);
        }

        async Task FlushRegistersAsync(List<(DeviceTagDefinition Tag, object? Value, ModbusAddress Point)> group)
        {
            var payload = group.SelectMany(x => EncodeRegisters(x.Tag, DeviceValueConversion.ConvertTo(x.Tag.DataType, x.Value), _options.RegisterOrder)).ToArray();
            await master.WriteMultipleRegistersAsync(_options.UnitId, group[0].Point.Address, payload);
        }
    }

    private async Task<object?> ReadValueAsync(DeviceTagDefinition tag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var master = RequireMaster();
        var point = ModbusAddress.Parse(tag.Address);
        return point.Area switch
        {
            ModbusArea.Coil => DecodeBoolean(tag, (await master.ReadCoilsAsync(_options.UnitId, point.Address, 1))[0]),
            ModbusArea.DiscreteInput => DecodeBoolean(tag, (await master.ReadInputsAsync(_options.UnitId, point.Address, 1))[0]),
            ModbusArea.HoldingRegister => DecodeRegisters(tag, await master.ReadHoldingRegistersAsync(_options.UnitId, point.Address, RegisterCount(tag)), _options.RegisterOrder),
            ModbusArea.InputRegister => DecodeRegisters(tag, await master.ReadInputRegistersAsync(_options.UnitId, point.Address, RegisterCount(tag)), _options.RegisterOrder),
            _ => throw new DeviceAddressException($"Unsupported Modbus area for '{tag.Address}'.")
        };
    }

    private async Task WriteValueAsync(DeviceTagDefinition tag, object? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var master = RequireMaster();
        var point = ModbusAddress.Parse(tag.Address);
        var converted = DeviceValueConversion.ConvertTo(tag.DataType, value);
        if (point.Area == ModbusArea.Coil)
        {
            if (tag.DataType != DeviceTagDataType.Boolean)
                throw new DeviceAddressException($"Coil tag '{tag.Id}' must be Boolean.");
            await master.WriteSingleCoilAsync(_options.UnitId, point.Address, (bool)converted!);
            return;
        }
        if (point.Area != ModbusArea.HoldingRegister)
            throw new DeviceWriteNotAllowedException($"Modbus area {point.Area} is read-only for tag '{tag.Id}'.");
        await master.WriteMultipleRegistersAsync(_options.UnitId, point.Address, EncodeRegisters(tag, converted, _options.RegisterOrder));
    }

    private sealed record ReadWindow(ModbusArea Area, ushort Start, ushort Count, IReadOnlyList<DeviceTagDefinition> Tags);

    private static IEnumerable<ReadWindow> BuildReadWindows(IReadOnlyList<DeviceTagDefinition> tags)
    {
        var sorted = tags.Select(t => (Tag: t, Point: ModbusAddress.Parse(t.Address), Count: RegisterCount(t)))
            .OrderBy(x => x.Point.Address).ToArray();
        if (sorted.Length == 0) yield break;

        var current = new List<DeviceTagDefinition>();
        var area = sorted[0].Point.Area;
        ushort start = sorted[0].Point.Address;
        var end = start + PointWidth(sorted[0].Tag, area);
        foreach (var item in sorted)
        {
            var itemStart = item.Point.Address;
            var itemEnd = itemStart + PointWidth(item.Tag, area);
            var maxPoints = area is ModbusArea.Coil or ModbusArea.DiscreteInput ? 1900 : 120;
            if (current.Count > 0 && (itemStart > end + 2 || itemEnd - start > maxPoints))
            {
                yield return new ReadWindow(area, start, checked((ushort)(end - start)), current.ToArray());
                current.Clear();
                start = itemStart;
                end = itemEnd;
            }
            current.Add(item.Tag);
            if (itemEnd > end) end = itemEnd;
        }
        if (current.Count > 0)
            yield return new ReadWindow(area, start, checked((ushort)(end - start)), current.ToArray());
    }

    private async Task ReadWindowAsync(ReadWindow window, IDictionary<string, DeviceTagSample> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var master = RequireMaster();
        if (window.Area is ModbusArea.Coil or ModbusArea.DiscreteInput)
        {
            var bits = window.Area == ModbusArea.Coil
                ? await master.ReadCoilsAsync(_options.UnitId, window.Start, window.Count)
                : await master.ReadInputsAsync(_options.UnitId, window.Start, window.Count);
            foreach (var tag in window.Tags)
            {
                var p = ModbusAddress.Parse(tag.Address);
                output[tag.Id] = Sample(tag, DecodeBoolean(tag, bits[p.Address - window.Start]));
            }
            return;
        }

        var regs = window.Area == ModbusArea.HoldingRegister
            ? await master.ReadHoldingRegistersAsync(_options.UnitId, window.Start, window.Count)
            : await master.ReadInputRegistersAsync(_options.UnitId, window.Start, window.Count);
        foreach (var tag in window.Tags)
        {
            var p = ModbusAddress.Parse(tag.Address);
            var offset = p.Address - window.Start;
            var count = RegisterCount(tag);
            output[tag.Id] = Sample(tag, DecodeRegisters(tag, regs.AsSpan(offset, count).ToArray(), _options.RegisterOrder));
        }
    }

    private static ushort PointWidth(DeviceTagDefinition tag, ModbusArea area)
        => area is ModbusArea.Coil or ModbusArea.DiscreteInput ? (ushort)1 : RegisterCount(tag);

    private static ushort RegisterCount(DeviceTagDefinition tag) => tag.DataType switch
    {
        DeviceTagDataType.Boolean => 1,
        DeviceTagDataType.Integer => 2,
        DeviceTagDataType.Double => 2,
        DeviceTagDataType.String => throw new DeviceAddressException($"Real NModbus driver does not support String tag '{tag.Id}' in V0.19."),
        _ => 1
    };

    private static object DecodeBoolean(DeviceTagDefinition tag, bool value)
    {
        if (tag.DataType != DeviceTagDataType.Boolean)
            throw new DeviceAddressException($"Bit area tag '{tag.Id}' must use Boolean data type.");
        return value;
    }

    private static object DecodeRegisters(DeviceTagDefinition tag, ushort[] registers, Modbus32BitOrder order)
    {
        if (tag.DataType == DeviceTagDataType.Boolean) return registers[0] != 0;
        Span<byte> wire = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(wire[..2], registers[0]);
        if (registers.Length > 1) BinaryPrimitives.WriteUInt16BigEndian(wire[2..], registers[1]);
        Span<byte> canonical = stackalloc byte[4];
        Normalize32BitOrder(wire, canonical, order);
        return tag.DataType switch
        {
            DeviceTagDataType.Integer => (long)BinaryPrimitives.ReadInt32BigEndian(canonical),
            DeviceTagDataType.Double => (double)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(canonical)),
            DeviceTagDataType.String => throw new DeviceAddressException($"String tag '{tag.Id}' is unsupported by the V0.19 NModbus scalar mapper."),
            _ => registers[0]
        };
    }

    private static ushort[] EncodeRegisters(DeviceTagDefinition tag, object? value, Modbus32BitOrder order)
    {
        if (tag.DataType == DeviceTagDataType.Boolean) return [(bool)DeviceValueConversion.ConvertTo(DeviceTagDataType.Boolean, value)! ? (ushort)1 : (ushort)0];
        Span<byte> canonical = stackalloc byte[4];
        switch (tag.DataType)
        {
            case DeviceTagDataType.Integer:
                BinaryPrimitives.WriteInt32BigEndian(canonical, checked((int)(long)DeviceValueConversion.ConvertTo(DeviceTagDataType.Integer, value)!));
                break;
            case DeviceTagDataType.Double:
                BinaryPrimitives.WriteInt32BigEndian(canonical, BitConverter.SingleToInt32Bits(checked((float)(double)DeviceValueConversion.ConvertTo(DeviceTagDataType.Double, value)!)));
                break;
            default:
                throw new DeviceAddressException($"String tag '{tag.Id}' is unsupported by the V0.19 NModbus scalar mapper.");
        }
        Span<byte> wire = stackalloc byte[4];
        Normalize32BitOrder(canonical, wire, order);
        return [BinaryPrimitives.ReadUInt16BigEndian(wire[..2]), BinaryPrimitives.ReadUInt16BigEndian(wire[2..])];
    }

    // These four permutations are involutions, so the same mapping converts wire→ABCD and ABCD→wire.
    private static void Normalize32BitOrder(ReadOnlySpan<byte> source, Span<byte> target, Modbus32BitOrder order)
    {
        var map = order switch
        {
            Modbus32BitOrder.ABCD => new[] { 0, 1, 2, 3 },
            Modbus32BitOrder.CDAB => new[] { 2, 3, 0, 1 },
            Modbus32BitOrder.BADC => new[] { 1, 0, 3, 2 },
            Modbus32BitOrder.DCBA => new[] { 3, 2, 1, 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        for (var i = 0; i < 4; i++) target[i] = source[map[i]];
    }

    private static void ValidateDefinition(DeviceTagDefinition tag)
    {
        var point = ModbusAddress.Parse(tag.Address);
        if (point.Area is ModbusArea.Coil or ModbusArea.DiscreteInput && tag.DataType != DeviceTagDataType.Boolean)
            throw new DeviceAddressException($"Modbus bit area '{tag.Address}' requires Boolean data type for tag '{tag.Id}'.");
        if (tag.DataType == DeviceTagDataType.String)
            throw new DeviceAddressException($"String tag '{tag.Id}' is not supported by NModbusTcpDeviceDriver V0.19.");
        if (tag.Writable && point.Area is ModbusArea.DiscreteInput or ModbusArea.InputRegister)
            throw new DeviceAddressException($"Tag '{tag.Id}' maps to read-only Modbus area {point.Area}.");
    }

    private DeviceTagDefinition RequireTag(string id)
        => _tags.TryGetValue(id, out var tag) ? tag : throw new DeviceTagNotFoundException($"Device '{Id}' has no tag '{id}'.");

    private IModbusMaster RequireMaster()
        => _master ?? throw new DeviceDisconnectedException($"Device '{Id}' is not connected.");

    private void EnsureConnected()
    {
        if (ConnectionState != DeviceConnectionState.Connected || _master is null || _client is null || !_client.Connected)
            throw new DeviceDisconnectedException($"Device '{Id}' is {ConnectionState}.");
    }

    private DeviceTagSample Sample(DeviceTagDefinition tag, object? value)
        => new(Id, tag.Id, tag.DataType, value, DeviceTagQuality.Good, DateTimeOffset.UtcNow);

    private void SetProtocolError(Exception ex)
    {
        if (ex is SocketException or IOException or ObjectDisposedException)
            SetState(DeviceConnectionState.Faulted, ex.Message);
        else
            lock (_stateGate) _error = ex.Message;
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
