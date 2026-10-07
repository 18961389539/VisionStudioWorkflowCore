namespace VisionStudio.Engine.Diagnostics;

public enum AssetKind
{
    Camera,
    Device,
    Robot
}

public enum AssetHealthLevel
{
    Unknown,
    Healthy,
    Degraded,
    Faulted,
    Offline
}

public sealed record AssetHealthSnapshot(
    AssetKind Kind,
    string Id,
    string Name,
    string Driver,
    AssetHealthLevel Level,
    string State,
    bool Connected,
    string Activity,
    string? Error,
    IReadOnlyDictionary<string, double> Metrics,
    DateTimeOffset UpdatedAt)
{
    public string Key => $"{Kind}:{Id}";
}

public enum AssetEventSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record AssetEventEnvelope(
    string EventId,
    DateTimeOffset Timestamp,
    AssetKind Kind,
    string AssetId,
    string AssetName,
    string Type,
    AssetEventSeverity Severity,
    AssetHealthLevel Level,
    string State,
    string Message,
    string? PreviousState = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Shared, protocol-neutral health surface. Camera/Device/Robot lifecycle remains owned by
/// their dedicated managers; Diagnostics consumes this normalized read-only projection.
/// </summary>
public interface IAssetHealthProvider
{
    string ProviderId { get; }
    IReadOnlyList<AssetHealthSnapshot> Snapshot();
}
