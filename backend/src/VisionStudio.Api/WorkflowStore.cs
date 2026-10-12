using VisionStudio.Api.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed class WorkflowStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WorkflowStore(IWebHostEnvironment env)
    {
        _root = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath), "workflows");
        Directory.CreateDirectory(_root);
    }

    /// <summary>R07：当前修订（旁路记录 '{id}.revision.json' 的内容哈希）；null = 尚无修订记录或记录不可读。</summary>
    public async Task<string?> GetRevisionAsync(string id, CancellationToken cancellationToken)
    {
        string path;
        try { path = RevisionPath(ValidateId(id)); }
        catch (ArgumentException) { return null; }
        return await ReadRevisionAsync(path, cancellationToken);
    }

    /// <summary>
    /// R07（2026-10-12 复审）：乐观并发校验与保存。
    /// expectedRevision 为 null 时不校验（历史调用方的兼容路径）；显式提供时必须与磁盘当前修订
    /// 一致（空串 = "期望尚无修订记录"），不一致抛 409——绝不静默覆盖另一编辑者已保存的修改。
    /// 返回本次写入后的新修订值（同时写入 '{id}.revision.json'，供 GET 端点输出给客户端）。
    /// </summary>
    public async Task<string> SaveAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken, string? expectedRevision = null)
    {
        var safeId = ValidateId(id);
        var finalPath = Path.Combine(_root, safeId + ".json");
        var revisionPath = RevisionPath(safeId);
        if (expectedRevision is not null)
        {
            var current = await ReadRevisionAsync(revisionPath, cancellationToken) ?? string.Empty;
            if (!string.Equals(expectedRevision.Trim(), current, StringComparison.Ordinal))
                throw new ApiConflictException(
                    $"Workflow '{safeId}' was modified by another session after it was loaded (expected revision '{expectedRevision.Trim()}', current '{current}'). " +
                    "Load the latest version to review the other change before deciding to overwrite.");
        }

        // 原子写入：先写临时文件再整体替换。旧的 File.Create 直写方式在取消/异常时会留下截断的半截文件。
        // R07：先序列化到字节、同一份字节既落盘又计算内容哈希——修订值与落盘内容严格一致。
        var payload = JsonSerializer.SerializeToUtf8Bytes(workflow with { Id = id }, _json);
        var revision = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var tempPath = Path.Combine(_root, $".{safeId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(payload, cancellationToken);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 尽力清理，不影响主流程 */ }
        }

        // 修订记录随内容一并更新（内容落盘成功后才写；原子写防止半截记录）。
        var revisionTemp = revisionPath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(revisionTemp,
                JsonSerializer.SerializeToUtf8Bytes(new WorkflowRevision(revision, DateTimeOffset.UtcNow), _json), cancellationToken);
            File.Move(revisionTemp, revisionPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(revisionTemp)) File.Delete(revisionTemp); } catch { /* 尽力清理 */ }
        }
        return revision;
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

    /// <summary>R07：读取修订记录。损坏/缺失按"无基线"处理——并发保护是 best-effort，工作流 JSON 才是权威数据。</summary>
    private async Task<string?> ReadRevisionAsync(string revisionPath, CancellationToken ct)
    {
        if (!File.Exists(revisionPath)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<WorkflowRevision>(await File.ReadAllBytesAsync(revisionPath, ct), _json);
            return string.IsNullOrWhiteSpace(meta?.Revision) ? null : meta.Revision;
        }
        catch
        {
            return null;
        }
    }

    private sealed record WorkflowRevision(string Revision, DateTimeOffset SavedAt);
}
