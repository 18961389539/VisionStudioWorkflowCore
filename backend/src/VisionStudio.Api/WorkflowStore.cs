using VisionStudio.Api.Infrastructure;
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

    public async Task SaveAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        var safeId = ValidateId(id);
        var finalPath = Path.Combine(_root, safeId + ".json");
        // 原子写入：先写临时文件再整体替换。旧的 File.Create 直写方式在取消/异常时会留下截断的半截文件。
        var tempPath = Path.Combine(_root, $".{safeId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, workflow with { Id = id }, _json, cancellationToken);
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 尽力清理，不影响主流程 */ }
        }
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
}
