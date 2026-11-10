namespace VisionStudio.Api.Tests;

/// <summary>
/// 通用资产层（P2）：kind 隔离、任意 metadata 往返、同步读取解析 latest。
/// shape 适配器只能看到 kind=shape 的资产——这是防止"模板资产被形状节点误读"的关键。
/// </summary>
public sealed class AssetStoreTests
{
    private static async Task<(TempWebHostEnvironment Env, AssetStore Assets, ShapeModelAssetStore Shapes)> CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var assets = new AssetStore(db);
        return (env, assets, new ShapeModelAssetStore(assets));
    }

    private static string Base64(int seed) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)((seed + i) % 251)).ToArray());

    [Fact]
    public async Task Kinds_AreIsolatedFromEachOther()
    {
        var (env, assets, shapes) = await CreateAsync();
        using var _ = env;

        await assets.CreateAsync(new CreateAssetRequest(null, "template", "T", null, """{"roi":"full"}""", Base64(1)), CancellationToken.None);
        await shapes.CreateAsync(new CreateShapeModelAssetRequest("shape-a", "S", null, Base64(2), "cfg"), CancellationToken.None);

        var templates = await assets.ListAsync("template", CancellationToken.None);
        var shapeList = await assets.ListAsync("shape", CancellationToken.None);
        Assert.Single(templates);
        Assert.Single(shapeList);
        Assert.Equal("template", templates[0].Kind);

        // 形状适配器看不到、也读不到别的 kind。
        Assert.Null(await shapes.GetAsync(templates[0].Id, CancellationToken.None));
        Assert.Null(await shapes.ReadBytesAsync(templates[0].Id, 1, CancellationToken.None));
        var error = await Assert.ThrowsAsync<VisionStudio.Api.Infrastructure.ApiValidationException>(() =>
            shapes.AppendVersionAsync(templates[0].Id, new AppendShapeModelVersionRequest(Base64(3), "cfg"), CancellationToken.None));
        Assert.Contains("not 'shape'", error.Message);
    }

    [Fact]
    public async Task GenericMetadata_RoundTripsArbitraryJson()
    {
        var (env, assets, _) = await CreateAsync();
        using var __ = env;
        const string metadata = """{"threshold":0.75,"labels":["a","b"],"nested":{"roi":[1,2,3,4]}}""";

        var created = await assets.CreateAsync(new CreateAssetRequest("tpl-1", "template", "Template", "desc", metadata, Base64(4), "note", "engineer"), CancellationToken.None);

        Assert.Equal("template", created.Kind);
        var version = await assets.GetVersionAsync("tpl-1", 1, CancellationToken.None);
        Assert.NotNull(version);
        Assert.Equal(metadata, version!.MetadataJson);
        Assert.Equal("engineer", version.CreatedBy);
    }

    [Fact]
    public async Task SyncRead_ResolvesLatestVersion()
    {
        var (env, assets, _) = await CreateAsync();
        using var ___ = env;
        await assets.CreateAsync(new CreateAssetRequest("tpl-2", "template", "T", null, "{}", Base64(5)), CancellationToken.None);
        await assets.AppendVersionAsync("tpl-2", new AppendAssetVersionRequest("{}", Base64(6)), CancellationToken.None);

        Assert.Equal(Convert.FromBase64String(Base64(6)), assets.ReadBytesSync("tpl-2", 0));
        Assert.Equal(Convert.FromBase64String(Base64(5)), assets.ReadBytesSync("tpl-2", 1));
        Assert.Null(assets.ReadBytesSync("missing", 0));
    }

    [Fact]
    public async Task InvalidKindAndMetadata_AreRejected()
    {
        var (env, assets, _) = await CreateAsync();
        using var ____ = env;

        await Assert.ThrowsAsync<VisionStudio.Api.Infrastructure.ApiValidationException>(() =>
            assets.CreateAsync(new CreateAssetRequest(null, "bad kind!", "x", null, "{}", Base64(7)), CancellationToken.None));
        await Assert.ThrowsAsync<VisionStudio.Api.Infrastructure.ApiValidationException>(() =>
            assets.CreateAsync(new CreateAssetRequest(null, "template", "x", null, "[1,2,3]", Base64(8)), CancellationToken.None));
        await Assert.ThrowsAsync<VisionStudio.Api.Infrastructure.ApiValidationException>(() =>
            assets.CreateAsync(new CreateAssetRequest(null, "template", "x", null, "not json", Base64(9)), CancellationToken.None));
    }
}
