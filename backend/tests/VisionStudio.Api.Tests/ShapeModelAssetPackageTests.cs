using System.Security.Cryptography;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// .vsasset 往返：包自校验（sha256 必须与字节一致）、导入永远创建新资产、篡改即拒绝。
/// 这是跨项目分发的入口，所以"包被改过"必须比"包不存在"更早被发现。
/// </summary>
public sealed class ShapeModelAssetPackageTests
{
    private static async Task<(TempWebHostEnvironment Env, ShapeModelAssetStore Store, ShapeModelAssetPackageService Packages)>
        CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new ShapeModelAssetStore(db);
        return (env, store, new ShapeModelAssetPackageService(store));
    }

    private static byte[] Bytes(int seed) => Enumerable.Range(0, 64).Select(i => (byte)((seed + i) % 251)).ToArray();

    [Fact]
    public async Task Export_ProducesASelfConsistentPackage()
    {
        var (env, store, packages) = await CreateAsync();
        using var _ = env;
        var model = Bytes(1);
        await store.CreateAsync(new CreateShapeModelAssetRequest(
            "shape-pkg", "Pkg", "desc", Convert.ToBase64String(model), "cfg-1", "src-1", "note", "engineer"), CancellationToken.None);

        var (package, bytes) = await packages.ExportAsync("shape-pkg", 1, CancellationToken.None);

        Assert.Equal(ShapeModelAssetPackage.CurrentFormat, package.Format);
        Assert.Equal("shape-pkg", package.SourceAssetId);
        Assert.Equal(1, package.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(model)), package.Sha256);
        Assert.Equal(model.Length, package.SizeBytes);
        Assert.Equal("cfg-1", package.TrainingConfig);
        Assert.Equal(model, Convert.FromBase64String(package.ModelBase64));
        // 导出体是合法 JSON，且能反序列化回同一个包。
        Assert.Equal(package.Sha256, System.Text.Json.JsonSerializer.Deserialize<ShapeModelAssetPackage>(bytes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Sha256);
    }

    [Fact]
    public async Task Import_RoundTripsBytesIntoANewAsset()
    {
        var (env, store, packages) = await CreateAsync();
        using var __ = env;
        var model = Bytes(2);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-src", "Source", null, Convert.ToBase64String(model), "cfg"), CancellationToken.None);
        var (package, _) = await packages.ExportAsync("shape-src", 1, CancellationToken.None);

        var imported = await packages.ImportAsync(package, CancellationToken.None);

        Assert.NotEqual("shape-src", imported.Id);
        Assert.Contains("from shape-src v1", imported.Name);
        Assert.Equal(1, imported.LatestVersion);
        Assert.Equal(package.Sha256, imported.Versions[0].Sha256);
        Assert.Equal(model, await store.ReadBytesAsync(imported.Id, 1, CancellationToken.None));
        // 原资产不受影响：导入是新增，不是覆盖。
        Assert.Equal(model, await store.ReadBytesAsync("shape-src", 1, CancellationToken.None));
    }

    [Fact]
    public async Task Import_RejectsTamperedBytes()
    {
        var (env, _, packages) = await CreateAsync();
        using var ___ = env;
        var package = new ShapeModelAssetPackage(
            ShapeModelAssetPackage.CurrentFormat, "shape-x", "Tampered", null, 1,
            Sha256: "DEADBEEF", SizeBytes: 4, TrainingConfig: "cfg",
            SourceImageSha256: null, Note: null, CreatedBy: null,
            ModelBase64: Convert.ToBase64String(new byte[4]));

        var error = await Assert.ThrowsAsync<ApiValidationException>(() => packages.ImportAsync(package, CancellationToken.None));
        Assert.Contains("integrity check failed", error.Message);
    }

    [Fact]
    public async Task Import_RejectsUnknownFormatAndMissingHash()
    {
        var (env, _, packages) = await CreateAsync();
        using var ____ = env;
        var wrongFormat = new ShapeModelAssetPackage("visionstudio.model-asset.v0", "a", "A", null, 1, "X", 4, "cfg", null, null, null, "AAAA");
        var formatError = await Assert.ThrowsAsync<ApiValidationException>(() => packages.ImportAsync(wrongFormat, CancellationToken.None));
        Assert.Contains("Unsupported model asset package format", formatError.Message);

        var missingHash = wrongFormat with { Format = ShapeModelAssetPackage.CurrentFormat, Sha256 = "  " };
        var hashError = await Assert.ThrowsAsync<ApiValidationException>(() => packages.ImportAsync(missingHash, CancellationToken.None));
        Assert.Contains("missing its sha256", hashError.Message);
    }
}
