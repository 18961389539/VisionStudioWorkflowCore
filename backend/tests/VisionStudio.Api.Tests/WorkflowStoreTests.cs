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
    /// <summary>WorkflowStore 的实际存储根（&lt;root&gt;/data/workflows，经 VisionStudioDataRoot.Resolve）——
    /// 注入/断言必须指向这里，指向其它层级的注入不会生效（2026-10-12 第三轮：漂移/损坏注入曾写错层级，
    /// 断言假通过）。</summary>
    private readonly string _storeRoot;
    private readonly WorkflowStore _store;

    public WorkflowStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vs-workflow-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _storeRoot = Path.Combine(_root, "data", "workflows");
        _store = new WorkflowStore(new FakeEnv(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录尽力清理 */ }
    }

    [Fact]
    public async Task List_ReflectsSaveAndExternalChange_WithoutStaleCache()
    {
        // 性能优化（2026-10-26）后 ListAsync 会缓存摘要：必须保证三条失效路径都真的生效，
        // 否则"打开已有流程"会显示旧名称/旧节点数。
        var workflow = new WorkflowDefinition("wf-cache", "原始名称", [new("n1", "image.threshold", null, null, null)], []);
        await _store.SaveAsync(workflow.Id, workflow, CancellationToken.None);

        var first = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("原始名称", first.Name);
        Assert.Equal(1, first.NodeCount);

        // ① 再次保存（名称变化）→ 缓存必须失效
        var renamed = new WorkflowDefinition("wf-cache", "改名后", [new("n1", "image.threshold", null, null, null), new("n2", "image.blur", null, null, null)], []);
        await _store.SaveAsync(renamed.Id, renamed, CancellationToken.None);
        var afterSave = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("改名后", afterSave.Name);
        Assert.Equal(2, afterSave.NodeCount);

        // ② 外部直接改文件（绕过 store）→ 依赖 (长度, mtime) 失效
        var path = Path.Combine(_storeRoot, "wf-cache.json");
        var external = new WorkflowDefinition("wf-cache", "外部改写", [], []);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(external));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var afterExternal = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("外部改写", afterExternal.Name);

        // ③ 文件被删除 → 结果与缓存条目都不再保留
        File.Delete(path);
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SavedRoiReferenceImageAndPoseSurviveReloadTogether()
    {
        var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            """{"referenceImage":"saved-png","referenceWidth":640,"referenceHeight":480,"baseX":123.125,"baseY":45.25,"baseThetaDeg":30,"referenceBindings":"image-and-pose","roi":{"type":"Circle","x":140,"y":50,"radius":12}}""")!;
        var workflow = new WorkflowDefinition("roi-reference", "Reference", [new("roi", "roi.transform", null, null, parameters)], []);
        await _store.SaveAsync(workflow.Id, workflow, CancellationToken.None);
        var loaded = await _store.LoadAsync(workflow.Id, CancellationToken.None);
        var saved = Assert.Single(loaded!.Nodes).Parameters!;
        Assert.Equal("saved-png", saved["referenceImage"].GetString());
        Assert.Equal(123.125, saved["baseX"].GetDouble());
        Assert.Equal(30, saved["baseThetaDeg"].GetDouble());
        Assert.Equal(140, saved["roi"].GetProperty("x").GetInt32());
        Assert.Equal("image-and-pose", saved["referenceBindings"].GetString());
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
        Assert.Empty(Directory.GetFiles(_storeRoot, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_storeRoot, ".*"));
    }

    [Fact]
    public async Task Save_WhenRevisionLagsContent_RejectsEvenMatchingStaleBaseline()
    {
        // A02（2026-10-12 第三轮）：内容已替换、修订写入失败会留下"旧修订 + 新内容"的漂移——
        // 旧修订绝不能通过检查去覆盖新代内容。
        var baseline = (await _store.SaveAsync("lag", new WorkflowDefinition("lag", "V1", [], []), CancellationToken.None)).Revision;
        // 模拟"内容已更新、修订未更新"的保存中断中间态（注入必须指向真实存储根）。
        await File.WriteAllTextAsync(Path.Combine(_storeRoot, "lag.json"),
            "{\"id\":\"lag\",\"name\":\"V2-content\",\"nodes\":[],\"edges\":[]}");

        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("lag", new WorkflowDefinition("lag", "V3", [], []), CancellationToken.None, expectedRevision: baseline));

        // 内容未被旧基线覆盖。
        Assert.Equal("V2-content", (await _store.LoadAsync("lag", CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Save_WhenContentExistsWithoutRevision_RejectsNewDocumentBaseline()
    {
        // A02：内容存在但修订缺失（不一致态）时，expected=''（"期望新建"）必须拒绝而不是覆盖。
        await _store.SaveAsync("orphan", new WorkflowDefinition("orphan", "V1", [], []), CancellationToken.None);
        File.Delete(Path.Combine(_storeRoot, "orphan.revision.json"));

        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("orphan", new WorkflowDefinition("orphan", "V2", [], []), CancellationToken.None, expectedRevision: ""));

        Assert.Equal("V1", (await _store.LoadAsync("orphan", CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Save_WithoutExpectedRevision_IsExplicitForcedOverride()
    {
        // A02（2026-10-12 第三轮）：无 expectedRevision 的兼容路径语义 = **显式强制覆盖**
        // （管理/迁移类后台操作）；交互式编辑器入口始终携带基线。此测试把该语义锁定为契约。
        await _store.SaveAsync("forced", new WorkflowDefinition("forced", "V1", [], []), CancellationToken.None);
        var v2 = (await _store.SaveAsync("forced", new WorkflowDefinition("forced", "V2", [], []), CancellationToken.None)).Revision;

        Assert.False(string.IsNullOrWhiteSpace(v2));
        Assert.Equal("V2", (await _store.LoadAsync("forced", CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Save_SameContentTwice_KeepsRevisionAndBaseline()
    {
        // A07 补充：明确"同内容幂等保存"语义——修订即内容哈希，重复保存同一内容成功且修订不变。
        var baseline = (await _store.SaveAsync("idem", new WorkflowDefinition("idem", "V1", [], []), CancellationToken.None)).Revision;
        var again = (await _store.SaveAsync("idem", new WorkflowDefinition("idem", "V1", [], []), CancellationToken.None, expectedRevision: baseline)).Revision;

        Assert.Equal(baseline, again);
        Assert.Equal(baseline, await _store.GetRevisionAsync("idem", CancellationToken.None));
    }

    [Fact]
    public async Task Save_WithExpectedRevision_RejectsStaleWriterAndAcceptsMatchingBaseline()
    {
        // R07（2026-10-12 复审）：乐观并发——A 读取后被 B 覆盖，A 的旧基线保存必须 409，
        // 绝不静默覆盖；不传基线保持历史兼容路径；空串表示"期望尚无修订记录"。
        var first = (await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V1", [], []), CancellationToken.None)).Revision;
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, await _store.GetRevisionAsync("wf", CancellationToken.None));

        // 以最新基线保存 → 成功且修订变化。
        var second = (await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V2", [], []), CancellationToken.None, expectedRevision: first)).Revision;
        Assert.NotEqual(first, second);

        // 会话仍持有旧基线 → 冲突，磁盘保持"胜者"内容。
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("wf", new WorkflowDefinition("wf", "loser", [], []), CancellationToken.None, expectedRevision: first));
        Assert.Equal("V2", (await _store.LoadAsync("wf", CancellationToken.None))!.Name);

        // 不传基线 = 兼容路径（旧调用方）：允许覆盖。
        var third = (await _store.SaveAsync("wf", new WorkflowDefinition("wf", "V3", [], []), CancellationToken.None)).Revision;
        Assert.NotEqual(second, third);

        // 空串 = 期望"尚无修订记录"：已有记录时冲突；全新 ID 则匹配成功。
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("wf", new WorkflowDefinition("wf", "V4", [], []), CancellationToken.None, expectedRevision: ""));
        var fresh = (await _store.SaveAsync("wf-new", new WorkflowDefinition("wf-new", "N", [], []), CancellationToken.None, expectedRevision: "")).Revision;
        Assert.False(string.IsNullOrWhiteSpace(fresh));
    }

    [Fact]
    public async Task ConcurrentSaves_WithSameBaseline_YieldExactlyOneWinner()
    {
        // F04（2026-10-12 复核）：读-比-写必须原子——真实并发（同一屏障起跑）下只允许一个成功，
        // 其余明确 409；旧实现 24 个请求全部越过检查，产生 I/O 竞争与覆盖。
        var baseline = (await _store.SaveAsync("race", new WorkflowDefinition("race", "V0", [], []), CancellationToken.None)).Revision;
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
        await File.WriteAllTextAsync(Path.Combine(_storeRoot, "drift.revision.json"),
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
        await File.WriteAllTextAsync(Path.Combine(_storeRoot, "broken.revision.json"), "not-json");

        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("broken", new WorkflowDefinition("broken", "V2", [], []), CancellationToken.None, expectedRevision: ""));
        await Assert.ThrowsAsync<ApiConflictException>(() =>
            _store.SaveAsync("broken", new WorkflowDefinition("broken", "V2", [], []), CancellationToken.None, expectedRevision: "some-hash"));
    }

    [Fact]
    public async Task Save_WhenRevisionFinalizeFails_CommitsContentAndReportsDeferred()
    {
        // Q06（2026-10-13 审查）：修订落定失败（文件被占用）时内容已提交——旧实现返回 500，
        // 前端据此提示"未写入后端"，实际文档已被覆盖。现在如实报告"延迟落定"，
        // 且读取路径按"内容为权威"自愈，修订与内容始终可核对。
        var baseline = (await _store.SaveAsync("defer", new WorkflowDefinition("defer", "V1", [], []), CancellationToken.None)).Revision;
        var revisionPath = Path.Combine(_storeRoot, "defer.revision.json");
        // 允许读取、阻止写入/替换——精确命中"修订落定失败"（与审查探针的锁模式一致）。
        using var lockHandle = new FileStream(revisionPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var outcome = await _store.SaveAsync("defer", new WorkflowDefinition("defer", "V2", [], []), CancellationToken.None, expectedRevision: baseline);

        Assert.True(outcome.RevisionRecordDeferred);
        // 内容已提交：文档确实是 V2，而不是"未写入"。
        Assert.Equal("V2", (await _store.LoadAsync("defer", CancellationToken.None))!.Name);

        lockHandle.Dispose();
        // 自愈后：读取返回的修订与保存返回的修订一致（内容为权威）。
        var loaded = await _store.LoadWithRevisionAsync("defer", CancellationToken.None);
        Assert.Equal(outcome.Revision, loaded!.Value.Revision);
        Assert.Equal(outcome.Revision, await _store.GetRevisionAsync("defer", CancellationToken.None));
    }

    [Fact]
    public async Task Save_CancelledBeforeCommit_LeavesNothingOnDiskAndReportsFailure()
    {
        // Q06 对照面：保存在任何落盘之前被取消 ⇒ 无内容、无临时文件残留，失败反馈是真实的。
        var baseline = (await _store.SaveAsync("defer-temp", new WorkflowDefinition("defer-temp", "V1", [], []), CancellationToken.None)).Revision;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.SaveAsync("defer-temp", new WorkflowDefinition("defer-temp", "V2", [], []), cancelled.Token, expectedRevision: baseline));
        Assert.Equal("V1", (await _store.LoadAsync("defer-temp", CancellationToken.None))!.Name);
        Assert.Empty(Directory.GetFiles(_storeRoot, ".*.tmp"));
    }

    [Fact]
    public async Task Save_RejectsIdsWithIllegalCharacters()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SaveAsync("a.b", new WorkflowDefinition("a.b", "X", [], []), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SaveAsync("../escape", new WorkflowDefinition("escape", "X", [], []), CancellationToken.None));

        // 被拒绝的 ID 不得落盘：旧的“过滤”实现会把 a.b 写成 ab.json，与合法 ab 相互覆盖
        Assert.Empty(Directory.GetFiles(_storeRoot, "*.json"));
    }

    [Fact]
    public async Task Load_InvalidId_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(await _store.LoadAsync("a.b", CancellationToken.None));
        Assert.Null(await _store.LoadAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task Save_StampsCurrentDocumentSchemaVersion()
    {
        // 调用方传入的版本号不能把文档"降级标记"；版本由宿主在落盘时盖章。
        await _store.SaveAsync("schema-stamp",
            new WorkflowDefinition("schema-stamp", "Schema", [], []) { SchemaVersion = 0 },
            CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_storeRoot, "schema-stamp.json")));
        Assert.Equal(WorkflowDocumentSchema.CurrentVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());

        var loaded = await _store.LoadAsync("schema-stamp", CancellationToken.None);
        Assert.Equal(WorkflowDocumentSchema.CurrentVersion, loaded!.SchemaVersion);
    }

    [Fact]
    public async Task Load_RejectsDocumentWrittenByNewerSchema()
    {
        // 新版宿主写出的文档不能被旧宿主打开后覆盖保存（未知字段会被静默丢弃）。
        Directory.CreateDirectory(_storeRoot);
        await File.WriteAllTextAsync(Path.Combine(_storeRoot, "future.json"),
            """{"id":"future","name":"Future","nodes":[],"edges":[],"schemaVersion":999}""");

        var load = await Assert.ThrowsAsync<ApiConflictException>(() => _store.LoadAsync("future", CancellationToken.None));
        Assert.Contains("newer VisionStudio version", load.Message);
        await Assert.ThrowsAsync<ApiConflictException>(() => _store.LoadWithRevisionAsync("future", CancellationToken.None));
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
