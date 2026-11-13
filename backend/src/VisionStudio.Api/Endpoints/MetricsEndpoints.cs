using System.Globalization;
using System.Text;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Diagnostics;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

/// <summary>
/// Prometheus 文本格式指标出口（2026-11-13）。
///
/// 为什么需要它：节点 span / 耗时分布此前只有进程内环形缓冲与人工查询端点，重启即清零，
/// 无法做趋势、容量规划或告警。这里把已有的进程内计数器与窗口统计按 Prometheus 0.0.4
/// 文本格式暴露出来，现场用任意抓取器即可留存；不引入额外 NuGet 依赖。
///
/// 口径说明（避免把窗口值当累计值用）：
/// <list type="bullet">
/// <item><c>*_total</c> 只用于真正单调的计数（span 捕获数、生产周期数、日志/审计失败数）；</item>
/// <item><c>node_duration_*</c> 与 <c>node_timeouts_window</c> 是最近 256 个样本的窗口统计，
///       重启或窗口滚动会变化，故不命名为 <c>_total</c>；</item>
/// <item>抓取本身会读取并排序各节点类型的样本窗口，属于诊断端点，不要把它当高频指标。</item>
/// </list>
/// </summary>
public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnostics/metrics", (
            NodeSpanBuffer spans,
            NodeDurationStatistics durations,
            ProductionRuntimeService production,
            AuditEventStore audits,
            RollingFileLoggerProvider logs,
            ScriptWorkerSupervisor scriptWorkers) =>
        {
            var body = Build(
                spans.Snapshot(0),
                durations.Snapshot(),
                production.Status,
                audits.PersistFailures,
                logs,
                scriptWorkers.Status,
                CameraSdkDiagnostics.Snapshot());
            return Results.Text(body, "text/plain; version=0.0.4; charset=utf-8");
        })
        .RequireOperator("diagnostics.metrics", "diagnostics");

        return app;
    }

    private static string Build(
        NodeSpanSnapshot spans,
        IReadOnlyList<NodeDurationStatistic> statistics,
        ProductionRuntimeStatus production,
        long auditPersistFailures,
        RollingFileLoggerProvider logs,
        PluginWorkerStatus scriptWorker,
        IReadOnlyList<CameraSdkDiagnostic> cameraSdk)
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("# HELP visionstudio_build_info Static build identity of the VisionStudio host.");
        builder.AppendLine("# TYPE visionstudio_build_info gauge");
        builder.Append("visionstudio_build_info{version=\"").Append(Escape(StorageBackupService.VisionStudioVersion())).AppendLine("\"} 1");

        builder.AppendLine("# HELP visionstudio_process_uptime_seconds Process uptime in seconds.");
        builder.AppendLine("# TYPE visionstudio_process_uptime_seconds gauge");
        builder.Append("visionstudio_process_uptime_seconds ").AppendLine(Number(Environment.TickCount64 / 1000d));

        builder.AppendLine("# HELP visionstudio_node_spans_captured_total Node spans captured since process start.");
        builder.AppendLine("# TYPE visionstudio_node_spans_captured_total counter");
        builder.Append("visionstudio_node_spans_captured_total ").AppendLine(spans.Captured.ToString(CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP visionstudio_node_span_buffer_capacity Ring buffer capacity for recent node spans.");
        builder.AppendLine("# TYPE visionstudio_node_span_buffer_capacity gauge");
        builder.Append("visionstudio_node_span_buffer_capacity ").AppendLine(spans.Capacity.ToString(CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP visionstudio_node_duration_samples Samples in the per-node-type rolling window.");
        builder.AppendLine("# TYPE visionstudio_node_duration_samples gauge");
        foreach (var statistic in statistics)
            builder.Append("visionstudio_node_duration_samples{node_type=\"").Append(Escape(statistic.NodeType)).Append("\"} ")
                .AppendLine(statistic.Samples.ToString(CultureInfo.InvariantCulture));

        AppendNodeDuration(builder, "visionstudio_node_duration_p50_milliseconds",
            "Rolling-window P50 node duration in milliseconds.", statistics, statistic => statistic.P50Ms);
        AppendNodeDuration(builder, "visionstudio_node_duration_p95_milliseconds",
            "Rolling-window P95 node duration in milliseconds.", statistics, statistic => statistic.P95Ms);
        AppendNodeDuration(builder, "visionstudio_node_duration_max_milliseconds",
            "Rolling-window maximum node duration in milliseconds.", statistics, statistic => statistic.MaxMs);

        builder.AppendLine("# HELP visionstudio_node_timeouts_window Node timeouts observed in the rolling window.");
        builder.AppendLine("# TYPE visionstudio_node_timeouts_window gauge");
        foreach (var statistic in statistics)
            builder.Append("visionstudio_node_timeouts_window{node_type=\"").Append(Escape(statistic.NodeType)).Append("\"} ")
                .AppendLine(statistic.TimeoutCount.ToString(CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP visionstudio_production_state Current production runtime state (one-hot).");
        builder.AppendLine("# TYPE visionstudio_production_state gauge");
        foreach (var state in Enum.GetValues<ProductionRuntimeState>())
            builder.Append("visionstudio_production_state{state=\"").Append(state).Append("\"} ")
                .AppendLine(state == production.State ? "1" : "0");

        AppendCounter(builder, "visionstudio_production_cycles_total", "Production cycles executed since runtime start.", production.CycleCount);
        AppendCounter(builder, "visionstudio_production_ok_total", "Production cycles with OK disposition since runtime start.", production.OkCount);
        AppendCounter(builder, "visionstudio_production_ng_total", "Production cycles with NG disposition since runtime start.", production.NgCount);
        AppendCounter(builder, "visionstudio_production_errors_total", "Production cycles that ended in error since runtime start.", production.ErrorCount);
        AppendCounter(builder, "visionstudio_production_watchdog_trips_total", "Production watchdog trips since runtime start.", production.WatchdogTrips);

        builder.AppendLine("# HELP visionstudio_production_consecutive_failures Consecutive failed production cycles.");
        builder.AppendLine("# TYPE visionstudio_production_consecutive_failures gauge");
        builder.Append("visionstudio_production_consecutive_failures ").AppendLine(production.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture));

        builder.AppendLine("# HELP visionstudio_production_locked Whether production is locked after a failure (1 = locked).");
        builder.AppendLine("# TYPE visionstudio_production_locked gauge");
        builder.Append("visionstudio_production_locked ").AppendLine(production.ProductionLocked ? "1" : "0");

        AppendCounter(builder, "visionstudio_audit_persist_failures_total", "Audit records that failed to persist.", auditPersistFailures);
        AppendCounter(builder, "visionstudio_log_dropped_entries_total", "Log entries dropped because the queue was full.", logs.DroppedEntries);
        AppendCounter(builder, "visionstudio_log_write_failures_total", "Log write failures.", logs.WriteFailures);
        AppendCounter(builder, "visionstudio_log_flush_failures_total", "Log flush failures.", logs.FlushFailures);

        builder.AppendLine("# HELP visionstudio_script_worker_restarts_total Script worker process restarts.");
        builder.AppendLine("# TYPE visionstudio_script_worker_restarts_total counter");
        builder.Append("visionstudio_script_worker_restarts_total ").AppendLine(scriptWorker.RestartCount.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("# HELP visionstudio_script_worker_busy_workers Script workers currently executing.");
        builder.AppendLine("# TYPE visionstudio_script_worker_busy_workers gauge");
        builder.Append("visionstudio_script_worker_busy_workers ").AppendLine(scriptWorker.BusyWorkers.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("# HELP visionstudio_script_worker_circuit_open Whether the script worker circuit breaker is open (1 = open).");
        builder.AppendLine("# TYPE visionstudio_script_worker_circuit_open gauge");
        builder.Append("visionstudio_script_worker_circuit_open ").AppendLine(scriptWorker.CircuitOpen ? "1" : "0");

        builder.AppendLine("# HELP visionstudio_camera_sdk_failures_total Camera SDK failures by driver and operation.");
        builder.AppendLine("# TYPE visionstudio_camera_sdk_failures_total counter");
        foreach (var diagnostic in cameraSdk)
            builder.Append("visionstudio_camera_sdk_failures_total{driver=\"").Append(Escape(diagnostic.Driver))
                .Append("\",operation=\"").Append(Escape(diagnostic.Operation)).Append("\"} ")
                .AppendLine(diagnostic.Failures.ToString(CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private static void AppendNodeDuration(
        StringBuilder builder,
        string name,
        string help,
        IReadOnlyList<NodeDurationStatistic> statistics,
        Func<NodeDurationStatistic, double> selector)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).AppendLine(" gauge");
        foreach (var statistic in statistics)
            builder.Append(name).Append("{node_type=\"").Append(Escape(statistic.NodeType)).Append("\"} ")
                .AppendLine(Number(selector(statistic)));
    }

    private static void AppendCounter(StringBuilder builder, string name, string help, long value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).AppendLine(" counter");
        builder.Append(name).Append(' ').AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static string Number(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
