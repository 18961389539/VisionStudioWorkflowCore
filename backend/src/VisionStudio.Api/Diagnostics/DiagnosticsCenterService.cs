using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Api.Diagnostics;

public sealed record DiagnosticsSummary(
    int Total,
    int Healthy,
    int Degraded,
    int Faulted,
    int Offline,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Normalizes health from all runtime asset managers, emits state/error/recovery events,
/// keeps a bounded in-memory diagnostic event stream, and pushes deltas to SignalR clients.
/// It never owns or mutates device lifecycle.
/// </summary>
public sealed class DiagnosticsCenterService(
    IEnumerable<IAssetHealthProvider> providers,
    IHubContext<DiagnosticsHub> hub,
    ILogger<DiagnosticsCenterService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly ConcurrentDictionary<string, AssetHealthSnapshot> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<AssetEventEnvelope> _events = new();
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue("Diagnostics:PollIntervalMs", 1000), 200, 60_000));
    private readonly int _maxEvents = Math.Clamp(configuration.GetValue("Diagnostics:MaxEvents", 1000), 100, 10000);

    public IReadOnlyList<AssetHealthSnapshot> Assets() => _assets.Values
        .OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<AssetEventEnvelope> Events(int take = 200, AssetKind? kind = null, string? assetId = null)
        => _events.Reverse()
            .Where(x => kind is null || x.Kind == kind)
            .Where(x => string.IsNullOrWhiteSpace(assetId) || x.AssetId.Equals(assetId, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(take, 1, 1000))
            .ToArray();

    public DiagnosticsSummary Summary()
    {
        var assets = Assets();
        return new DiagnosticsSummary(
            assets.Count,
            assets.Count(x => x.Level == AssetHealthLevel.Healthy),
            assets.Count(x => x.Level == AssetHealthLevel.Degraded),
            assets.Count(x => x.Level == AssetHealthLevel.Faulted),
            assets.Count(x => x.Level == AssetHealthLevel.Offline),
            DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SampleAsync(stoppingToken);
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SampleAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task SampleAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            IReadOnlyList<AssetHealthSnapshot> snapshots;
            try { snapshots = provider.Snapshot(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Diagnostics provider {ProviderId} snapshot failed", provider.ProviderId);
                continue;
            }

            foreach (var current in snapshots)
            {
                seen.Add(current.Key);
                _assets.TryGetValue(current.Key, out var previous);
                _assets[current.Key] = current;
                var evt = CreateChangeEvent(previous, current);
                if (evt is not null) await PublishAsync(evt, ct);
            }
        }

        foreach (var pair in _assets.ToArray())
        {
            if (seen.Contains(pair.Key)) continue;
            if (!_assets.TryRemove(pair.Key, out var removed)) continue;
            await PublishAsync(new AssetEventEnvelope(
                Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, removed.Kind, removed.Id, removed.Name,
                "AssetRemoved", AssetEventSeverity.Warning, AssetHealthLevel.Offline, "Removed",
                $"Asset '{removed.Name}' was removed from the runtime registry.", removed.State), ct);
        }

        await SafeBroadcastAsync("diagnosticsSummary", Summary(), ct);
    }

    private static AssetEventEnvelope? CreateChangeEvent(AssetHealthSnapshot? previous, AssetHealthSnapshot current)
    {
        if (previous is null)
            return NewEvent(current, "AssetDiscovered", AssetEventSeverity.Info, $"Asset '{current.Name}' discovered.", null);

        if (previous.Level != current.Level)
        {
            var recovered = (previous.Level is AssetHealthLevel.Faulted or AssetHealthLevel.Degraded) && current.Level == AssetHealthLevel.Healthy;
            var severity = current.Level switch
            {
                AssetHealthLevel.Faulted => AssetEventSeverity.Error,
                AssetHealthLevel.Degraded => AssetEventSeverity.Warning,
                AssetHealthLevel.Offline => AssetEventSeverity.Warning,
                _ => AssetEventSeverity.Info
            };
            return NewEvent(current, recovered ? "Recovered" : "HealthChanged", severity,
                $"{current.Name}: {previous.Level} → {current.Level} ({current.State}).", previous.State);
        }

        if (!string.Equals(previous.State, current.State, StringComparison.OrdinalIgnoreCase))
            return NewEvent(current, "StateChanged", AssetEventSeverity.Info,
                $"{current.Name}: {previous.State} → {current.State}.", previous.State);

        if (!string.Equals(previous.Error, current.Error, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(current.Error))
                return NewEvent(current, "ErrorChanged", AssetEventSeverity.Error, current.Error!, previous.State);
            if (!string.IsNullOrWhiteSpace(previous.Error))
                return NewEvent(current, "ErrorCleared", AssetEventSeverity.Info, $"{current.Name}: error cleared.", previous.State);
        }

        return null;
    }

    private static AssetEventEnvelope NewEvent(AssetHealthSnapshot current, string type, AssetEventSeverity severity, string message, string? previousState)
        => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, current.Kind, current.Id, current.Name,
            type, severity, current.Level, current.State, message, previousState);

    private async Task PublishAsync(AssetEventEnvelope evt, CancellationToken ct)
    {
        _events.Enqueue(evt);
        while (_events.Count > _maxEvents && _events.TryDequeue(out _)) { }
        await SafeBroadcastAsync("assetEvent", evt, ct);
        if (_assets.TryGetValue($"{evt.Kind}:{evt.AssetId}", out var asset))
            await SafeBroadcastAsync("assetHealth", asset, ct);
    }

    private async Task SafeBroadcastAsync(string method, object payload, CancellationToken ct)
    {
        try { await hub.Clients.All.SendAsync(method, payload, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Diagnostics SignalR broadcast {Method} failed; runtime control is unaffected.", method);
        }
    }
}
