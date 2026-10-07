using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Camera;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed record CameraSynchronizationGroup(
    string Id,
    string Name,
    string Driver,
    IReadOnlyList<string> CameraIds,
    int DeviceKey,
    int GroupKey,
    int GroupMask,
    string BroadcastAddress,
    bool RequirePtpLocked,
    long MaxPtpOffsetNs,
    double MaxTriggerSkewUs,
    int ScheduledLeadTimeMs,
    string ConfigurationHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CameraSynchronizationGroupRequest(
    string Id,
    string Name,
    string Driver,
    IReadOnlyList<string> CameraIds,
    int DeviceKey = 1,
    int GroupKey = 1,
    int GroupMask = -1,
    string BroadcastAddress = "255.255.255.255",
    bool RequirePtpLocked = true,
    long MaxPtpOffsetNs = 1_000_000,
    double MaxTriggerSkewUs = 100,
    int ScheduledLeadTimeMs = 100);

public sealed record CameraSynchronizationMemberStatus(
    string CameraId,
    string Driver,
    CameraState CameraState,
    CameraAcquisitionState AcquisitionState,
    CameraTimeSynchronizationStatus TimeSync,
    CameraFrameTimingSnapshot? LatestFrame);

public sealed record CameraSynchronizationGroupStatus(
    CameraSynchronizationGroup Group,
    bool Ready,
    bool PtpReady,
    string? Error,
    IReadOnlyList<CameraSynchronizationMemberStatus> Members,
    double? LatestSkewUs,
    string TimestampBasis);

public sealed record CameraSynchronizationTriggerRequest(
    bool Scheduled = false,
    int? LeadTimeMs = null,
    int FrameTimeoutMs = 3000);

public sealed record CameraSynchronizationFrameResult(
    string CameraId,
    long Sequence,
    DateTimeOffset HostTimestamp,
    long? DeviceTimestampNs,
    long? TriggerId);

public sealed record CameraSynchronizationCaptureResult(
    string GroupId,
    bool Scheduled,
    long? ScheduledDeviceTimeNs,
    CameraActionCommandResult Command,
    IReadOnlyList<CameraSynchronizationFrameResult> Frames,
    double TriggerSkewUs,
    string TimestampBasis,
    bool WithinTolerance,
    double MaxAllowedSkewUs,
    DateTimeOffset CompletedAt);

public sealed class CameraSynchronizationGroupStore(SqliteMetadataDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<CameraSynchronizationGroup>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,name,driver,camera_ids_json,device_key,group_key,group_mask,broadcast_address,require_ptp_locked,max_ptp_offset_ns,max_trigger_skew_us,scheduled_lead_time_ms,config_hash,created_at,updated_at FROM camera_sync_groups ORDER BY name,id;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var output = new List<CameraSynchronizationGroup>();
        while (await reader.ReadAsync(ct)) output.Add(Read(reader));
        return output;
    }

    public async Task<CameraSynchronizationGroup> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,name,driver,camera_ids_json,device_key,group_key,group_mask,broadcast_address,require_ptp_locked,max_ptp_offset_ns,max_trigger_skew_us,scheduled_lead_time_ms,config_hash,created_at,updated_at FROM camera_sync_groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Camera synchronization group '{id}' does not exist.");
        return Read(reader);
    }

    public async Task<CameraSynchronizationGroup> UpsertAsync(CameraSynchronizationGroupRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(request);
        var hash = ComputeHash(normalized);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        var created = now;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT created_at FROM camera_sync_groups WHERE id=$id;";
            existing.Parameters.AddWithValue("$id", normalized.Id);
            var raw = await existing.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrWhiteSpace(raw)) created = DateTimeOffset.Parse(raw);
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
INSERT INTO camera_sync_groups(id,name,driver,camera_ids_json,device_key,group_key,group_mask,broadcast_address,require_ptp_locked,max_ptp_offset_ns,max_trigger_skew_us,scheduled_lead_time_ms,config_hash,created_at,updated_at)
VALUES($id,$name,$driver,$cameras,$device,$group,$mask,$broadcast,$requirePtp,$offset,$skew,$lead,$hash,$created,$updated)
ON CONFLICT(id) DO UPDATE SET
name=excluded.name,driver=excluded.driver,camera_ids_json=excluded.camera_ids_json,device_key=excluded.device_key,group_key=excluded.group_key,group_mask=excluded.group_mask,broadcast_address=excluded.broadcast_address,require_ptp_locked=excluded.require_ptp_locked,max_ptp_offset_ns=excluded.max_ptp_offset_ns,max_trigger_skew_us=excluded.max_trigger_skew_us,scheduled_lead_time_ms=excluded.scheduled_lead_time_ms,config_hash=excluded.config_hash,updated_at=excluded.updated_at;
""";
            cmd.Parameters.AddWithValue("$id", normalized.Id);
            cmd.Parameters.AddWithValue("$name", normalized.Name);
            cmd.Parameters.AddWithValue("$driver", normalized.Driver);
            cmd.Parameters.AddWithValue("$cameras", JsonSerializer.Serialize(normalized.CameraIds, Json));
            cmd.Parameters.AddWithValue("$device", normalized.DeviceKey);
            cmd.Parameters.AddWithValue("$group", normalized.GroupKey);
            cmd.Parameters.AddWithValue("$mask", normalized.GroupMask);
            cmd.Parameters.AddWithValue("$broadcast", normalized.BroadcastAddress);
            cmd.Parameters.AddWithValue("$requirePtp", normalized.RequirePtpLocked ? 1 : 0);
            cmd.Parameters.AddWithValue("$offset", normalized.MaxPtpOffsetNs);
            cmd.Parameters.AddWithValue("$skew", normalized.MaxTriggerSkewUs);
            cmd.Parameters.AddWithValue("$lead", normalized.ScheduledLeadTimeMs);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$created", created.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new CameraSynchronizationGroup(normalized.Id, normalized.Name, normalized.Driver, normalized.CameraIds, normalized.DeviceKey, normalized.GroupKey, normalized.GroupMask, normalized.BroadcastAddress, normalized.RequirePtpLocked, normalized.MaxPtpOffsetNs, normalized.MaxTriggerSkewUs, normalized.ScheduledLeadTimeMs, hash, created, now);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM camera_sync_groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"Camera synchronization group '{id}' does not exist.");
    }

    public async Task<IReadOnlyList<string>> GetConfigurationHashesForCameraAsync(string cameraId, CancellationToken ct = default)
        => (await ListAsync(ct)).Where(x => x.CameraIds.Contains(cameraId, StringComparer.OrdinalIgnoreCase)).Select(x => x.ConfigurationHash).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static CameraSynchronizationGroupRequest Normalize(CameraSynchronizationGroupRequest request)
    {
        var id = (request.Id ?? string.Empty).Trim();
        var name = (request.Name ?? string.Empty).Trim();
        var driver = (request.Driver ?? string.Empty).Trim();
        var broadcast = (request.BroadcastAddress ?? string.Empty).Trim();
        var cameras = (request.CameraIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (id.Length is < 1 or > 80) throw new ArgumentException("Synchronization group Id must be 1..80 characters.");
        if (name.Length is < 1 or > 160) throw new ArgumentException("Synchronization group Name must be 1..160 characters.");
        if (driver.Length is < 1 or > 80) throw new ArgumentException("Synchronization group Driver is required.");
        if (cameras.Length < 2) throw new ArgumentException("A synchronization group requires at least two unique cameras.");
        if (cameras.Length > 64) throw new ArgumentException("A synchronization group supports at most 64 cameras.");
        if (request.DeviceKey < 0 || request.GroupKey < 0) throw new ArgumentException("Action DeviceKey and GroupKey must be non-negative Int32 values.");
        if (string.IsNullOrWhiteSpace(broadcast)) throw new ArgumentException("BroadcastAddress is required.");
        if (!IPAddress.TryParse(broadcast, out var broadcastIp) || broadcastIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("BroadcastAddress must be a valid IPv4 address.");
        return request with
        {
            Id = id, Name = name, Driver = driver, CameraIds = cameras, BroadcastAddress = broadcast,
            MaxPtpOffsetNs = Math.Clamp(request.MaxPtpOffsetNs, 0, 1_000_000_000),
            MaxTriggerSkewUs = Math.Clamp(request.MaxTriggerSkewUs, 0.01, 1_000_000),
            ScheduledLeadTimeMs = Math.Clamp(request.ScheduledLeadTimeMs, 10, 60_000)
        };
    }

    private static string ComputeHash(CameraSynchronizationGroupRequest request)
    {
        var canonical = new
        {
            request.Id, request.Driver, cameraIds = request.CameraIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            request.DeviceKey, request.GroupKey, request.GroupMask, request.BroadcastAddress, request.RequirePtpLocked,
            request.MaxPtpOffsetNs, request.MaxTriggerSkewUs, request.ScheduledLeadTimeMs
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static CameraSynchronizationGroup Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<string[]>(r.GetString(3), Json) ?? [],
        r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7), r.GetInt32(8) != 0,
        r.GetInt64(9), r.GetDouble(10), r.GetInt32(11), r.GetString(12), DateTimeOffset.Parse(r.GetString(13)), DateTimeOffset.Parse(r.GetString(14)));
}


public sealed record CameraSynchronizationRunRecord(
    string RunId,
    string GroupId,
    string GroupConfigurationHash,
    string Source,
    bool Scheduled,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    long DurationMs,
    long? ScheduledDeviceTimeNs,
    string TimestampBasis,
    double? TriggerSkewUs,
    double MaxAllowedSkewUs,
    bool? WithinTolerance,
    string Outcome,
    string? Error,
    CameraActionCommandResult? Command,
    IReadOnlyList<CameraSynchronizationFrameResult> Frames,
    IReadOnlyList<CameraTimeSynchronizationStatus> PtpSnapshots,
    IReadOnlyList<CameraTransportTelemetry>? TransportSnapshots = null);


public sealed record CameraSynchronizationCommissioningAssessment(
    string Status,
    bool Passed,
    int MinimumSamples,
    double RequiredCompletionRate,
    double RequiredTolerancePassRate,
    IReadOnlyList<string> Reasons);

public sealed record CameraSynchronizationPtpCameraDiagnostics(
    string CameraId,
    int Samples,
    int ReadySamples,
    double ReadyRate,
    int OffsetSamples,
    double? AverageAbsOffsetNs,
    double? P95AbsOffsetNs,
    long? MaxAbsOffsetNs,
    int MasterClockChanges,
    IReadOnlyList<string> MasterClockIds);

public sealed record CameraSynchronizationPtpTrendPoint(
    string RunId,
    DateTimeOffset CompletedAt,
    string Outcome,
    double? TriggerSkewUs,
    long? MaxAbsOffsetNs,
    bool AllPtpReady,
    bool MasterClockConsistent,
    string? MasterClockId);

public sealed record CameraSynchronizationPtpDiagnostics(
    string GroupId,
    int SampleLimit,
    int TotalRuns,
    int RunsWithPtpEvidence,
    int PtpReadyRuns,
    int PtpNotReadyRuns,
    int InconsistentMasterClockRuns,
    double? OffsetSkewPearsonCorrelation,
    long? MaxAbsOffsetNs,
    double? P95MaxAbsOffsetNs,
    IReadOnlyList<CameraSynchronizationPtpCameraDiagnostics> Cameras,
    IReadOnlyList<CameraSynchronizationPtpTrendPoint> Trend);

public sealed record CameraSynchronizationStatistics(
    string GroupId,
    int SampleLimit,
    int TotalRuns,
    int CompletedRuns,
    int FailedRuns,
    int CancelledRuns,
    int MeasuredRuns,
    int WithinToleranceRuns,
    double CompletionRate,
    double TolerancePassRate,
    double? AverageSkewUs,
    double? P50SkewUs,
    double? P95SkewUs,
    double? P99SkewUs,
    double? MaxSkewUs,
    double? AverageDurationMs,
    double? P95DurationMs,
    int DevicePtpRuns,
    int HostArrivalRuns,
    DateTimeOffset? FirstCompletedAt,
    DateTimeOffset? LastCompletedAt,
    CameraSynchronizationCommissioningAssessment Commissioning);

public sealed class CameraSynchronizationRunStore(SqliteMetadataDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task RecordAsync(CameraSynchronizationRunRecord run, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
INSERT INTO camera_sync_runs(run_id,group_id,group_config_hash,source,scheduled,requested_at,completed_at,duration_ms,scheduled_device_time_ns,timestamp_basis,trigger_skew_us,max_allowed_skew_us,within_tolerance,outcome,error,command_json,frames_json,ptp_json,transport_json)
VALUES($run,$group,$hash,$source,$scheduled,$requested,$completed,$duration,$scheduledTime,$basis,$skew,$maxSkew,$within,$outcome,$error,$command,$frames,$ptp,$transport);
""";
        cmd.Parameters.AddWithValue("$run", run.RunId);
        cmd.Parameters.AddWithValue("$group", run.GroupId);
        cmd.Parameters.AddWithValue("$hash", run.GroupConfigurationHash);
        cmd.Parameters.AddWithValue("$source", run.Source);
        cmd.Parameters.AddWithValue("$scheduled", run.Scheduled ? 1 : 0);
        cmd.Parameters.AddWithValue("$requested", run.RequestedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", run.CompletedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$duration", run.DurationMs);
        cmd.Parameters.AddWithValue("$scheduledTime", (object?)run.ScheduledDeviceTimeNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$basis", run.TimestampBasis);
        cmd.Parameters.AddWithValue("$skew", (object?)run.TriggerSkewUs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxSkew", run.MaxAllowedSkewUs);
        cmd.Parameters.AddWithValue("$within", run.WithinTolerance is null ? DBNull.Value : run.WithinTolerance.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$outcome", run.Outcome);
        cmd.Parameters.AddWithValue("$error", (object?)run.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$command", run.Command is null ? DBNull.Value : JsonSerializer.Serialize(run.Command, Json));
        cmd.Parameters.AddWithValue("$frames", JsonSerializer.Serialize(run.Frames, Json));
        cmd.Parameters.AddWithValue("$ptp", JsonSerializer.Serialize(run.PtpSnapshots, Json));
        cmd.Parameters.AddWithValue("$transport", JsonSerializer.Serialize(run.TransportSnapshots ?? [], Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CameraSynchronizationRunRecord>> ListAsync(string? groupId = null, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(groupId)
            ? "SELECT run_id,group_id,group_config_hash,source,scheduled,requested_at,completed_at,duration_ms,scheduled_device_time_ns,timestamp_basis,trigger_skew_us,max_allowed_skew_us,within_tolerance,outcome,error,command_json,frames_json,ptp_json,transport_json FROM camera_sync_runs ORDER BY completed_at DESC LIMIT $limit;"
            : "SELECT run_id,group_id,group_config_hash,source,scheduled,requested_at,completed_at,duration_ms,scheduled_device_time_ns,timestamp_basis,trigger_skew_us,max_allowed_skew_us,within_tolerance,outcome,error,command_json,frames_json,ptp_json,transport_json FROM camera_sync_runs WHERE group_id=$group ORDER BY completed_at DESC LIMIT $limit;";
        if (!string.IsNullOrWhiteSpace(groupId)) cmd.Parameters.AddWithValue("$group", groupId.Trim());
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CameraSynchronizationRunRecord>();
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    public async Task<CameraSynchronizationRunRecord> GetAsync(string runId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT run_id,group_id,group_config_hash,source,scheduled,requested_at,completed_at,duration_ms,scheduled_device_time_ns,timestamp_basis,trigger_skew_us,max_allowed_skew_us,within_tolerance,outcome,error,command_json,frames_json,ptp_json,transport_json FROM camera_sync_runs WHERE run_id=$run;";
        cmd.Parameters.AddWithValue("$run", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException($"Camera synchronization run '{runId}' does not exist.");
        return Read(reader);
    }

    private static CameraSynchronizationRunRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0,
        DateTimeOffset.Parse(r.GetString(5)), DateTimeOffset.Parse(r.GetString(6)), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8),
        r.GetString(9), r.IsDBNull(10) ? null : r.GetDouble(10), r.GetDouble(11), r.IsDBNull(12) ? null : r.GetInt32(12) != 0,
        r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : JsonSerializer.Deserialize<CameraActionCommandResult>(r.GetString(15), Json),
        JsonSerializer.Deserialize<CameraSynchronizationFrameResult[]>(r.GetString(16), Json) ?? [],
        r.IsDBNull(17) ? [] : JsonSerializer.Deserialize<CameraTimeSynchronizationStatus[]>(r.GetString(17), Json) ?? [],
        r.IsDBNull(18) ? [] : JsonSerializer.Deserialize<CameraTransportTelemetry[]>(r.GetString(18), Json) ?? []);
}

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
            var provider = _actionProviders[group.Driver];
            var before = group.CameraIds.ToDictionary(x => x, x => cameras.GetLatestSequence(x), StringComparer.OrdinalIgnoreCase);
            long? tickFrequencyHz = null;
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
                scheduledTime = checked(clock + lead * 1_000_000L);
            }
            command = await provider.IssueActionCommandAsync(new CameraActionCommandRequest(
                DeviceKey: group.DeviceKey, GroupKey: group.GroupKey, GroupMask: group.GroupMask, BroadcastAddress: group.BroadcastAddress,
                ScheduledDeviceTimeNs: scheduledTime, TimeoutMs: 1000, DeviceTickFrequencyHz: tickFrequencyHz), ct);

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.FrameTimeoutMs, 100, 30_000));
            var tasks = group.CameraIds.Select(async cameraId => await cameras.WaitForFrameTimingAsync(cameraId, before[cameraId], timeout, ct).AsTask()).ToArray();
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
            var provider = _actionProviders[group.Driver];
            var before = group.CameraIds.ToDictionary(x => x, x => cameras.GetLatestSequence(x), StringComparer.OrdinalIgnoreCase);
            long? tickFrequencyHz = null;
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
            var tasks = group.CameraIds
                .Select(cameraId => cameras.WaitForFrameLeaseAsync(cameraId, before[cameraId], timeout, cancellationToken).AsTask())
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
