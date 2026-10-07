using System.Collections.ObjectModel;
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
    public bool AsBoolean()
    {
        if (Value is bool b) return b;
        if (Value is string s && bool.TryParse(s, out var parsed)) return parsed;
        if (Value is IConvertible convertible) return Math.Abs(convertible.ToDouble(null)) > double.Epsilon;
        throw new InvalidOperationException($"Tag '{TagId}' value cannot be converted to Boolean.");
    }

    public double AsDouble()
    {
        if (Value is bool b) return b ? 1 : 0;
        if (Value is IConvertible convertible) return convertible.ToDouble(null);
        throw new InvalidOperationException($"Tag '{TagId}' value cannot be converted to Double.");
    }

    public long AsInteger() => checked((long)Math.Round(AsDouble()));
    public string AsText() => Value?.ToString() ?? string.Empty;
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
                    IConvertible c => Math.Abs(c.ToDouble(null)) > double.Epsilon,
                    _ => throw new FormatException()
                },
                DeviceTagDataType.Integer => value switch
                {
                    bool b => b ? 1L : 0L,
                    IConvertible c => checked((long)Math.Round(c.ToDouble(null))),
                    _ => throw new FormatException()
                },
                DeviceTagDataType.Double => value switch
                {
                    bool b => b ? 1d : 0d,
                    IConvertible c => c.ToDouble(null),
                    _ => throw new FormatException()
                },
                DeviceTagDataType.String => value.ToString() ?? string.Empty,
                _ => value
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"Value '{value}' cannot be converted to {type}.", ex);
        }
    }

    public static IReadOnlyDictionary<string, DeviceTagSample> SnapshotDictionary(IDictionary<string, DeviceTagSample> source)
        => new ReadOnlyDictionary<string, DeviceTagSample>(new Dictionary<string, DeviceTagSample>(source, StringComparer.OrdinalIgnoreCase));
}
