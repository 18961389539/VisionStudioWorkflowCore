using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed record ShapeModelGcCandidate(string AssetId, int Version, string Sha256, long SizeBytes);

public sealed record ShapeModelAssetGcPlan(
    IReadOnlyList<ShapeModelGcCandidate> Candidates,
    long ReclaimableBytes,
    int TotalVersions,
    int ProtectedAsLatest,
    int ProtectedByReference);

/// <summary>
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
/// </summary>
public sealed class ShapeModelAssetGc(SqliteMetadataDatabase db, WorkflowStore workflows)
{
    public async Task<ShapeModelAssetGcPlan> PlanAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var versions = new List<ShapeModelGcCandidate>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT asset_id, version, sha256, size_bytes FROM shape_model_versions;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                versions.Add(new ShapeModelGcCandidate(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3)));
        }

        var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, latest_version FROM shape_model_assets;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) latest[reader.GetString(0)] = reader.GetInt32(1);
        }

        var references = new HashSet<(string AssetId, int Version)>(new RefComparer());
        foreach (var summary in await workflows.ListAsync(ct))
        {
            var workflow = await workflows.LoadAsync(summary.Id, ct);
            if (workflow is not null) Collect(workflow, references);
        }

        await using (var command = connection.CreateCommand())
        {
            // 配方每个版本都可以被回滚重新发布：它们的引用同样受保护。
            command.CommandText = "SELECT workflow_json FROM job_versions;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                WorkflowDefinition? workflow = null;
                try { workflow = JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(0), Json); }
                catch (JsonException) { /* 损坏的历史版本不参与保护，也不会阻止回收 */ }
                if (workflow is not null) Collect(workflow, references);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            // 历史发布清单锁定的具体版本（含发布时由 0 解析出的 latest）必须保留。
            command.CommandText = "SELECT manifest_json FROM runtime_dependency_manifests;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                RuntimeDependencyManifest? manifest = null;
                try { manifest = JsonSerializer.Deserialize<RuntimeDependencyManifest>(reader.GetString(0), Json); }
                catch (JsonException) { /* 同上 */ }
                foreach (var pin in manifest?.ModelAssets ?? [])
                    references.Add((pin.AssetId, pin.Version));
            }
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

        await using var connection = await db.OpenConnectionAsync(ct, strongDurability: true);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var candidate in plan.Candidates)
        {
            await using var deleteVersion = connection.CreateCommand();
            deleteVersion.Transaction = transaction;
            deleteVersion.CommandText = "DELETE FROM shape_model_versions WHERE asset_id=$id AND version=$version;";
            deleteVersion.Parameters.AddWithValue("$id", candidate.AssetId);
            deleteVersion.Parameters.AddWithValue("$version", candidate.Version);
            await deleteVersion.ExecuteNonQueryAsync(ct);
        }
        // 内容寻址回收：版本行都删掉后，没人引用的字节才清。
        await using (var deleteBlobs = connection.CreateCommand())
        {
            deleteBlobs.Transaction = transaction;
            deleteBlobs.CommandText = "DELETE FROM shape_model_blobs WHERE sha256 NOT IN (SELECT sha256 FROM shape_model_versions);";
            await deleteBlobs.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return plan;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Collect(WorkflowDefinition workflow, HashSet<(string AssetId, int Version)> references)
    {
        foreach (var node in workflow.Nodes)
        {
            if (!node.Type.Equals("match.shape", StringComparison.OrdinalIgnoreCase) || node.Parameters is null) continue;
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
