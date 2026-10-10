using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Security;

namespace VisionStudio.Api.Endpoints;

public sealed record StorageBackupCreateRequest(bool? IncludeArtifacts = null);

public static class TraceEndpoints
{
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/traces", async (int? take, string? jobId, string? disposition, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.ListAsync(take ?? 100, jobId, disposition, ct)));

        app.MapGet("/api/traces/page", async (int? offset, int? limit, string? jobId, string? disposition, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.PageAsync(offset ?? 0, limit ?? 100, jobId, disposition, ct)));

        app.MapGet("/api/traces/stats", async (int? days, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.StatsAsync(days ?? 7, ct)));

        app.MapGet("/api/traces/{runId}", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var trace = await traces.GetAsync(runId, ct);
            return trace is null ? Results.NotFound() : Results.Ok(trace);
        });

        app.MapGet("/api/traces/{runId}/observability", async (string runId, int? days, int? history, RunObservabilityService observability, CancellationToken ct) =>
            Results.Ok(await observability.GetAsync(runId, days ?? 7, history ?? 100, ct)));

        app.MapGet("/api/traces/{runId}/compare", async (string runId, string? baselineRunId, TraceAnalysisService analysis, CancellationToken ct) =>
            Results.Ok(await analysis.CompareAsync(runId, baselineRunId, ct)));

        app.MapGet("/api/traces/{runId}/failure-signature", async (string runId, int? days, int? take, TraceAnalysisService analysis, CancellationToken ct) =>
            Results.Ok(await analysis.AnalyzeFailureAsync(runId, days ?? 30, take ?? 12, ct)));

        app.MapPut("/api/traces/{runId}/disposition", async (string runId, UpdateDispositionRequest request, TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.UpdateDispositionAsync(runId, request, ct))).RequireOperator("trace.disposition.update", "trace");

        app.MapGet("/api/traces/{runId}/preview", (string runId, TraceabilityStore traces) =>
        {
            var path = traces.FindPreviewPath(runId);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        app.MapGet("/api/traces/{runId}/workflow", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var workflow = await traces.GetWorkflowSnapshotAsync(runId, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        app.MapGet("/api/traces/{runId}/dependencies", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var manifest = await traces.GetDependencyManifestAsync(runId, ct);
            return manifest is null ? Results.NotFound() : Results.Ok(manifest);
        }).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/traces/{runId}/overlays", async (string runId, TraceabilityStore traces, CancellationToken ct) =>
        {
            var overlays = await traces.GetOverlaysAsync(runId, ct);
            return overlays is null ? Results.NotFound() : Results.Ok(overlays);
        });

        app.MapGet("/api/storage/status", async (TraceabilityStore traces, CancellationToken ct) =>
            Results.Ok(await traces.StorageStatusAsync(ct)));

        app.MapGet("/api/storage/schema", async (SqliteMetadataDatabase database, CancellationToken ct) =>
            Results.Ok(await database.GetSchemaStatusAsync(ct))).RequireAuthorization(SecurityPolicies.Engineer);

        app.MapGet("/api/storage/capacity", async (StorageCapacityService capacity, CancellationToken ct) =>
            Results.Ok(await capacity.RefreshAsync(ct)));

        app.MapPost("/api/storage/cleanup", async (TraceabilityStore traces, StorageCapacityService capacity, CancellationToken ct) =>
        {
            var changed = await traces.CleanupAsync(ct);
            return Results.Ok(new { changed, capacity = await capacity.RefreshAsync(ct) });
        }).RequireAdministrator("storage.cleanup", "storage");

        app.MapGet("/api/storage/backups", async (StorageBackupService backups, CancellationToken ct) =>
            Results.Ok(await backups.ListAsync(ct))).RequireAuthorization(SecurityPolicies.Administrator);

        app.MapPost("/api/storage/backups", async (StorageBackupCreateRequest? request, StorageBackupService backups, CancellationToken ct) =>
            Results.Ok(await backups.CreateAsync(request?.IncludeArtifacts, ct)))
            .RequireAdministrator("storage.backup.create", "storage");

        app.MapDelete("/api/storage/backups/{backupId}", async (string backupId, HttpContext http, StorageBackupService backups, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, backupId);
            await backups.DeleteAsync(backupId, ct);
            return Results.NoContent();
        }).RequireAdministrator("storage.backup.delete", "storage");

        app.MapPost("/api/storage/restore", async (StorageRestoreRequest request, HttpContext http, StorageBackupService backups, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, request.BackupId);
            return Results.Ok(await backups.RestoreAsync(request, ct));
        }).RequireAdministrator("storage.restore", "storage");

        // F03：受控恢复入口——失败锁激活时唯一受认证 + 审计的解锁路径（中间件豁免使其在维护激活时
        // 仍可达）。Q07：解锁条件从"数据库能打开、目录存在"强化为**可证明的资产自洽**：
        // schema 版本有效、SQLite 完整性、关键表存在，且抽样核对追溯记录的附件引用在磁盘上真实存在。
        // 任一项不通过 ⇒ 保持锁定并返回问题清单（"核对后解锁"，而不是"盲目放行写入"）。
        app.MapPost("/api/storage/maintenance/recover", async (StorageMaintenanceCoordinator coordinator, SqliteMetadataDatabase database,
            IWebHostEnvironment env, HttpContext http, CancellationToken ct) =>
        {
            AuditContext.SetTarget(http, "storage-failure-lock");
            if (!coordinator.IsFailureLocked)
                return Results.Ok(new { recovered = false, maintenanceActive = coordinator.IsMaintenanceActive, reason = coordinator.Reason });

            var dataRoot = VisionStudioDataRoot.Resolve(env.ContentRootPath);
            var artifactsRoot = Path.Combine(dataRoot, "artifacts");
            var problems = new List<string>();
            var databaseReadable = false;

            try
            {
                await using var connection = await database.OpenConnectionAsync(ct);
                databaseReadable = true;

                // 1) schema 版本必须存在且有效（旧实现丢弃了查询返回值——空表/异常值同样算"可读"）。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT version FROM schema_info WHERE id=1;";
                    var value = await command.ExecuteScalarAsync(ct);
                    var text = value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (text is null || !long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var version) || version < 1 || version > database.CurrentSchemaVersion)
                        problems.Add($"schema_info.version is missing or invalid ('{text ?? "null"}'); the database is not a usable metadata database");
                }

                // 2) SQLite 页级完整性（quick_check；"ok" 之外的任何结果都不放行）。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA quick_check;";
                    var result = Convert.ToString(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        problems.Add($"SQLite quick_check reported '{result}'");
                }

                // 3) 关键表必须存在（空库/被截断的恢复不是"可解锁"状态）。
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('schema_info','run_traces');";
                    var tables = Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
                    if (tables < 2) problems.Add("required metadata tables (schema_info, run_traces) are missing");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"database is not readable: {ex.Message}");
            }

            if (!Directory.Exists(artifactsRoot))
            {
                problems.Add($"artifacts root '{artifactsRoot}' is missing; restore it from the preserved copies before clearing the lock");
            }
            else if (databaseReadable && problems.Count == 0)
            {
                // 4) 附件引用一致性抽样：恢复后的追溯记录若指向缺失文件，说明 DB 与资产不是同一份
                //    恢复版本（典型混合状态），绝不解锁。
                try
                {
                    await using var connection = await database.OpenConnectionAsync(ct);
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT artifact FROM (" +
                        "SELECT preview_relative_path AS artifact FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL " +
                        "UNION ALL SELECT replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL) " +
                        "ORDER BY artifact;";
                    var checkedCount = 0;
                    var missing = new List<string>();
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var relative = reader.GetString(0);
                        checkedCount++;
                        var normalized = relative.Replace('\\', '/');
                        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is ".." or "."))
                        {
                            missing.Add(relative);
                            continue;
                        }
                        var candidate = Path.GetFullPath(Path.Combine(artifactsRoot,
                            normalized.Replace('/', Path.DirectorySeparatorChar)));
                        if (!candidate.StartsWith(Path.GetFullPath(artifactsRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            missing.Add(relative);
                            continue;
                        }
                        if (!File.Exists(candidate)) missing.Add(relative);
                    }
                    if (missing.Count > 0)
                        problems.Add($"{missing.Count} of {checkedCount} artifact references point to files that do not exist " +
                                     $"(e.g. '{missing[0]}'); the database and on-disk assets are not the same restored generation");
                }
                catch (Exception ex)
                {
                    problems.Add($"artifact reference verification failed: {ex.Message}");
                }
            }

            if (problems.Count > 0)
                throw new ApiConflictException(
                    "Storage recovery checks failed; the failure lock stays engaged: " + string.Join("; ", problems) + ".");

            var transaction = coordinator.ReadTransaction();
            if (coordinator.HasRestoreTransactionMarker && transaction is null)
                throw new ApiConflictException("The restore transaction marker exists but cannot be read; preserve it and use the full administrator restore path to recover from a verified backup.");
            if (transaction is not null)
            {
                // Presence/readability cannot prove which generation was installed. An unresolved
                // transaction can only be retired by a new full restore from a hash-verified backup;
                // this endpoint must never turn an operator click into evidence.
                throw new ApiConflictException("An unresolved restore transaction is present " +
                    $"(backup '{transaction.BackupId}', stage '{transaction.Stage}'). Use the administrator full-restore endpoint " +
                    "to restore a hash-verified backup; this verification endpoint will not discard transaction evidence.");
            }
            // With no durable restore intent there is no trusted generation manifest to compare the
            // system assets against. Readability and a populated artifact tree cannot prove rollback.
            throw new ApiConflictException("Storage checks passed, but there is no hash-verified restore manifest proving a complete database and system-asset generation. " +
                "Use the administrator full-restore endpoint; this endpoint never clears a failure lock without transaction evidence.");
        }).RequireAdministrator("storage.maintenance.recover", "storage");

        return app;
    }
}
