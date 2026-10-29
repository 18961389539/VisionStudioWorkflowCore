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

            // 第二次写入必须覆盖而不是追加，且不留下 .tmp。
            RunCounterSnapshotFile.Save(path, [new RunCounterSnapshot("wf-1", "yield", 9, 9, 8, 1)]);
            Assert.Equal(9L, RunCounterSnapshotFile.Load(path)[0].Count);
            Assert.False(File.Exists(path + ".tmp"));
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
