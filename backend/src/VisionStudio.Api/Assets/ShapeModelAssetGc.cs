using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Abstractions;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Api;

public sealed record ShapeModelGcCandidate(string AssetId, int Version, string Sha256, long SizeBytes);

public sealed record ShapeModelAssetGcPlan(
    IReadOnlyList<ShapeModelGcCandidate> Candidates,
    long ReclaimableBytes,
    int TotalVersions,
    int ProtectedAsLatest,
    int ProtectedByReference);

/// <summary>
/// 未引用版本回收（P1；v25 起作用于通用 assets 表）。
///
/// 注意：候选范围是**所有 kind**，但引用扫描目前只覆盖 shape（match.shape 节点 + 发布清单）。
/// 新增模板图/ONNX 资产类型时，必须同步扩展 <see cref="Collect"/> 与清单捕获，否则它们的
/// 历史版本会被误回收——这是后续接入新 kind 的前置检查项。
/// </summary>
/// <remarks>
/// 未引用版本回收（P1）。
///
/// 保守优先：只回收"确定没人用"的版本，且每个版本的处置都能说清理由。
/// 受保护的三类：
/// <list type="number">
/// <item>每个资产的 latest_version——节点里 modelAssetVersion=0 指向它；</item>
/// <item>活工作流节点显式引用的 (assetId, version)；</item>
/// <item>任一配方版本的工作流（可被回滚重新发布）与**任何历史发布清单**里锁定的 (assetId, version)。</item>
/// </list>
/// 字节是内容寻址的：版本行删除后，只有"没有任何版本再引用"的 blob 才会被删。
/// 预览与执行分离，执行必须显式 confirm。
/// </remarks>
public sealed class ShapeModelAssetGc(IVisionStudioDbFactory dbFactory, WorkflowStore workflows)
{
    public ShapeModelAssetGc(SqliteMetadataDatabase db, WorkflowStore workflows)
        : this(new VisionStudioDbFactory(db), workflows) { }

    public async Task<ShapeModelAssetGcPlan> PlanAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var versions = await db.AssetVersions
            .Select(x => new ShapeModelGcCandidate(x.AssetId, x.Version, x.Sha256, x.SizeBytes))
            .ToListAsync(ct);

        var latest = (await db.Assets.Select(x => new { x.Id, x.LatestVersion }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.LatestVersion, StringComparer.OrdinalIgnoreCase);

        var references = new HashSet<(string AssetId, int Version)>(new RefComparer());
        foreach (var summary in await workflows.ListAsync(ct))
        {
            var workflow = await workflows.LoadAsync(summary.Id, ct);
            if (workflow is not null) Collect(workflow, references);
        }

        // 配方每个版本都可以被回滚重新发布：它们的引用同样受保护。
        foreach (var workflowJson in await db.JobVersions.Select(x => x.WorkflowJson).ToListAsync(ct))
        {
            WorkflowDefinition? workflow = null;
            try { workflow = JsonSerializer.Deserialize<WorkflowDefinition>(workflowJson, Json); }
            catch (JsonException) { /* 损坏的历史版本不参与保护，也不会阻止回收 */ }
            if (workflow is not null) Collect(workflow, references);
        }

        // 历史发布清单锁定的具体版本（含发布时由 0 解析出的 latest）必须保留。
        foreach (var manifestJson in await db.RuntimeDependencyManifests.Select(x => x.ManifestJson).ToListAsync(ct))
        {
            RuntimeDependencyManifest? manifest = null;
            try { manifest = JsonSerializer.Deserialize<RuntimeDependencyManifest>(manifestJson, Json); }
            catch (JsonException) { /* 同上 */ }
            foreach (var pin in manifest?.ModelAssets ?? [])
                references.Add((pin.AssetId, pin.Version));
        }

        var candidates = new List<ShapeModelGcCandidate>();
        var protectedAsLatest = 0;
        var protectedByReference = 0;
        foreach (var version in versions)
        {
            if (latest.TryGetValue(version.AssetId, out var latestVersion) && latestVersion == version.Version)
            {
                protectedAsLatest++;
                continue;
            }
            if (references.Contains((version.AssetId, version.Version)))
            {
                protectedByReference++;
                continue;
            }
            candidates.Add(version);
        }

        return new ShapeModelAssetGcPlan(
            candidates.OrderBy(x => x.AssetId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Version).ToArray(),
            candidates.Sum(x => x.SizeBytes),
            versions.Count,
            protectedAsLatest,
            protectedByReference);
    }

    public async Task<ShapeModelAssetGcPlan> ApplyAsync(CancellationToken ct)
    {
        var plan = await PlanAsync(ct);
        if (plan.Candidates.Count == 0) return plan;

        await using var db = await dbFactory.CreateDbContextAsync(ct, strongDurability: true);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        foreach (var candidate in plan.Candidates)
        {
            await db.AssetVersions
                .Where(x => x.AssetId == candidate.AssetId && x.Version == candidate.Version)
                .ExecuteDeleteAsync(ct);
        }
        // 内容寻址回收：版本行都删掉后，没人引用的字节才清。
        var referencedShas = db.AssetVersions.Select(v => v.Sha256);
        await db.AssetBlobs
            .Where(b => !referencedShas.Contains(b.Sha256))
            .ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return plan;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Collect(WorkflowDefinition workflow, HashSet<(string AssetId, int Version)> references)
    {
        foreach (var node in workflow.Nodes)
        {
            if (node.Parameters is null) continue;
            if (MatchingTemplateAsset.SupportsNode(node.Type))
            {
                // 模板图资产：与模型资产共用同一份回收保护集合。
                if (node.Parameters.TryGetValue("templateAssetId", out var templateId)
                    && templateId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(templateId.GetString()))
                {
                    var templateVersion = node.GetInt("templateAssetVersion", 0);
                    if (templateVersion > 0) references.Add((templateId.GetString()!.Trim(), templateVersion));
                }
                continue;
            }
            if (!BuiltInNodeTypes.Is(node, BuiltInNodeTypes.MatchShape)) continue;
            if (!node.Parameters.TryGetValue("modelAssetId", out var idElement)) continue;
            var assetId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.ToString();
            if (string.IsNullOrWhiteSpace(assetId)) continue;
            var version = node.Parameters.TryGetValue("modelAssetVersion", out var versionElement) && versionElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            // version<=0 指向 latest，由 latest 保护规则覆盖。
            if (version > 0) references.Add((assetId!.Trim(), version));
        }
    }

    private sealed class RefComparer : IEqualityComparer<(string AssetId, int Version)>
    {
        public bool Equals((string AssetId, int Version) x, (string AssetId, int Version) y)
            => x.Version == y.Version && string.Equals(x.AssetId, y.AssetId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string AssetId, int Version) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AssetId), obj.Version);
    }
}
