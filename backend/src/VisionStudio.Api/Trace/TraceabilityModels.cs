using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record RunTraceContext(
    string Source,
    string? JobId = null,
    int? JobVersion = null,
    string? WorkflowHash = null,
    string? DebugMode = null,
    string? DependencyManifestHash = null,
    string? Note = null);

public sealed record RunTraceRecord(
    string RunId,
    DateTimeOffset StartedAt,
    string Source,
    string? JobId,
    int? JobVersion,
    string WorkflowId,
    string WorkflowName,
    string? WorkflowHash,
    string? DependencyManifestHash,
    string ExecutionStatus,
    string Disposition,
    double TotalDurationMs,
    int NodeCount,
    int OverlayCount,
    bool HasPreview,
    bool HasReplayInput,
    string? ReplaySourceNodeId,
    string? Error,
    string? Note,
    IReadOnlyList<NodeRunReport> NodeReports,
    string? ErrorCode = null);

public sealed record UpdateDispositionRequest(string Disposition, string? Note = null);

public sealed record TraceStats(
    int Total,
    int Ok,
    int Ng,
    int Review,
    int Errors,
    double AverageDurationMs);

public sealed record TracePage(
    int Offset,
    int Limit,
    int Total,
    IReadOnlyList<RunTraceRecord> Items);

/// <summary>
/// 追溯查询过滤条件：分页与统计共用同一组谓词，保证“命中 N 条”与统计口径一致。
/// 时间一律 UTC（started_at 以 UTC ISO "O" 格式落库，字符串比较即时间比较）；
/// RunIdPrefix 供“输入运行 ID 直接查找”——前端在直查模式下不附加时间窗。
/// </summary>
public sealed record TraceQuery(
    int Offset = 0,
    int Limit = 100,
    string? JobId = null,
    int? JobVersion = null,
    string? Disposition = null,
    string? ExecutionStatus = null,
    string? Source = null,
    string? RunIdPrefix = null,
    string? WorkflowKeyword = null,
    string? ErrorCode = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    /// <summary>
    /// 是否在列表里带上逐节点报告（2026-10-26 性能）。默认 true 保持既有契约；
    /// 追溯面板按页展示时传 false——50 条记录各带几十份报告，实际只用得到选中那一条。
    /// </summary>
    bool IncludeReports = true);

public sealed class TraceRetentionOptions
{
    public int MetadataRetentionDays { get; set; } = 3650;
    public int OkArtifactRetentionDays { get; set; } = 7;
    public int NgArtifactRetentionDays { get; set; } = 90;
    public int ErrorArtifactRetentionDays { get; set; } = 90;
    public int ReviewArtifactRetentionDays { get; set; } = 30;
    public int OkPreviewSampleEvery { get; set; } = 20;
    public int OkReplaySampleEvery { get; set; } = 20;
    public int CleanupIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// 存储状态（/api/storage/status）。F05：SnapshotSkippedPreviews/Replays 暴露在线备份冻结窗口
/// 期间跳过的附件落盘次数——"跳过是明确的行为变化，其数量必须可见并由现场验收确认"。
/// </summary>
public sealed record TraceStorageStatus(
    string DatabaseFile,
    long DatabaseBytes,
    int TraceCount,
    int PreviewCount,
    int ReplayInputCount,
    long ArtifactBytes,
    TraceRetentionOptions Retention,
    StorageCapacityStatus? Capacity = null,
    SchemaMigrationStatus? Schema = null,
    long SnapshotSkippedPreviews = 0,
    long SnapshotSkippedReplays = 0);

/// <summary>
/// SQLite-backed trace index and structured run payload store.
/// Node reports, exact workflow snapshots and overlays live in SQLite JSON columns;
/// binary preview and replay-input artifacts live on disk. No recursive directory scan is used for list/stats/get.
/// </summary>
