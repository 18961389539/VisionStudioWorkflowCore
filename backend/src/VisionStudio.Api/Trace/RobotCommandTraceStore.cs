using VisionStudio.Api.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api;

public sealed record RobotCommandTraceRecord(
    string TraceId,
    string RobotId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long CommandId,
    int MaxAttempt,
    string LastStage,
    string Status,
    VisionRobotTarget2D? Target,
    string? Error,
    IReadOnlyList<RobotCommandTraceEvent> Events);

/// <summary>
/// 机器人命令追踪的保留策略（2026-11-10）。机器人握手按命令逐条落盘，此前只增不减：
/// 一条产线每天数千条命令时，数据根会持续增长且没有任何清理入口。
/// 默认保留 30 天、最多 20000 个追踪文件；24 小时内修改的文件不按数量清理，
/// 避免故障排查期间把正在使用的证据删掉。
/// </summary>
public sealed class RobotTraceRetentionOptions
{
    /// <summary>按时间保留的天数；0 关闭时间维度清理。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>追踪文件数上限；0 关闭数量维度清理。</summary>
    public int MaxTraceFiles { get; set; } = 20000;

    /// <summary>后台清理周期（分钟），最小 5 分钟。</summary>
    public int CleanupIntervalMinutes { get; set; } = 1440;
}

/// <summary>
/// File-backed command trace observer. Each handshake trace is stored independently from vision RunTrace so
/// robot communication failures can be inspected even when no workflow was running.
/// </summary>
public sealed class RobotCommandTraceStore : IRobotCommandObserver
{
    private static readonly TimeSpan QuantityProtectionAge = TimeSpan.FromHours(24);
    private readonly string _root;
    private readonly RobotTraceRetentionOptions _retention;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RobotCommandTraceStore(IWebHostEnvironment env, IOptions<RobotTraceRetentionOptions> retention)
        : this(Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "robot-traces"), retention)
    {
    }

    internal RobotCommandTraceStore(string root, IOptions<RobotTraceRetentionOptions> retention)
    {
        _root = root;
        _retention = retention.Value;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask OnEventAsync(RobotCommandTraceEvent traceEvent, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = FindPath(traceEvent.TraceId) ?? TracePath(traceEvent.Timestamp, traceEvent.TraceId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var current = File.Exists(path) ? await ReadAsync<RobotCommandTraceRecord>(path, cancellationToken) : null;
            var events = current?.Events.ToList() ?? [];
            events.Add(traceEvent);
            var status = traceEvent.Stage switch
            {
                "Done" => "Complete",
                "Error" => "Error",
                "Retry" => "Retrying",
                _ => "Running"
            };
            var record = new RobotCommandTraceRecord(
                traceEvent.TraceId,
                traceEvent.RobotId,
                current?.StartedAt ?? traceEvent.Timestamp,
                traceEvent.Timestamp,
                traceEvent.CommandId != 0 ? traceEvent.CommandId : current?.CommandId ?? 0,
                Math.Max(traceEvent.Attempt, current?.MaxAttempt ?? 0),
                traceEvent.Stage,
                status,
                traceEvent.Target ?? current?.Target,
                traceEvent.Error ?? current?.Error,
                events);
            await WriteAsync(path, record, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RobotCommandTraceRecord>> ListAsync(int take, string? robotId, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return [];
        var result = new List<RobotCommandTraceRecord>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var item = await ReadAsync<RobotCommandTraceRecord>(path, ct);
                if (item is null) continue;
                if (!string.IsNullOrWhiteSpace(robotId) && !string.Equals(item.RobotId, robotId, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(item);
            }
            catch { /* 损坏/不可读的追踪文件：跳过该条 */ }
        }
        return result.OrderByDescending(x => x.StartedAt).Take(Math.Clamp(take, 1, 500)).ToArray();
    }

    public async Task<RobotCommandTraceRecord?> GetAsync(string traceId, CancellationToken ct)
    {
        var path = FindPath(traceId);
        return path is null ? null : await ReadAsync<RobotCommandTraceRecord>(path, ct);
    }

    private string TracePath(DateTimeOffset at, string traceId)
        => Path.Combine(_root, at.UtcDateTime.ToString("yyyy-MM-dd"), traceId + ".json");

    private string? FindPath(string traceId)
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, traceId + ".json", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary>按保留策略清理追踪文件；返回实际删除的文件数。</summary>
    public async Task<int> CleanupAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_root)) return 0;
            ct.ThrowIfCancellationRequested();
            var files = new List<(string Path, DateTimeOffset LastWrite, long Length)>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (info.Exists)
                    files.Add((info.FullName, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length));
            }

            var deleted = 0;
            foreach (var path in SelectForDeletion(files, _retention, now))
            {
                try { File.Delete(path); deleted++; }
                catch (IOException) { /* 并发写入/占用：下一轮再清 */ }
                catch (UnauthorizedAccessException) { /* 权限问题：保留文件，不中断清理 */ }
            }

            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (IOException) { /* 空目录删除失败（并发写入）：下一轮再清 */ }
                catch (UnauthorizedAccessException) { /* 空目录无权限删除：保留，不中断清理 */ }
            }
            return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>纯选择逻辑（便于单测）：先按时间过期，再按数量从最旧开始裁，24 小时内文件不受数量上限影响。</summary>
    public static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<(string Path, DateTimeOffset LastWrite, long Length)> files,
        RobotTraceRetentionOptions options,
        DateTimeOffset now)
    {
        var doomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.RetentionDays > 0)
        {
            var cutoff = now.AddDays(-options.RetentionDays);
            foreach (var file in files)
                if (file.LastWrite < cutoff) doomed.Add(file.Path);
        }

        if (options.MaxTraceFiles > 0)
        {
            var kept = files.Where(file => !doomed.Contains(file.Path))
                .OrderByDescending(file => file.LastWrite)
                .ToList();
            var excess = kept.Count - options.MaxTraceFiles;
            var protectionCutoff = now - QuantityProtectionAge;
            for (var index = kept.Count - 1; index >= 0 && excess > 0; index--)
            {
                if (kept[index].LastWrite > protectionCutoff) break;
                doomed.Add(kept[index].Path);
                excess--;
            }
        }

        return [.. doomed];
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, _json, ct);
        File.Move(temp, path, true);
    }
}

/// <summary>后台按 <see cref="RobotTraceRetentionOptions"/> 清理机器人命令追踪；恢复维护期间让出。</summary>
public sealed class RobotTraceRetentionHostedService : BackgroundService
{
    private readonly RobotCommandTraceStore _store;
    private readonly RobotTraceRetentionOptions _options;
    private readonly ILogger<RobotTraceRetentionHostedService> _logger;
    private readonly StorageMaintenanceCoordinator _maintenance;

    public RobotTraceRetentionHostedService(
        RobotCommandTraceStore store,
        IOptions<RobotTraceRetentionOptions> options,
        ILogger<RobotTraceRetentionHostedService> logger,
        StorageMaintenanceCoordinator maintenance)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        _maintenance = maintenance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(5, _options.CleanupIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var maintenanceLease = _maintenance.TryEnterRequest();
                if (maintenanceLease is not null)
                {
                    var deleted = await _store.CleanupAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (deleted > 0) _logger.LogInformation("Robot command trace retention deleted {Count} trace file(s).", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Robot command trace retention cleanup failed."); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
