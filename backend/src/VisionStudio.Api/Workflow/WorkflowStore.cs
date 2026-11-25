using VisionStudio.Api.Infrastructure;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record WorkflowDocumentSummary(string Id, string Name, int NodeCount, int EdgeCount, DateTimeOffset UpdatedAt);

/// <summary>
/// 工作流正文是唯一持久化版本。修订号是正文的 SHA-256，读、比较和原子替换在同一单宿主临界区。
/// </summary>
public sealed class WorkflowStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>F04：保存与一致性读取的进程内临界区（单宿主部署的并发契约）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 列表摘要缓存（2026-10-26）：ListAsync 原来对每个文件做一次完整 JSON 反序列化，只为了拿
    /// 名称 / 节点数 / 连线数。工作流一多（数百份、每份上百 KB），打开"打开已有流程"就是几十 MB 的解析。
    /// 以 (文件长度, 最后写入时间) 作为失效条件；保存时主动失效，避免同一秒内改写命中旧值。
    /// </summary>
    private readonly ConcurrentDictionary<string, SummaryCacheEntry> _summaryCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct SummaryCacheEntry(long Length, DateTime MtimeUtc, WorkflowDocumentSummary Summary);

    public WorkflowStore(IWebHostEnvironment env, IConfiguration? configuration = null)
    {
        _root = Path.Combine(VisionStudioDataRoot.Resolve(env.ContentRootPath, configuration), "workflows");
        Directory.CreateDirectory(_root);
    }

    public async Task<IReadOnlyList<WorkflowDocumentSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var documents = new List<WorkflowDocumentSummary>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.EndsWith(".revision.json", StringComparison.OrdinalIgnoreCase)) continue;
                seen.Add(path);

                var id = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var info = new FileInfo(path);
                    var mtime = info.LastWriteTimeUtc;
                    if (_summaryCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.MtimeUtc == mtime)
                    {
                        documents.Add(cached.Summary);
                        continue;
                    }

                    await using var stream = File.OpenRead(path);
                    var workflow = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, _json, cancellationToken);
                    if (workflow is null) continue;
                    var summary = new WorkflowDocumentSummary(
                        id,
                        string.IsNullOrWhiteSpace(workflow.Name) ? id : workflow.Name,
                        workflow.Nodes.Count,
                        workflow.Edges.Count,
                        new DateTimeOffset(mtime, TimeSpan.Zero));
                    _summaryCache[path] = new SummaryCacheEntry(info.Length, mtime, summary);
                    documents.Add(summary);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A damaged document should not prevent other saved workflows from being listed.
                }
            }

            // 已删除的文件不进结果，缓存条目同时清掉，避免长期占用
            foreach (var key in _summaryCache.Keys)
                if (!seen.Contains(key)) _summaryCache.TryRemove(key, out _);

            return documents
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetRevisionAsync(string id, CancellationToken cancellationToken)
    {
        var stored = await LoadWithRevisionAsync(id, cancellationToken);
        return stored?.Revision;
    }

    /// <summary>空修订只允许新建。空引用只用于管理员显式覆盖。</summary>
    public async Task<SaveOutcome> SaveAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken, string? expectedRevision = null)
    {
        var safeId = ValidateId(id);
        var finalPath = Path.Combine(_root, safeId + ".json");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedRevision is not null)
            {
                var expected = expectedRevision.Trim();
                var current = File.Exists(finalPath) ? ComputeContentSha256(finalPath) : string.Empty;
                if (!string.Equals(expected, current, StringComparison.Ordinal))
                    throw Conflict(safeId, expected, current, "Load the latest version to review the other change before deciding to overwrite.");
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                workflow with { Id = id, SchemaVersion = WorkflowDocumentSchema.CurrentVersion }, _json);
            var revision = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var tempPath = Path.Combine(_root, $".{safeId}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await stream.WriteAsync(payload, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, finalPath, overwrite: true);
                _summaryCache.TryRemove(finalPath, out _);
                return new SaveOutcome(revision);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 尽力清理，不影响主流程 */ }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<(WorkflowDefinition Workflow, string? Revision)?> LoadWithRevisionAsync(string id, CancellationToken cancellationToken)
    {
        string safeId;
        try { safeId = ValidateId(id); }
        catch (ArgumentException) { return null; }
        var path = Path.Combine(_root, safeId + ".json");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(bytes, _json);
            if (workflow is null) return null;
            EnsureSupportedSchema(workflow, safeId);
            return (workflow, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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
        var workflow = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, _json, cancellationToken);
        if (workflow is not null) EnsureSupportedSchema(workflow, id);
        return workflow;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var safeId = ValidateId(id);
        var path = Path.Combine(_root, safeId + ".json");
        var legacyRevisionPath = Path.Combine(_root, safeId + ".revision.json");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            _summaryCache.TryRemove(path, out _);
            if (File.Exists(legacyRevisionPath))
            {
                try { File.Delete(legacyRevisionPath); } catch { /* 清理历史遗留文件失败不阻断主删除 */ }
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A document written by a newer host must not be edited here: the editor would drop the parts it
    /// does not understand and saving would destroy them. Failing closed with a 409 lets the operator
    /// upgrade the host (or export the document) instead of silently losing configuration.
    /// </summary>
    internal static void EnsureSupportedSchema(WorkflowDefinition workflow, string id)
    {
        if (workflow.SchemaVersion <= WorkflowDocumentSchema.CurrentVersion) return;
        throw new ApiConflictException(
            $"Workflow '{id}' was written by a newer VisionStudio version (document schema {workflow.SchemaVersion}; " +
            $"this host supports {WorkflowDocumentSchema.CurrentVersion}). Upgrade this host before editing the document.");
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

    /// <summary>A02：当前内容文件的内容哈希（不存在时由调用方判空）——修订与内容必须同代。</summary>
    private static string ComputeContentSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public sealed record SaveOutcome(string Revision)
    {
        // 旧资产提取响应仍包含此字段。
        public bool RevisionRecordDeferred => false;
    }
}
