namespace VisionStudio.Api;

public sealed partial class StorageBackupService
{
    /// <summary>R01：设备动作安全账本的完整路径集合（含两个 store 的核对证据目录）。</summary>
    private IEnumerable<string> SafetyLedgerPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _safetyStore.StatePath,
            _safetyStore.ResolutionDirectory,
            _intentStore.StatePath,
            _intentStore.ResolutionDirectory
        };
        paths.UnionWith(_production.SafetyLedgers.LedgerPaths());
        foreach (var triggerPath in _production.TriggerQueuePaths)
        {
            paths.Add(triggerPath);
            paths.Add(triggerPath + ".journal");
        }
        if (Directory.Exists(_productionConfigRoot))
        {
            foreach (var statePath in Directory.EnumerateFiles(_productionConfigRoot, "device-action-safety*.json", SearchOption.TopDirectoryOnly))
            {
                paths.Add(statePath);
                paths.Add(statePath + ".resolutions");
            }
            foreach (var triggerPath in Directory.EnumerateFiles(_productionConfigRoot, "trigger-queue*.json", SearchOption.TopDirectoryOnly))
            {
                paths.Add(triggerPath);
                paths.Add(triggerPath + ".journal");
            }
            paths.UnionWith(Directory.EnumerateFiles(_productionConfigRoot, "trigger-queue*.json.journal", SearchOption.TopDirectoryOnly));
        }
        return paths;
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
