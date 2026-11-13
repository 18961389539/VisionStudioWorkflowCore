using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Api;

public sealed record ProductionSynchronizationGuardIssue(
    string Code,
    string GroupId,
    string Message,
    bool LiveRecoverable = false);

public sealed record ProductionSynchronizationTransportWindow(
    string EvidenceMode,
    int NativeCameras,
    int NativeEvidenceRuns,
    long ReceivedFrames,
    long LostFrames,
    long FailedFrames,
    long BufferUnderruns,
    double? NativeFrameLossRate,
    long LostPackets,
    long FailedPackets,
    long ResendRequests,
    long ResentPackets,
    long Resynchronizations,
    double? AverageThroughputMbps);

public sealed record ProductionSynchronizationGuardGroupStatus(
    string GroupId,
    string GroupName,
    bool LiveReady,
    int WindowRuns,
    int EvidenceRuns,
    int CompletedRuns,
    int FailedRuns,
    int CancelledRuns,
    double CompletionRate,
    int FrameTimeoutRuns,
    double FrameTimeoutRate,
    int ActionCommandFailureRuns,
    int ActionAckShortfallRuns,
    int SkewViolationRuns,
    int ConsecutiveFailures,
    int ConsecutiveSkewViolations,
    long EstimatedSequenceGapFrames,
    double EstimatedSequenceGapRate,
    bool Healthy,
    string? Reason,
    ProductionSynchronizationTransportWindow? Transport = null);

public sealed record ProductionSynchronizationGuardSnapshot(
    bool Enabled,
    bool Healthy,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ProductionSynchronizationGuardGroupStatus> Groups,
    IReadOnlyList<ProductionSynchronizationGuardIssue> Issues,
    string? Summary = null)
{
    public static ProductionSynchronizationGuardSnapshot Disabled { get; } = new(false, true, DateTimeOffset.MinValue, [], [], "Synchronization health guard disabled.");

    public bool LiveRecoverableOnly => !Healthy && Issues.Count > 0 && Issues.All(x => x.LiveRecoverable);
}

/// <summary>
/// Production policy over the canonical camera_sync_runs history plus current camera/group state.
/// Vendor transport counters are the primary frame-loss evidence when available. Sequence continuity
/// remains an explicit fallback for adapters/cameras that do not expose native transport statistics.
/// PTP clock quality remains the responsibility of ProductionPtpDriftGuardService.
/// </summary>
public sealed class ProductionSynchronizationHealthGuardService(CameraSynchronizationService synchronization)
{
    public async Task<ProductionSynchronizationGuardSnapshot> EvaluateAsync(
        WorkflowDefinition workflow,
        ProductionRuntimeConfig config,
        CancellationToken ct = default)
    {
        if (!config.SynchronizationHealthGuardEnabled)
            return ProductionSynchronizationGuardSnapshot.Disabled with { EvaluatedAt = DateTimeOffset.UtcNow };

        var groupIds = ResolveGroupIds(workflow);
        if (groupIds.Count == 0)
            return new ProductionSynchronizationGuardSnapshot(true, true, DateTimeOffset.UtcNow, [], [], "Workflow has no synchronized camera capture nodes.");

        var statuses = new List<ProductionSynchronizationGuardGroupStatus>();
        var issues = new List<ProductionSynchronizationGuardIssue>();

        foreach (var groupId in groupIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            CameraSynchronizationGroup group;
            try
            {
                group = await synchronization.GetAsync(groupId, ct);
            }
            catch (Exception ex)
            {
                var message = $"Synchronization group '{groupId}' cannot be resolved: {ex.Message}";
                issues.Add(new("PROD-SYNC-CAMERA-NOT-READY", groupId, message, false));
                statuses.Add(new(groupId, groupId, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, message));
                continue;
            }

            CameraSynchronizationGroupStatus live;
            try
            {
                live = await synchronization.GetStatusAsync(group.Id, ct);
            }
            catch (Exception ex)
            {
                var message = $"Live synchronization status failed for group '{group.Id}': {ex.Message}";
                issues.Add(new("PROD-SYNC-CAMERA-NOT-READY", group.Id, message, true));
                statuses.Add(new(group.Id, group.Name, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, message));
                continue;
            }

            var liveReady = live.Error is null && live.Members.Count == group.CameraIds.Count && live.Members.All(x =>
                (x.CameraState is CameraState.Open or CameraState.Streaming) &&
                (x.AcquisitionState is CameraAcquisitionState.Running or CameraAcquisitionState.WaitingTrigger));

            var history = await synchronization.ListRunsAsync(group.Id, config.SynchronizationGuardWindowRuns, ct);
            var total = history.Count;
            var completed = history.Count(IsCompleted);
            var failed = history.Count(IsFailed);
            var cancelled = history.Count(IsCancelled);
            var completionRate = total == 0 ? 1d : completed / (double)total;
            var frameTimeoutRuns = history.Count(IsFrameTimeout);
            var frameTimeoutRate = total == 0 ? 0d : frameTimeoutRuns / (double)total;
            var actionFailures = history.Count(IsActionFailure);
            var capabilities = synchronization.GetActionCommandCapabilities(group.Driver);
            var ackShortfalls = capabilities?.Acknowledgements == true
                ? history.Count(x => x.Command is not null && x.Command.AcknowledgedDevices < group.CameraIds.Count)
                : 0;
            var skewViolations = history.Count(x => IsCompleted(x) && x.WithinTolerance == false);
            var consecutiveFailures = CountConsecutive(history, x => !IsCompleted(x));
            var measuredCompleted = history.Where(x => IsCompleted(x) && x.TriggerSkewUs is not null).ToArray();
            var consecutiveSkew = CountConsecutive(measuredCompleted, x => x.WithinTolerance == false);
            var (sequenceGapFrames, sequenceGapRate) = EstimateSequenceGaps(group, history);
            var transport = ComputeNativeTransport(group, history);
            var enoughHistory = total >= config.SynchronizationGuardMinimumEvidenceRuns;
            var enoughNativeEvidence = transport.NativeCameras > 0 && transport.NativeEvidenceRuns >= config.SynchronizationGuardMinimumEvidenceRuns;
            var sequenceFallbackRequired = transport.NativeCameras < group.CameraIds.Count || !enoughNativeEvidence;

            string? reason = null;
            if (!liveReady)
            {
                reason = live.Error ?? $"One or more cameras in synchronization group '{group.Id}' are disconnected, reconnecting or faulted.";
                issues.Add(new("PROD-SYNC-CAMERA-NOT-READY", group.Id, reason, true));
            }
            else if (enoughHistory && consecutiveFailures >= config.SynchronizationGuardMaxConsecutiveFailures)
            {
                reason = $"{consecutiveFailures} consecutive synchronization runs failed/cancelled; production limit is {config.SynchronizationGuardMaxConsecutiveFailures - 1}.";
                issues.Add(new("PROD-SYNC-ACTION", group.Id, reason));
            }
            else if (enoughHistory && frameTimeoutRate > config.SynchronizationGuardMaximumFrameTimeoutRate)
            {
                reason = $"Frame-timeout rate {frameTimeoutRate:P1} exceeds production threshold {config.SynchronizationGuardMaximumFrameTimeoutRate:P1} in the last {total} runs.";
                issues.Add(new("PROD-SYNC-FRAME-TIMEOUT", group.Id, reason));
            }
            else if (enoughHistory && actionFailures > 0)
            {
                reason = $"{actionFailures} Action Command failure(s) are present in the active synchronization window.";
                issues.Add(new("PROD-SYNC-ACTION", group.Id, reason));
            }
            else if (enoughHistory && ackShortfalls > 0)
            {
                reason = $"{ackShortfalls} Action Command run(s) acknowledged fewer devices than the configured {group.CameraIds.Count}-camera group.";
                issues.Add(new("PROD-SYNC-ACTION-ACK", group.Id, reason));
            }
            else if (enoughHistory && consecutiveSkew >= config.SynchronizationGuardMaxConsecutiveSkewViolations)
            {
                reason = $"Trigger skew exceeded {group.MaxTriggerSkewUs:F1} us for {consecutiveSkew} consecutive measured runs.";
                issues.Add(new("PROD-SYNC-SKEW", group.Id, reason));
            }
            else if (enoughNativeEvidence && transport.NativeFrameLossRate is { } nativeLoss && nativeLoss > config.SynchronizationGuardMaximumNativeFrameLossRate)
            {
                reason = $"Vendor-native transport frame-loss/error rate {nativeLoss:P3} exceeds production threshold {config.SynchronizationGuardMaximumNativeFrameLossRate:P3} ({transport.EvidenceMode}).";
                issues.Add(new("PROD-SYNC-TRANSPORT", group.Id, reason));
            }
            else if (enoughNativeEvidence && transport.BufferUnderruns > config.SynchronizationGuardMaximumBufferUnderruns)
            {
                reason = $"Vendor transport reported {transport.BufferUnderruns} buffer underrun(s) in the active window; threshold is {config.SynchronizationGuardMaximumBufferUnderruns}.";
                issues.Add(new("PROD-SYNC-TRANSPORT", group.Id, reason));
            }
            else if (enoughNativeEvidence && transport.Resynchronizations > config.SynchronizationGuardMaximumResynchronizations)
            {
                reason = $"Vendor transport reported {transport.Resynchronizations} stream resynchronization(s) in the active window; threshold is {config.SynchronizationGuardMaximumResynchronizations}.";
                issues.Add(new("PROD-SYNC-TRANSPORT", group.Id, reason));
            }
            else if (enoughHistory && sequenceFallbackRequired && sequenceGapRate > config.SynchronizationGuardMaximumSequenceGapRate)
            {
                reason = $"Estimated camera sequence-gap rate {sequenceGapRate:P2} exceeds fallback threshold {config.SynchronizationGuardMaximumSequenceGapRate:P2}. Native transport telemetry coverage is {transport.NativeCameras}/{group.CameraIds.Count} cameras.";
                issues.Add(new("PROD-SYNC-FRAME-LOSS", group.Id, reason));
            }
            else if (enoughHistory && completionRate < 1d - config.SynchronizationGuardMaximumFailureRate)
            {
                reason = $"Synchronization completion rate {completionRate:P1} is below production threshold {(1d - config.SynchronizationGuardMaximumFailureRate):P1} in the last {total} runs.";
                issues.Add(new("PROD-SYNC-ACTION", group.Id, reason));
            }

            statuses.Add(new ProductionSynchronizationGuardGroupStatus(
                group.Id,
                group.Name,
                liveReady,
                total,
                total,
                completed,
                failed,
                cancelled,
                completionRate,
                frameTimeoutRuns,
                frameTimeoutRate,
                actionFailures,
                ackShortfalls,
                skewViolations,
                consecutiveFailures,
                consecutiveSkew,
                sequenceGapFrames,
                sequenceGapRate,
                reason is null,
                reason ?? HealthyReason(group, total, config, transport, enoughNativeEvidence),
                transport));
        }

        var healthy = issues.Count == 0;
        return new ProductionSynchronizationGuardSnapshot(
            true,
            healthy,
            DateTimeOffset.UtcNow,
            statuses,
            issues,
            healthy ? "Synchronization production guard healthy." : string.Join(" ", issues.Take(3).Select(x => x.Message)));
    }

    private static string HealthyReason(CameraSynchronizationGroup group, int total, ProductionRuntimeConfig config,
        ProductionSynchronizationTransportWindow transport, bool enoughNativeEvidence)
    {
        if (total < config.SynchronizationGuardMinimumEvidenceRuns)
            return $"Live synchronization path is ready; only {total}/{config.SynchronizationGuardMinimumEvidenceRuns} historical runs are available, so history thresholds are warming up.";
        if (transport.NativeCameras == group.CameraIds.Count && enoughNativeEvidence)
            return "Camera transport, Action Command, frame arrival and trigger-skew history satisfy the production guard using vendor-native transport counters.";
        if (transport.NativeCameras > 0)
            return $"Synchronization guard healthy with mixed evidence: vendor-native transport counters cover {transport.NativeCameras}/{group.CameraIds.Count} cameras and sequence continuity covers the remainder.";
        return "Synchronization guard healthy using sequence continuity fallback because vendor transport counters are unavailable.";
    }

    public static ProductionSynchronizationTransportWindow ComputeNativeTransport(
        CameraSynchronizationGroup group,
        IReadOnlyList<CameraSynchronizationRunRecord> history)
    {
        var orderedRuns = history.OrderBy(x => x.CompletedAt).ToArray();
        var nativeCameras = 0;
        var nativeSampleCounts = new List<int>();
        long receivedFrames = 0, lostFrames = 0, failedFrames = 0, underruns = 0;
        long lostPackets = 0, failedPackets = 0, resendRequests = 0, resentPackets = 0, resynchronizations = 0;
        var latestThroughputs = new List<double>();

        foreach (var cameraId in group.CameraIds)
        {
            var snapshots = orderedRuns
                .SelectMany(run => run.TransportSnapshots ?? [])
                .Where(x => string.Equals(x.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) && x.Native && x.HasNativeCounters)
                .OrderBy(x => x.CapturedAt)
                .ToArray();
            if (snapshots.Length < 2) continue;
            nativeCameras++;
            nativeSampleCounts.Add(snapshots.Length);
            receivedFrames += CounterDelta(snapshots, x => x.ReceivedFrames);
            lostFrames += CounterDelta(snapshots, x => x.LostFrames);
            failedFrames += CounterDelta(snapshots, x => x.FailedFrames);
            underruns += CounterDelta(snapshots, x => x.BufferUnderruns);
            lostPackets += CounterDelta(snapshots, x => x.LostPackets);
            failedPackets += CounterDelta(snapshots, x => x.FailedPackets);
            resendRequests += CounterDelta(snapshots, x => x.ResendRequests);
            resentPackets += CounterDelta(snapshots, x => x.ResentPackets);
            resynchronizations += CounterDelta(snapshots, x => x.Resynchronizations);
            var throughput = snapshots.LastOrDefault(x => x.ThroughputMbps is not null)?.ThroughputMbps;
            if (throughput is { } value && double.IsFinite(value)) latestThroughputs.Add(value);
        }

        var nativeEvidenceRuns = nativeSampleCounts.Count == 0 ? 0 : nativeSampleCounts.Min();
        var issueFrames = lostFrames + failedFrames;
        var denominator = receivedFrames + lostFrames;
        double? frameLossRate = denominator <= 0 ? null : Math.Clamp(issueFrames / (double)denominator, 0d, 1d);
        var mode = nativeCameras == 0 ? "sequence-estimate" : nativeCameras == group.CameraIds.Count ? "vendor-native" : "mixed-native+sequence";
        return new ProductionSynchronizationTransportWindow(
            mode, nativeCameras, nativeEvidenceRuns, receivedFrames, lostFrames, failedFrames, underruns, frameLossRate,
            lostPackets, failedPackets, resendRequests, resentPackets, resynchronizations,
            latestThroughputs.Count == 0 ? null : latestThroughputs.Average());
    }

    private static long CounterDelta(IReadOnlyList<CameraTransportTelemetry> snapshots, Func<CameraTransportTelemetry, long?> selector)
    {
        long total = 0;
        long? previous = null;
        foreach (var snapshot in snapshots)
        {
            var current = selector(snapshot);
            if (current is null) continue;
            if (previous is not null)
                total += current.Value >= previous.Value ? current.Value - previous.Value : Math.Max(0, current.Value);
            previous = current.Value;
        }
        return total;
    }

    public static IReadOnlySet<string> ResolveGroupIds(WorkflowDefinition workflow)
    {
        var output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes.Where(x => string.Equals(x.Type, "camera.syncCapture", StringComparison.OrdinalIgnoreCase)))
        {
            if (node.Parameters is null || !node.Parameters.TryGetValue("groupId", out var element)) continue;
            var groupId = ReadString(element)?.Trim();
            if (!string.IsNullOrWhiteSpace(groupId)) output.Add(groupId);
        }
        return output;
    }

    private static string? ReadString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();

    private static bool IsCompleted(CameraSynchronizationRunRecord run)
        => string.Equals(run.Outcome, "Completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(CameraSynchronizationRunRecord run)
        => string.Equals(run.Outcome, "Failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelled(CameraSynchronizationRunRecord run)
        => string.Equals(run.Outcome, "Cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsFrameTimeout(CameraSynchronizationRunRecord run)
    {
        if (!IsFailed(run) || string.IsNullOrWhiteSpace(run.Error)) return false;
        var text = run.Error!;
        return text.Contains("timed out waiting for camera frame", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("frame", StringComparison.OrdinalIgnoreCase) && text.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsActionFailure(CameraSynchronizationRunRecord run)
    {
        if (!IsFailed(run) || string.IsNullOrWhiteSpace(run.Error)) return false;
        var text = run.Error!;
        return text.Contains("Action Command", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("IssueActionCommand", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Action keys", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("broadcast", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountConsecutive(IEnumerable<CameraSynchronizationRunRecord> newestFirst, Func<CameraSynchronizationRunRecord, bool> predicate)
    {
        var count = 0;
        foreach (var run in newestFirst)
        {
            if (!predicate(run)) break;
            count++;
        }
        return count;
    }

    private static (long GapFrames, double GapRate) EstimateSequenceGaps(CameraSynchronizationGroup group, IReadOnlyList<CameraSynchronizationRunRecord> history)
    {
        long lost = 0;
        long observed = 0;
        foreach (var cameraId in group.CameraIds)
        {
            var sequences = history
                .Where(IsCompleted)
                .OrderBy(x => x.CompletedAt)
                .SelectMany(x => x.Frames)
                .Where(x => string.Equals(x.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Sequence)
                .ToArray();
            observed += sequences.Length;
            for (var i = 1; i < sequences.Length; i++)
            {
                var delta = sequences[i] - sequences[i - 1];
                if (delta > 1) lost += delta - 1;
            }
        }
        var expected = observed + lost;
        return (lost, expected == 0 ? 0d : lost / (double)expected);
    }
}
