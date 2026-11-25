using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed class CameraSynchronizationGroupStore(IVisionStudioDbFactory dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public CameraSynchronizationGroupStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task<IReadOnlyList<CameraSynchronizationGroup>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entities = await db.CameraSyncGroups.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<CameraSynchronizationGroup> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.CameraSyncGroups.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException($"Camera synchronization group '{id}' does not exist.");
        return ToModel(entity);
    }

    public async Task<CameraSynchronizationGroup> UpsertAsync(CameraSynchronizationGroupRequest request, CancellationToken ct = default)
    {
        var normalized = Normalize(request);
        var hash = ComputeHash(normalized);
        var now = DateTimeOffset.UtcNow;
        var camerasJson = JsonSerializer.Serialize(normalized.CameraIds, Json);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var entity = await db.CameraSyncGroups.AsTracking().FirstOrDefaultAsync(x => x.Id == normalized.Id, ct);
            if (entity is null)
            {
                entity = new CameraSyncGroupEntity
                {
                    Id = normalized.Id,
                    Name = normalized.Name,
                    Driver = normalized.Driver,
                    CameraIdsJson = camerasJson,
                    DeviceKey = normalized.DeviceKey,
                    GroupKey = normalized.GroupKey,
                    GroupMask = normalized.GroupMask,
                    BroadcastAddress = normalized.BroadcastAddress,
                    RequirePtpLocked = normalized.RequirePtpLocked,
                    MaxPtpOffsetNs = normalized.MaxPtpOffsetNs,
                    MaxTriggerSkewUs = normalized.MaxTriggerSkewUs,
                    ScheduledLeadTimeMs = normalized.ScheduledLeadTimeMs,
                    ConfigHash = hash,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.CameraSyncGroups.Add(entity);
            }
            else
            {
                entity.Name = normalized.Name;
                entity.Driver = normalized.Driver;
                entity.CameraIdsJson = camerasJson;
                entity.DeviceKey = normalized.DeviceKey;
                entity.GroupKey = normalized.GroupKey;
                entity.GroupMask = normalized.GroupMask;
                entity.BroadcastAddress = normalized.BroadcastAddress;
                entity.RequirePtpLocked = normalized.RequirePtpLocked;
                entity.MaxPtpOffsetNs = normalized.MaxPtpOffsetNs;
                entity.MaxTriggerSkewUs = normalized.MaxTriggerSkewUs;
                entity.ScheduledLeadTimeMs = normalized.ScheduledLeadTimeMs;
                entity.ConfigHash = hash;
                entity.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new CameraSynchronizationGroup(normalized.Id, normalized.Name, normalized.Driver, normalized.CameraIds, normalized.DeviceKey, normalized.GroupKey, normalized.GroupMask, normalized.BroadcastAddress, normalized.RequirePtpLocked, normalized.MaxPtpOffsetNs, normalized.MaxTriggerSkewUs, normalized.ScheduledLeadTimeMs, hash, entity.CreatedAt, now);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.CameraSyncGroups.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        if (affected == 0) throw new KeyNotFoundException($"Camera synchronization group '{id}' does not exist.");
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

    private static CameraSynchronizationGroup ToModel(CameraSyncGroupEntity e) => new(
        e.Id, e.Name, e.Driver,
        JsonSerializer.Deserialize<string[]>(e.CameraIdsJson, Json) ?? [],
        (int)e.DeviceKey, (int)e.GroupKey, (int)e.GroupMask, e.BroadcastAddress, e.RequirePtpLocked,
        e.MaxPtpOffsetNs, e.MaxTriggerSkewUs, e.ScheduledLeadTimeMs, e.ConfigHash, e.CreatedAt, e.UpdatedAt);
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

public sealed class CameraSynchronizationRunStore(IVisionStudioDbFactory dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public CameraSynchronizationRunStore(SqliteMetadataDatabase database) : this(new VisionStudioDbFactory(database)) { }

    public async Task RecordAsync(CameraSynchronizationRunRecord run, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.CameraSyncRuns.Add(new CameraSyncRunEntity
        {
            RunId = run.RunId,
            GroupId = run.GroupId,
            GroupConfigHash = run.GroupConfigurationHash,
            Source = run.Source,
            Scheduled = run.Scheduled,
            RequestedAt = run.RequestedAt,
            CompletedAt = run.CompletedAt,
            DurationMs = run.DurationMs,
            ScheduledDeviceTimeNs = run.ScheduledDeviceTimeNs,
            TimestampBasis = run.TimestampBasis,
            TriggerSkewUs = run.TriggerSkewUs,
            MaxAllowedSkewUs = run.MaxAllowedSkewUs,
            WithinTolerance = run.WithinTolerance,
            Outcome = run.Outcome,
            Error = run.Error,
            CommandJson = run.Command is null ? null : JsonSerializer.Serialize(run.Command, Json),
            FramesJson = JsonSerializer.Serialize(run.Frames, Json),
            PtpJson = JsonSerializer.Serialize(run.PtpSnapshots, Json),
            TransportJson = JsonSerializer.Serialize(run.TransportSnapshots ?? [], Json)
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CameraSynchronizationRunRecord>> ListAsync(string? groupId = null, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<CameraSyncRunEntity> query = db.CameraSyncRuns;
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            var trimmed = groupId.Trim();
            query = query.Where(x => x.GroupId == trimmed);
        }
        var entities = await query
            .OrderByDescending(x => x.CompletedAt)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<CameraSynchronizationRunRecord> GetAsync(string runId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.CameraSyncRuns.FirstOrDefaultAsync(x => x.RunId == runId, ct)
            ?? throw new KeyNotFoundException($"Camera synchronization run '{runId}' does not exist.");
        return ToModel(entity);
    }

    private static CameraSynchronizationRunRecord ToModel(CameraSyncRunEntity e) => new(
        e.RunId, e.GroupId, e.GroupConfigHash, e.Source, e.Scheduled,
        e.RequestedAt, e.CompletedAt, e.DurationMs, e.ScheduledDeviceTimeNs,
        e.TimestampBasis, e.TriggerSkewUs, e.MaxAllowedSkewUs, e.WithinTolerance,
        e.Outcome, e.Error,
        e.CommandJson is null ? null : JsonSerializer.Deserialize<CameraActionCommandResult>(e.CommandJson, Json),
        JsonSerializer.Deserialize<CameraSynchronizationFrameResult[]>(e.FramesJson, Json) ?? [],
        string.IsNullOrWhiteSpace(e.PtpJson) ? [] : JsonSerializer.Deserialize<CameraTimeSynchronizationStatus[]>(e.PtpJson, Json) ?? [],
        string.IsNullOrWhiteSpace(e.TransportJson) ? [] : JsonSerializer.Deserialize<CameraTransportTelemetry[]>(e.TransportJson, Json) ?? []);
}

