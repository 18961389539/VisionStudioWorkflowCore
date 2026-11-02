using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// value.counter 的**新增**契约（2026-10-29）。累加/复位/作用域语义已由
/// <c>P0CompletionNodeTests</c> 覆盖，这里只锁这次修复引入的三件事：
/// 1. 没注册 <see cref="IRunCounterStore"/> 时显式失败（旧实现会退到进程级静态实例，
///    计数跨工作流串号、重启静默清零，现场只看到"良率对不上"）；
/// 2. 快照/恢复后计数接着走（data/runtime/counters.json 的落盘依赖它）；
/// 3. 快照顺序稳定、文件读写原子且可测（损坏文件由宿主降级，不在这里吞异常）。
/// </summary>
public sealed class ValueCounterNodeTests
{
    [Fact]
    public async Task Execute_WithoutHostStore_FailsFastInsteadOfCountingIntoAProcessGlobalBucket()
    {
        var node = new ValueCounterNode();
        var definition = new NodeDefinition("counter-1", "value.counter", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"counterId":"ng"}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(new NodeExecutionContext(
                new Dictionary<string, VisionValue> { ["pass"] = VisionValue.Boolean(true) }, "wf-1", "run-1"),
                definition, CancellationToken.None));

        // 报错要说清怎么修：现场装错/漏注册时，日志里只有这一句可用。
        Assert.Contains("IRunCounterStore", error.Message);
        Assert.Contains("AddSingleton<IRunCounterStore", error.Message);
    }

    [Fact]
    public void SnapshotAndRestore_KeepShiftTotalsAcrossRestart()
    {
        var first = new InMemoryRunCounterStore();
        var state = first.GetOrAdd("wf-1", "yield");
        state.Count = 42; state.Total = 42; state.Pass = 40; state.Fail = 2;

        var snapshot = first.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(("wf-1", "yield"), (snapshot[0].Scope, snapshot[0].CounterId));
        Assert.Equal((42L, 42L, 40L, 2L), (snapshot[0].Count, snapshot[0].Total, snapshot[0].Pass, snapshot[0].Fail));

        // 模拟重启：新进程的新存储从快照恢复，计数接着走。
        var second = new InMemoryRunCounterStore();
        second.Restore(snapshot);
        var restored = second.GetOrAdd("wf-1", "yield");
        Assert.Equal(42L, restored.Count);
        Assert.Equal(2L, restored.Fail);

        restored.Count++; // 下一件产品
        Assert.Equal(43L, second.Snapshot()[0].Count);
    }

    [Fact]
    public void Snapshot_IsStableOrderedSoPersistenceCanDebounceByContent()
    {
        var store = new InMemoryRunCounterStore();
        store.GetOrAdd("wf-b", "z").Count = 1;
        store.GetOrAdd("wf-a", "b").Count = 2;
        store.GetOrAdd("wf-a", "a").Count = 3;

        // 顺序稳定 = 内容没变时序列化结果逐字节相同 = 落盘可以按字符串比较跳过写入。
        Assert.Equal(["wf-a/a", "wf-a/b", "wf-b/z"],
            store.Snapshot().Select(x => $"{x.Scope}/{x.CounterId}").ToArray());
    }

    [Fact]
    public void GetOrAdd_DoesNotMergeScopesThatContainTheLegacySeparator()
    {
        // 回归：旧键是 $"{scope}\u001f{counterId}"，于是这两对会算出同一个字符串——
        // 两个工位的良率静默合并成一个计数器，而且没有任何异常可查。
        var store = new InMemoryRunCounterStore();
        var left = store.GetOrAdd("A", "B\u001FC");
        var right = store.GetOrAdd("A\u001FB", "C");

        Assert.NotSame(left, right);
        left.Count = 5;
        Assert.Equal(0L, right.Count);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void Snapshot_CarriesLastTouchedSoTheHostCanReclaimIdleCounters()
    {
        var store = new InMemoryRunCounterStore();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        store.GetOrAdd("wf-1", "yield");

        var touched = store.Snapshot()[0].LastTouchedAt;
        Assert.True(touched >= before, "GetOrAdd must stamp LastTouchedAt so retention can judge age");

        // 恢复的条目要原样带上时间戳，否则重启一次所有计数器就都"变年轻"，
        // 孤儿永远不会被回收。
        var restored = new InMemoryRunCounterStore();
        restored.Restore([new RunCounterSnapshot("wf-1", "yield", 1, 1, 1, 0, touched)]);
        Assert.Equal(touched, restored.Snapshot()[0].LastTouchedAt);
    }

    [Fact]
    public void Snapshot_OfLegacyFileWithoutTimestampReportsDefaultSoHostKeepsIt()
    {
        // 旧版快照没有 lastTouchedAt 字段。反序列化后为 default，宿主据此"无法判定年龄 → 保留"。
        var directory = Path.Combine(Path.GetTempPath(), "vs-counters-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "counters.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """[{"scope":"wf-1","counterId":"yield","count":3,"total":3,"pass":3,"fail":0}]""");

            var loaded = RunCounterSnapshotFile.Load(path);
            Assert.Single(loaded);
            Assert.Equal(default, loaded[0].LastTouchedAt);
            Assert.Equal(3L, loaded[0].Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFile_FailsFastWhenAnotherHostHoldsTheWriteLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vs-counters-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "counters.json");
        try
        {
            Directory.CreateDirectory(directory);

            // 升级重启的重叠窗口：旧进程还在补写，新进程已经启动。
            // 后来者必须显式失败（由宿主的失败重试 + 降级告警兜住），不能覆盖对方。
            using var held = new FileStream(
                path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var error = Assert.Throws<IOException>(() => RunCounterSnapshotFile.Save(
                path, [new RunCounterSnapshot("wf-1", "yield", 1, 1, 1, 0)], TimeSpan.FromMilliseconds(120)));

            Assert.Contains(".lock", error.Message);
            // 失败既不留半成品，也不产生正式文件（否则会读到一个不属于自己的快照）。
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFile_ConcurrentWritersNeverLeaveACorruptFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vs-counters-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "counters.json");
        try
        {
            Directory.CreateDirectory(directory);

            // 旧实现用固定名 path + ".tmp"：A 关闭临时文件后 B 以 Create 打开（截断），
            // A 再把 B 的**半成品** rename 到正式位置 —— 下次启动读到半个 JSON 就从零开始，
            // 整班次良率清零。唯一临时名 + 写锁让这种交叉不可能发生。
            Parallel.For(0, 16, i => RunCounterSnapshotFile.Save(
                path, [new RunCounterSnapshot("wf-1", "yield", i, i, i, 0)]));

            // 结果必须是某个完整快照，而不是拼接/截断的产物。
            var loaded = RunCounterSnapshotFile.Load(path);
            Assert.Equal(("wf-1", "yield"), (loaded[0].Scope, loaded[0].CounterId));
            Assert.InRange(loaded[0].Count, 0L, 15L);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFile_RoundTripsAndReplacesAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vs-counters-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "counters.json");
        try
        {
            // 文件不存在 = 首启：返回空而不是抛异常。
            Assert.Empty(RunCounterSnapshotFile.Load(path));

            RunCounterSnapshotFile.Save(path, [new RunCounterSnapshot("wf-1", "yield", 7, 7, 6, 1)]);
            var loaded = RunCounterSnapshotFile.Load(path);
            Assert.Single(loaded);
            Assert.Equal(7L, loaded[0].Count);

            // 第二次写入必须覆盖而不是追加，且不留下任何临时文件
            // （临时名带 pid+guid，所以要查 *.tmp 而不是某个固定名）。
            RunCounterSnapshotFile.Save(path, [new RunCounterSnapshot("wf-1", "yield", 9, 9, 8, 1)]);
            Assert.Equal(9L, RunCounterSnapshotFile.Load(path)[0].Count);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            // 写入改走手写 FileStream（为了 fsync）后，编码必须与原来的 File.WriteAllText 一致：
            // UTF-8 无 BOM。带 BOM 会让按字节解析的消费方读到 \uFEFF 前缀。
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "snapshot must be written as UTF-8 without BOM");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotFile_CorruptJsonThrowsSoTheHostCanLogAndStartFromZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vs-counters-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "counters.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ not json");

            // 不吞异常：宿主 (RunCounterPersistence.Load) 负责记 warning 并降级，
            // 这里若静默返回空列表，"快照文件写坏了"永远查不出来。
            Assert.Throws<JsonException>(() => RunCounterSnapshotFile.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
