// 快照/恢复流程用到的持久化模型（从 Program.cs 移出，纯移动）。
// 顶层语句程序里的本地函数必须与入口同文件，但 record/class 可以独立——
// 拆出来只为让 Program.cs 回到 800 行目标内，语义与字段顺序未改动。
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record CheckpointManifest
{
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("source_database")] public string SourceDatabase { get; init; } = string.Empty;
    [JsonPropertyName("snapshot_sha256")] public string SnapshotSha256 { get; init; } = string.Empty;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("integrity")] public string Integrity { get; init; } = string.Empty;
}

/// <summary>Q04：命名互斥锁租约（进程退出/崩溃时由内核自动释放，不会永久阻塞后续维护）。</summary>
internal sealed class MutexLease(Mutex mutex) : IDisposable
{
    public void Dispose()
    {
        try { mutex.ReleaseMutex(); } catch { /* 进程正在退出时尽力释放 */ }
        mutex.Dispose();
    }
}

/// <summary>Q04：跨进程"维护进行中"标记的内容（pid + 进程启动时间用于残留判定）。</summary>
internal sealed record MaintenanceActiveMarker(int Pid, DateTimeOffset ProcessStartedAt, DateTimeOffset At);

/// <summary>
/// Q01：统一恢复标记的内存表示（兼容宿主 restore-transaction.json 与本工具旧版
/// .visionstudio-restore-intent.json 两种格式）。目标身份、检查点哈希与保护副本哈希
/// 是中断恢复判定的三个事实来源；BackupId 用于自证和解分支登记设备核对闸门（R2 复审）。
/// </summary>
internal sealed record RestoreMarker(
    string Stage,
    string TargetDatabase,
    string BackupDir,
    string CheckpointSha256,
    string SafetyDatabaseSha256,
    IReadOnlyList<string> ProtectedFiles,
    string RawPath,
    string BackupId = "cli:reconciled");

/// <summary>宿主协议记录（PascalCase 与 StorageMaintenanceCoordinator.RestoreTransaction 对齐）。</summary>
internal sealed record RestoreTransactionRecord
{
    [JsonPropertyName("BackupId")] public string BackupId { get; init; } = string.Empty;
    [JsonPropertyName("StartedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("Stage")] public string Stage { get; init; } = "switching";
    [JsonPropertyName("PlannedStages")] public IReadOnlyList<string> PlannedStages { get; init; } = [];
    [JsonPropertyName("UpdatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("Failure")] public string? Failure { get; init; }
    /// <summary>Q04（2026-10-13）：意图的目标数据库——中断恢复前必须与当前目标核对。</summary>
    [JsonPropertyName("TargetDatabase")] public string? TargetDatabase { get; init; }
    [JsonPropertyName("SafetyDatabase")] public string SafetyDatabase { get; init; } = string.Empty;
    [JsonPropertyName("SafetyDatabaseSha256")] public string SafetyDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("SourceDatabaseSha256")] public string SourceDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("SourceSchemaVersion")] public int SourceSchemaVersion { get; init; }
    [JsonPropertyName("ExpectedDatabaseSha256")] public string ExpectedDatabaseSha256 { get; init; } = string.Empty;
    [JsonPropertyName("ExpectedAssetSha256")] public IReadOnlyDictionary<string, string> ExpectedAssetSha256 { get; init; } = new Dictionary<string, string>();
    /// <summary>宿主侧是对象数组（RestoreProtectionAsset）；此处只按通用元素读取，本工具只写空数组。</summary>
    [JsonPropertyName("ProtectedAssets")] public IReadOnlyList<JsonElement> ProtectedAssets { get; init; } = [];
    [JsonPropertyName("PreviousTransaction")] public object? PreviousTransaction { get; init; }
}

/// <summary>旧版工具意图文件（snake_case，仅读取兼容）。</summary>
internal sealed record LegacyRestoreIntent
{
    [JsonPropertyName("target_database")] public string TargetDatabase { get; init; } = string.Empty;
    [JsonPropertyName("backup_dir")] public string BackupDir { get; init; } = string.Empty;
    [JsonPropertyName("protected_files")] public IReadOnlyList<string>? ProtectedFiles { get; init; }
    [JsonPropertyName("checkpoint_dir")] public string CheckpointDir { get; init; } = string.Empty;
    [JsonPropertyName("checkpoint_sha256")] public string CheckpointSha256 { get; init; } = string.Empty;
    [JsonPropertyName("stage")] public string Stage { get; init; } = "switching";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>设备安全状态记录（camelCase，与 DeviceActionSafetyState 的 Web 默认序列化对齐）。</summary>
internal sealed record DeviceActionSafetyStateRecord(
    [property: JsonPropertyName("manifestHash")] string ManifestHash,
    [property: JsonPropertyName("deviceIds")] IReadOnlyList<string> DeviceIds,
    [property: JsonPropertyName("robotIds")] IReadOnlyList<string> RobotIds,
    [property: JsonPropertyName("setAt")] DateTimeOffset SetAt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("unreadable")] bool Unreadable = false,
    [property: JsonPropertyName("phase")] int Phase = 0,
    [property: JsonPropertyName("runId")] string? RunId = null,
    [property: JsonPropertyName("deviceFingerprints")] IReadOnlyDictionary<string, string>? DeviceFingerprints = null,
    [property: JsonPropertyName("processId")] string? ProcessId = null,
    [property: JsonPropertyName("cameraIds")] IReadOnlyList<string>? CameraIds = null);
