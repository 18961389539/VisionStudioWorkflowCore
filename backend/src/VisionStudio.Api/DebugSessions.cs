using System.Collections.Concurrent;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record DebugSessionCreateRequest(WorkflowDefinition Workflow, VisionRunOptions Options);

public sealed record DebugSessionRunNodeRequest(string NodeId);

/// <summary>
/// Process-wide owner of live debug sessions: enforces a capacity limit and an idle timeout, holds the
/// hardware lease for the whole session lifetime (paused sessions keep devices owned), and guarantees
/// that native vision resources (Mats, plan leases) and hardware leases are released on delete,
/// eviction, idle expiry or host shutdown.
/// </summary>
public sealed class DebugSessionService : IDisposable
{
    private sealed class Entry(WorkflowDebugSession session, DeviceLease lease)
    {
        public WorkflowDebugSession Session { get; } = session;
        public DeviceLease Lease { get; } = lease;
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly WorkflowDebugSessionService _engine;
    private readonly RuntimeDependencyManifestService _dependencies;
    private readonly DeviceLeaseRegistry _leases;
    private readonly int _maxSessions;
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger<DebugSessionService> _logger;
    private readonly Timer _sweeper;
    private bool _disposed;

    public DebugSessionService(
        WorkflowDebugSessionService engine,
        RuntimeDependencyManifestService dependencies,
        DeviceLeaseRegistry leases,
        IConfiguration configuration,
        ILogger<DebugSessionService> logger)
    {
        _engine = engine;
        _dependencies = dependencies;
        _leases = leases;
        _maxSessions = Math.Clamp(configuration.GetValue("DebugSessions:MaxSessions", 4), 1, 32);
        _idleTimeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("DebugSessions:IdleTimeoutSeconds", 300), 30, 3600));
        _logger = logger;
        _sweeper = new Timer(_ => SweepIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<(string SessionId, WorkflowRunResult Result)> CreateAsync(
        WorkflowDefinition workflow,
        VisionRunOptions options,
        CancellationToken ct)
    {
        SweepIdle();
        EvictIfFull();

        // The lease must cover the engine's initial run to the first breakpoint (it can already touch
        // hardware), so the id is reserved here and the lease owner matches the session for its lifetime.
        var sessionId = Guid.NewGuid().ToString("N");
        WorkflowRuntimeReferences references;
        try
        {
            references = await _dependencies.ExtractReferencesAsync(workflow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ApiConflictException(
                $"Cannot arbitrate debug session hardware references for workflow '{workflow.Id}': {ex.Message}", ex);
        }

        var lease = _leases.AcquireOrThrow(
            DeviceLeaseOwner.DebugSession(sessionId),
            DeviceLeaseResource.FromReferences(references),
            $"Cannot start a debug session for workflow '{workflow.Id}'");

        (WorkflowDebugSession Session, WorkflowRunResult Result) started;
        try
        {
            started = await _engine.StartAsync(workflow, options, ct, sessionId);
        }
        catch (InvalidOperationException ex)
        {
            lease.Dispose();
            throw new ApiValidationException(ex.Message, ex);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        _sessions[started.Session.SessionId] = new Entry(started.Session, lease);
        _logger.LogInformation(
            "Debug session {SessionId} started: halt={HaltNodeId}, reports={ReportCount}.",
            started.Session.SessionId, started.Result.HaltNodeId ?? "-", started.Result.NodeReports.Count);
        return (started.Session.SessionId, started.Result);
    }

    public async Task<WorkflowRunResult> ContinueAsync(string sessionId, CancellationToken ct)
    {
        var entry = Require(sessionId);
        EnsureLeaseHeld(entry, "continue");
        try
        {
            return await _engine.ContinueAsync(entry.Session, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiConflictException(ex.Message, ex);
        }
    }

    public async Task<WorkflowRunResult> RunNodeAsync(string sessionId, string nodeId, CancellationToken ct)
    {
        var entry = Require(sessionId);
        if (string.IsNullOrWhiteSpace(nodeId) ||
            !entry.Session.Workflow.Nodes.Any(n => n.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase)))
            throw new ApiNotFoundException($"Node '{nodeId}' is not part of debug session '{sessionId}'.");
        EnsureLeaseHeld(entry, "run-node");
        try
        {
            return await _engine.RunNodeWithCachedInputsAsync(entry.Session, nodeId, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiConflictException(ex.Message, ex);
        }
    }

    public DebugSessionSnapshot Snapshot(string sessionId)
    {
        var entry = Require(sessionId);
        entry.Session.Touch();
        return entry.Session.Snapshot();
    }

    public bool Delete(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return false;
        Release(entry, "deleted");
        return true;
    }

    public int ActiveSessionCount => _sessions.Count;

    private Entry Require(string sessionId)
        => _sessions.TryGetValue(sessionId, out var entry)
            ? entry
            : throw new ApiNotFoundException($"Debug session '{sessionId}' does not exist (it may have expired).");

    /// <summary>
    /// Paused sessions keep hardware leased; resuming must assert the lease is still held so a session
    /// can never touch hardware after its ownership was lost.
    /// </summary>
    private void EnsureLeaseHeld(Entry entry, string operation)
    {
        var lease = entry.Lease;
        if (!lease.IsReleased && _leases.IsHeldBy(lease.Owner, lease.Resources)) return;
        throw new ApiConflictException(
            $"Debug session '{entry.Session.SessionId}' no longer holds its hardware lease; the '{operation}' request was blocked. Delete the session and start a new one.");
    }

    private void EvictIfFull()
    {
        while (_sessions.Count >= _maxSessions)
        {
            var oldest = _sessions.Values.OrderBy(x => x.Session.LastActivityAt).FirstOrDefault();
            if (oldest is null) return;
            if (_sessions.TryRemove(oldest.Session.SessionId, out var evicted))
                Release(evicted, "evicted (capacity)");
        }
    }

    private void SweepIdle()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _sessions.Values)
        {
            if (now - entry.Session.LastActivityAt <= _idleTimeout) continue;
            if (_sessions.TryRemove(entry.Session.SessionId, out var expired))
                Release(expired, "expired (idle)");
        }
    }

    private void Release(Entry entry, string reason)
    {
        try
        {
            // Release native vision resources before the hardware lease so the next owner cannot start
            // using an asset that is still being torn down here.
            entry.Session.Dispose();
            _logger.LogInformation("Debug session {SessionId} released: {Reason}.", entry.Session.SessionId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Debug session {SessionId} release failed.", entry.Session.SessionId);
        }
        finally
        {
            entry.Lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sweeper.Dispose();
        foreach (var entry in _sessions.Values) Release(entry, "host shutdown");
        _sessions.Clear();
    }
}