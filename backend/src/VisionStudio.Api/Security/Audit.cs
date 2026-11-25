using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

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

public sealed partial class AuditEventStore
{
    private long _persistFailures;
    private sealed record PersistFailure(DateTimeOffset At, string Message);
    private PersistFailure? _lastPersistFailure;

    /// <summary>审计持久化失败计数（单例计数器）：数据库忙/磁盘故障时记录写入失败；同时会写错误日志并记录最近一次失败详情。</summary>
    public long PersistFailures => Interlocked.Read(ref _persistFailures);
    public DateTimeOffset? LastPersistFailureAt => Volatile.Read(ref _lastPersistFailure)?.At;
    public string? LastPersistFailure => Volatile.Read(ref _lastPersistFailure)?.Message;

    private void RecordPersistFailure(Exception ex, int count = 1)
    {
        Interlocked.Add(ref _persistFailures, count);
        Volatile.Write(ref _lastPersistFailure, new PersistFailure(DateTimeOffset.UtcNow, ex.Message));
    }

    public async Task RecordAsync(HttpContext context, AuditActionMetadata meta, int statusCode, CancellationToken ct = default)
    {
        var user = context.User;
        var now = DateTimeOffset.UtcNow;
        var atText = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var entry = new AuditChainEntry(
            0,
            atText,
            NormalizeNullable(user.FindFirstValue(ClaimTypes.NameIdentifier)),
            NormalizeNullable(user.Identity?.Name),
            NormalizeNullable(user.FindFirstValue(ClaimTypes.Role)),
            meta.Action,
            meta.Resource,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            statusCode,
            statusCode is >= 200 and < 400,
            NormalizeNullable(AuditContext.GetTarget(context) ?? RouteTarget(context)),
            NormalizeNullable(RequestCorrelation.Get(context)),
            NormalizeNullable(context.Connection.RemoteIpAddress?.ToString()));

        await EnqueueAsync(entry, ct);
    }

    private async Task PersistBatchAsync(IReadOnlyList<PendingAudit> batch, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // 每批只读一次链尾；事务仍防御其它进程/独立 Store 实例的并发追加。
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var head = await db.AuditEvents
            .OrderByDescending(x => x.Seq)
            .Select(x => new { x.Seq, x.Hash })
            .FirstOrDefaultAsync(ct);
        var nextSeq = head is null ? 1 : head.Seq + 1;
        var previous = head?.Hash ?? AuditChain.GenesisHash;
        foreach (var item in batch)
        {
            var entry = item.Entry with { Seq = nextSeq++ };
            var hash = AuditChain.ComputeHash(previous, entry);
            db.AuditEvents.Add(new AuditEventEntity
            {
                Seq = entry.Seq,
                At = DateTimeOffset.Parse(entry.At, CultureInfo.InvariantCulture),
                UserId = entry.UserId,
                Username = entry.Username,
                Role = entry.Role,
                Action = entry.Action,
                Resource = entry.Resource,
                Method = entry.Method,
                Path = entry.Path,
                StatusCode = entry.StatusCode,
                Success = entry.Success,
                Target = entry.Target,
                CorrelationId = entry.CorrelationId,
                ClientIp = entry.ClientIp,
                PrevHash = previous,
                Hash = hash
            });
            previous = hash;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 全链校验（P3-7）：按 seq 升序逐行检查前驱链接与重算哈希，返回第一个不可信行的 seq。
    /// 只读扫描，不改库、不修复——"能自动修复的校验"等于没有校验。
    /// </summary>
    public async Task<AuditChainVerification> VerifyChainAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        long checkedCount = 0;
        long? firstInvalidSeq = null;
        string? firstInvalidReason = null;
        string? previous = null;
        long? lastSeq = null;
        string? lastHash = null;

        await foreach (var row in db.AuditEvents.OrderBy(x => x.Seq).AsAsyncEnumerable().WithCancellation(ct))
        {
            var entry = new AuditChainEntry(
                row.Seq,
                row.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                row.UserId,
                row.Username,
                row.Role,
                row.Action,
                row.Resource,
                row.Method,
                row.Path,
                row.StatusCode,
                row.Success,
                row.Target,
                row.CorrelationId,
                row.ClientIp);
            var storedPrev = row.PrevHash;
            var storedHash = row.Hash;
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

    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(int offset, int limit, string? username, string? action, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<AuditEventEntity> query = db.AuditEvents;
        if (!string.IsNullOrWhiteSpace(username))
        {
            var u = username.Trim();
            query = query.Where(x => x.Username == u);
        }
        if (!string.IsNullOrWhiteSpace(action))
        {
            var a = action.Trim();
            query = query.Where(x => x.Action == a);
        }
        var entities = await query
            .OrderByDescending(x => x.Seq)
            .Skip(Math.Max(offset, 0))
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return entities.Select(e => new AuditEventDto(
            e.Seq, e.At, e.UserId, e.Username, e.Role, e.Action, e.Resource,
            e.Method, e.Path, e.StatusCode, e.Success, e.Target, e.CorrelationId, e.ClientIp)).ToList();
    }

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
                    logger.LogError(ex,
                        "Audit event persistence failed (audit trail degraded). Action={Action} Resource={Resource} {Method} {Path} CorrelationId={CorrelationId}",
                        effective.Action, effective.Resource, context.Request.Method, context.Request.Path, RequestCorrelation.Get(context));
                }
            }
        }
    }
}
