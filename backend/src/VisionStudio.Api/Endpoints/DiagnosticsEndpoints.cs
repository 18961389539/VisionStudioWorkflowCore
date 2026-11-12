using VisionStudio.Api.Diagnostics;
using VisionStudio.Api.Hosting;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    /// <summary>
    /// 诊断读接口。
    ///
    /// 2026-10-29：这四个端点此前完全没有授权要求。认证处理器在 <c>Security:Enabled=true</c> 时
    /// 对无 token 请求返回 <c>NoResult</c>，所以"没要求授权"等于"匿名可读"——资产状态、事件流、
    /// 快照文件路径和错误原文都会泄露给任何能访问端口的人。同域的 SignalR 通道
    /// （/hubs/diagnostics）却要求授权，camera-sync 的两个 *-diagnostics 端点也都是 Operator 级，
    /// 这里是同一功能域里唯一的缺口。现统一到 Operator，与通道保持一致。
    /// </summary>
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/summary", (DiagnosticsCenterService diagnostics) => Results.Ok(diagnostics.Summary()))
            .RequireOperator("diagnostics.summary", "diagnostics").Produces<DiagnosticsSummary>();

        app.MapGet("/api/diagnostics/assets", (DiagnosticsCenterService diagnostics) => Results.Ok(diagnostics.Assets()))
            .RequireOperator("diagnostics.assets", "diagnostics").Produces<IReadOnlyList<AssetHealthSnapshot>>();

        app.MapGet("/api/diagnostics/events", (int? take, AssetKind? kind, string? assetId, DiagnosticsCenterService diagnostics) =>
            Results.Ok(diagnostics.Events(take ?? 200, kind, assetId)))
            .RequireOperator("diagnostics.events", "diagnostics").Produces<IReadOnlyList<AssetEventEnvelope>>();

        // 跨轮计数器落盘状态：计数在内存里累加、磁盘副本可能落后。Degraded=true 时
        // 界面应提示"班次统计未落盘"，否则现场只会在重启后看到数字倒退。
        // Produces<T> 是必要的：Minimal API 的 Results.Ok(obj) 推导不出 schema，
        // 缺了它 openapi.json 里这个端点只有 "200 OK"，前端生成不出返回类型。
        app.MapGet("/api/diagnostics/run-counters", (RunCounterPersistence persistence) =>
            Results.Ok(persistence.Status()))
            .Produces<RunCounterPersistenceStatus>()
            .RequireOperator("diagnostics.run-counters", "diagnostics");

        // 每节点耗时分位数。存在的唯一理由是回答"__timeoutMs 该填多少"——
        // 没有这组数据，工程师只能凭感觉填：填小了两周后偶发停机，填大了等于没有保护。
        app.MapGet("/api/diagnostics/node-durations", (NodeDurationStatistics statistics) =>
            Results.Ok(statistics.Snapshot()))
            .Produces<IReadOnlyList<NodeDurationStatistic>>()
            .RequireOperator("diagnostics.node-durations", "diagnostics");

        // 一键诊断包（2026-11-12）：现场排障不再要求"把整个数据根拷回来"。管理员专属——
        // 包含审计尾部与日志尾部，里面会出现用户名、路径和设备地址。
        app.MapGet("/api/diagnostics/bundle", async (DiagnosticBundleService bundle, CancellationToken ct) =>
        {
            var bytes = await bundle.CreateAsync(ct);
            return Results.File(bytes, "application/zip", $"visionstudio-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        }).RequireAdministrator("diagnostics.bundle", "diagnostics");

        return app;
    }
}
