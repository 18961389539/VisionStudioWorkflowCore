using VisionStudio.Api.Contracts;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;
using VisionStudio.Engine;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // Q11：status 不再恒为 ok——审计/日志持久化失败或维护锁激活时返回 degraded，
        // 并把原因显式列出（存活 ≠ 就绪）。ready 供编排/现场判断"能否接受业务"。
        // R07：进一步区分 **API 服务就绪**（ready）与 **生产动作就绪**（productionReady）——
        // 能响应 HTTP 不等于可以接受设备动作；未闭合动作意图、维护/待重启、生产 Faulted
        // 都必须在这里显式表达，供现场监控使用正确的业务指标。
        // N04：管理员显式释放已放弃的租约（TTL 过期且持有者从未释放）——要求设备空闲并写入审计。
        // 自动接管被移除后，这是不重启进程时的唯一受控恢复路径。
        app.MapPost("/api/leases/{kind}/{id}/release", (string kind, string id, DeviceLeaseRegistry leases, HttpContext http) =>
        {
            if (!leases.TryForceRelease(kind, id, out var reason))
                throw new ApiConflictException($"Cannot release lease {kind}:{id}: {reason}.");
            AuditContext.SetTarget(http, $"{kind}:{id}");
            return Results.Ok(new { released = true, detail = reason });
        }).RequireAdministrator("lease.release", "lease");

        app.MapGet("/api/health", (AuditEventStore audits, RollingFileLoggerProvider logs, StorageMaintenanceCoordinator maintenance,
            ProductionRuntimeService production, DeviceManager devices, RobotManager robots, DeviceActionAuthorizationService authorization,
            DeviceProfileStore deviceProfiles) =>
        {
            var reasons = new List<string>();
            if (audits.PersistFailures > 0) reasons.Add("audit persistence failures");
            if (logs.DroppedEntries > 0) reasons.Add("log entries dropped");
            if (logs.WriteFailures > 0) reasons.Add("log write failures");
            if (logs.FlushFailures > 0) reasons.Add("log flush failures");
            // Q01（2026-10-13 审查）：维护门的原因必须进入健康正文——现场据此判断"为什么被阻断、
            // 核对什么才能放行"，而不是只看到一句"storage failure lock engaged"。
            if (maintenance.IsFailureLocked)
                reasons.Add($"storage failure lock engaged: {maintenance.Reason ?? "unspecified"}");
            if (maintenance.IsRestartPending)
                reasons.Add($"restart pending after storage restore: {maintenance.Reason ?? "unspecified"}");
            // R3-04（2026-10-13 R3 审查）：损坏的设备配置让设备从运行时列表消失——必须表达为
            // **配置完整性降级**（现场可查询"为什么缺设备"），而不是与"设备被删除"混同后仍报 ok。
            var profileProblems = deviceProfiles.Problems;
            if (profileProblems.Count > 0)
            {
                var details = string.Join("; ", profileProblems.Take(3).Select(p => $"{p.FileName}: {p.Error}"));
                if (profileProblems.Count > 3) details += $"; +{profileProblems.Count - 3} more";
                reasons.Add($"device profile integrity: {profileProblems.Count} unreadable file(s) ({details})");
            }
            var degraded = reasons.Count > 0;

            // R07：生产动作就绪——阻断原因必须可直接操作（现场据此决定能否下发设备动作）。
            var productionBlockers = new List<string>();
            if (authorization.UnresolvedProductionState() is { } pending)
                productionBlockers.Add($"unresolved device-action intent ({pending.Phase}): {pending.Reason}");
            // F03：手动/临时/调试/作业运行的未闭合意图同样必须表达为"生产动作未就绪"，
            // 否则会出现"动作被拒但 productionReady=true"的不一致（探针已确认该反例）。
            try
            {
                foreach (var intent in authorization.UnresolvedManualIntents())
                    productionBlockers.Add($"unresolved manual device action ({intent.Action}): {intent.ClosedReason ?? "awaiting reconciliation"}");
            }
            catch (Exception ex)
            {
                productionBlockers.Add($"manual action intent file could not be read: {ex.Message}");
            }
            // Fix1/A01：意图台账落盘失败会置同进程故障锁（逐意图独立登记）——
            // 每个故障都必须表达为"生产动作未就绪"（只报第一个会掩盖其余故障）。
            foreach (var processFailure in authorization.StateTransitionFailures)
                productionBlockers.Add($"in-process device-action failure lock engaged: {processFailure}");
            if (maintenance.IsMaintenanceActive) productionBlockers.Add($"storage maintenance is active: {maintenance.Reason ?? "unspecified"}");
            if (maintenance.IsRestartPending) productionBlockers.Add("restart required after storage restore");
            var productionStatus = production.Status;
            if (productionStatus.State == ProductionRuntimeState.Faulted)
                productionBlockers.Add($"production runtime is Faulted: {productionStatus.LastError ?? "unspecified"}");

            // 依赖可见性（信息性，不计入 blocker）：未连接的设备/机器人是标称待机状态，
            // 不应把"设备未接线"笼统算作服务故障；真正影响动作就绪的是上面的阻断项。
            var offlineDependencies = devices.List()
                .Where(x => x.ConnectionState != DeviceConnectionState.Connected).Select(x => $"device:{x.Id}={x.ConnectionState}")
                .Concat(robots.List().Where(x => x.ConnectionState != RobotConnectionState.Connected).Select(x => $"robot:{x.Id}={x.ConnectionState}"))
                .ToArray();

            return Results.Ok(new
            {
                status = degraded ? "degraded" : "ok",
                ready = !degraded && !maintenance.IsMaintenanceActive,
                degradedReasons = reasons,
                productionReady = productionBlockers.Count == 0 && !degraded,
                productionBlockers,
                offlineDependencies,
                service = "VisionStudio.Api",
                // 程序版本单一来源（程序集版本）：安装包 / API / UI / 备份清单显示同一版本号
                version = StorageBackupService.VisionStudioVersion(),
                workflowEngine = "Workflow Core 3.21.0",
                pluginLoader = "McMaster.NETCore.Plugins 2.0.0",
                mvp = "V0.63 case verification gate + dataset regression evidence",
                // 审计链健康：持久化失败会让审计记录静默丢失，失败计数/最近错误在此可见
                audit = new
                {
                    persistFailures = audits.PersistFailures,
                    lastFailureAt = audits.LastPersistFailureAt,
                    lastFailure = audits.LastPersistFailure
                },
                // F10：日志可靠性计数——满队列丢弃、写入/刷盘失败、关闭排空超时不再静默（现场排障可见）。
                logging = new
                {
                    droppedEntries = logs.DroppedEntries,
                    writeFailures = logs.WriteFailures,
                    flushFailures = logs.FlushFailures,
                    drainTimeouts = logs.DrainTimeouts
                }
            });
        }).AllowAnonymous();

        app.MapGet("/api/host-mode", (IConfiguration configuration) =>
        {
            var runtimeOnly = configuration.GetValue("VisionStudio:RuntimeOnly", false);
            return Results.Ok(new { runtimeOnly, mode = runtimeOnly ? "Runtime" : "Designer" });
        });

        app.MapGet("/api/catalog", (HttpContext http, VisionNodeRegistry registry) =>
        {
            var catalog = registry.Catalog;
            var hash = VisionCatalogSnapshot.ComputeHash(catalog);
            http.Response.Headers["ETag"] = $"\"{hash}\"";
            http.Response.Headers["X-VisionStudio-Catalog-Schema"] = VisionCatalogSnapshot.SchemaVersion.ToString();
            http.Response.Headers["X-VisionStudio-Catalog-Hash"] = hash;
            return Results.Ok(catalog);
        // 契约单一真相源：声明响应类型后 NodeCatalogItem 及其嵌套类型进入 OpenAPI spec，
        // 前端 types.ts 才能由生成替换手写镜像（见 docs/improvement-backlog-2026-10-23.md P1-4）。
        }).Produces<IReadOnlyList<NodeCatalogItem>>();

        app.MapGet("/api/catalog/meta", (VisionNodeRegistry registry) =>
        {
            var catalog = registry.Catalog;
            return Results.Ok(new
            {
                schemaVersion = VisionCatalogSnapshot.SchemaVersion,
                hash = VisionCatalogSnapshot.ComputeHash(catalog),
                nodeCount = catalog.Count,
                builtInNodeCount = catalog.Count(x => string.Equals(x.PluginId, "builtin", StringComparison.OrdinalIgnoreCase)),
                pluginNodeCount = catalog.Count(x => !string.Equals(x.PluginId, "builtin", StringComparison.OrdinalIgnoreCase))
            });
        });
        app.MapPost("/api/frames/resolve", (FrameResolveRequest request) =>
        {
            try
            {
                var transforms = request.Transforms
                    .Select(x => VisionTransform2DMath.Rigid(x.SourceFrame, x.TargetFrame, x.Unit, x.X, x.Y, x.AngleDeg))
                    .ToArray();
                var resolved = new VisionFrameTree2D(transforms).Resolve(request.SourceFrame, request.TargetFrame);
                return Results.Ok(new { resolved.Transform, resolved.Path });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                throw new ApiValidationException(ex.Message, ex);
            }
        }).RequireAuthorization(SecurityPolicies.Engineer);

        return app;
    }
}
