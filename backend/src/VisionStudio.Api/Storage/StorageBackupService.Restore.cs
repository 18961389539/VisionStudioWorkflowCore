using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed partial class StorageBackupService
{

    public async Task<StorageRestoreResult> RestoreAsync(StorageRestoreRequest request, CancellationToken ct)
    {
        if (_production.Status.State != ProductionRuntimeState.Stopped)
            throw new ApiConflictException("Production Runtime must be Stopped before restoring storage.");

        // Do not hold the backup gate while waiting for in-flight API requests to drain: an
        // already-running backup/delete request may legitimately be waiting for that same gate.
        // Restore remains available to an authenticated administrator while failure-locked; it is
        // the practical rollback/retry path for unresolved transactions. Ordinary maintenance stays blocked.
        await using var maintenance = await _maintenance.BeginExclusiveAsync("storage restore", ct, allowFailureLockedRestore: true);
        if (_production.Status.State != ProductionRuntimeState.Stopped)
            throw new ApiConflictException("Production Runtime changed state while storage restore was waiting for maintenance mode.");

        await _gate.WaitAsync(ct);
        var workspace = Path.Combine(_tempRoot, $"restore-{Guid.NewGuid():N}");
        // 回滚未完成时保留工作区（含 pre-restore.db 快照）与旧附件目录，绝不允许清理路径
        // 删除最后的安全副本——恢复失败必须可以再次恢复或人工取证。
        var preserveSafetyCopies = false;
        try
        {
            var archivePath = BackupPath(request.BackupId);
            if (!File.Exists(archivePath)) throw new ApiNotFoundException($"Backup '{request.BackupId}' does not exist.");
            Directory.CreateDirectory(workspace);
            var incomingDb = Path.Combine(workspace, "incoming.db");
            var stagedArtifacts = Path.Combine(workspace, "artifacts");
            var stagedSystem = Path.Combine(workspace, "system");
            StorageBackupManifest manifest;
            var extractedAssetHashes = new Dictionary<string, string>(StringComparer.Ordinal);

            await using (var input = File.OpenRead(archivePath))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false))
            {
                manifest = await ReadManifestAsync(zip, ct);
                if (manifest.FormatVersion != BackupFormatVersion)
                    throw new ApiValidationException($"Unsupported backup format v{manifest.FormatVersion}.");
                var dbEntry = zip.GetEntry("database/visionstudio.db") ?? throw new ApiValidationException("Backup bundle has no database snapshot.");
                await ExtractEntryAsync(dbEntry, incomingDb, ct);
                var actualHash = await Sha256FileAsync(incomingDb, ct);
                if (!string.Equals(actualHash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ApiValidationException("Backup database SHA-256 does not match manifest.");

                if (manifest.IncludesArtifacts)
                {
                    Directory.CreateDirectory(stagedArtifacts);
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("artifacts/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["artifacts/".Length..].Replace('/', Path.DirectorySeparatorChar);
                        var destination = SafeChildPath(stagedArtifacts, relative);
                        await ExtractEntryAsync(entry, destination, ct);
                        extractedAssetHashes[$"artifacts/{relative.Replace('\\', '/')}"] = ComputeFileSha256(destination);
                    }
                }

                if (manifest.IncludesSystemAssets)
                {
                    Directory.CreateDirectory(stagedSystem);
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("system/", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["system/".Length..].Replace('/', Path.DirectorySeparatorChar);
                        var destination = SafeChildPath(stagedSystem, relative);
                        await ExtractEntryAsync(entry, destination, ct);
                        extractedAssetHashes[$"system/{relative.Replace('\\', '/')}"] = ComputeFileSha256(destination);
                    }
                    foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("system/", StringComparison.Ordinal) && string.IsNullOrEmpty(x.Name)))
                    {
                        var relative = entry.FullName["system/".Length..].TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
                        Directory.CreateDirectory(SafeChildPath(stagedSystem, relative));
                    }
                }
            }

            if (manifest.AssetSha256 is not null && (manifest.AssetSha256.Count != extractedAssetHashes.Count || manifest.AssetSha256.Any(pair =>
                    !extractedAssetHashes.TryGetValue(pair.Key, out var actual) ||
                    !string.Equals(actual, pair.Value, StringComparison.OrdinalIgnoreCase))))
                throw new ApiValidationException("Backup artifact/system-asset manifest does not match the extracted file set and SHA-256 hashes.");

            if (_maintenance.IsFailureLocked || _maintenance.HasRestoreTransactionMarker)
            {
                if (!request.RestoreArtifacts || !manifest.IncludesArtifacts ||
                    !request.RestoreSystemAssets || !manifest.IncludesSystemAssets ||
                    manifest.AssetSha256 is null ||
                    _systemSections.Any(section => !Directory.Exists(Path.Combine(stagedSystem, section.Prefix))))
                    throw new ApiConflictException("A failure-locked or unresolved restore requires a complete backup with database, artifacts, and every system-asset root.");
            }

            var validation = await SqliteMetadataDatabase.ValidateSnapshotAsync(incomingDb, ct);
            if (!validation.Ok) throw new ApiValidationException($"Backup database integrity check failed: {validation.Result}");
            if (validation.SchemaVersion > _database.CurrentSchemaVersion)
                throw new ApiConflictException($"Backup schema v{validation.SchemaVersion} is newer than this host supports (v{_database.CurrentSchemaVersion}).");
            var sourceDatabaseHash = ComputeFileSha256(incomingDb);
            var sourceSchemaVersion = validation.SchemaVersion;
            var prepared = await _database.PrepareSnapshotForRestoreAsync(incomingDb, ct);
            if (!prepared.Ok || prepared.SchemaVersion != _database.CurrentSchemaVersion)
                throw new ApiValidationException($"Migrated backup snapshot failed validation: integrity={prepared.Result}, schema={prepared.SchemaVersion}.");
            var preparedDatabaseHash = ComputeFileSha256(incomingDb);
            await ValidateArtifactReferencesAsync(incomingDb,
                request.RestoreArtifacts && manifest.IncludesArtifacts ? stagedArtifacts : _artifactRoot, ct);

            // Q08：进入实际改动（DB/目录切换）之前耐久登记恢复事务意图——进程若在"数据库已替换、
            // 目录未全部切换"的中途被终止，启动检查会读到该文件并进入失败锁（见协调器构造函数），
            // 不必依赖 catch/finally 是否来得及执行。校验全部通过后才登记：此前的失败没有改动任何东西。
            var safetyDb = Path.Combine(workspace, "pre-restore.db");
            await _database.CreateSnapshotAsync(safetyDb, ct);
            // R01（2026-10-12 复审）：设备动作安全账本是"当前事实"，不属于可回滚业务配置——
            // 恢复到旧备份绝不能清空/覆盖未闭合的动作意图或核对证据。时序：
            //  · 正常恢复（无未完成事务标记）→ 从 production 根快照当前账本到固定位置；
            //  · 未完成恢复的重试（有标记）→ 复用首次恢复前留下的快照（其中才是真正的"恢复前事实"）；
            //  · 系统资产切换完成后 → 先清除备份带入的旧版本，再用快照严格复刻恢复前状态。
            var ledgerPreserve = Path.Combine(_tempRoot, "safety-ledgers-pre-restore");
            var ledgerRelativePaths = PrepareSafetyLedgerPreserve(ledgerPreserve);
            var previousTransaction = _maintenance.ReadTransaction();
            var oldArtifacts = Path.Combine(_tempRoot, $"artifacts-pre-restore-{Guid.NewGuid():N}");
            var systemSwaps = _systemSections.Select(section => new SystemSwap(
                section.Root, Path.Combine(_tempRoot, $"{section.Prefix}-pre-restore-{Guid.NewGuid():N}"))).ToArray();
            var protectedAssets = new List<StorageMaintenanceCoordinator.RestoreProtectionAsset>();
            if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                protectedAssets.Add(new(_artifactRoot, oldArtifacts, Directory.Exists(_artifactRoot), Directory.Exists(_artifactRoot) ? ComputeDirectorySha256(_artifactRoot) : null));
            if (request.RestoreSystemAssets && manifest.IncludesSystemAssets)
            for (var i = 0; i < _systemSections.Length; i++)
            {
                var root = _systemSections[i].Root;
                var exists = Directory.Exists(root);
                protectedAssets.Add(new(root, systemSwaps[i].Old, exists, exists ? ComputeDirectorySha256(root) : null));
            }
            var expectedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.RestoreArtifacts && manifest.IncludesArtifacts) expectedAssets[_artifactRoot] = ComputeDirectorySha256(stagedArtifacts);
            if (request.RestoreSystemAssets && manifest.IncludesSystemAssets)
                foreach (var (prefix, root) in _systemSections)
                {
                    var stagedRoot = Path.Combine(stagedSystem, prefix);
                    // R01：production 段的一致性哈希排除安全账本——账本不参与备份、
                    // 安装后按"当前事实"复刻，不属于备份资产，也不参与恢复的资产校验。
                    if (Directory.Exists(stagedRoot))
                        expectedAssets[root] = prefix == "production-config"
                            ? ComputeDirectorySha256Except(stagedRoot, ledgerRelativePaths)
                            : ComputeDirectorySha256(stagedRoot);
                }
            // Intent must be durable before the first live database or asset replacement. A failed write
            // aborts here; the safety snapshot is still in the disposable workspace.
            // Q04（2026-10-13 审查）：一并登记目标数据库——中断恢复前必须与当前目标核对。
            _maintenance.BeginRestoreTransaction(manifest.BackupId,
                ["database-replaced", "artifacts-installed", "system-assets-installed", "committed"], safetyDb,
                ComputeFileSha256(safetyDb), sourceDatabaseHash, sourceSchemaVersion,
                preparedDatabaseHash, expectedAssets, protectedAssets, _database.DatabasePath);
            var movedOldArtifacts = false;
            var installedArtifacts = false;
            var rollbackFailed = false;
            var restoreAttemptFailed = false;
            try
            {
                await _database.RestoreSnapshotAsync(incomingDb, ct);
                _maintenance.AdvanceRestoreTransaction("database-replaced");
                if (request.RestoreArtifacts && manifest.IncludesArtifacts)
                {
                    if (Directory.Exists(_artifactRoot)) { Directory.Move(_artifactRoot, oldArtifacts); movedOldArtifacts = true; }
                    Directory.Move(stagedArtifacts, _artifactRoot);
                    installedArtifacts = true;
                    _maintenance.AdvanceRestoreTransaction("artifacts-installed");
                }

                // 系统资产（媒体库/设备与来源配置/生产配置/插件仓库）：与 artifacts 相同的
                // "旧根→保护副本、staged→目标根"原子切换，回滚时逆序复原。
                //
                // R03：每一项在**第一次目录移动之前**就登记进回滚清单（含阶段），因此"旧目录已移走、
                // 新目录尚未安装"的半完成状态也会被回滚覆盖——不会出现原目录缺失却不在清单中的孤儿。
                if (request.RestoreSystemAssets && manifest.IncludesSystemAssets && Directory.Exists(stagedSystem))
                {
                    foreach (var (prefix, root) in _systemSections)
                    {
                        var staged = Path.Combine(stagedSystem, prefix);
                        if (!Directory.Exists(staged)) continue;
                        var swap = systemSwaps.First(x => string.Equals(x.Target, root, StringComparison.OrdinalIgnoreCase));
                        if (Directory.Exists(root))
                        {
                            Directory.Move(root, swap.Old);
                            swap.OldMoved = true;
                        }
                        Directory.Move(staged, root);
                        swap.Installed = true;
                    }
                    _maintenance.AdvanceRestoreTransaction("system-assets-installed");
                    // R01：系统资产（含 production 配置目录）已切换——立刻把安全账本复刻回恢复前状态，
                    // 使"旧备份中的旧账本"无法清空/覆盖未闭合动作（当前事实）。
                    RestoreSafetyLedgerPreserve(ledgerPreserve, ledgerRelativePaths);
                }
                // R01/F01（2026-10-12 复核）：提交前的最后一步——耐久登记"恢复后必须核对"事实。
                // 写入失败**直接抛出**（进入失败/回滚路径），绝不在"核对标记缺失"的情况下提交；
                // 提交点之后进程崩溃时该事实已在磁盘上（启动闸门据此持续阻断，直到提交核对证据）。
                RegisterPostRestoreReconciliation(manifest.BackupId);
                // Commit only after the installed generation matches the hashes captured in the
                // durable intent. This makes the transaction record evidence, not just a list of paths.
                // Q08：SQLite Backup API 产生的是**语义等价、字节不同**的文件（页布局/头部字段/空闲页），
                // 直接比较两份快照的文件哈希会误报——这里按表名 + rowid 逐行规范化哈希（不做 VACUUM），
                // 比较的是逻辑内容而不是文件字节。
                var installedSnapshot = Path.Combine(workspace, "post-restore-verified.db");
                await _database.CreateSnapshotAsync(installedSnapshot, ct);
                if (!string.Equals(await ComputeSnapshotContentSha256Async(installedSnapshot, ct),
                        await ComputeSnapshotContentSha256Async(incomingDb, ct), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Installed metadata database does not match the isolated migrated snapshot (content mismatch).");
                await ValidateArtifactReferencesAsync(installedSnapshot, _artifactRoot, ct);
                foreach (var (assetRoot, expectedHash) in expectedAssets)
                {
                    // R01：production 段的一致性比对排除安全账本（expected 侧同理计算）——
                    // 账本按"当前事实"复刻，不属于备份资产。
                    var exclusions = string.Equals(assetRoot, _productionConfigRoot, StringComparison.OrdinalIgnoreCase)
                        ? ledgerRelativePaths : Array.Empty<string>();
                    if (!Directory.Exists(assetRoot) || !string.Equals(ComputeDirectorySha256Except(assetRoot, exclusions), expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Installed asset root '{assetRoot}' does not match its staged manifest hash.");
                }
                foreach (var protectedAsset in protectedAssets)
                {
                    if (protectedAsset.Existed && (!Directory.Exists(protectedAsset.PreservedPath) ||
                        !string.Equals(ComputeDirectorySha256(protectedAsset.PreservedPath), protectedAsset.Sha256, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException($"Protected rollback asset '{protectedAsset.Target}' does not match its pre-restore hash.");
                    if (!protectedAsset.Existed && Directory.Exists(protectedAsset.PreservedPath))
                        throw new IOException($"Unexpected rollback copy exists for previously absent asset '{protectedAsset.Target}'.");
                }
                // 测试夹具：在提交前注入故障（生产为 null，不产生任何行为变化）。
                if (RestoreCommitFaultInjector is { } fault) await fault();

                // R03：提交点——耐久记录"恢复已提交"。此后的任何清理失败（保护副本删除、
                // 事务文件退役）都不再触发回滚或失败锁：恢复结果已定，残留由启动清理兜底。
                _maintenance.AdvanceRestoreTransaction("committed");
            }
            catch (Exception restoreError)
            {
                restoreAttemptFailed = true;
                // 回滚：数据库、附件与系统资产各自尝试；任何一步失败都必须保留对应的保护副本。
                try { await _database.RestoreSnapshotAsync(safetyDb, CancellationToken.None); }
                catch { rollbackFailed = true; }
                try
                {
                    if (installedArtifacts && Directory.Exists(_artifactRoot)) Directory.Delete(_artifactRoot, true);
                    if (movedOldArtifacts && Directory.Exists(oldArtifacts)) Directory.Move(oldArtifacts, _artifactRoot);
                }
                catch { rollbackFailed = true; }

                for (var i = systemSwaps.Length - 1; i >= 0; i--)
                {
                    var swap = systemSwaps[i];
                    try
                    {
                        // R03：按阶段回滚半完成项——旧目录可能已移走但新目录未安装（swap.Installed=false），
                        // 此时不能删除目标根（那可能是残留的真实数据），只需把旧目录移回。
                        if (swap.Installed && Directory.Exists(swap.Target)) Directory.Delete(swap.Target, true);
                        if (swap.OldMoved && Directory.Exists(swap.Old) && !Directory.Exists(swap.Target))
                            Directory.Move(swap.Old, swap.Target);
                    }
                    catch { rollbackFailed = true; }
                }

                if (rollbackFailed)
                {
                    // 清理路径对此失败完全锁定：保留 pre-restore.db、旧附件目录与全部系统资产副本，
                    // 并把可用副本位置随异常返回，允许再次恢复或人工恢复。
                    preserveSafetyCopies = true;

                    // R04：协调器在本方法的具体操作中将被 Dispose（ExclusiveLease 结束），
                    // 若仅依赖 lease，失败锁会在异常离开作用域时被意外解除，主机会带着
                    // "半恢复"状态继续接受写入。因此把失败状态提升到协调器自身的持久锁：
                    // IsMaintenanceActive 保持 true，常规变更/生产启动一律 409，直到人工
                    // 完成恢复核对后显式 ClearFailureLock。
                    _maintenance.EnterFailureLock(
                        $"Storage restore failed and rollback was incomplete (backup '{request.BackupId}'). " +
                        $"Preserved copies: database '{safetyDb}', artifacts '{oldArtifacts}', system assets under '{_tempRoot}'.");
                    throw new ApiConflictException(
                        "Storage restore failed and rollback could not be fully completed. " +
                        $"Preserved safety copies: database snapshot '{safetyDb}', previous artifacts '{oldArtifacts}', " +
                        $"and system asset copies under '{_tempRoot}'. " +
                        "The host is now locked in maintenance mode; retry restore or recover manually from the listed copies, " +
                        "then clear the maintenance failure lock.",
                        restoreError);
                }
                throw;
            }
            finally
            {
                // 只有提交成功或回滚成功后才允许清理保护副本；回滚失败时全部保留。
                if (!rollbackFailed)
                {
                    if (restoreAttemptFailed && previousTransaction is not null)
                    {
                        // A failed retry returned to an already-locked mixed generation. Keep this
                        // retry's safety DB and all moved asset copies; its transaction nests the
                        // prior verified protection group. Do not retire the marker or delete paths it names.
                        preserveSafetyCopies = true;
                        _maintenance.AdvanceRestoreTransaction("retry-rolled-back-to-previously-locked-generation");
                    }
                    else
                    {
                        // R03：保护副本的清理顺序——先把事务推进到 committed（已在提交点完成），
                        // 再删除旧附件/系统资产副本，最后退役事务文件。事务文件退役失败**不再**
                        // 进入失败锁、也不再抛异常（旧实现把它升级为"副本已删 + 重试被拒"的
                        // 不可恢复状态）：committed 事务在下次启动时只做剩余清理，主机可继续服务。
                        try { if (Directory.Exists(oldArtifacts)) Directory.Delete(oldArtifacts, true); } catch { /* 已提交事务的旧附件清理失败：启动时重试 */ }
                        foreach (var swap in systemSwaps)
                        {
                            try { if (Directory.Exists(swap.Old)) Directory.Delete(swap.Old, true); } catch { /* 系统资产旧副本清理失败：启动时重试 */ }
                        }
                        // R01：提交成功——安全账本快照的使命完成（内容已复刻回 production）。
                        try { if (Directory.Exists(ledgerPreserve)) Directory.Delete(ledgerPreserve, true); } catch { /* 安全账本快照清理失败：不影响恢复结果 */ }
                        try { _maintenance.CompleteRestoreTransaction(); }
                        catch (Exception transactionError)
                        {
                            // 保留工作区（含 pre-restore.db 安全快照），让启动清理/人工核对仍有完整锚点。
                            preserveSafetyCopies = true;
                            _logger?.LogWarning(transactionError,
                                "The committed restore transaction marker could not be removed; startup will retry the cleanup (the restore itself succeeded).");
                        }
                    }
                }
            }

            // R04：恢复正常完成（可能覆盖了之前一次失败的失败锁）后，解除协调器失败锁，
            // 让主机重新接受常规变更与生产启动。
            _maintenance.ClearFailureLock();
            // Q08：恢复成功不是"继续服务"的许可——磁盘资产已替换，而设备适配器/插件/配置绑定
            // 仍是恢复前的内存实例。置"待重启"门：除认证/健康外一律 503（restart_required），
            // 仅进程重启后由协调器构造函数自动解除（届时全部依赖随启动重新加载并校验）。
            _maintenance.MarkRestartPending(
                $"storage restore from backup '{manifest.BackupId}' completed; restart the host to reload device, plugin and configuration state");

            try { await _capacity.RefreshAsync(ct); } catch { /* restore already committed; telemetry refresh is best-effort */ }
            return new StorageRestoreResult(manifest.BackupId, validation.SchemaVersion, _database.CurrentSchemaVersion,
                request.RestoreArtifacts && manifest.IncludesArtifacts, RestartRecommended: true,
                SystemAssetsRestored: request.RestoreSystemAssets && manifest.IncludesSystemAssets);
        }
        finally
        {
            // 回滚未完成时保留整个工作区（含 pre-restore.db 安全副本），只清理已完成/已回滚的恢复工作区。
            if (!preserveSafetyCopies)
            {
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); } catch { /* 工作区清理失败：保留待启动清理，不影响已提交结果 */ }
            }
            _gate.Release();
        }
    }

    private async Task<StorageBackupManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new ApiValidationException("Backup bundle has no manifest.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<StorageBackupManifest>(stream, _json, ct)
            ?? throw new ApiValidationException("Backup manifest is invalid.");
    }

    private string BackupPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ApiValidationException("Backup id is invalid.");
        return Path.Combine(_backupRoot, id + ".vsbackup");
    }

    private static StorageBackupDescriptor ToDescriptor(StorageBackupManifest manifest, long archiveBytes)
        => new(manifest.BackupId, manifest.CreatedAt, manifest.SchemaVersion, archiveBytes, manifest.IncludesArtifacts, manifest.ArtifactCount, manifest.ArtifactBytes, manifest.IncludesSystemAssets);

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct);
    }

    private static string SafeChildPath(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new ApiValidationException("Backup artifact path escapes the restore root.");
        return path;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Q08：快照的**逻辑内容**指纹——按名称遍历所有用户表、按 rowid 逐行累积哈希。
    /// 与文件字节无关：SQLite 备份输出的页布局/头字段/空闲页差异不影响结果，只有真实的数据
    /// 差异才会导致不一致（此前用文件哈希比较两个独立备份，属于必然误报的比较）。
    /// </summary>
    internal static async Task<string> ComputeSnapshotContentSha256Async(string snapshotPath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);

        var tables = new List<string>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(table));
            hash.AppendData([0]);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i)
                        ? "<null>"
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
                    hash.AppendData([0]);
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ComputeDirectorySha256(string path) => ComputeDirectorySha256Except(path, []);

    /// <summary>
    /// R01：目录哈希（可排除指定相对路径，统一以 '/' 规范化）。恢复的安装一致性校验对
    /// production 段必须忽略安全账本——它们按"当前事实"复刻，不属于备份资产。
    /// </summary>
    internal static string ComputeDirectorySha256Except(string path, IReadOnlyList<string> excludedRelativePaths)
    {
        HashSet<string>? excluded = excludedRelativePaths.Count == 0
            ? null
            : new HashSet<string>(excludedRelativePaths.Select(x => x.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            if (excluded is not null && excluded.Contains(relative)) continue;
            var relativeBytes = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(relativeBytes.Length));
            hash.AppendData(relativeBytes);
            using var stream = File.OpenRead(file);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task ValidateArtifactReferencesAsync(string databasePath, string artifactsRoot, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact FROM (" +
            "SELECT preview_relative_path AS artifact FROM run_traces WHERE has_preview=1 AND preview_relative_path IS NOT NULL " +
            "UNION ALL SELECT replay_relative_path FROM run_traces WHERE has_replay_input=1 AND replay_relative_path IS NOT NULL);";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var root = Path.GetFullPath(artifactsRoot);
        while (await reader.ReadAsync(ct))
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is ".." or "."))
                throw new ApiValidationException($"Backup contains unsafe artifact reference '{relative}'.");
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                throw new ApiValidationException($"Database artifact reference '{relative}' has no file in the restored asset set.");
        }
    }

    /// <summary>R01：设备动作安全账本的完整路径集合（含两个 store 的核对证据目录）。</summary>
    private IEnumerable<string> SafetyLedgerPaths()
    {
        yield return _safetyStore.StatePath;
        yield return _safetyStore.ResolutionDirectory;
        yield return _intentStore.StatePath;
        yield return _intentStore.ResolutionDirectory;
    }

    private static bool IsSafetyLedgerPath(string fullPath, IReadOnlyList<string> ledgerPaths)
    {
        foreach (var ledgerFull in ledgerPaths)
        {
            if (string.Equals(fullPath, ledgerFull, StringComparison.OrdinalIgnoreCase)) return true;
            if (fullPath.StartsWith(ledgerFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// R01：把"恢复前的当前安全账本"快照到固定位置，返回需要复刻的相对路径清单。
    /// 未完成恢复的重试会复用现有快照——那里面才是真正的"恢复前事实"
    /// （当前 production 目录可能已被上次中断的恢复部分替换）。
    /// </summary>
    private IReadOnlyList<string> PrepareSafetyLedgerPreserve(string preserveRoot)
    {
        var relativePaths = SafetyLedgerPaths()
            .Where(IsUnderProductionRoot)
            .Select(path => Path.GetRelativePath(_productionConfigRoot, Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (relativePaths.Length == 0) return relativePaths;

        var interruptedRestore = _maintenance.HasRestoreTransactionMarker;
        if (interruptedRestore && Directory.Exists(preserveRoot) && Directory.EnumerateFileSystemEntries(preserveRoot).Any())
            return relativePaths;

        try { if (Directory.Exists(preserveRoot)) Directory.Delete(preserveRoot, true); } catch { /* 恢复保护区清理失败：下次启动重试 */ }
        Directory.CreateDirectory(preserveRoot);
        foreach (var relativePath in relativePaths)
        {
            var source = Path.Combine(_productionConfigRoot, relativePath);
            var destination = Path.Combine(preserveRoot, relativePath);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(source, destination);
            }
        }
        return relativePaths;
    }

    /// <summary>
    /// R01/F01（2026-10-12 复核）：提交前的最后一步——耐久登记"恢复后设备核对"事实。
    /// 若写入失败**直接抛出**（恢复进入失败/回滚路径），绝不出现"恢复已提交但核对标记缺失"
    /// 的窗口：提交点之后进程崩溃时，该事实已在磁盘上（或恢复从未提交）。
    /// 当前已有未闭合动作时保持原状态不变（它本身已阻断启动并要求核对，绝不能被合成状态覆盖，
    /// 否则会丢失原故障的设备/相机清单）。
    /// </summary>
    private void RegisterPostRestoreReconciliation(string backupId)
    {
        var carriedOver = _safetyStore.Load();
        if (carriedOver is not null && DeviceActionSafetyStore.IsUnresolved(carriedOver))
        {
            _logger?.LogWarning(
                "Storage restore completed while an unresolved device-action safety state was carried over (run {RunId}); the existing reconciliation gate remains in force.",
                carriedOver.RunId);
            return;
        }
        var restoreRunId = $"storage-restore:{backupId}";
        _safetyStore.Save(new DeviceActionSafetyState(
            restoreRunId, [], [], DateTimeOffset.UtcNow,
            $"a storage restore from backup '{backupId}' replaced system assets (devices, plugins, configuration); " +
            "verify that the physical device state matches the restored configuration before starting production.",
            Phase: DeviceActionSafetyPhase.Unknown,
            RunId: restoreRunId,
            DeviceFingerprints: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ProcessId: null, CameraIds: []));
    }

    /// <summary>R01：系统资产切换完成后，把安全账本严格复刻回恢复前状态（先清备份带入的版本，再回拷快照）。</summary>
    private void RestoreSafetyLedgerPreserve(string preserveRoot, IReadOnlyList<string> relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var target = Path.Combine(_productionConfigRoot, relativePath);
            if (File.Exists(target)) File.Delete(target);
            else if (Directory.Exists(target)) Directory.Delete(target, true);

            var source = Path.Combine(preserveRoot, relativePath);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            else if (Directory.Exists(source))
            {
                CopyDirectory(source, target);
            }
        }
    }

    private bool IsUnderProductionRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_productionConfigRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
