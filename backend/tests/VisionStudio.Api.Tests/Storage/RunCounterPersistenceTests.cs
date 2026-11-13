using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api.Hosting;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 跨轮计数器落盘的可靠性边界（2026-10-29）。
///
/// 这里只测在真实停机路径上会出事、但手工验证很难覆盖的几件事：
/// 1. 宿主 shutdown 令牌已取消时，停机补写仍然必须完成——否则"关得慢"直接等于"丢增量"；
/// 2. 落盘失败不阻断停机（不冒泡取消异常）；
/// 3. 启动时按保留期回收孤儿计数器，并把裁剪结果写回磁盘（否则文件只增不减）。
///
/// 隔离约定：全部走显式路径构造，绝不经过 <c>VISIONSTUDIO_DATA_ROOT</c>。质量门禁带着该变量
/// 跑测试，一旦走生产构造函数就会与并发宿主用例共享数据根（2026-10-29 实测会写脏 runtime/）。
/// </summary>
public sealed class RunCounterPersistenceTests
{
    [Fact]
    public async Task StopAsync_StillWritesFinalSnapshotWhenHostTokenIsAlreadyCancelled()
    {
        var root = NewRoot();
        try
        {
            var store = new InMemoryRunCounterStore();
            store.GetOrAdd("wf-1", "yield").Count = 42;

            var persistence = NewPersistence(store, root);

            // 模拟 ShutdownTimeout 已到期：宿主传进来的令牌处于取消状态。
            using var hostToken = new CancellationTokenSource();
            hostToken.Cancel();

            await persistence.StopAsync(hostToken.Token);

            // 补写用的是独立预算，不受这个已取消令牌影响。
            var loaded = RunCounterSnapshotFile.Load(persistence.SnapshotPath);
            Assert.Equal(42L, Assert.Single(loaded).Count);
            Assert.Equal("yield", loaded[0].CounterId);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task StopAsync_DoesNotThrowWhenTheSnapshotPathBecameUnwritable()
    {
        var root = NewRoot();
        try
        {
            var store = new InMemoryRunCounterStore();
            store.GetOrAdd("wf-1", "yield").Count = 7;
            var persistence = NewPersistence(store, root);

            // 用同名目录占住锁文件路径，让写入必然失败（UnauthorizedAccessException）。
            //
            // 注意不能像旧版本那样去占 "<path>.tmp"：临时文件名现在是 pid+guid 唯一的，
            // 占住固定名只会让"写失败"这条路径根本不被执行，测试变成永远通过的空壳。
            Directory.CreateDirectory(persistence.SnapshotPath + ".lock");

            // 计数是辅助信息：落盘失败只记 warning，不能让停机抛异常。
            await persistence.StopAsync(CancellationToken.None);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Load_ReclaimsIdleCountersAndRewritesTheFile()
    {
        var root = NewRoot();
        try
        {
            var store = new InMemoryRunCounterStore();
            var path = Path.Combine(root, "runtime", "counters.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 一个 60 天没动过的孤儿（流程已改名）+ 一个刚用过的计数器。
            RunCounterSnapshotFile.Save(path,
            [
                new RunCounterSnapshot("wf-old", "yield", 900, 900, 880, 20, DateTimeOffset.UtcNow.AddDays(-60)),
                new RunCounterSnapshot("wf-new", "yield", 12, 12, 12, 0, DateTimeOffset.UtcNow)
            ]);

            var persistence = NewPersistence(store, root, new RunCounterPersistenceOptions(
                TimeSpan.FromSeconds(5), TimeSpan.FromDays(30)));
            persistence.Load();

            // 孤儿不进内存：否则它会被当作"本班次数据"一直带着。
            var restored = store.Snapshot();
            Assert.Equal(["wf-new"], restored.Select(x => x.Scope).ToArray());

            // 磁盘也要干净：不写回的话每次重启都会重新读到它、再裁一遍，文件只增不减。
            var onDisk = RunCounterSnapshotFile.Load(path);
            Assert.Equal(["wf-new"], onDisk.Select(x => x.Scope).ToArray());
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Load_KeepsEntriesWithoutATimestampBecauseTheirAgeIsUnknown()
    {
        var root = NewRoot();
        try
        {
            var store = new InMemoryRunCounterStore();
            var path = Path.Combine(root, "runtime", "counters.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 旧版快照没有 lastTouchedAt：无法判定年龄，一律保留（不做静默丢弃）。
            RunCounterSnapshotFile.Save(path, [new RunCounterSnapshot("wf-legacy", "yield", 5, 5, 5, 0)]);

            var persistence = NewPersistence(store, root, new RunCounterPersistenceOptions(
                TimeSpan.FromSeconds(5), TimeSpan.FromDays(1)));
            persistence.Load();

            Assert.Equal(5L, Assert.Single(store.Snapshot()).Count);
            Assert.Equal(5L, Assert.Single(RunCounterSnapshotFile.Load(path)).Count);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Load_SweepsAbandonedTemporariesLeftByAKilledProcess()
    {
        var root = NewRoot();
        try
        {
            var store = new InMemoryRunCounterStore();
            var persistence = NewPersistence(store, root);
            var directory = Path.GetDirectoryName(persistence.SnapshotPath)!;
            Directory.CreateDirectory(directory);

            // 模拟"写到一半被 Kill"：临时文件留在原地，且时间戳已经过期。
            var abandoned = persistence.SnapshotPath + ".1a2b.0123456789abcdef.tmp";
            File.WriteAllText(abandoned, "{ half");
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));

            persistence.Load();

            Assert.False(File.Exists(abandoned), "abandoned temporaries must be swept on startup");
        }
        finally
        {
            Delete(root);
        }
    }

    private static RunCounterPersistence NewPersistence(
        InMemoryRunCounterStore store,
        string root,
        RunCounterPersistenceOptions? options = null)
        => new(
            store,
            Path.Combine(root, "runtime", "counters.json"),
            NullLogger<RunCounterPersistence>.Instance,
            alarms: null,
            options: options);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vs-counter-persist-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
