using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Security;

public sealed record AuditActionMetadata(string Action, string Resource);

/// <summary>
/// 只对服务账号令牌的调用留审计（P3-6）：追溯读接口是人类高频轮询路径，
/// 给人也记一条会把审计表变成访问日志；而 MES 每读一次结果都属于"令牌被谁用在哪"的证据。
/// </summary>
public sealed record ServiceUsageAuditMetadata(string Action, string Resource);

public static class AuditEndpointExtensions
{
    public static TBuilder WithAudit<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.WithMetadata(new AuditActionMetadata(action, resource)); return builder; }

    public static TBuilder WithServiceUsageAudit<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.WithMetadata(new ServiceUsageAuditMetadata(action, resource)); return builder; }
}

public static class AuditContext
{
    private const string TargetKey = "VisionStudio.AuditTarget";
    public static void SetTarget(HttpContext context, string? target) { if (!string.IsNullOrWhiteSpace(target)) context.Items[TargetKey] = target; }
    internal static string? GetTarget(HttpContext context) => context.Items.TryGetValue(TargetKey, out var value) ? value?.ToString() : null;
}

public sealed class AuditEventStore(SqliteMetadataDatabase database)
{
    private long _persistFailures;
    private DateTimeOffset? _lastPersistFailureAt;
    private string? _lastPersistFailure;

    /// <summary>审计持久化失败计数（单例计数器）：数据库忙/磁盘故障时记录写入失败；同时会写错误日志并记录最近一次失败详情。</summary>
    public long PersistFailures => Interlocked.Read(ref _persistFailures);
    public DateTimeOffset? LastPersistFailureAt => _lastPersistFailureAt;
    public string? LastPersistFailure => _lastPersistFailure;

    internal void RecordPersistFailure(Exception ex)
    {
        Interlocked.Increment(ref _persistFailures);
        _lastPersistFailureAt = DateTimeOffset.UtcNow;
        _lastPersistFailure = ex.Message;
    }

    public async Task RecordAsync(HttpContext context, AuditActionMetadata meta, int statusCode, CancellationToken ct = default)
    {
        var user = context.User;
        var now = DateTimeOffset.UtcNow;
        var entry = new AuditChainEntry(
            0,
            now.ToString("O"),
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.Identity?.Name,
            user.FindFirstValue(ClaimTypes.Role),
            meta.Action,
            meta.Resource,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            statusCode,
            statusCode is >= 200 and < 400,
            AuditContext.GetTarget(context) ?? RouteTarget(context),
            RequestCorrelation.Get(context),
            context.Connection.RemoteIpAddress?.ToString());

        await using var connection = await database.OpenConnectionAsync(ct);
        // 链式追加必须独占写事务：并发审计同时读"最后一行 hash"会把链分叉成两条同 seq 的链。
        await using var transaction = connection.BeginTransaction(deferred: false);
        var head = await ReadChainHeadAsync(connection, transaction, ct);
        entry = entry with { Seq = head.NextSeq };
        var previous = head.Hash ?? AuditChain.GenesisHash;
        var hash = AuditChain.ComputeHash(previous, entry);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT INTO audit_events(seq,at,user_id,username,role,action,resource,method,path,status_code,success,target,correlation_id,client_ip,prev_hash,hash)
VALUES($seq,$at,$userId,$username,$role,$action,$resource,$method,$path,$status,$success,$target,$correlation,$ip,$prev,$hash);
""";
        command.Parameters.AddWithValue("$seq", entry.Seq);
        command.Parameters.AddWithValue("$at", entry.At);
        command.Parameters.AddWithValue("$userId", Db(entry.UserId));
        command.Parameters.AddWithValue("$username", Db(entry.Username));
        command.Parameters.AddWithValue("$role", Db(entry.Role));
        command.Parameters.AddWithValue("$action", entry.Action); command.Parameters.AddWithValue("$resource", entry.Resource);
        command.Parameters.AddWithValue("$method", entry.Method); command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$status", entry.StatusCode); command.Parameters.AddWithValue("$success", entry.Success ? 1 : 0);
        command.Parameters.AddWithValue("$target", Db(entry.Target));
        command.Parameters.AddWithValue("$correlation", Db(entry.CorrelationId)); command.Parameters.AddWithValue("$ip", Db(entry.ClientIp));
        command.Parameters.AddWithValue("$prev", previous); command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 全链校验（P3-7）：按 seq 升序逐行检查前驱链接与重算哈希，返回第一个不可信行的 seq。
    /// 只读扫描，不改库、不修复——"能自动修复的校验"等于没有校验。
    /// </summary>
    public async Task<AuditChainVerification> VerifyChainAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT seq,at,user_id,username,role,action,resource,method,path,status_code,success,target,correlation_id,client_ip,prev_hash,hash
FROM audit_events ORDER BY seq;
""";
        long checkedCount = 0;
        long? firstInvalidSeq = null;
        string? firstInvalidReason = null;
        string? previous = null;
        long? lastSeq = null;
        string? lastHash = null;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = new AuditChainEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                ReadNullable(reader, 2),
                ReadNullable(reader, 3),
                ReadNullable(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetBoolean(10),
                ReadNullable(reader, 11),
                ReadNullable(reader, 12),
                ReadNullable(reader, 13));
            var storedPrev = ReadNullable(reader, 14);
            var storedHash = ReadNullable(reader, 15);
            checkedCount++;
            lastSeq = entry.Seq;
            lastHash = storedHash;

            if (firstInvalidSeq is null)
            {
                var expectedPrev = previous ?? AuditChain.GenesisHash;
                if (storedHash is null || storedPrev is null)
                {
                    firstInvalidSeq = entry.Seq;
                    firstInvalidReason = "missing-hash";
                }
                else if (!string.Equals(storedPrev, expectedPrev, StringComparison.OrdinalIgnoreCase))
                {
                    firstInvalidSeq = entry.Seq;
                    firstInvalidReason = "link-mismatch";
                }
                else if (!string.Equals(AuditChain.ComputeHash(storedPrev, entry), storedHash, StringComparison.OrdinalIgnoreCase))
                {
                    firstInvalidSeq = entry.Seq;
                    firstInvalidReason = "hash-mismatch";
                }
            }
            previous = storedHash;
        }

        return new AuditChainVerification(firstInvalidSeq is null, checkedCount, firstInvalidSeq, firstInvalidReason, lastSeq, lastHash, DateTimeOffset.UtcNow);
    }

    private static async Task<(long NextSeq, string? Hash)> ReadChainHeadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seq, hash FROM audit_events ORDER BY seq DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (1, null);
        return (reader.GetInt64(0) + 1, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(int offset, int limit, string? username, string? action, CancellationToken ct = default)
    {
        var result = new List<AuditEventDto>();
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(username)) { where.Add("username=$username"); command.Parameters.AddWithValue("$username", username.Trim()); }
        if (!string.IsNullOrWhiteSpace(action)) { where.Add("action=$action"); command.Parameters.AddWithValue("$action", action.Trim()); }
        command.CommandText = $"SELECT seq,at,user_id,username,role,action,resource,method,path,status_code,success,target,correlation_id,client_ip FROM audit_events {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY seq DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500)); command.Parameters.AddWithValue("$offset", Math.Max(offset, 0));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new AuditEventDto(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), ReadNullable(reader,2), ReadNullable(reader,3), ReadNullable(reader,4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetBoolean(10), ReadNullable(reader,11), ReadNullable(reader,12), ReadNullable(reader,13)));
        return result;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    private static string? ReadNullable(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
    private static string? RouteTarget(HttpContext context)
    {
        var values = context.Request.RouteValues.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value?.ToString());
        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }
}

public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuditEventStore store, StorageMaintenanceCoordinator maintenance, ILogger<AuditMiddleware> logger)
    {
        var endpoint = context.GetEndpoint();
        var meta = endpoint?.Metadata.GetMetadata<AuditActionMetadata>();
        var serviceUsage = endpoint?.Metadata.GetMetadata<ServiceUsageAuditMetadata>();
        if (meta is null && serviceUsage is null) { await next(context); return; }
        var status = StatusCodes.Status500InternalServerError;
        try { await next(context); status = context.Response.StatusCode; }
        finally
        {
            // 中间件在 UseAuthentication 之前，进入时 User 还是空的；finally 里身份与响应都已就绪。
            // 服务令牌的只读调用只在确属服务主体时补记，人类轮询不写审计。
            var effective = meta ?? (serviceUsage is not null && context.User.HasClaim(c => c.Type == SecurityClaims.ServiceTokenId)
                ? new AuditActionMetadata(serviceUsage.Action, serviceUsage.Resource)
                : null);
            if (effective is not null && !maintenance.IsMaintenanceActive)
            {
                try { await store.RecordAsync(context, effective, status, CancellationToken.None); }
                catch (Exception ex)
                {
                    // 审计持久化失败不得替换主请求结果，但必须留下可观测信号：
                    // 结构化错误日志 + 单例失败计数（经 /api/health 的 audit 字段暴露，审计链降级可见）。
                    store.RecordPersistFailure(ex);
                    logger.LogError(ex,
                        "Audit event persistence failed (audit trail degraded). Action={Action} Resource={Resource} {Method} {Path} CorrelationId={CorrelationId}",
                        effective.Action, effective.Resource, context.Request.Method, context.Request.Path, RequestCorrelation.Get(context));
                }
            }
        }
    }
}
