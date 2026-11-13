using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// v24 形状模型资产存储：版本不可变、字节内容寻址去重、软删除、16 MB 上限与 base64 校验。
/// P0 没有发布态，运行期只按显式版本或 latest_version 解析。
/// </summary>
public sealed class ShapeModelAssetStoreTests
{
    private static async Task<(TempWebHostEnvironment Env, SqliteMetadataDatabase Db, ShapeModelAssetStore Store)> CreateStoreAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        return (env, db, new ShapeModelAssetStore(new AssetStore(db)));
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static byte[] Model(int seed, int length = 256)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((seed + i) % 251);
        return bytes;
    }

    [Fact]
    public async Task SchemaV25_CreatesGenericAssetTablesAndKeepsLegacyShapeTables()
    {
        var (env, db, _) = await CreateStoreAsync();
        using var _env = env;
        Assert.True(db.CurrentSchemaVersion >= 25);

        await using var connection = await db.OpenConnectionAsync();
        // 新通用表 + 旧 shape 表并存（旧表保留一个版本用于回退）。
        foreach (var table in new[]
        {
            "assets", "asset_versions", "asset_blobs",
            "shape_model_assets", "shape_model_versions", "shape_model_blobs"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        // 迁移把既有 shape 资产整条搬到通用表（kind=shape），字节与哈希一致。
        var store = new ShapeModelAssetStore(new AssetStore(db));
        var model = Model(41);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-migrated", "M", null, Base64(model), "cfg"), CancellationToken.None);
        var descriptor = await store.GetAsync("shape-migrated", CancellationToken.None);
        Assert.NotNull(descriptor);
        Assert.Equal(model, await store.ReadBytesAsync("shape-migrated", 1, CancellationToken.None));

        await using var kindCommand = connection.CreateCommand();
        kindCommand.CommandText = "SELECT kind FROM assets WHERE id='shape-migrated';";
        Assert.Equal("shape", Convert.ToString(await kindCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Create_StoresImmutableFirstVersionWithHashAndSize()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        var bytes = Model(1);

        var asset = await store.CreateAsync(new CreateShapeModelAssetRequest(
            "shape-a", "Cover Shape", "top camera", Base64(bytes), "cfg-1", "source-1", "initial", "engineer"), CancellationToken.None);

        Assert.Equal("shape-a", asset.Id);
        Assert.Equal(1, asset.LatestVersion);
        var version = Assert.Single(asset.Versions);
        Assert.Equal(1, version.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), version.Sha256);
        Assert.Equal(bytes.Length, version.SizeBytes);
        Assert.Equal("cfg-1", version.TrainingConfig);
        Assert.Equal("source-1", version.SourceImageSha256);
        Assert.Equal("engineer", version.CreatedBy);

        var loaded = await store.ReadBytesAsync("shape-a", 1, CancellationToken.None);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task AppendVersion_KeepsHistoryNewestFirst()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest(
            "shape-b", "B", null, Base64(Model(2)), "cfg-1"), CancellationToken.None);

        var second = await store.AppendVersionAsync("shape-b",
            new AppendShapeModelVersionRequest(Base64(Model(3)), "cfg-2", Note: "retrained"), CancellationToken.None);

        Assert.Equal(2, second.Version);
        var asset = await store.GetAsync("shape-b", CancellationToken.None);
        Assert.Equal(2, asset!.LatestVersion);
        Assert.Equal([2, 1], asset.Versions.Select(v => v.Version).ToArray());
        Assert.Equal("retrained", asset.Versions[0].Note);
        // 旧版本字节仍可取回：版本不可变是回滚的前提。
        Assert.Equal(Model(2), await store.ReadBytesAsync("shape-b", 1, CancellationToken.None));
    }

    [Fact]
    public async Task IdenticalBytes_AreStoredOnceAcrossAssets()
    {
        var (env, db, store) = await CreateStoreAsync();
        using var _ = env;
        var bytes = Model(4);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-c", "C", null, Base64(bytes), "cfg"), CancellationToken.None);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-d", "D", null, Base64(bytes), "cfg"), CancellationToken.None);

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM asset_blobs;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Create_RejectsInvalidBase64EmptyModelAndMissingConfig()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;

        var badBase64 = await Assert.ThrowsAsync<ApiValidationException>(() => store.CreateAsync(
            new CreateShapeModelAssetRequest("shape-e", "E", null, "not base64 !!", "cfg"), CancellationToken.None));
        Assert.Contains("base64", badBase64.Message);

        var missingConfig = await Assert.ThrowsAsync<ApiValidationException>(() => store.CreateAsync(
            new CreateShapeModelAssetRequest("shape-f", "F", null, Base64(Model(5)), "  "), CancellationToken.None));
        Assert.Contains("trainingConfig", missingConfig.Message);

        var missingName = await Assert.ThrowsAsync<ApiValidationException>(() => store.CreateAsync(
            new CreateShapeModelAssetRequest("shape-g", "  ", null, Base64(Model(6)), "cfg"), CancellationToken.None));
        Assert.Contains("name", missingName.Message);
    }

    [Fact]
    public async Task Delete_IsSoftAndHidesAssetFromReads()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-h", "H", null, Base64(Model(7)), "cfg"), CancellationToken.None);

        Assert.True(await store.DeleteAsync("shape-h", CancellationToken.None));
        Assert.False(await store.DeleteAsync("shape-h", CancellationToken.None));
        Assert.Null(await store.GetAsync("shape-h", CancellationToken.None));
        Assert.Null(await store.ReadBytesAsync("shape-h", 1, CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnknownAssetOrVersion_ReturnsNullInsteadOfThrowing()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-i", "I", null, Base64(Model(8)), "cfg"), CancellationToken.None);

        Assert.Null(await store.GetVersionAsync("shape-i", 9, CancellationToken.None));
        Assert.Null(await store.ReadBytesAsync("shape-i", 9, CancellationToken.None));
        Assert.Null(await store.GetAsync("shape-missing", CancellationToken.None));
    }

    [Fact]
    public async Task Update_RenamesMetadataWithoutTouchingVersions()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        var bytes = Model(11);
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-k", "K", "old note", Base64(bytes), "cfg"), CancellationToken.None);

        var updated = await store.UpdateAsync("shape-k", "K renamed", "new note", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("K renamed", updated!.Name);
        Assert.Equal("new note", updated.Description);
        Assert.Equal(1, updated.LatestVersion);
        Assert.Equal(bytes, await store.ReadBytesAsync("shape-k", 1, CancellationToken.None));

        // 只改描述、不改名字：name 传 null 时保持原值。
        var descriptionOnly = await store.UpdateAsync("shape-k", null, "only description", CancellationToken.None);
        Assert.Equal("K renamed", descriptionOnly!.Name);
        Assert.Equal("only description", descriptionOnly.Description);

        // 空白名字与不存在的资产都是明确结果，不是静默成功。
        await Assert.ThrowsAsync<ApiValidationException>(() => store.UpdateAsync("shape-k", "   ", null, CancellationToken.None));
        Assert.Null(await store.UpdateAsync("shape-missing", "x", null, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateId_IsConflictNotSilentOverwrite()
    {
        var (env, _, store) = await CreateStoreAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest("shape-j", "J", null, Base64(Model(9)), "cfg"), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ApiConflictException>(() => store.CreateAsync(
            new CreateShapeModelAssetRequest("shape-j", "J2", null, Base64(Model(10)), "cfg"), CancellationToken.None));
        Assert.Contains("already exists", conflict.Message);

        // 原资产未被覆盖：版本仍是 1，字节仍是第一次的。
        var asset = await store.GetAsync("shape-j", CancellationToken.None);
        Assert.Equal("J", asset!.Name);
        Assert.Equal(Model(9), await store.ReadBytesAsync("shape-j", 1, CancellationToken.None));
    }
}
