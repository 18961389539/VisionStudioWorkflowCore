using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Api.Tests;

/// <summary>
/// P1 未引用版本回收。核心是"保守"：latest 永远保留；活工作流、配方各版本工作流、历史发布清单
/// 引用的版本都保留；字节只在没有任何版本再引用时才删（内容寻址去重）。
/// </summary>
public sealed class ShapeModelAssetGcTests
{
    private static async Task<(TempWebHostEnvironment Env, SqliteMetadataDatabase Db, ShapeModelAssetStore Store, ShapeModelAssetGc Gc)>
        CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new ShapeModelAssetStore(db);
        var workflows = new WorkflowStore(env);
        return (env, db, store, new ShapeModelAssetGc(db, workflows));
    }

    private static byte[] Bytes(int seed) => Enumerable.Range(0, 32).Select(i => (byte)((seed + i) % 251)).ToArray();

    private static WorkflowDefinition PinWorkflow(string id, string assetId, int version)
        => new(id, id, [new NodeDefinition("shape-1", "match.shape", null, null, new Dictionary<string, JsonElement>
        {
            ["modelMode"] = JsonSerializer.SerializeToElement("trained"),
            ["modelAssetId"] = JsonSerializer.SerializeToElement(assetId),
            ["modelAssetVersion"] = JsonSerializer.SerializeToElement(version)
        })], []);

    [Fact]
    public async Task Plan_KeepsLatestButOffersOlderUnreferencedVersions()
    {
        var (env, _, store, gc) = await CreateAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-a", "A", null, Convert.ToBase64String(Bytes(1)), "cfg"), CancellationToken.None);
        await store.AppendVersionAsync("shape-a", new AppendShapeModelVersionRequest(Convert.ToBase64String(Bytes(2)), "cfg2"), CancellationToken.None);

        var plan = await gc.PlanAsync(CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(("shape-a", 1), (candidate.AssetId, candidate.Version));
        Assert.Equal(1, plan.ProtectedAsLatest);
        Assert.Equal(0, plan.ProtectedByReference);
    }

    [Fact]
    public async Task Plan_ProtectsVersionsReferencedByLiveWorkflows()
    {
        var (env, _, store, gc) = await CreateAsync();
        using var __ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-b", "B", null, Convert.ToBase64String(Bytes(3)), "cfg"), CancellationToken.None);
        await store.AppendVersionAsync("shape-b", new AppendShapeModelVersionRequest(Convert.ToBase64String(Bytes(4)), "cfg2"), CancellationToken.None);
        var workflows = new WorkflowStore(env);
        await workflows.SaveAsync("wf-b", PinWorkflow("wf-b", "shape-b", 1), CancellationToken.None);

        var plan = await gc.PlanAsync(CancellationToken.None);

        Assert.Empty(plan.Candidates);
        Assert.Equal(1, plan.ProtectedByReference);
    }

    [Fact]
    public async Task Plan_ProtectsVersionsPinnedByAnyHistoricalPublicationManifest()
    {
        var (env, db, store, gc) = await CreateAsync();
        using var ___ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-c", "C", null, Convert.ToBase64String(Bytes(5)), "cfg"), CancellationToken.None);
        await store.AppendVersionAsync("shape-c", new AppendShapeModelVersionRequest(Convert.ToBase64String(Bytes(6)), "cfg2"), CancellationToken.None);

        var manifest = new RuntimeDependencyManifest(
            8, "pinned-manifest", DateTimeOffset.UtcNow,
            new EngineRuntimeDependency("VisionStudio.Engine", "1", "1", "hash"),
            [], [], [], [], [],
            ModelAssets: [new ModelAssetRuntimeDependency("shape-c", 1, "any")]);
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO runtime_dependency_manifests(manifest_hash,schema_version,captured_at,manifest_json) VALUES($hash,8,$at,$json);";
            command.Parameters.AddWithValue("$hash", manifest.ManifestHash);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await command.ExecuteNonQueryAsync();
        }

        var plan = await gc.PlanAsync(CancellationToken.None);

        Assert.Empty(plan.Candidates);
        Assert.True(plan.ProtectedByReference >= 1);
    }

    [Fact]
    public async Task Apply_DeletesCandidateVersionsAndOnlyTrulyOrphanedBlobs()
    {
        var (env, db, store, gc) = await CreateAsync();
        using var _ = env;
        var shared = Bytes(7);
        // 两个资产共用同一份字节（内容寻址去重）；A 追加独有字节的 v2。
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-d", "D", null, Convert.ToBase64String(shared), "cfg"), CancellationToken.None);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-e", "E", null, Convert.ToBase64String(shared), "cfg"), CancellationToken.None);
        await store.AppendVersionAsync("shape-d", new AppendShapeModelVersionRequest(Convert.ToBase64String(Bytes(8)), "cfg2"), CancellationToken.None);

        var plan = await gc.ApplyAsync(CancellationToken.None);

        Assert.Contains(plan.Candidates, x => x.AssetId == "shape-d" && x.Version == 1);
        // A v1 被回收，但同一份字节仍被 E v1 使用：blob 必须保留。
        Assert.Null(await store.GetVersionAsync("shape-d", 1, CancellationToken.None));
        Assert.Equal(Bytes(8), await store.ReadBytesAsync("shape-d", 2, CancellationToken.None));
        Assert.Equal(shared, await store.ReadBytesAsync("shape-e", 1, CancellationToken.None));

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM shape_model_blobs;";
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Apply_IsIdempotentWhenNothingIsReclaimable()
    {
        var (env, _, store, gc) = await CreateAsync();
        using var __ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-f", "F", null, Convert.ToBase64String(Bytes(9)), "cfg"), CancellationToken.None);

        var first = await gc.ApplyAsync(CancellationToken.None);
        var second = await gc.ApplyAsync(CancellationToken.None);

        Assert.Empty(first.Candidates);
        Assert.Empty(second.Candidates);
        Assert.NotNull(await store.ReadBytesAsync("shape-f", 1, CancellationToken.None));
    }
}
