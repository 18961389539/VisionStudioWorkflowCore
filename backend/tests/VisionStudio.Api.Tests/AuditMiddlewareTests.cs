using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 审计链故障信号与请求级关联 ID：
/// 持久化失败必须被计数并可观测（/api/health），且不替换主请求结果；
/// 关联 ID 在请求内稳定且与异常响应/审计记录来源一致。
/// </summary>
public sealed class AuditMiddlewareTests
{
    [Fact]
    public async Task AuditPersistenceFailure_IsCountedAndObservable_WithoutReplacingRequestResult()
    {
        using var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        var store = new AuditEventStore(db);
        var middleware = new AuditMiddleware(_ => Task.CompletedTask);
        var maintenance = new StorageMaintenanceCoordinator();
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuditActionMetadata("test.action", "test")),
            "audit-test"));
        context.Response.StatusCode = StatusCodes.Status200OK;

        // 正常路径：记录成功、无失败计数
        await middleware.InvokeAsync(context, store, maintenance, NullLogger<AuditMiddleware>.Instance);
        Assert.Equal(0, store.PersistFailures);

        // 故障注入：删除审计表 → 持久化失败被计数并记录最近错误；主请求结果不被替换
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE audit_events;";
            await drop.ExecuteNonQueryAsync();
        }

        await middleware.InvokeAsync(context, store, maintenance, NullLogger<AuditMiddleware>.Instance);

        Assert.Equal(1, store.PersistFailures);
        Assert.NotNull(store.LastPersistFailureAt);
        Assert.False(string.IsNullOrWhiteSpace(store.LastPersistFailure));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthEndpoint_ExposesAuditPersistenceCounters()
    {
        using var factory = new VisionStudioApiFactory();
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");

        Assert.True(health.TryGetProperty("audit", out var audit));
        Assert.Equal(0, audit.GetProperty("persistFailures").GetInt64());
    }

    [Fact]
    public void RequestCorrelation_IsStableWithinARequest_AndHonoursPrecomputedValue()
    {
        var context = new DefaultHttpContext();

        var first = RequestCorrelation.Get(context);
        var second = RequestCorrelation.Get(context);
        Assert.Equal(first, second);

        context.Items["VisionStudio.CorrelationId"] = "preset-correlation";
        Assert.Equal("preset-correlation", RequestCorrelation.Get(context));
    }
}
