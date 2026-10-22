using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace VisionStudio.Engine.Device;

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

public enum DeviceTagQuality
{
    Good,
    Uncertain,
    Bad,
    Disconnected
}

public enum DeviceTagDataType
{
    Boolean,
    Integer,
    Double,
    String
}

public sealed record DeviceTagDefinition(
    string Id,
    string Name,
    string Address,
    DeviceTagDataType DataType,
    bool Writable = false,
    string? Unit = null,
    string? Description = null);

public sealed record DeviceTagSample(
    string DeviceId,
    string TagId,
    DeviceTagDataType DataType,
    object? Value,
    DeviceTagQuality Quality,
    DateTimeOffset Timestamp,
    string? Error = null)
{
    public bool AsBoolean() => DeviceValueConversion.ConvertTo(DeviceTagDataType.Boolean, Value) is bool value
        ? value : throw new InvalidOperationException($"Tag '{TagId}' value cannot be converted to Boolean.");

    public double AsDouble() => DeviceValueConversion.ConvertTo(DeviceTagDataType.Double, Value) is double value
        ? value : throw new InvalidOperationException($"Tag '{TagId}' value cannot be converted to Double.");

    public long AsInteger() => DeviceValueConversion.ConvertTo(DeviceTagDataType.Integer, Value) is long value
        ? value : throw new InvalidOperationException($"Tag '{TagId}' value cannot be converted to Integer.");
    public string AsText() => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty;
}

public sealed record DeviceRuntimeSettings(
    int PollIntervalMs = 100,
    int ReconnectDelayMs = 1000,
    int HeartbeatIntervalMs = 500,
    string HostHeartbeatTagId = "hostHeartbeat",
    string DeviceHeartbeatTagId = "deviceHeartbeat")
{
    public DeviceRuntimeSettings Normalize() => this with
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 20, 60000),
        ReconnectDelayMs = Math.Clamp(ReconnectDelayMs, 100, 60000),
        HeartbeatIntervalMs = Math.Clamp(HeartbeatIntervalMs, 100, 60000)
    };
}

public sealed record DeviceRuntimeStats(
    long PollCycles,
    long BatchReadCycles,
    long ReadErrors,
    long WriteErrors,
    long ReconnectCount,
    double ActualPollHz,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? LastGoodReadAt,
    DateTimeOffset? LastDeviceHeartbeatAt,
    bool HostHeartbeat,
    bool DeviceHeartbeat,
    string? RuntimeError = null);

public sealed record DeviceDriverCapabilities(
    bool SupportsBatchRead = false,
    bool SupportsBatchWrite = false,
    bool SupportsDiagnostics = false,
    bool RealIo = false,
    bool SupportsString = false);

public sealed record DeviceProtocolDiagnostics(
    long Transactions = 0,
    long BatchReads = 0,
    long BatchWrites = 0,
    double LastRoundTripMs = 0,
    double AverageRoundTripMs = 0,
    DateTimeOffset? LastTransactionAt = null,
    string? LastProtocolError = null);

public sealed record DeviceDescriptor(
    string Id,
    string Name,
    string Vendor,
    string Model,
    string Driver,
    string Protocol,
    string Endpoint,
    DeviceConnectionState ConnectionState,
    DeviceRuntimeSettings Settings,
    DeviceRuntimeStats Stats,
    DeviceDriverCapabilities Capabilities,
    DeviceProtocolDiagnostics Diagnostics,
    IReadOnlyList<DeviceTagDefinition> Tags,
    IReadOnlyDictionary<string, DeviceTagSample> Values,
    string? Error,
    DateTimeOffset UpdatedAt);

public interface IDeviceDriver : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Vendor { get; }
    string Model { get; }
    string Driver { get; }
    string Protocol { get; }
    string Endpoint { get; }
    DeviceConnectionState ConnectionState { get; }
    string? Error { get; }
    DeviceDriverCapabilities Capabilities { get; }
    IReadOnlyList<DeviceTagDefinition> Tags { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default);
    Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional protocol optimization. DeviceManager uses this interface for a single polling
/// transaction plan instead of issuing one protocol request per tag.
/// </summary>
public interface IDeviceBatchDriver
{
    Task<IReadOnlyList<DeviceTagSample>> ReadManyAsync(
        IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken = default);

    Task WriteManyAsync(
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
}

public interface IDeviceDiagnosticsProvider
{
    DeviceProtocolDiagnostics ProtocolDiagnostics { get; }
}

public sealed class DeviceTagNotFoundException(string message) : KeyNotFoundException(message);
public sealed class DeviceDisconnectedException(string message) : InvalidOperationException(message);
public sealed class DeviceWriteNotAllowedException(string message) : InvalidOperationException(message);
public sealed class DeviceAddressException(string message) : FormatException(message);

/// <summary>
/// R02（2026-10-12 复审）：协议操作超过配置预算未完成（对端接受连接但不回复、半包等）。
/// 抛出前驱动已关闭/隔离底层连接，挂起的 I/O 不会继续占用传输层。超时后设备动作是否已到达
/// 对端**不保证**——调用方必须按"结果未知"处理（设备安全账本），不得未经状态核对自动重发。
/// </summary>
public sealed class DeviceProtocolTimeoutException(string message) : TimeoutException(message);

public static class DeviceValueConversion
{
    public static object? ConvertTo(DeviceTagDataType type, object? value)
    {
        if (value is null) return null;
        if (value is JsonElement json)
        {
            value = json.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when json.TryGetInt64(out var i) => i,
                JsonValueKind.Number when type == DeviceTagDataType.Integer && json.TryGetDecimal(out var m) => m,
                JsonValueKind.Number when json.TryGetDouble(out var d) => d,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Null => null,
                _ => json.ToString()
            };
            if (value is null) return null;
        }

        try
        {
            return type switch
            {
                DeviceTagDataType.Boolean => value switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var parsed) => parsed,
                    IConvertible c => FiniteDouble(c) != 0d,
                    _ => throw new FormatException()
                },
                DeviceTagDataType.Integer => value switch
                {
                    bool b => b ? 1L : 0L,
                    sbyte or byte or short or ushort or int or uint or long or ulong
                        => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                    decimal m => checked((long)decimal.Round(m, 0, MidpointRounding.ToEven)),
                    string s => checked((long)decimal.Round(decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture), 0, MidpointRounding.ToEven)),
                    IConvertible c => checked((long)Math.Round(FiniteDouble(c), MidpointRounding.ToEven)),
                    _ => throw new FormatException()
                },
                DeviceTagDataType.Double => value switch
                {
                    bool b => b ? 1d : 0d,
                    IConvertible c => FiniteDouble(c),
                    _ => throw new FormatException()
                },
                DeviceTagDataType.String => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"Value '{value}' cannot be converted to {type}.", ex);
        }
    }

    private static double FiniteDouble(IConvertible value)
    {
        var number = value.ToDouble(CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new FormatException("Device numeric values must be finite.");
        return number;
    }

    public static IReadOnlyDictionary<string, DeviceTagSample> SnapshotDictionary(IDictionary<string, DeviceTagSample> source)
        => new ReadOnlyDictionary<string, DeviceTagSample>(new Dictionary<string, DeviceTagSample>(source, StringComparer.OrdinalIgnoreCase));
}
