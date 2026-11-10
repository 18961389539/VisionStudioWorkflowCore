using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 模板图资产（P2，kind=template）：验证通用资产层对第二种类型的复用，
/// 以及两种 kind 的读隔离——形状适配器不能读到模板图，反之亦然。
/// </summary>
public sealed class TemplateAssetStoreTests
{
    private static async Task<(TempWebHostEnvironment Env, AssetStore Assets, TemplateAssetStore Templates, ShapeModelAssetStore Shapes)>
        CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var assets = new AssetStore(db);
        return (env, assets, new TemplateAssetStore(assets), new ShapeModelAssetStore(assets));
    }

    private static string Base64(int length = 64) => Convert.ToBase64String(Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray());

    [Fact]
    public async Task Create_StoresImageMetadataAndBytes()
    {
        var (env, assets, templates, _) = await CreateAsync();
        using var __ = env;

        var created = await templates.CreateAsync(new CreateTemplateAssetRequest(
            "tpl-a", "上盖模板", "top", Base64(), 1024, 768, "image/png", "initial", "engineer"), CancellationToken.None);

        Assert.Equal("tpl-a", created.Id);
        Assert.Equal(1, created.LatestVersion);
        var version = Assert.Single(created.Versions);
        Assert.Equal((1024, 768), (version.Width, version.Height));
        Assert.Equal("image/png", version.ContentType);
        Assert.Equal(Convert.FromBase64String(Base64()), await templates.ReadImageAsync("tpl-a", 1, CancellationToken.None));

        // 物理上是通用 kind=template，且字节在通用表里。
        Assert.Equal("template", (await assets.GetAsync("tpl-a", CancellationToken.None))!.Kind);
    }

    [Fact]
    public async Task Kinds_AreIsolatedBetweenTemplateAndShape()
    {
        var (env, _, templates, shapes) = await CreateAsync();
        using var ___ = env;
        await templates.CreateAsync(new CreateTemplateAssetRequest("tpl-b", "T", null, Base64(), 320, 240), CancellationToken.None);
        await shapes.CreateAsync(new CreateShapeModelAssetRequest("shape-b", "S", null, Base64(32), "cfg"), CancellationToken.None);

        Assert.Null(await templates.GetAsync("shape-b", CancellationToken.None));
        Assert.Null(await templates.ReadImageAsync("shape-b", 1, CancellationToken.None));
        Assert.Null(await shapes.GetAsync("tpl-b", CancellationToken.None));

        var error = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.AppendVersionAsync("shape-b", new AppendTemplateAssetVersionRequest(Base64(), 10, 10), CancellationToken.None));
        Assert.Contains("not 'template'", error.Message);
    }

    [Fact]
    public async Task Create_RejectsUnsupportedContentTypeAndBadDimensions()
    {
        var (env, _, templates, _) = await CreateAsync();
        using var ____ = env;

        var typeError = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.CreateAsync(new CreateTemplateAssetRequest("tpl-c", "T", null, Base64(), 100, 100, "image/gif"), CancellationToken.None));
        Assert.Contains("Unsupported template content type", typeError.Message);

        var sizeError = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.CreateAsync(new CreateTemplateAssetRequest("tpl-d", "T", null, Base64(), 0, 100), CancellationToken.None));
        Assert.Contains("width/height", sizeError.Message);
    }

    [Fact]
    public async Task Engine_Resolver_SeesTemplateBytesThroughTheSharedAssetSource()
    {
        var (env, assets, templates, _) = await CreateAsync();
        using var _____ = env;
        var bytes = Convert.FromBase64String(Base64());
        await templates.CreateAsync(new CreateTemplateAssetRequest("tpl-e", "T", null, Base64(), 64, 64), CancellationToken.None);

        // 引擎只认通用资产源：模板图与形状模型走同一条读取路径。
        Assert.Equal(bytes, assets.ReadBytesSync("tpl-e", 0));
        Assert.Equal(bytes, assets.ReadBytesSync("tpl-e", 1));
    }
}
