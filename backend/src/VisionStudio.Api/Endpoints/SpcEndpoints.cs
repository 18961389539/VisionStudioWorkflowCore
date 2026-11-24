using VisionStudio.Api.Security;
using VisionStudio.Api.Spc;

namespace VisionStudio.Api.Endpoints;

/// <summary>
/// SPC 看板接口（2026-11-23）：数据集汇总 + 控制图分析。
/// 与追溯读取共用 results 权限（Operator 及以上或服务令牌 results:read），
/// 报表/MES 拉取 SPC 与拉取 trace 是同一类只读访问。
/// </summary>
public static class SpcEndpoints
{
    public static IEndpointRouteBuilder MapSpcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/spc/datasets", async (int? limit, SpcSeriesReader reader, CancellationToken ct) =>
            Results.Ok(await reader.ListDatasetsAsync(limit ?? 200, ct)))
            .RequireResultsRead("spc.read", "spc")
            .Produces<IReadOnlyList<SpcDatasetSummary>>();

        app.MapGet("/api/spc/series", async (
            string dataset, string? chartType, int? subgroupSize, string? grouping,
            DateTimeOffset? from, DateTimeOffset? to, int? limit, double? lsl, double? usl,
            SpcSeriesReader reader, CancellationToken ct) =>
        {
            var options = SpcAnalysisOptions.Resolve(chartType, subgroupSize, grouping, lsl, usl);
            var series = await reader.ReadSeriesAsync(dataset, from, to, limit ?? 2000, ct);
            return Results.Ok(SpcCalculator.Analyze(series, options));
        }).RequireResultsRead("spc.read", "spc").Produces<SpcAnalysis>();

        return app;
    }
}