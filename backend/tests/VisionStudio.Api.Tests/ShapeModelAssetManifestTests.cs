using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisionStudio.Api;
using VisionStudio.Abstractions;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api.Tests;

/// <summary>
/// P1：模型资产进入运行时依赖清单（schema 8）。发布时把 (assetId, version, sha256) 锁定，
/// version=0 就地解析成具体版本；运行前校验逐项比对，漂移即拒绝。
/// 不含资产的流程必须继续走旧 schema，历史 ManifestHash 不变。
/// </summary>
public sealed class ShapeModelAssetManifestTests
{
    private static async Task<(TempWebHostEnvironment Env, RuntimeDependencyManifestService Service, ShapeModelAssetStore Store)>
        CreateAsync()
    {
        var env = new TempWebHostEnvironment();
        var db = new SqliteMetadataDatabase(env);
        await db.EnsureInitializedAsync();
        var store = new ShapeModelAssetStore(db);
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("match.shape"), new ShapeMatchNode(), "builtin");
        var cameras = new CameraManager();
        var devices = new DeviceManager();
        var robots = new RobotManager();
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var provenance = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")),
            cameras, devices, robots, new VendorProvenanceProbeRegistry());
        var service = new RuntimeDependencyManifestService(
            registry, plugins, cameras, devices,
            new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices")), robots,
            new CalibrationAssetStore(db, new CalibrationWorkspaceService()), provenance,
            modelAssets: store);
        return (env, service, store);
    }

    private static WorkflowDefinition Workflow(params (string Id, string? AssetId, int Version)[] nodes)
        => new("manifest-wf", "Manifest",
            nodes.Select(x => new NodeDefinition(x.Id, "match.shape", null, null, new Dictionary<string, JsonElement>
            {
                ["modelMode"] = JsonSerializer.SerializeToElement("trained"),
                ["modelAssetId"] = JsonSerializer.SerializeToElement(x.AssetId ?? string.Empty),
                ["modelAssetVersion"] = JsonSerializer.SerializeToElement(x.Version)
            })).ToArray(), []);

    [Fact]
    public async Task Capture_PinsLatestVersionToAConcreteNumberAndSha()
    {
        var (env, service, store) = await CreateAsync();
        using var _ = env;
        var created = await store.CreateAsync(new CreateShapeModelAssetRequest(
            "shape-a", "A", null, Convert.ToBase64String(new byte[32]), "cfg"), CancellationToken.None);

        var manifest = await service.CaptureAsync(Workflow(("shape-1", "shape-a", 0)), CancellationToken.None);

        Assert.Equal(8, manifest.SchemaVersion);
        var pin = Assert.Single(manifest.ModelAssets!);
        Assert.Equal("shape-a", pin.AssetId);
        Assert.Equal(created.LatestVersion, pin.Version);
        Assert.Equal(created.Versions[0].Sha256, pin.Sha256);

        // 再捕获一次哈希必须稳定：清单是内容寻址的，不含时间戳。
        var again = await service.CaptureAsync(Workflow(("shape-1", "shape-a", 0)), CancellationToken.None);
        Assert.Equal(manifest.ManifestHash, again.ManifestHash);
    }

    [Fact]
    public async Task Capture_LeavesAssetFreeWorkflowsOnTheLegacySchema()
    {
        var (env, service, _) = await CreateAsync();
        using var __ = env;
        var manifest = await service.CaptureAsync(Workflow(("shape-1", null, 0)), CancellationToken.None);

        Assert.NotEqual(8, manifest.SchemaVersion);
        Assert.Null(manifest.ModelAssets);
    }

    [Fact]
    public async Task Validate_ReportsDriftWhenThePinnedVersionIsNoLongerTheOneInTheWorkflow()
    {
        var (env, service, store) = await CreateAsync();
        using var _ = env;
        await store.CreateAsync(new CreateShapeModelAssetRequest(
            "shape-b", "B", null, Convert.ToBase64String(new byte[24]), "cfg"), CancellationToken.None);
        await store.AppendVersionAsync("shape-b",
            new AppendShapeModelVersionRequest(Convert.ToBase64String(new byte[25]), "cfg2"), CancellationToken.None);

        var published = await service.CaptureAsync(Workflow(("shape-1", "shape-b", 1)), CancellationToken.None);
        Assert.Equal(1, published.ModelAssets!.Single().Version);

        // 发布后流程被改成引用 v2：清单里锁的还是 v1，必须报 ModelAsset 漂移而不是静默用新模型。
        var validation = await service.ValidateAsync(published, Workflow(("shape-1", "shape-b", 2)), CancellationToken.None);

        Assert.False(validation.Compatible);
        Assert.Contains(validation.Drifts, x => x.Kind == "ModelAsset");
    }

    [Fact]
    public async Task Capture_RejectsMissingAssetsInsteadOfPublishingABrokenPin()
    {
        var (env, service, _) = await CreateAsync();
        using var __ = env;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CaptureAsync(Workflow(("shape-1", "shape-missing", 0)), CancellationToken.None));
        Assert.Contains("shape-missing", error.Message);
        Assert.Contains("does not exist", error.Message);
    }
}
