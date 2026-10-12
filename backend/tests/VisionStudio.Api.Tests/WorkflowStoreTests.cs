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
