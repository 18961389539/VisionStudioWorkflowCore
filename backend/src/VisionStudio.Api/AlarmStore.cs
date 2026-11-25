using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public enum AlarmSeverity { Info, Warning, Error, Critical }

/// <summary>报警分页结果；Total 是裁剪到 take 之前的条数。</summary>
public sealed record AlarmPage(IReadOnlyList<AlarmRecord> Items, int Total);

public sealed record AlarmSummary(int ActiveCount, int CriticalCount, string? LatestActiveMessage, int PendingCount);

public sealed record AlarmRecord(
    string Id,
    string Code,
    AlarmSeverity Severity,
    string Source,
    string Message,
    bool Active,
    bool Acknowledged,
    DateTimeOffset RaisedAt,
    DateTimeOffset? RecoveredAt = null,
    DateTimeOffset? AcknowledgedAt = null,
    string? RunId = null,
    DateTimeOffset? LastRaisedAt = null,
    int OccurrenceCount = 1);

public sealed class AlarmStore
{
    /// <summary>
    /// 报警历史的保留上限（2026-10-26）。规则：**活动报警永不裁剪**（未恢复的报警是现场依据），
    /// 其余按 RaisedAt 保留最近的 N 条。
    /// </summary>
    public const int MaxRetainedRecords = 2000;

    /// <summary>列表接口的默认返回条数（未传 take 时）。</summary>
    public const int DefaultPageSize = 200;

    /// <summary>列表接口允许的最大 take，避免调用方一次拉全量。</summary>
    public const int MaxPageSize = 1000;

    private const string Columns = "id, code, severity, source, message, active, acknowledged, raised_at, recovered_at, acknowledged_at, run_id, COALESCE(last_raised_at, raised_at), occurrence_count";

    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly ILogger<AlarmStore> _logger;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private readonly Action<string, string> _archiveLegacyFile;

    /// <summary>测试与非 DI 场景的便捷构造：独立打开内容根下的元数据库。</summary>
    public AlarmStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, NullLogger<AlarmStore>.Instance)
    {
    }

    public AlarmStore(IVisionStudioDbFactory dbFactory, IWebHostEnvironment env, ILogger<AlarmStore> logger)
        : this(dbFactory, logger, File.Move)
    {
    }

    public AlarmStore(SqliteMetadataDatabase database, IWebHostEnvironment env, ILogger<AlarmStore> logger)
        : this(new VisionStudioDbFactory(database), logger, File.Move)
    {
    }

    internal AlarmStore(SqliteMetadataDatabase database, IWebHostEnvironment env, ILogger<AlarmStore> logger,
        Action<string, string> archiveLegacyFile)
        : this(new VisionStudioDbFactory(database), logger, archiveLegacyFile)
    {
    }

    private AlarmStore(IVisionStudioDbFactory dbFactory, ILogger<AlarmStore> logger, Action<string, string> archiveLegacyFile)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _archiveLegacyFile = archiveLegacyFile;
        var dir = Path.Combine(dbFactory.Database.DataRoot, "production");
        Directory.CreateDirectory(dir);
        _legacyPath = Path.Combine(dir, "alarms.json");
    }

    /// <summary>
    /// 报警库的轻量版本戳（2026-11-13）：监视推送用它判断"有没有新报警/恢复/确认"，
    /// 不必每秒拉一遍列表。条数 + 三类时间最大值 + 两个布尔和，足以区分任何一次写入。
    /// </summary>
    public string RevisionStamp()
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var rows = db.Alarms
                .Select(x => new
                {
                    Last = x.LastRaisedAt ?? x.RaisedAt,
                    x.RecoveredAt,
                    x.AcknowledgedAt,
                    Active = x.Active ? 1 : 0,
                    Acknowledged = x.Acknowledged ? 1 : 0,
                    x.OccurrenceCount
                })
                .ToList();
            if (rows.Count == 0) return "0::::0:0:0";
            var maxLast = rows.Max(x => x.Last);
            var maxRecovered = rows.Where(x => x.RecoveredAt != null).Max(x => x.RecoveredAt);
            var maxAcknowledged = rows.Where(x => x.AcknowledgedAt != null).Max(x => x.AcknowledgedAt);
            return string.Join(':',
                rows.Count.ToString(CultureInfo.InvariantCulture),
                FormatTimestamp(maxLast),
                maxRecovered is { } r ? FormatTimestamp(r) : string.Empty,
                maxAcknowledged is { } a ? FormatTimestamp(a) : string.Empty,
                rows.Sum(x => x.Active).ToString(CultureInfo.InvariantCulture),
                rows.Sum(x => x.Acknowledged).ToString(CultureInfo.InvariantCulture),
                rows.Sum(x => (long)x.OccurrenceCount).ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // 数据库尚未初始化/被维护占用时退化为"未知"：下一次 tick 会重新取到稳定值。
            return "unknown";
        }
    }

    /// <summary>
    /// 分页读取报警。总数为筛选后、分页前的条数（供 X-Total-Count）。
    /// 待确认筛选只包含仍在活动且尚未确认的报警。
    /// take 会被夹在 [1, MaxPageSize]，skip 负数按 0 处理。
    /// </summary>
    public async Task<AlarmPage> ListAsync(bool activeOnly, int skip, int take, CancellationToken ct, bool pendingOnly = false, string? source = null)
    {
        await EnsureLegacyImportedAsync(ct);
        var effectiveSkip = Math.Max(0, skip);
        var effectiveTake = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        IQueryable<AlarmEntity> query = db.Alarms;
        if (pendingOnly) query = query.Where(x => x.Active && !x.Acknowledged);
        else if (activeOnly) query = query.Where(x => x.Active);
        if (source is not null) query = query.Where(x => x.Source == source);

        var total = await query.CountAsync(ct);
        var entities = await query
            .OrderByDescending(x => x.RaisedAt)
            .ThenByDescending(x => x.Id)
            .Skip(effectiveSkip)
            .Take(effectiveTake)
            .ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new AlarmPage(entities.Select(ToRecord).ToList(), total);
    }

    public async Task<AlarmSummary> GetSummaryAsync(CancellationToken ct, string? source = null)
    {
        await EnsureLegacyImportedAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Alarms.Where(x => x.Active);
        if (source is not null) query = query.Where(x => x.Source == source);

        var criticalName = nameof(AlarmSeverity.Critical);
        var activeCount = await query.CountAsync(ct);
        var criticalCount = await query.CountAsync(x => x.Severity == criticalName, ct);
        var pendingCount = await query.CountAsync(x => !x.Acknowledged, ct);
        var latestMessage = await query
            .OrderByDescending(x => x.LastRaisedAt ?? x.RaisedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Message)
            .FirstOrDefaultAsync(ct);
        return new AlarmSummary(activeCount, criticalCount, latestMessage, pendingCount);
    }

    /// <summary>
    /// 保留策略：活动报警全留，其余按时间倒序保留到上限。
    /// 抽成纯函数是为了能在单测里直接覆盖边界（不需要写 2000 条记录）。
    /// </summary>
    public static List<AlarmRecord> TrimToRetention(IEnumerable<AlarmRecord> items, int maxRetained = MaxRetainedRecords)
    {
        var all = items.ToList();
        if (all.Count <= maxRetained) return all;
        var active = all.Where(x => x.Active).ToList();
        var rest = all.Where(x => !x.Active).OrderByDescending(x => x.RaisedAt).ToList();
        var keepInactive = Math.Max(0, maxRetained - active.Count);
        return active.Concat(rest.Take(keepInactive)).ToList();
    }

    public async Task<AlarmRecord> RaiseAsync(string code, AlarmSeverity severity, string source, string message, string? runId, CancellationToken ct)
    {
        await EnsureLegacyImportedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var entity = await db.Alarms
                .AsTracking()
                .FirstOrDefaultAsync(x => x.Active && x.Code == code && x.Source == source, ct);
            if (entity is not null)
            {
                entity.Severity = severity.ToString();
                entity.Message = message;
                entity.RunId = runId;
                var currentLast = entity.LastRaisedAt ?? entity.RaisedAt;
                entity.LastRaisedAt = now > currentLast ? now : currentLast;
                entity.OccurrenceCount += 1;
            }
            else
            {
                entity = new AlarmEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Code = code,
                    Severity = severity.ToString(),
                    Source = source,
                    Message = message,
                    Active = true,
                    Acknowledged = false,
                    RaisedAt = now,
                    LastRaisedAt = now,
                    RunId = runId,
                    OccurrenceCount = 1
                };
                db.Alarms.Add(entity);
            }

            await db.SaveChangesAsync(ct);
            await TrimAsync(db, ct);
            await tx.CommitAsync(ct);
            return ToRecord(entity);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task RecoverAsync(string code, string source, CancellationToken ct)
    {
        await EnsureLegacyImportedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Alarms
            .Where(x => x.Active && x.Code == code && x.Source == source)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Active, false)
                .SetProperty(x => x.RecoveredAt, now), ct);
    }

    public async Task<AlarmRecord> AcknowledgeAsync(string id, CancellationToken ct)
    {
        await EnsureLegacyImportedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var entity = await db.Alarms
                .AsTracking()
                .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new ApiNotFoundException($"Alarm '{id}' does not exist.");

            entity.Acknowledged = true;
            entity.AcknowledgedAt ??= now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToRecord(entity);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 保留策略的事务内版本：活动报警全留，已恢复报警只留最新到上限。
    /// 与 <see cref="TrimToRetention"/> 同口径，避免把上限条记录读进内存。
    /// </summary>
    private static async Task TrimAsync(VisionStudioDbContext db, CancellationToken ct)
    {
        var active = await db.Alarms.CountAsync(x => x.Active, ct);
        var keep = Math.Max(0, MaxRetainedRecords - active);
        var keepIds = db.Alarms
            .Where(x => !x.Active)
            .OrderByDescending(x => x.RaisedAt)
            .ThenByDescending(x => x.Id)
            .Take(keep)
            .Select(x => x.Id);
        await db.Alarms
            .Where(x => !x.Active && !keepIds.Contains(x.Id))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// 导入和完成标记一起提交。提交后归档失败时，后续请求只重试归档。
    /// </summary>
    private async Task EnsureLegacyImportedAsync(CancellationToken ct)
    {
        if (!File.Exists(_legacyPath)) return;
        await _legacyGate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_legacyPath)) return;
            await ImportLegacyFileAsync(ct);
            ct.ThrowIfCancellationRequested();

            var archived = $"{_legacyPath}.migrated-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}";
            _archiveLegacyFile(_legacyPath, archived);
            _logger.LogInformation("Archived imported legacy alarms from {Path} to {Archived}.", _legacyPath, archived);
        }
        catch (FileNotFoundException) when (!File.Exists(_legacyPath))
        {
            // 另一宿主已将文件归档。
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not import or archive legacy alarms from {Path}.", _legacyPath);
        }
        finally { _legacyGate.Release(); }
    }

    private async Task ImportLegacyFileAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct, strongDurability: true);
        await using var tx = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        if (await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM legacy_alarm_import WHERE id = 1").FirstAsync(ct) != 0)
            return;

        List<AlarmRecord> items;
        await using (var stream = new FileStream(_legacyPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            items = await JsonSerializer.DeserializeAsync<List<AlarmRecord>>(stream, LegacyJson, ct) ?? [];
        }

        // 保留数据库中已有的状态。旧文件内同一活动报警取最新一条。
        var existingIds = (await db.Alarms.Select(x => x.Id).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var activeKeys = (await db.Alarms.Where(x => x.Active).Select(x => new { x.Code, x.Source }).ToListAsync(ct))
            .Select(x => (x.Code, x.Source))
            .ToHashSet();

        foreach (var item in items.OrderByDescending(x => x.RaisedAt))
        {
            ct.ThrowIfCancellationRequested();
            if (!existingIds.Add(item.Id)) continue;
            if (item.Active && !activeKeys.Add((item.Code, item.Source))) continue;
            db.Alarms.Add(new AlarmEntity
            {
                Id = item.Id,
                Code = item.Code,
                Severity = item.Severity.ToString(),
                Source = item.Source,
                Message = item.Message,
                Active = item.Active,
                Acknowledged = item.Acknowledged,
                RaisedAt = item.RaisedAt,
                RecoveredAt = item.RecoveredAt,
                AcknowledgedAt = item.AcknowledgedAt,
                RunId = item.RunId,
                LastRaisedAt = item.LastRaisedAt,
                OccurrenceCount = item.OccurrenceCount
            });
        }
        await db.SaveChangesAsync(ct);
        await TrimAsync(db, ct);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO legacy_alarm_import(id) VALUES(1);", ct);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(ct);
        _logger.LogInformation("Imported legacy alarms from {Path} into SQLite.", _legacyPath);
    }

    private static AlarmRecord ToRecord(AlarmEntity entity) => new(
        Id: entity.Id,
        Code: entity.Code,
        Severity: ParseSeverity(entity.Severity),
        Source: entity.Source,
        Message: entity.Message,
        Active: entity.Active,
        Acknowledged: entity.Acknowledged,
        RaisedAt: entity.RaisedAt,
        RecoveredAt: entity.RecoveredAt,
        AcknowledgedAt: entity.AcknowledgedAt,
        RunId: entity.RunId,
        LastRaisedAt: entity.LastRaisedAt ?? entity.RaisedAt,
        OccurrenceCount: entity.OccurrenceCount);

    private static AlarmSeverity ParseSeverity(string value)
        => Enum.TryParse<AlarmSeverity>(value, ignoreCase: true, out var severity) ? severity : AlarmSeverity.Warning;

    /// <summary>统一存 UTC 往返格式：字符串排序与时间排序一致，聚合最大值才能当版本戳。</summary>
    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
