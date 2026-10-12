using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;
using Xunit;

namespace VisionStudio.Api.Tests;

/// <summary>
/// WorkflowStore 持久化契约：ID 严格校验（拒绝而非过滤非法字符，防 a.b ≡ ab 同槽覆盖）
/// 与原子写入（临时文件 + 整体替换，失败不留半截文件）。
/// </summary>
public sealed class WorkflowStoreTests : IDisposable
{
    private readonly string _root;
    private readonly WorkflowStore _store;

    public WorkflowStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs-workflow-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new WorkflowStore(new FakeEnv(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsWorkflowWithSameId()
    {
        await _store.SaveAsync("demo-linear", new WorkflowDefinition("whatever", "Linear", [], []), CancellationToken.None);

        var loaded = await _store.LoadAsync("demo-linear", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("demo-linear", loaded!.Id); // 后端把文件内 ID 统一改写为保存地址的 ID
        Assert.Equal("Linear", loaded.Name);
    }

    [Fact]
    public async Task Save_OverwritesPreviousContentCompletely()
    {
        await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V1", [], []), CancellationToken.None);
        await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V2-longer-replacement-content", [], []), CancellationToken.None);

        var loaded = await _store.LoadAsync("wf", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("V2-longer-replacement-content", loaded!.Name);
        // 原子写不残留临时文件（旧实现直接 File.Create，异常时会留下截断文件）
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_root, ".*"));
    }

    [Fact]
    public async Task Save_WithExpectedRevision_RejectsStaleWriterAndAcceptsMatchingBaseline()
    {
        // R07（2026-10-12 复审）：乐观并发——A 读取后被 B 覆盖，A 的旧基线保存必须 409，
        // 绝不静默覆盖；不传基线保持历史兼容路径；空串表示"期望尚无修订记录"。
        var first = await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V1", [], []), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, await _store.GetRevisionAsync("wf", CancellationToken.None));

        // 以最新基线保存 → 成功且修订变化。
        var second = await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V2", [], []), CancellationToken.None, expectedRevision: first);
        Assert.NotEqual(first, second);

        // 会话仍持有旧基线 → 冲突，磁盘保持"胜者"内容。
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("wf", new WorkflowDefinition("wf", "loser", [], []), CancellationToken.None, expectedRevision: first));
        Assert.Equal("V2", (await _store.LoadAsync("wf", CancellationToken.None))!.Name);

        // 不传基线 = 兼容路径（旧调用方）：允许覆盖。
        var third = await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V3", [], []), CancellationToken.None);
        Assert.NotEqual(second, third);

        // 空串 = 期望"尚无修订记录"：已有记录时冲突；全新 ID 则匹配成功。
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("wf", new WorkflowDefinition("wf", "V4", [], []), CancellationToken.None, expectedRevision: ""));
        var fresh = await _store.SaveAsync("wf-new", new WorkflowDefinition("wf-new", "N", [], []), CancellationToken.None, expectedRevision: "");
        Assert.False(string.IsNullOrWhiteSpace(fresh));
    }

    [Fact]
    public async Task ConcurrentSaves_WithSameBaseline_YieldExactlyOneWinner()
    {
        // F04（2026-10-12 复核）：读-比-写必须原子——真实并发（同一屏障起跑）下只允许一个成功，
        // 其余明确 409；旧实现 24 个请求全部越过检查，产生 I/O 竞争与覆盖。
        var baseline = await _store.SaveAsync("race", new WorkflowDefinition("race", "V0", [], []), CancellationToken.None);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 任务内容一律不同于基线（V1..V16）：若某个候选内容与基线**完全相同**，其写入不改动
        // 修订哈希，会为下一个候选继续放行（Expected 1 / Actual 2 的间歇失败即由此而来）。
        var tasks = Enumerable.Range(1, 16).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            try
            {
                await _store.SaveAsync("race", new WorkflowDefinition("race", $"V{i}", [], []), CancellationToken.None, expectedRevision: baseline);
                return "ok";
            }
            catch (ApiConflictException)
            {
                return "conflict";
            }
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(x => x == "ok"));
        Assert.Equal(15, results.Count(x => x == "conflict"));

        // 内容与修订仍然同代（一致性读取自愈保证）。
        var loaded = await _store.LoadWithRevisionAsync("race", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(loaded!.Value.Revision, await _store.GetRevisionAsync("race", CancellationToken.None));
    }

    [Fact]
    public async Task LoadWithRevision_SelfHealsDriftedRevisionRecord()
    {
        // F04：保存中断/外部改动让旁路修订漂移——GET 必须返回**内容对应**的修订（内容为权威自愈），
        // 且以该修订保存不再误报 409。
        await _store.SaveAsync("drift", new WorkflowDefinition("drift", "V1", [], []), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "drift.revision.json"),
            "{\"revision\":\"deadbeef\",\"savedAt\":\"2026-01-01T00:00:00Z\"}");

        var loaded = await _store.LoadWithRevisionAsync("drift", CancellationToken.None);
        Assert.NotNull(loaded);
        var healed = loaded!.Value.Revision;
        Assert.NotEqual("deadbeef", healed);
        Assert.Equal(healed, await _store.GetRevisionAsync("drift", CancellationToken.None)); // 已自愈落盘

        await _store.SaveAsync("drift", new WorkflowDefinition("drift", "V2", [], []), CancellationToken.None, expectedRevision: healed);
        Assert.Equal("V2", (await _store.LoadAsync("drift", CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Save_WithUnreadableRevisionRecord_IsRejected()
    {
        // F04：损坏的修订不能当作"安全的无基线"——显式基线一律 409，要求先核对/修复。
        await _store.SaveAsync("broken", new WorkflowDefinition("broken", "V1", [], []), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.revision.json"), "not-json");

        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("broken", new WorkflowDefinition("broken", "V2", [], []), CancellationToken.None, expectedRevision: ""));
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("broken", new WorkflowDefinition("broken", "V2", [], []), CancellationToken.None, expectedRevision: "some-hash"));
    }

    [Fact]
    public async Task Save_RejectsIdsWithIllegalCharacters()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SaveAsync("a.b", new WorkflowDefinition("a.b", "X", [], []), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SaveAsync("../escape", new WorkflowDefinition("escape", "X", [], []), CancellationToken.None));

        // 被拒绝的 ID 不得落盘：旧的“过滤”实现会把 a.b 写成 ab.json，与合法 ab 相互覆盖
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public async Task Load_InvalidId_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(await _store.LoadAsync("a.b", CancellationToken.None));
        Assert.Null(await _store.LoadAsync("", CancellationToken.None));
    }

    private sealed class FakeEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
