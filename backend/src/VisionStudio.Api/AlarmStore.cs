using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Infrastructure;

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

    private readonly SqliteMetadataDatabase _database;
    private readonly ILogger<AlarmStore> _logger;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private readonly Action<string, string> _archiveLegacyFile;

    /// <summary>测试与非 DI 场景的便捷构造：独立打开内容根下的元数据库。</summary>
    public AlarmStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, NullLogger<AlarmStore>.Instance)
    {
    }

    public AlarmStore(SqliteMetadataDatabase database, IWebHostEnvironment env, ILogger<AlarmStore> logger)
        : this(database, env, logger, File.Move)
    {
    }

    internal AlarmStore(SqliteMetadataDatabase database, IWebHostEnvironment env, ILogger<AlarmStore> logger,
        Action<string, string> archiveLegacyFile)
    {
        _database = database;
        _logger = logger;
        _archiveLegacyFile = archiveLegacyFile;
        var dir = Path.Combine(database.DataRoot, "production");
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
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT COUNT(*),
       COALESCE(MAX(COALESCE(last_raised_at, raised_at)), ''),
       COALESCE(MAX(recovered_at), ''),
       COALESCE(MAX(acknowledged_at), ''),
       COALESCE(SUM(active), 0),
       COALESCE(SUM(acknowledged), 0),
       COALESCE(SUM(occurrence_count), 0)
FROM alarms;
""";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return "0";
            return string.Join(':',
                reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(5).ToString(CultureInfo.InvariantCulture),
                reader.GetInt64(6).ToString(CultureInfo.InvariantCulture));
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
        var filter = (pendingOnly, activeOnly) switch
        {
            (true, _) => " WHERE active = 1 AND acknowledged = 0",
            (_, true) => " WHERE active = 1",
            _ => string.Empty
        };
        if (source is not null)
            filter += filter.Length == 0 ? " WHERE source = $source" : " AND source = $source";

        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: true);
        var total = source is null
            ? await ScalarIntAsync(connection, tx, $"SELECT COUNT(*) FROM alarms{filter};", ct)
            : await ScalarIntAsync(connection, tx, $"SELECT COUNT(*) FROM alarms{filter};", ct, ("$source", source));

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"""
SELECT {Columns}
FROM alarms{filter}
ORDER BY raised_at DESC, id DESC
LIMIT $take OFFSET $skip;
""";
        command.Parameters.AddWithValue("$take", effectiveTake);
        command.Parameters.AddWithValue("$skip", effectiveSkip);
        if (source is not null) command.Parameters.AddWithValue("$source", source);

        var items = new List<AlarmRecord>(Math.Min(effectiveTake, 128));
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) items.Add(ReadRecord(reader));
        }
        await tx.CommitAsync(ct);
        return new AlarmPage(items, total);
    }

    public async Task<AlarmSummary> GetSummaryAsync(CancellationToken ct, string? source = null)
    {
        await EnsureLegacyImportedAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var sourceFilter = source is null ? string.Empty : " AND source = $source";
        command.CommandText = $"""
SELECT COUNT(*),
       COUNT(CASE WHEN severity = $critical THEN 1 END),
       (SELECT message FROM alarms WHERE active = 1{sourceFilter} ORDER BY COALESCE(last_raised_at, raised_at) DESC, id DESC LIMIT 1),
       COUNT(CASE WHEN acknowledged = 0 THEN 1 END)
FROM alarms
WHERE active = 1{sourceFilter};
""";
        command.Parameters.AddWithValue("$critical", nameof(AlarmSeverity.Critical));
        if (source is not null) command.Parameters.AddWithValue("$source", source);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new AlarmSummary(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3));
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
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"""
INSERT INTO alarms(id, code, severity, source, message, active, acknowledged, raised_at, last_raised_at, run_id)
VALUES($id, $code, $severity, $source, $message, 1, 0, $now, $now, $runId)
ON CONFLICT(code, source) WHERE active = 1 DO UPDATE SET
    severity = excluded.severity, message = excluded.message, run_id = excluded.run_id,
    last_raised_at = MAX(COALESCE(alarms.last_raised_at, alarms.raised_at), excluded.last_raised_at),
    occurrence_count = alarms.occurrence_count + 1
RETURNING {Columns};
""";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$severity", severity.ToString());
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
            AlarmRecord alarm;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Alarm write returned no record.");
                alarm = ReadRecord(reader);
            }
            await TrimAsync(connection, tx, ct);
            await tx.CommitAsync(ct);
            return alarm;
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
        await using var connection = await _database.OpenConnectionAsync(ct);
        await ExecuteAsync(connection, null, """
UPDATE alarms
SET active = 0, recovered_at = $now
WHERE active = 1 AND code = $code AND source = $source;
""", ct,
            ("$now", FormatTimestamp(DateTimeOffset.UtcNow)),
            ("$code", code),
            ("$source", source));
    }

    public async Task<AlarmRecord> AcknowledgeAsync(string id, CancellationToken ct)
    {
        await EnsureLegacyImportedAsync(ct);
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            var affected = await ExecuteAsync(connection, tx,
                "UPDATE alarms SET acknowledged = 1, acknowledged_at = COALESCE(acknowledged_at, $now) WHERE id = $id;", ct,
                ("$now", FormatTimestamp(DateTimeOffset.UtcNow)), ("$id", id));
            if (affected == 0) throw new ApiNotFoundException($"Alarm '{id}' does not exist.");

            await using var query = connection.CreateCommand();
            query.Transaction = tx;
            query.CommandText = $"SELECT {Columns} FROM alarms WHERE id = $id;";
            query.Parameters.AddWithValue("$id", id);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ApiNotFoundException($"Alarm '{id}' does not exist.");
            var record = ReadRecord(reader);
            await reader.DisposeAsync();
            await tx.CommitAsync(ct);
            return record;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 保留策略的事务内版本：活动报警全留，已恢复报警只留最新到上限。
    /// 与 <see cref="TrimToRetention"/> 同口径，但用 SQL 完成，避免把上限条记录读进内存。
    /// </summary>
    private static async Task TrimAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken ct)
    {
        var active = await ScalarIntAsync(connection, tx, "SELECT COUNT(*) FROM alarms WHERE active = 1;", ct);
        var keep = Math.Max(0, MaxRetainedRecords - active);
        await ExecuteAsync(connection, tx, """
DELETE FROM alarms
WHERE active = 0
  AND id NOT IN (SELECT id FROM alarms WHERE active = 0 ORDER BY raised_at DESC, id DESC LIMIT $keep);
""", ct, ("$keep", keep));
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
        await using var connection = await _database.OpenConnectionAsync(ct, strongDurability: true);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (await ScalarIntAsync(connection, tx, "SELECT COUNT(*) FROM legacy_alarm_import WHERE id = 1;", ct) != 0)
            return;

        List<AlarmRecord> items;
        await using (var stream = new FileStream(_legacyPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            items = await JsonSerializer.DeserializeAsync<List<AlarmRecord>>(stream, LegacyJson, ct) ?? [];
        }

        // 保留数据库中已有的状态。旧文件内同一活动报警取最新一条。
        foreach (var item in items.OrderByDescending(x => x.RaisedAt))
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteAsync(connection, tx, """
INSERT INTO alarms(id, code, severity, source, message, active, acknowledged, raised_at, recovered_at, acknowledged_at, run_id)
VALUES($id, $code, $severity, $source, $message, $active, $acknowledged, $raisedAt, $recoveredAt, $acknowledgedAt, $runId)
ON CONFLICT DO NOTHING;
""", ct,
                ("$id", item.Id),
                ("$code", item.Code),
                ("$severity", item.Severity.ToString()),
                ("$source", item.Source),
                ("$message", item.Message),
                ("$active", item.Active ? 1 : 0),
                ("$acknowledged", item.Acknowledged ? 1 : 0),
                ("$raisedAt", FormatTimestamp(item.RaisedAt)),
                ("$recoveredAt", item.RecoveredAt is { } recovered ? FormatTimestamp(recovered) : null),
                ("$acknowledgedAt", item.AcknowledgedAt is { } acknowledged ? FormatTimestamp(acknowledged) : null),
                ("$runId", item.RunId));
        }
        await TrimAsync(connection, tx, ct);
        await ExecuteAsync(connection, tx, "INSERT INTO legacy_alarm_import(id) VALUES(1);", ct);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(ct);
        _logger.LogInformation("Imported legacy alarms from {Path} into SQLite.", _legacyPath);
    }

    private static AlarmRecord ReadRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Code: reader.GetString(1),
        Severity: ParseSeverity(reader.GetString(2)),
        Source: reader.GetString(3),
        Message: reader.GetString(4),
        Active: reader.GetInt32(5) != 0,
        Acknowledged: reader.GetInt32(6) != 0,
        RaisedAt: ParseTimestamp(reader.GetString(7)),
        RecoveredAt: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
        AcknowledgedAt: reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
        RunId: reader.IsDBNull(10) ? null : reader.GetString(10),
        LastRaisedAt: ParseTimestamp(reader.GetString(11)),
        OccurrenceCount: reader.GetInt32(12));

    private static AlarmSeverity ParseSeverity(string value)
        => Enum.TryParse<AlarmSeverity>(value, ignoreCase: true, out var severity) ? severity : AlarmSeverity.Warning;

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>统一存 UTC 往返格式：字符串排序与时间排序一致，聚合最大值才能当版本戳。</summary>
    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
