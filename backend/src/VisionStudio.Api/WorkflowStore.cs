using VisionStudio.Api.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Engine;

namespace VisionStudio.Api;

/// <summary>
/// 工作流文档的持久化存储（data/workflows 下的 JSON 文件 + 旁路修订记录）。
///
/// R07：乐观并发基线——'{id}.revision.json' 保存内容的 SHA-256，保存方携带 expectedRevision
/// 校验，不一致返回 409。
/// F04（2026-10-12 复核）：**读-比-写必须在同一临界区**——多请求可同时通过同一预期修订产生
/// 覆盖与共享临时文件竞争；GET 的"内容 + 修订"分离读取也可能返回两代数据。修复要点：
///   · 进程内所有保存与一致性读取经同一个信号量串行（本产品单宿主部署；跨进程部署需另行约束）；
///   · 临时文件带 GUID，消除共享写/删除竞争；
///   · 读取时校验"修订 == 内容哈希"，不一致以**内容为权威**自愈修订记录——GET 返回的修订
///     永远对应其 body；
///   · 修订记录缺失（期望非空时）或损坏不再被当作"可覆盖的无基线"——一律 409，要求先核对/修复。
/// Q06（2026-10-13 审查）：**保存事实与反馈必须一致**。旧实现"先内容后修订"分两步，修订写失败
/// 时内容已提交却返回 500，前端据此提示"未写入后端"——实际文档已被覆盖。现在：
///   · 写序调整为"修订临时文件 → 内容提交 → 修订落定"，任何一步失败都可判定事实；
///   · 修订**落定**失败（内容已提交）不再抛 500，而是返回 <see cref="SaveOutcome.RevisionRecordDeferred"/>
///     =true——读取路径的"内容为权威"自愈会把修订对齐到内容哈希，反馈如实报告"已保存、版本
///     记录延迟"；
///   · 修订**临时文件**或**内容**提交失败 ⇒ 什么都不曾落盘，仍如实按失败处理。
/// </summary>
public sealed class WorkflowStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>F04：保存与一致性读取的进程内临界区（单宿主部署的并发契约）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "workflows");
        Directory.CreateDirectory(_root);
    }

    /// <summary>R07：当前修订的轻量读取（不经内容校验）。一致性读取请用 <see cref="LoadWithRevisionAsync"/>。</summary>
    public Task<string?> GetRevisionAsync(string id, CancellationToken cancellationToken)
    {
        string path;
        try { path = RevisionPath(ValidateId(id)); }
        catch (ArgumentException) { return Task.FromResult<string?>(null); }
        var (_, readable, value) = ReadRevision(path);
        return Task.FromResult(readable ? value : null);
    }

    /// <summary>
    /// R07/F04：乐观并发校验与保存。整个"读取当前修订 → 比较 → 写入内容与修订"在进程内串行执行。
    /// expectedRevision 为 null 时**不校验**（历史调用方的兼容路径）——该路径的明确语义是
    /// **显式强制覆盖**：仅限管理员专用覆盖端点（Q05，2026-10-13 审查：交互保存必须携带基线，
    /// 强制覆盖另设授权 + 原因 + 审计入口）使用。
    /// expectedRevision 显式提供时：
    ///   · 空串 = 期望尚无修订记录（文档从未保存或已一致迁移；同时核实内容文件不存在——A02）；
    ///   · 非空必须与磁盘当前修订一致，且修订必须与当前内容哈希同代（A02）；
    ///   · 修订记录缺失但期望非空、或记录损坏 ⇒ 409（无法核对，绝不静默覆盖）。
    /// 返回写入结果：修订（= 落盘内容的 SHA-256）与"修订记录是否延迟落定"（Q06）。
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken, string? expectedRevision = null)
    {
        var safeId = ValidateId(id);
        var finalPath = Path.Combine(_root, safeId + ".json");
        var revisionPath = RevisionPath(safeId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedRevision is not null)
            {
                var expected = expectedRevision.Trim();
                var (exists, readable, current) = ReadRevision(revisionPath);
                var contentHash = File.Exists(finalPath) ? ComputeContentSha256(finalPath) : null;
                if (!exists)
                {
                    if (expected.Length != 0)
                        throw Conflict(safeId, expected, "no revision record exists",
                            "The stored document has no revision record (fresh store or manual cleanup); a non-empty baseline cannot be verified.");
                    // A02（2026-10-12 第三轮）：期望"新建"必须同时核实内容文件不存在——
                    // "缺修订但有内容"是不一致态，绝不能当作新建来覆盖。
                    if (contentHash is not null)
                        throw Conflict(safeId, expected, "content exists without a revision record",
                            "The stored document has no revision record; reconcile or repair the revision metadata before overwriting it as a new document.");
                }
                else if (!readable)
                {
                    throw Conflict(safeId, expected, "revision record unreadable",
                        "The revision record exists but cannot be read; reconcile or repair it before saving (it cannot be treated as a safe no-baseline).");
                }
                else if (!string.Equals(expected, current, StringComparison.Ordinal))
                {
                    throw Conflict(safeId, expected, current ?? string.Empty,
                        "Load the latest version to review the other change before deciding to overwrite.");
                }
                // A02：修订必须与当前内容同代——上一次保存"内容已替换、修订写入失败"会留下漂移；
                // 旧修订绝不能通过检查去覆盖新代内容（内容为权威，须重新加载后再保存）。
                else if (contentHash is null || !string.Equals(current, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw Conflict(safeId, expected, current ?? string.Empty,
                        "The revision record does not match the stored content (a previous save was interrupted); reload the latest version before saving.");
                }
            }

            // 原子写入：先写临时文件再整体替换。旧的 File.Create 直写方式在取消/异常时会留下截断的半截文件。
            // R07：先序列化到字节、同一份字节既落盘又计算内容哈希——修订值与落盘内容严格一致。
            var payload = JsonSerializer.SerializeToUtf8Bytes(workflow with { Id = id }, _json);
            var revision = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var tempPath = Path.Combine(_root, $".{safeId}.{Guid.NewGuid():N}.tmp");
            // Q06：修订临时文件先落盘（与内容同代的第一证物），内容提交后再把它原子落定。
            var revisionTemp = $"{revisionPath}.{Guid.NewGuid():N}.tmp";
            var revisionDeferred = false;
            try
            {
                await File.WriteAllBytesAsync(revisionTemp,
                    JsonSerializer.SerializeToUtf8Bytes(new WorkflowRevision(revision, DateTimeOffset.UtcNow), _json), cancellationToken);
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(payload, cancellationToken);
                }
                File.Move(tempPath, finalPath, overwrite: true);
                // 修订落定：失败（文件被占用/权限）时内容与版本事实均已成立——读取路径会按
                // "内容为权威"把修订对齐到内容哈希。此时如实报告"延迟"而不是谎报失败。
                try
                {
                    File.Move(revisionTemp, revisionPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    revisionDeferred = true;
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 尽力清理，不影响主流程 */ }
                try { if (File.Exists(revisionTemp)) File.Delete(revisionTemp); } catch { /* 尽力清理，不影响主流程 */ }
            }
            return new SaveOutcome(revision, revisionDeferred);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// F04：一致性读取——返回与内容同代的修订。读取时校验"修订 == 内容哈希"（保存中断、
    /// 外部改动会让旁路记录漂移），不一致时以**内容为权威**重写修订记录再返回；
    /// 保证 GET 的 body 与 X-Workflow-Revision 永远同代。
    /// </summary>
    public async Task<(WorkflowDefinition Workflow, string? Revision)?> LoadWithRevisionAsync(string id, CancellationToken cancellationToken)
    {
        string safeId;
        try { safeId = ValidateId(id); }
        catch (ArgumentException) { return null; }
        var path = Path.Combine(_root, safeId + ".json");
        var revisionPath = RevisionPath(safeId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(bytes, _json);
            if (workflow is null) return null;
            var contentRevision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var (_, readable, value) = ReadRevision(revisionPath);
            if (!readable || !string.Equals(value, contentRevision, StringComparison.Ordinal))
            {
                try { await WriteRevisionAsync(revisionPath, contentRevision, cancellationToken); }
                catch { /* 自愈写入失败：本次仍返回内容对应的修订，下一次读取重试 */ }
            }
            return (workflow, contentRevision);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkflowDefinition?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = Path.Combine(_root, ValidateId(id) + ".json");
        }
        catch (ArgumentException)
        {
            return null; // 非法 ID 一律视为不存在（404），而不是 500
        }
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, _json, cancellationToken);
    }

    private static ApiConflictException Conflict(string safeId, string expected, string current, string guidance)
        => new($"Workflow '{safeId}' revision check failed (expected '{expected}', current '{current}'). {guidance}");

    /// <summary>
    /// 严格校验工作流 ID：只允许字母、数字、连字符与下划线。
    /// 旧实现把非法字符“过滤掉”，会让 a.b 与 ab 落到同一个文件，存在相互覆盖的风险；
    /// 这里改为直接拒绝，并限制长度防止异常长的文件名。
    /// </summary>
    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128
            || id.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException("工作流 ID 只能包含字母、数字、连字符和下划线。", nameof(id));
        }
        return id;
    }

    private string RevisionPath(string safeId) => Path.Combine(_root, safeId + ".revision.json");

    /// <summary>A02：当前内容文件的内容哈希（不存在时由调用方判空）——修订与内容必须同代。</summary>
    private static string ComputeContentSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>R07/F04：修订记录状态——存在性、可读性与值分离："损坏"不再等同于"无基线"。</summary>
    private (bool Exists, bool Readable, string? Value) ReadRevision(string revisionPath)
    {
        if (!File.Exists(revisionPath)) return (false, true, null);
        try
        {
            var meta = JsonSerializer.Deserialize<WorkflowRevision>(File.ReadAllBytes(revisionPath), _json);
            return string.IsNullOrWhiteSpace(meta?.Revision) ? (true, false, null) : (true, true, meta.Revision);
        }
        catch
        {
            return (true, false, null);
        }
    }

    private async Task WriteRevisionAsync(string revisionPath, string revision, CancellationToken ct)
    {
        var temp = $"{revisionPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp,
                JsonSerializer.SerializeToUtf8Bytes(new WorkflowRevision(revision, DateTimeOffset.UtcNow), _json), ct);
            File.Move(temp, revisionPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 尽力清理 */ }
        }
    }

    private sealed record WorkflowRevision(string Revision, DateTimeOffset SavedAt);

    /// <summary>
    /// Q06（2026-10-13 审查）：保存结果——修订（= 落盘内容哈希）与"修订记录是否延迟落定"。
    /// RevisionRecordDeferred=true 表示内容已提交、旁路修订文件短暂未对齐（读取路径自愈），
    /// 调用方必须如实反馈"已保存、版本基线将自动对齐"，不得声称失败或声称未写入。
    /// </summary>
    public sealed record SaveOutcome(string Revision, bool RevisionRecordDeferred);
}
