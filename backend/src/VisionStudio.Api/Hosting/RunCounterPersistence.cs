using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Hosting;

/// <summary>
/// 跨轮计数器的落盘（2026-10-29）。<see cref="InMemoryRunCounterStore"/> 本身只是进程内状态，
/// 停机就丢——"本班次良率""连续 3 件 NG"这类数字一旦跨班次清零，现场会直接怀疑视觉算错。
///
/// 策略（刻意保守）：
/// - 启动时读 <c>data/runtime/counters.json</c> 并恢复，文件损坏只记日志不阻断启动；
/// - 运行期每 5s 取一次快照，内容有变化才写；写临时文件再原子替换，避免半截 JSON；
/// - 停机时补写一次最终快照（StopAsync）；
/// - 任何 IO 异常都不影响主流程（计数是辅助信息，不能让采集停线），但会连续记日志。
///
/// 为什么不让节点自己写盘：value.counter 每轮都可能执行（几千次/小时），节点内同步写盘
/// 会把 IO 抖动直接带到节拍上。快照+节流把写盘次数压到每 5 秒最多一次。
/// </summary>
public sealed class RunCounterPersistence : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly InMemoryRunCounterStore _store;
    private readonly ILogger<RunCounterPersistence> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>上次成功写盘的内容；相同则跳过写入（计数器不动时零 IO）。</summary>
    private string _lastWritten = string.Empty;

    public RunCounterPersistence(
        InMemoryRunCounterStore store,
        IWebHostEnvironment environment,
        ILogger<RunCounterPersistence> logger)
    {
        _store = store;
        _logger = logger;
        var directory = Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "runtime");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "counters.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();

        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // 停机补写：最后一次 5s 窗口内的增量不能丢。
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取并恢复快照；文件不存在属正常首启，损坏则记日志后按空状态继续。</summary>
    public void Load()
    {
        try
        {
            var snapshots = RunCounterSnapshotFile.Load(_path);
            if (snapshots.Count == 0) return;
            _store.Restore(snapshots);
            _logger.LogInformation("Restored {Count} run counters from {Path}.", snapshots.Count, _path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Run counter snapshot at {Path} could not be restored; starting from zero.", _path);
        }
    }

    /// <summary>内容有变化才写盘；原子替换由 <see cref="RunCounterSnapshotFile"/> 负责。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = _store.Snapshot();
            var json = RunCounterSnapshotFile.Serialize(snapshots);
            if (string.Equals(json, _lastWritten, StringComparison.Ordinal)) return;

            await Task.Run(() => RunCounterSnapshotFile.Save(_path, snapshots), cancellationToken)
                .ConfigureAwait(false);
            _lastWritten = json;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不阻断采集：下一个 5s 窗口会重试。
            _logger.LogWarning(ex, "Run counter snapshot could not be written to {Path}.", _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>测试与诊断用：当前落盘路径。</summary>
    public string SnapshotPath => _path;
}
