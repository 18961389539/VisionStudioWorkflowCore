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
    string? RunId = null);

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

    private const string Columns = "id, code, severity, source, message, active, acknowledged, raised_at, recovered_at, acknowledged_at, run_id";

    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SqliteMetadataDatabase _database;
    private readonly ILogger<AlarmStore> _logger;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _legacyGate = new(1, 1);
    private volatile bool _legacyImported;

    /// <summary>测试与非 DI 场景的便捷构造：独立打开内容根下的元数据库。</summary>
    public AlarmStore(IWebHostEnvironment env)
        : this(new SqliteMetadataDatabase(env), env, NullLogger<AlarmStore>.Instance)
    {
    }

    public AlarmStore(SqliteMetadataDatabase database, IWebHostEnvironment env, ILogger<AlarmStore> logger)
    {
        _database = database;
        _logger = logger;
        var dir = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "production");
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
       COALESCE(MAX(raised_at), ''),
       COALESCE(MAX(recovered_at), ''),
       COALESCE(MAX(acknowledged_at), ''),
       COALESCE(SUM(active), 0),
       COALESCE(SUM(acknowledged), 0)
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
                reader.GetInt32(5).ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // 数据库尚未初始化/被维护占用时退化为"未知"：下一次 tick 会重新取到稳定值。
            return "unknown";
        }
    }

    /// <summary>
    /// 分页读取报警。返回分页结果与**裁剪前**的总数（供 X-Total-Count），
    /// take 会被夹在 [1, MaxPageSize]，skip 负数按 0 处理。
    /// </summary>
    public async Task<AlarmPage> ListAsync(bool activeOnly, int skip, int take, CancellationToken ct)
    {
        await EnsureLegacyImportedAsync(ct);
        var effectiveSkip = Math.Max(0, skip);
        var effectiveTake = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);
        var filter = activeOnly ? " WHERE active = 1" : string.Empty;

        await using var connection = await _database.OpenConnectionAsync(ct);
        var total = await ScalarIntAsync(connection, null, $"SELECT COUNT(*) FROM alarms{filter};", ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
SELECT {Columns}
FROM alarms{filter}
ORDER BY raised_at DESC
LIMIT $take OFFSET $skip;
""";
        command.Parameters.AddWithValue("$take", effectiveTake);
        command.Parameters.AddWithValue("$skip", effectiveSkip);

        var items = new List<AlarmRecord>(Math.Min(effectiveTake, 128));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadRecord(reader));
        return new AlarmPage(items, total);
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
        var alarm = new AlarmRecord(Guid.NewGuid().ToString("N"), code, severity, source, message, true, false, DateTimeOffset.UtcNow, null, null, runId);

        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            // 旧 JSON 实现用"找到活动同类就替换"维持 (code, source) 唯一；这里先删后插，
            // 部分唯一索引 ux_alarms_active_code_source 负责兜底并发写入。
            await ExecuteAsync(connection, tx, "DELETE FROM alarms WHERE active = 1 AND code = $code AND source = $source;", ct,
                ("$code", code), ("$source", source));
            await ExecuteAsync(connection, tx, """
INSERT INTO alarms(id, code, severity, source, message, active, acknowledged, raised_at, recovered_at, acknowledged_at, run_id)
VALUES($id, $code, $severity, $source, $message, 1, 0, $raisedAt, NULL, NULL, $runId);
""", ct,
                ("$id", alarm.Id),
                ("$code", alarm.Code),
                ("$severity", alarm.Severity.ToString()),
                ("$source", alarm.Source),
                ("$message", alarm.Message),
                ("$raisedAt", FormatTimestamp(alarm.RaisedAt)),
                ("$runId", alarm.RunId));
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
                "UPDATE alarms SET acknowledged = 1, acknowledged_at = $now WHERE id = $id;", ct,
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
  AND id NOT IN (SELECT id FROM alarms WHERE active = 0 ORDER BY raised_at DESC LIMIT $keep);
""", ct, ("$keep", keep));
    }

    /// <summary>
    /// 旧 alarms.json 一次性导入（2026-11-13）。导入成功后把文件改名为
    /// <c>alarms.json.migrated-时间戳</c>，避免下次启动重复覆盖新数据；导入失败只告警，
    /// 保留原文件供人工恢复，报警功能继续用空表运行。
    /// </summary>
    private async Task EnsureLegacyImportedAsync(CancellationToken ct)
    {
        if (_legacyImported) return;
        await _legacyGate.WaitAsync(ct);
        try
        {
            if (_legacyImported) return;
            if (File.Exists(_legacyPath))
            {
                try
                {
                    List<AlarmRecord> items;
                    await using (var stream = new FileStream(_legacyPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        items = await JsonSerializer.DeserializeAsync<List<AlarmRecord>>(stream, LegacyJson, ct) ?? [];
                    }

                    await using var connection = await _database.OpenConnectionAsync(ct);
                    await using var tx = connection.BeginTransaction(deferred: false);
                    foreach (var item in items.OrderBy(x => x.RaisedAt))
                    {
                        // OR REPLACE：同一 (code, source) 的活动报警保留最新一条，与 RaiseAsync 语义一致。
                        await ExecuteAsync(connection, tx, """
INSERT OR REPLACE INTO alarms(id, code, severity, source, message, active, acknowledged, raised_at, recovered_at, acknowledged_at, run_id)
VALUES($id, $code, $severity, $source, $message, $active, $acknowledged, $raisedAt, $recoveredAt, $acknowledgedAt, $runId);
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
                    await tx.CommitAsync(ct);

                    var archived = $"{_legacyPath}.migrated-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                    File.Move(_legacyPath, archived, overwrite: true);
                    _logger.LogInformation("Migrated {Count} alarms from {Path} to SQLite (archived as {Archived}).", items.Count, _legacyPath, archived);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import legacy alarms from {Path}; the file is left in place for manual recovery.", _legacyPath);
                }
            }
            _legacyImported = true;
        }
        finally { _legacyGate.Release(); }
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
        RunId: reader.IsDBNull(10) ? null : reader.GetString(10));

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
