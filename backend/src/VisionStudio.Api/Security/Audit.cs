using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Security;

public sealed record AuditActionMetadata(string Action, string Resource);

public static class AuditEndpointExtensions
{
    public static TBuilder WithAudit<TBuilder>(this TBuilder builder, string action, string resource) where TBuilder : IEndpointConventionBuilder
    { builder.WithMetadata(new AuditActionMetadata(action, resource)); return builder; }
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

    /// <summary>审计持久化失败计数（单例计数器）：数据库忙/磁盘故障时审计记录会静默丢失，此计数是唯一信号。</summary>
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
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO audit_events(at,user_id,username,role,action,resource,method,path,status_code,success,target,correlation_id,client_ip)
VALUES($at,$userId,$username,$role,$action,$resource,$method,$path,$status,$success,$target,$correlation,$ip);
""";
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        command.Parameters.AddWithValue("$userId", Db(user.FindFirstValue(ClaimTypes.NameIdentifier)));
        command.Parameters.AddWithValue("$username", Db(user.Identity?.Name));
        command.Parameters.AddWithValue("$role", Db(user.FindFirstValue(ClaimTypes.Role)));
        command.Parameters.AddWithValue("$action", meta.Action); command.Parameters.AddWithValue("$resource", meta.Resource);
        command.Parameters.AddWithValue("$method", context.Request.Method); command.Parameters.AddWithValue("$path", context.Request.Path.Value ?? string.Empty);
        command.Parameters.AddWithValue("$status", statusCode); command.Parameters.AddWithValue("$success", statusCode is >= 200 and < 400 ? 1 : 0);
        command.Parameters.AddWithValue("$target", Db(AuditContext.GetTarget(context) ?? RouteTarget(context)));
        command.Parameters.AddWithValue("$correlation", Db(RequestCorrelation.Get(context))); command.Parameters.AddWithValue("$ip", Db(context.Connection.RemoteIpAddress?.ToString()));
        await command.ExecuteNonQueryAsync(ct);
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
        var meta = context.GetEndpoint()?.Metadata.GetMetadata<AuditActionMetadata>();
        if (meta is null) { await next(context); return; }
        var status = StatusCodes.Status500InternalServerError;
        try { await next(context); status = context.Response.StatusCode; }
        finally
        {
            if (!maintenance.IsMaintenanceActive)
            {
                try { await store.RecordAsync(context, meta, status, CancellationToken.None); }
                catch (Exception ex)
                {
                    // 审计持久化失败不得替换主请求结果，但必须留下可观测信号：
                    // 结构化错误日志 + 单例失败计数（经 /api/health 的 audit 字段暴露，审计链降级可见）。
                    store.RecordPersistFailure(ex);
                    logger.LogError(ex,
                        "Audit event persistence failed (audit trail degraded). Action={Action} Resource={Resource} {Method} {Path} CorrelationId={CorrelationId}",
                        meta.Action, meta.Resource, context.Request.Method, context.Request.Path, RequestCorrelation.Get(context));
                }
            }
        }
    }
}
