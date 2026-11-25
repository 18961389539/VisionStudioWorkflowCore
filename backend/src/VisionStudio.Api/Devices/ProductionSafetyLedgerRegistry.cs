using System.Collections.Concurrent;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class ProductionSafetyLedgerRegistry
{
    private readonly ConcurrentDictionary<string, DeviceActionSafetyStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string stationId, DeviceActionSafetyStore store)
        => _stores[ProductionRuntimeStationIds.Normalize(stationId)] = store;

    public IReadOnlyList<(string StationId, DeviceActionSafetyState State)> UnresolvedStates()
        => _stores.Select(pair => (pair.Key, pair.Value.Load()))
            .Where(item => item.Item2 is { } state && DeviceActionSafetyStore.IsUnresolved(state))
            .Select(item => (item.Key, item.Item2!))
            .ToArray();

    public IReadOnlyList<string> LedgerPaths()
        => _stores.Values.SelectMany(store => new[] { store.StatePath, store.ResolutionDirectory })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void EnsureNoConflictingUnresolvedState(
        string action,
        IReadOnlyCollection<string>? deviceIds = null,
        IReadOnlyCollection<string>? robotIds = null,
        IReadOnlyCollection<string>? cameraIds = null,
        bool allowEmptyTarget = false)
    {
        var target = ResourceSet(deviceIds, robotIds, cameraIds);
        foreach (var (stationId, state) in UnresolvedStates())
        {
            var guarded = ResourceSet(state.DeviceIds, state.RobotIds, state.CameraIds);
            if (state.Unreadable || guarded.Count == 0 || (!allowEmptyTarget && target.Count == 0) || target.Overlaps(guarded))
                throw new ApiConflictException(
                    $"Cannot perform '{action}': station '{stationId}' has an unresolved device-action intent " +
                    $"({state.Phase}: {state.Reason}). Reconcile the affected hardware before issuing another action.");
        }
    }

    private static HashSet<string> ResourceSet(
        IEnumerable<string>? devices,
        IEnumerable<string>? robots,
        IEnumerable<string>? cameras)
        => (devices ?? [])
            .Concat(robots ?? [])
            .Concat(cameras ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
