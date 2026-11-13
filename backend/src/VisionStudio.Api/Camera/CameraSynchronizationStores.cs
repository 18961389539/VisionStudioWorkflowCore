using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Camera;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

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

