using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Camera;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed class CameraSynchronizationService(
    CameraSynchronizationGroupStore groups,
    CameraSynchronizationRunStore runs,
    CameraManager cameras,
    IEnumerable<ICameraActionCommandProvider> actionProviders) : ISynchronizedFrameSetService
{
    private readonly IReadOnlyDictionary<string, ICameraActionCommandProvider> _actionProviders = actionProviders.ToDictionary(x => x.Driver, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<CameraSynchronizationGroup>> ListAsync(CancellationToken ct = default) => groups.ListAsync(ct);
    public Task<CameraSynchronizationGroup> GetAsync(string id, CancellationToken ct = default) => groups.GetAsync(id, ct);
    public Task<IReadOnlyList<CameraSynchronizationRunRecord>> ListRunsAsync(string? groupId = null, int limit = 50, CancellationToken ct = default) => runs.ListAsync(groupId, limit, ct);
    public Task<CameraSynchronizationRunRecord> GetRunAsync(string runId, CancellationToken ct = default) => runs.GetAsync(runId, ct);

    public CameraActionCommandCapabilities? GetActionCommandCapabilities(string driver)
        => _actionProviders.TryGetValue(driver, out var provider) ? provider.ActionCommandCapabilities : null;

    public async Task<CameraSynchronizationStatistics> GetStatisticsAsync(string groupId, int limit = 100, int minimumSamples = 20, CancellationToken ct = default)
    {
        var group = await groups.GetAsync(groupId, ct);
        limit = Math.Clamp(limit, 20, 500);
        minimumSamples = Math.Clamp(minimumSamples, 5, 500);
        var history = await runs.ListAsync(group.Id, limit, ct);
        return ComputeStatistics(group, history, limit, minimumSamples);
    }

    public async Task<CameraSynchronizationPtpDiagnostics> GetPtpDiagnosticsAsync(string groupId, int limit = 100, CancellationToken ct = default)
    {
        var group = await groups.GetAsync(groupId, ct);
        limit = Math.Clamp(limit, 5, 500);
        var history = await runs.ListAsync(group.Id, limit, ct);
        return ComputePtpDiagnostics(group, history, limit);
    }

    public static CameraSynchronizationStatistics ComputeStatistics(
        CameraSynchronizationGroup group, IReadOnlyList<CameraSynchronizationRunRecord> history, int sampleLimit, int minimumSamples)
    {
        var total = history.Count;
        var completed = history.Count(x => string.Equals(x.Outcome, "Completed", StringComparison.OrdinalIgnoreCase));
        var failed = history.Count(x => string.Equals(x.Outcome, "Failed", StringComparison.OrdinalIgnoreCase));
        var cancelled = history.Count(x => string.Equals(x.Outcome, "Cancelled", StringComparison.OrdinalIgnoreCase));
        var measured = history.Where(x => x.TriggerSkewUs is not null && double.IsFinite(x.TriggerSkewUs.Value)).ToArray();
        var within = measured.Count(x => x.WithinTolerance == true);
        var skew = measured.Select(x => x.TriggerSkewUs!.Value).OrderBy(x => x).ToArray();
        var durations = history.Where(x => x.DurationMs >= 0).Select(x => (double)x.DurationMs).OrderBy(x => x).ToArray();
        var completionRate = total == 0 ? 0d : completed / (double)total;
        var tolerancePassRate = measured.Length == 0 ? 0d : within / (double)measured.Length;
        const double requiredCompletionRate = 0.99;
        const double requiredTolerancePassRate = 0.99;
        var p99 = Percentile(skew, 0.99);
        var reasons = new List<string>();
        if (total < minimumSamples) reasons.Add($"Need at least {minimumSamples} runs; only {total} are available.");
        if (total >= minimumSamples && completionRate < requiredCompletionRate) reasons.Add($"Completion rate {completionRate:P1} is below {requiredCompletionRate:P0}.");
        if (total >= minimumSamples && measured.Length < minimumSamples) reasons.Add($"Need at least {minimumSamples} measured-skew runs; only {measured.Length} are available.");
        if (measured.Length >= minimumSamples && tolerancePassRate < requiredTolerancePassRate) reasons.Add($"Tolerance pass rate {tolerancePassRate:P1} is below {requiredTolerancePassRate:P0}.");
        if (measured.Length >= minimumSamples && p99 is not null && p99.Value > group.MaxTriggerSkewUs) reasons.Add($"p99 skew {p99.Value:F1} us exceeds group limit {group.MaxTriggerSkewUs:F1} us.");
        var enoughData = total >= minimumSamples && measured.Length >= minimumSamples;
        var passed = enoughData && reasons.Count == 0;
        var status = !enoughData ? "InsufficientData" : passed ? "Pass" : "Fail";
        if (passed) reasons.Add($"Last {total} runs satisfy the commissioning gate.");
        return new CameraSynchronizationStatistics(
            group.Id, sampleLimit, total, completed, failed, cancelled, measured.Length, within, completionRate, tolerancePassRate,
            Average(skew), Percentile(skew, 0.50), Percentile(skew, 0.95), p99, skew.Length == 0 ? null : skew[^1],
            Average(durations), Percentile(durations, 0.95),
            history.Count(x => string.Equals(x.TimestampBasis, "device-ptp", StringComparison.OrdinalIgnoreCase)),
            history.Count(x => string.Equals(x.TimestampBasis, "host-arrival", StringComparison.OrdinalIgnoreCase)),
            history.Count == 0 ? null : history.Min(x => x.CompletedAt),
            history.Count == 0 ? null : history.Max(x => x.CompletedAt),
            new CameraSynchronizationCommissioningAssessment(status, passed, minimumSamples, requiredCompletionRate, requiredTolerancePassRate, reasons));
    }

    private static double? Average(IReadOnlyList<double> values) => values.Count == 0 ? null : values.Average();

    public static CameraSynchronizationPtpDiagnostics ComputePtpDiagnostics(
        CameraSynchronizationGroup group, IReadOnlyList<CameraSynchronizationRunRecord> history, int sampleLimit)
    {
        var evidenceRuns = history.Where(x => x.PtpSnapshots.Count > 0).OrderBy(x => x.CompletedAt).ToArray();
        var trend = evidenceRuns.Select(run =>
        {
            var offsets = run.PtpSnapshots.Where(x => x.OffsetFromMasterNs is not null).Select(x => Math.Abs(x.OffsetFromMasterNs!.Value)).ToArray();
            var masters = run.PtpSnapshots.Select(x => x.MasterClockId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var ready = run.PtpSnapshots.Count > 0 && run.PtpSnapshots.All(x => IsPtpReady(x, group.MaxPtpOffsetNs));
            return new CameraSynchronizationPtpTrendPoint(run.RunId, run.CompletedAt, run.Outcome, run.TriggerSkewUs,
                offsets.Length == 0 ? null : offsets.Max(), ready, masters.Length <= 1, masters.Length == 1 ? masters[0] : null);
        }).ToArray();

        var cameraDiagnostics = evidenceRuns.SelectMany(run => run.PtpSnapshots.Select(snapshot => (run.CompletedAt, Snapshot: snapshot)))
            .GroupBy(x => x.Snapshot.CameraId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(grouped =>
            {
                var ordered = grouped.OrderBy(x => x.CompletedAt).ToArray();
                var offsets = ordered.Where(x => x.Snapshot.OffsetFromMasterNs is not null).Select(x => (double)Math.Abs(x.Snapshot.OffsetFromMasterNs!.Value)).OrderBy(x => x).ToArray();
                var masters = ordered.Select(x => x.Snapshot.MasterClockId).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
                var changes = 0;
                for (var i = 1; i < masters.Length; i++) if (!string.Equals(masters[i - 1], masters[i], StringComparison.OrdinalIgnoreCase)) changes++;
                var ready = ordered.Count(x => IsPtpReady(x.Snapshot, group.MaxPtpOffsetNs));
                return new CameraSynchronizationPtpCameraDiagnostics(grouped.Key, ordered.Length, ready, ordered.Length == 0 ? 0 : ready / (double)ordered.Length,
                    offsets.Length, offsets.Length == 0 ? null : offsets.Average(), Percentile(offsets, 0.95), offsets.Length == 0 ? null : (long?)offsets.Max(), changes,
                    masters.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
            }).ToArray();

        var correlationPairs = trend.Where(x => x.MaxAbsOffsetNs is not null && x.TriggerSkewUs is not null && double.IsFinite(x.TriggerSkewUs.Value))
            .Select(x => ((double)x.MaxAbsOffsetNs!.Value, x.TriggerSkewUs!.Value)).ToArray();
        var correlation = Pearson(correlationPairs);
        var maxOffsets = trend.Where(x => x.MaxAbsOffsetNs is not null).Select(x => (double)x.MaxAbsOffsetNs!.Value).OrderBy(x => x).ToArray();
        return new CameraSynchronizationPtpDiagnostics(group.Id, sampleLimit, history.Count, evidenceRuns.Length, trend.Count(x => x.AllPtpReady),
            trend.Count(x => !x.AllPtpReady), trend.Count(x => !x.MasterClockConsistent), correlation,
            maxOffsets.Length == 0 ? null : (long?)maxOffsets.Max(), Percentile(maxOffsets, 0.95), cameraDiagnostics, trend);
    }

    private static double? Pearson(IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count < 3) return null;
        var meanX = pairs.Average(x => x.X);
        var meanY = pairs.Average(x => x.Y);
        var numerator = pairs.Sum(x => (x.X - meanX) * (x.Y - meanY));
        var denomX = Math.Sqrt(pairs.Sum(x => Math.Pow(x.X - meanX, 2)));
        var denomY = Math.Sqrt(pairs.Sum(x => Math.Pow(x.Y - meanY, 2)));
        if (denomX <= double.Epsilon || denomY <= double.Epsilon) return null;
        return numerator / (denomX * denomY);
    }

    private static double? Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return null;
        var rank = Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Count) - 1);
        return sortedValues[Math.Min(rank, sortedValues.Count - 1)];
    }

    public async Task<CameraSynchronizationGroupStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        var group = await groups.GetAsync(id, ct);
        var members = new List<CameraSynchronizationMemberStatus>();
        string? error = null;
        foreach (var cameraId in group.CameraIds)
        {
            try
            {
                var descriptor = cameras.Get(cameraId);
                var ptp = await cameras.GetTimeSynchronizationStatusAsync(cameraId, ct);
                members.Add(new CameraSynchronizationMemberStatus(cameraId, descriptor.Driver, descriptor.State, descriptor.Acquisition.AcquisitionState, ptp, cameras.GetLatestFrameTiming(cameraId)));
                if (!string.Equals(descriptor.Driver, group.Driver, StringComparison.OrdinalIgnoreCase))
                    error ??= $"Camera '{cameraId}' uses driver '{descriptor.Driver}', expected '{group.Driver}'.";
                else if (descriptor.State == CameraState.Closed)
                    error ??= $"Camera '{cameraId}' is closed.";
                else if (descriptor.Settings.TriggerMode != CameraTriggerMode.External || !descriptor.Settings.ExternalTriggerSource.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                    error ??= $"Camera '{cameraId}' is not configured for External Action trigger.";
                else
                {
                    var profile = await cameras.ReadCommissioningProfileAsync(cameraId, ct);
                    if (profile.ActionDeviceKey != group.DeviceKey || profile.ActionGroupKey != group.GroupKey || profile.ActionGroupMask != group.GroupMask)
                        error ??= $"Camera '{cameraId}' Action keys do not match synchronization group '{group.Id}'.";
                }
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
                members.Add(new CameraSynchronizationMemberStatus(cameraId, "unknown", CameraState.Faulted, CameraAcquisitionState.Faulted,
                    new CameraTimeSynchronizationStatus(cameraId, "unknown", false, false, CameraPtpClockState.Faulted, Error: ex.Message, CapturedAt: DateTimeOffset.UtcNow), null));
            }
        }
        var ptpReady = members.All(x => IsPtpReady(x.TimeSync, group.MaxPtpOffsetNs));
        var (skew, basis) = ComputeSkew(members.Select(x => x.LatestFrame).Where(x => x is not null).Cast<CameraFrameTimingSnapshot>().ToArray());
        var ready = error is null && _actionProviders.ContainsKey(group.Driver) && members.All(x => x.CameraState is CameraState.Open or CameraState.Streaming) && (!group.RequirePtpLocked || ptpReady);
        if (!_actionProviders.ContainsKey(group.Driver)) error ??= $"Action Command provider '{group.Driver}' is not registered.";
        return new CameraSynchronizationGroupStatus(group, ready, ptpReady, error, members, skew, basis);
    }

    public async Task ValidateOrThrowAsync(CameraSynchronizationGroup group, bool scheduled, CancellationToken ct = default)
    {
        if (!_actionProviders.TryGetValue(group.Driver, out var provider)) throw new InvalidOperationException($"Action Command provider '{group.Driver}' is unavailable.");
        if (scheduled && !provider.ActionCommandCapabilities.Scheduled) throw new InvalidOperationException($"Driver '{group.Driver}' does not support scheduled Action Commands.");
        foreach (var cameraId in group.CameraIds)
        {
            var camera = cameras.Get(cameraId);
            if (!string.Equals(camera.Driver, group.Driver, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Camera '{cameraId}' uses driver '{camera.Driver}', but sync group '{group.Id}' broadcasts through '{group.Driver}'. Mixed-driver groups are intentionally rejected in V0.36.");
            if (camera.State == CameraState.Closed) throw new InvalidOperationException($"Camera '{cameraId}' must be open/streaming before synchronization.");
            if (camera.Settings.TriggerMode != CameraTriggerMode.External || !camera.Settings.ExternalTriggerSource.StartsWith("Action", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Camera '{cameraId}' must use External trigger with Action1/ActionN source before Action Command synchronization.");
            var profile = await cameras.ReadCommissioningProfileAsync(cameraId, ct);
            if (profile.ActionDeviceKey != group.DeviceKey || profile.ActionGroupKey != group.GroupKey || profile.ActionGroupMask != group.GroupMask)
                throw new InvalidOperationException($"Camera '{cameraId}' Action keys do not match synchronization group '{group.Id}'. Apply a matching commissioning profile first.");
            if (scheduled || group.RequirePtpLocked)
            {
                var ptp = await cameras.GetTimeSynchronizationStatusAsync(cameraId, ct);
                if (!IsPtpReady(ptp, group.MaxPtpOffsetNs))
                    throw new InvalidOperationException($"Camera '{cameraId}' PTP is not ready: state={ptp.State}, offset={ptp.OffsetFromMasterNs?.ToString() ?? "unknown"} ns.");
            }
        }
    }

    public async Task<CameraSynchronizationCaptureResult> TriggerAsync(string groupId, CameraSynchronizationTriggerRequest request, CancellationToken ct = default, string source = "manual-api")
    {
        var group = await groups.GetAsync(groupId, ct);
        var runId = Guid.NewGuid().ToString("N");
        var requestedAt = DateTimeOffset.UtcNow;
        CameraActionCommandResult? command = null;
        long? scheduledTime = null;
        IReadOnlyList<CameraTimeSynchronizationStatus> ptpSnapshots = [];
        try
        {
            ptpSnapshots = await CapturePtpSnapshotsAsync(group, ct);
            await ValidateOrThrowAsync(group, request.Scheduled, ct);
            // R02/A02：真实设备动作——组内每台相机都在同一派发许可边界内下发 Action Command。
            using var commandPermits = AcquireGroupCommandPermits(group);
            var provider = _actionProviders[group.Driver];
            var before = group.CameraIds.ToDictionary(x => x, x => cameras.GetLatestSequence(x), StringComparer.OrdinalIgnoreCase);
            long? tickFrequencyHz = null;
            long? scheduledLeadMs = null;
            if (request.Scheduled)
            {
                var statuses = ptpSnapshots;
                var clock = statuses.Select(x => x.DeviceTimestampNs).Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty().Max();
                if (clock <= 0) throw new InvalidOperationException("Scheduled Action Command requires a readable synchronized device timestamp.");
                if (provider.ActionCommandCapabilities.ScheduledRequiresTickFrequency)
                {
                    var frequencies = statuses.Select(x => x.DeviceTickFrequencyHz).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
                    if (frequencies.Length != 1) throw new InvalidOperationException("Scheduled Action Command for this driver requires one readable, consistent device timestamp tick frequency across the synchronization group.");
                    tickFrequencyHz = frequencies[0];
                }
                var lead = Math.Clamp(request.LeadTimeMs ?? group.ScheduledLeadTimeMs, 10, 60_000);
                scheduledLeadMs = lead;
                scheduledTime = checked(clock + lead * 1_000_000L);
            }
            command = await provider.IssueActionCommandAsync(new CameraActionCommandRequest(
                DeviceKey: group.DeviceKey, GroupKey: group.GroupKey, GroupMask: group.GroupMask, BroadcastAddress: group.BroadcastAddress,
                ScheduledDeviceTimeNs: scheduledTime, TimeoutMs: 1000, DeviceTickFrequencyHz: tickFrequencyHz), ct);

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.FrameTimeoutMs, 100, 30_000));
            // R02（2026-10-11 复审）：调度触发的设备端计划最远可排在 60s 后——帧等待窗口必须覆盖
            // 目标时刻（成帧超时 + lead + 派发裕量）。否则请求超时会与设备端计划执行解耦：
            // HTTP 已判失败，相机却仍会在计划时刻出帧，等于"以结束 HTTP 请求替代设备端确认"。
            var wait = scheduledLeadMs is { } leadMs
                ? timeout + TimeSpan.FromMilliseconds(leadMs) + TimeSpan.FromSeconds(2)
                : timeout;
            var tasks = group.CameraIds.Select(async cameraId => await cameras.WaitForFrameTimingAsync(cameraId, before[cameraId], wait, ct).AsTask()).ToArray();
            var timings = await Task.WhenAll(tasks);
            var (skew, basis) = ComputeSkew(timings);
            var resultFrames = timings.Select(x => new CameraSynchronizationFrameResult(x.CameraId, x.Sequence, x.HostTimestamp, x.DeviceTimestampNs, x.TriggerId)).ToArray();
            var completedAt = DateTimeOffset.UtcNow;
            var result = new CameraSynchronizationCaptureResult(group.Id, request.Scheduled, scheduledTime, command, resultFrames, skew ?? double.PositiveInfinity, basis, skew is not null && skew <= group.MaxTriggerSkewUs, group.MaxTriggerSkewUs, completedAt);
            var transportSnapshots = CaptureTransportSnapshots(group);
            await runs.RecordAsync(new CameraSynchronizationRunRecord(
                runId, group.Id, group.ConfigurationHash, source, request.Scheduled,
                requestedAt, completedAt, Math.Max(0, (long)(completedAt - requestedAt).TotalMilliseconds), scheduledTime,
                basis, skew, group.MaxTriggerSkewUs, skew is not null && skew <= group.MaxTriggerSkewUs, "Completed", null, command, resultFrames, ptpSnapshots, transportSnapshots), ct);
            return result;
        }
        catch (Exception ex)
        {
            await RecordFailureBestEffortAsync(runId, group, source, request.Scheduled, requestedAt, scheduledTime, command, ptpSnapshots, ex);
            throw;
        }
    }

    public async Task<VisionFrameSet> CaptureAsync(SynchronizedFrameSetCaptureRequest request, CancellationToken cancellationToken = default)
    {
        var group = await groups.GetAsync(request.GroupId, cancellationToken);
        var runId = Guid.NewGuid().ToString("N");
        var requestedAt = DateTimeOffset.UtcNow;
        CameraActionCommandResult? command = null;
        long? scheduledTime = null;
        IReadOnlyList<CameraTimeSynchronizationStatus> ptpSnapshots = [];
        CameraFrameLease[] leases = [];
        var images = new Dictionary<string, IVisionImage>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ptpSnapshots = await CapturePtpSnapshotsAsync(group, cancellationToken);
            await ValidateOrThrowAsync(group, request.Scheduled, cancellationToken);
            // R02/A02：真实设备动作——工作流路径与手动路径共用同一派发许可边界。
            using var commandPermits = AcquireGroupCommandPermits(group);
            var provider = _actionProviders[group.Driver];
            var before = group.CameraIds.ToDictionary(x => x, x => cameras.GetLatestSequence(x), StringComparer.OrdinalIgnoreCase);
            long? tickFrequencyHz = null;
            long? scheduledLeadMs = null;
            if (request.Scheduled)
            {
                var statuses = ptpSnapshots;
                var clock = statuses.Select(x => x.DeviceTimestampNs).Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty().Max();
                if (clock <= 0) throw new InvalidOperationException("Scheduled synchronized FrameSet capture requires a readable synchronized device timestamp.");
                if (provider.ActionCommandCapabilities.ScheduledRequiresTickFrequency)
                {
                    var frequencies = statuses.Select(x => x.DeviceTickFrequencyHz).Where(x => x is > 0).Select(x => x!.Value).Distinct().ToArray();
                    if (frequencies.Length != 1) throw new InvalidOperationException("Scheduled FrameSet capture requires one readable, consistent device timestamp tick frequency across the synchronization group.");
                    tickFrequencyHz = frequencies[0];
                }
                var lead = Math.Clamp(request.LeadTimeMs ?? group.ScheduledLeadTimeMs, 10, 60_000);
                scheduledLeadMs = lead;
                scheduledTime = checked(clock + lead * 1_000_000L);
            }

            command = await provider.IssueActionCommandAsync(new CameraActionCommandRequest(
                DeviceKey: group.DeviceKey,
                GroupKey: group.GroupKey,
                GroupMask: group.GroupMask,
                BroadcastAddress: group.BroadcastAddress,
                ScheduledDeviceTimeNs: scheduledTime,
                TimeoutMs: 1000,
                DeviceTickFrequencyHz: tickFrequencyHz), cancellationToken);

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.FrameTimeoutMs, 100, 30_000));
            // R02（2026-10-11 复审）：工作流路径与手动路径同修——调度等待窗口覆盖目标时刻。
            var wait = scheduledLeadMs is { } leadMs
                ? timeout + TimeSpan.FromMilliseconds(leadMs) + TimeSpan.FromSeconds(2)
                : timeout;
            var tasks = group.CameraIds
                .Select(cameraId => cameras.WaitForFrameLeaseAsync(cameraId, before[cameraId], wait, cancellationToken).AsTask())
                .ToArray();
            try
            {
                leases = await Task.WhenAll(tasks);
            }
            catch
            {
                foreach (var task in tasks)
                    if (task.Status == TaskStatus.RanToCompletion)
                        task.Result.Dispose();
                throw;
            }

            var frames = new Dictionary<string, VisionFrameSetFrameInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var lease in leases)
            {
                var image = VisionImage.FromLease(lease);
                images.Add(lease.CameraId, image);
                frames.Add(lease.CameraId, new VisionFrameSetFrameInfo(
                    lease.CameraId, lease.Sequence, lease.Timestamp, lease.DeviceTimestampNs, lease.TriggerId, lease.PixelFormat));
            }
            var timings = frames.Values.Select(x => new CameraFrameTimingSnapshot(x.CameraId, x.Sequence, x.HostTimestamp, x.DeviceTimestampNs, x.TriggerId)).ToArray();
            var (skew, basis) = ComputeSkew(timings);
            var completedAt = DateTimeOffset.UtcNow;
            var resultFrames = frames.Values.Select(x => new CameraSynchronizationFrameResult(x.CameraId, x.Sequence, x.HostTimestamp, x.DeviceTimestampNs, x.TriggerId)).ToArray();
            var transportSnapshots = CaptureTransportSnapshots(group);
            await runs.RecordAsync(new CameraSynchronizationRunRecord(
                runId, group.Id, group.ConfigurationHash, "workflow", request.Scheduled,
                requestedAt, completedAt, Math.Max(0, (long)(completedAt - requestedAt).TotalMilliseconds), scheduledTime,
                basis, skew, group.MaxTriggerSkewUs, skew is not null && skew <= group.MaxTriggerSkewUs, "Completed", null, command, resultFrames, ptpSnapshots, transportSnapshots), cancellationToken);
            return new VisionFrameSet(group.Id, basis, skew ?? double.PositiveInfinity, skew is not null && skew <= group.MaxTriggerSkewUs, images, frames);
        }
        catch (Exception ex)
        {
            foreach (var disposable in images.Values.OfType<IDisposable>()) disposable.Dispose();
            foreach (var lease in leases)
                if (!images.ContainsKey(lease.CameraId)) lease.Dispose();
            await RecordFailureBestEffortAsync(runId, group, "workflow", request.Scheduled, requestedAt, scheduledTime, command, ptpSnapshots, ex);
            throw;
        }
    }

    /// <summary>
    /// R02：组级命令派发许可——覆盖组内全部相机（真实 Action Command 会同时作用于每台成员相机）。
    /// 任一台相机的所有权校验失败 ⇒ 全部回滚并抛出（绝不下发"部分相机无许可"的组命令）。
    /// </summary>
    private IDisposable AcquireGroupCommandPermits(CameraSynchronizationGroup group)
    {
        var permits = new List<IDisposable>(group.CameraIds.Count);
        try
        {
            foreach (var cameraId in group.CameraIds) permits.Add(cameras.AcquireCommandPermit(cameraId));
            return new CommandPermitScope(permits);
        }
        catch
        {
            foreach (var permit in permits) permit.Dispose();
            throw;
        }
    }

    private sealed class CommandPermitScope(List<IDisposable> permits) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var permit in permits) permit.Dispose();
        }
    }

    private async Task RecordFailureBestEffortAsync(string runId, CameraSynchronizationGroup group, string source, bool scheduled,
        DateTimeOffset requestedAt, long? scheduledTime, CameraActionCommandResult? command, IReadOnlyList<CameraTimeSynchronizationStatus> ptpSnapshots, Exception error)
    {
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            var outcome = error is OperationCanceledException ? "Cancelled" : "Failed";
            var transportSnapshots = CaptureTransportSnapshots(group);
            await runs.RecordAsync(new CameraSynchronizationRunRecord(
                runId, group.Id, group.ConfigurationHash, source, scheduled, requestedAt, completedAt,
                Math.Max(0, (long)(completedAt - requestedAt).TotalMilliseconds), scheduledTime, "unavailable", null,
                group.MaxTriggerSkewUs, null, outcome, error.Message, command, [], ptpSnapshots, transportSnapshots), CancellationToken.None);
        }
        catch
        {
            // Run-history persistence must never hide the original acquisition failure.
        }
    }


    private IReadOnlyList<CameraTransportTelemetry> CaptureTransportSnapshots(CameraSynchronizationGroup group)
    {
        var snapshots = new List<CameraTransportTelemetry>(group.CameraIds.Count);
        foreach (var cameraId in group.CameraIds)
        {
            try { snapshots.Add(cameras.GetTransportTelemetry(cameraId)); }
            catch (Exception ex)
            {
                string driver;
                try { driver = cameras.Get(cameraId).Driver; } catch { driver = "unknown"; }
                snapshots.Add(new CameraTransportTelemetry(cameraId, driver, false, "unavailable", DateTimeOffset.UtcNow, Error: ex.Message));
            }
        }
        return snapshots;
    }

    private async Task<IReadOnlyList<CameraTimeSynchronizationStatus>> CapturePtpSnapshotsAsync(CameraSynchronizationGroup group, CancellationToken ct)
    {
        var snapshots = new List<CameraTimeSynchronizationStatus>(group.CameraIds.Count);
        foreach (var cameraId in group.CameraIds)
        {
            try
            {
                var snapshot = await cameras.GetTimeSynchronizationStatusAsync(cameraId, ct);
                snapshots.Add(snapshot with { CapturedAt = snapshot.CapturedAt ?? DateTimeOffset.UtcNow });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string driver;
                try { driver = cameras.Get(cameraId).Driver; } catch { driver = "unknown"; }
                snapshots.Add(new CameraTimeSynchronizationStatus(cameraId, driver, false, false, CameraPtpClockState.Faulted, Error: ex.Message, CapturedAt: DateTimeOffset.UtcNow));
            }
        }
        return snapshots;
    }

    public async Task<CameraSynchronizationGroup> SaveAsync(CameraSynchronizationGroupRequest request, CancellationToken ct = default)
    {
        // Validate runtime camera ids/provider before persisting a production-relevant group.
        if (!_actionProviders.ContainsKey(request.Driver)) throw new InvalidOperationException($"Action Command provider '{request.Driver}' is unavailable.");
        foreach (var cameraId in request.CameraIds)
        {
            var descriptor = cameras.Get(cameraId);
            if (!string.Equals(descriptor.Driver, request.Driver, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Camera '{cameraId}' uses driver '{descriptor.Driver}', not synchronization driver '{request.Driver}'.");
        }
        return await groups.UpsertAsync(request, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => groups.DeleteAsync(id, ct);

    private static bool IsPtpReady(CameraTimeSynchronizationStatus status, long maxOffsetNs)
    {
        if (!status.PtpSupported || !status.PtpEnabled) return false;
        var stateReady = status.State is CameraPtpClockState.Locked or CameraPtpClockState.Slave or CameraPtpClockState.Master;
        var offsetReady = status.State == CameraPtpClockState.Master || status.OffsetFromMasterNs is null || Math.Abs(status.OffsetFromMasterNs.Value) <= maxOffsetNs;
        return stateReady && offsetReady;
    }

    private static (double? SkewUs, string Basis) ComputeSkew(IReadOnlyList<CameraFrameTimingSnapshot> frames)
    {
        if (frames.Count < 2) return (null, "none");
        if (frames.All(x => x.DeviceTimestampNs is not null))
        {
            var values = frames.Select(x => x.DeviceTimestampNs!.Value).ToArray();
            return ((values.Max() - values.Min()) / 1000d, "device-ptp");
        }
        var host = frames.Select(x => x.HostTimestamp.UtcDateTime.Ticks / 10d).ToArray();
        return ((host.Max() - host.Min()), "host-arrival");
    }
}
