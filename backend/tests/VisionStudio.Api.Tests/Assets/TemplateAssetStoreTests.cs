using OpenCvSharp;
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

    /// <summary>真实可解码的 PNG：尺寸校验以解码结果为准，随机字节不再是合法输入。</summary>
    private static (string Base64, byte[] Bytes) Png(int width, int height)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mat, new Rect(2, 2, Math.Max(1, width - 4), Math.Max(1, height - 4)), Scalar.All(200));
        Cv2.ImEncode(".png", mat, out var bytes);
        return (Convert.ToBase64String(bytes), bytes);
    }

    [Fact]
    public async Task Create_StoresImageMetadataAndBytes()
    {
        var (env, assets, templates, _) = await CreateAsync();
        using var __ = env;

        var (imageBase64, imageBytes) = Png(64, 48);
        var created = await templates.CreateAsync(new CreateTemplateAssetRequest(
            "tpl-a", "上盖模板", "top", imageBase64, 0, 0, "image/png", "initial", "engineer"), CancellationToken.None);

        Assert.Equal("tpl-a", created.Id);
        Assert.Equal(1, created.LatestVersion);
        var version = Assert.Single(created.Versions);
        Assert.Equal((64, 48), (version.Width, version.Height));
        Assert.Equal("image/png", version.ContentType);
        Assert.Equal(imageBytes, await templates.ReadImageAsync("tpl-a", 1, CancellationToken.None));

        // 物理上是通用 kind=template，且字节在通用表里。
        Assert.Equal("template", (await assets.GetAsync("tpl-a", CancellationToken.None))!.Kind);
    }

    [Fact]
    public async Task Kinds_AreIsolatedBetweenTemplateAndShape()
    {
        var (env, _, templates, shapes) = await CreateAsync();
        using var ___ = env;
        var (imageBase64, _) = Png(32, 24);
        await templates.CreateAsync(new CreateTemplateAssetRequest("tpl-b", "T", null, imageBase64, 0, 0), CancellationToken.None);
        await shapes.CreateAsync(new CreateShapeModelAssetRequest("shape-b", "S", null, Convert.ToBase64String(new byte[32]), "cfg"), CancellationToken.None);

        Assert.Null(await templates.GetAsync("shape-b", CancellationToken.None));
        Assert.Null(await templates.ReadImageAsync("shape-b", 1, CancellationToken.None));
        Assert.Null(await shapes.GetAsync("tpl-b", CancellationToken.None));

        var error = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.AppendVersionAsync("shape-b", new AppendTemplateAssetVersionRequest(Png(8, 8).Base64, 0, 0), CancellationToken.None));
        Assert.Contains("not 'template'", error.Message);
    }

    [Fact]
    public async Task Create_RejectsUnsupportedContentTypeAndBadDimensions()
    {
        var (env, _, templates, _) = await CreateAsync();
        using var ____ = env;

        var (imageBase64, _) = Png(32, 32);
        var typeError = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.CreateAsync(new CreateTemplateAssetRequest("tpl-c", "T", null, imageBase64, 0, 0, "image/gif"), CancellationToken.None));
        Assert.Contains("Unsupported template content type", typeError.Message);

        // 声明尺寸与解码结果不一致必须拒绝，而不是静默改用实际值。
        var sizeError = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.CreateAsync(new CreateTemplateAssetRequest("tpl-d", "T", null, imageBase64, 999, 1), CancellationToken.None));
        Assert.Contains("does not match the decoded image", sizeError.Message);

        var decodeError = await Assert.ThrowsAsync<ApiValidationException>(() =>
            templates.CreateAsync(new CreateTemplateAssetRequest("tpl-e2", "T", null, Convert.ToBase64String(new byte[] { 1, 2, 3 }), 0, 0), CancellationToken.None));
        Assert.Contains("could not be decoded", decodeError.Message);
    }

    [Fact]
    public async Task Engine_Resolver_SeesTemplateBytesThroughTheSharedAssetSource()
    {
        var (env, assets, templates, _) = await CreateAsync();
        using var _____ = env;
        var (imageBase64, bytes) = Png(16, 16);
        await templates.CreateAsync(new CreateTemplateAssetRequest("tpl-e", "T", null, imageBase64, 0, 0), CancellationToken.None);

        // 引擎只认通用资产源：模板图与形状模型走同一条读取路径。
        Assert.Equal(bytes, assets.ReadBytesSync("tpl-e", 0));
        Assert.Equal(bytes, assets.ReadBytesSync("tpl-e", 1));
    }
}
