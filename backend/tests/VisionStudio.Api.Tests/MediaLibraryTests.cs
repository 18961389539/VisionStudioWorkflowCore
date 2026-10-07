using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Api.Media;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api.Tests;

public sealed class MediaLibraryTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    [Fact]
    public void ResolveSource_RejectsAbsoluteAndTraversalPaths()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        Assert.Throws<ApiValidationException>(() => media.ResolveSource(Path.GetFullPath(Path.Combine(env.ContentRootPath, "outside.png"))));
        Assert.Throws<ApiValidationException>(() => media.ResolveSource("media://../outside.png"));
        Assert.Throws<ApiValidationException>(() => media.ResolveSource("file:///tmp/outside.png"));
    }

    [Fact]
    public async Task Import_StoresOnlyLogicalMediaReferenceInsideRoot()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var bytes = new MemoryStream(TinyPng);
        var item = await media.ImportAsync("line-a", "NG", "bad-part.png", bytes, bytes.Length, default);

        Assert.Equal("line-a", item.Collection);
        Assert.Equal("NG", item.Label);
        Assert.Equal("media://line-a/NG/bad-part.png", item.Source);
        Assert.DoesNotContain(env.ContentRootPath, item.Source, StringComparison.OrdinalIgnoreCase);
        var resolved = media.ResolveSource(item.Source);
        Assert.True(File.Exists(resolved.PhysicalPath));
        Assert.StartsWith(Path.Combine(env.ContentRootPath, "data", "media"), resolved.PhysicalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileCameraSourceFingerprint_ChangesWhenMediaBytesChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-media-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "sample.png");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            var camera = new FileCameraDevice("file-test", "File", "media://test", root);
            var first = camera.GetSourceProvenance();
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            var second = camera.GetSourceProvenance();

            Assert.NotEqual(first.ContentSha256, second.ContentSha256);
            Assert.Equal(1, second.ItemCount);
            Assert.Equal(4, second.TotalBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    [Fact]
    public async Task Import_RejectsContentThatDoesNotMatchImageExtension()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var bytes = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        await Assert.ThrowsAsync<ApiValidationException>(() => media.ImportAsync("bad", "Unlabeled", "fake.png", bytes, bytes.Length, default));
        Assert.Empty(media.ListItems("bad", null));
    }

    [Fact]
    public void FileCameraDevice_RequiresLogicalMediaSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-media-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<ArgumentException>(() => new FileCameraDevice("bad", "Bad", root, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Import_DuplicateFilenameCreatesNewItemInsteadOfOverwriting()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var firstBytes = new MemoryStream(TinyPng);
        using var secondBytes = new MemoryStream(TinyPng);
        var first = await media.ImportAsync("repeat", "OK", "sample.png", firstBytes, firstBytes.Length, default);
        var second = await media.ImportAsync("repeat", "OK", "sample.png", secondBytes, secondBytes.Length, default);

        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Equal(2, media.ListItems("repeat", "OK").Count);
        Assert.Equal(TinyPng, File.ReadAllBytes(media.ResolveItemPath(first.RelativePath)));
    }

    [Fact]
    public async Task RuntimeManifest_ReportsFileCameraMediaContentDrift()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using (var bytes = new MemoryStream(TinyPng))
            await media.ImportAsync("production-set", "NG", "part.png", bytes, bytes.Length, default);
        var source = media.ResolveSource("media://production-set");

        var cameras = new CameraManager();
        cameras.Register(new FileCameraDevice("file-1", "File Camera", source.Source, source.PhysicalPath));
        var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.acquire"), new AcquireImageNode(cameras), "builtin");
        var plugins = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        var db = new SqliteMetadataDatabase(env);
        var devices = new DeviceManager();
        var robots = new RobotManager();
        var hardware = new HardwareProvenanceService(
            new HardwareProvenanceStore(Path.Combine(env.ContentRootPath, "data", "provenance")), cameras, devices, robots, new VendorProvenanceProbeRegistry());
        var dependencies = new RuntimeDependencyManifestService(
            registry, plugins, cameras, devices,
            new DeviceProfileStore(Path.Combine(env.ContentRootPath, "data", "devices")),
            robots, new CalibrationAssetStore(db, new CalibrationWorkspaceService()), hardware);
        var workflow = new WorkflowDefinition("file-camera", "File Camera", [new NodeDefinition("acquire", "image.acquire", "Acquire", null, new Dictionary<string, JsonElement> { ["cameraId"] = JsonSerializer.SerializeToElement("file-1") })], []);

        var published = await dependencies.CaptureAsync(workflow, default);
        var cameraDependency = Assert.Single(published.Cameras);
        Assert.False(string.IsNullOrWhiteSpace(cameraDependency.SourceFingerprint));

        using (var append = new FileStream(media.ResolveItemPath("production-set/NG/part.png"), FileMode.Append, FileAccess.Write, FileShare.None)) append.WriteByte(0x00);
        var validation = await dependencies.ValidateAsync(published, workflow, default);

        Assert.False(validation.Compatible);
        Assert.Contains(validation.Drifts, x => x.Kind == "Camera" && x.Id == "file-1");
    }

    private static MediaLibraryService CreateMedia(TempWebHostEnvironment env)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaLibrary:RootPath"] = "data/media",
                ["MediaLibrary:MaxImportBytes"] = "1048576",
                ["MediaLibrary:MaxBatchFiles"] = "20",
                ["MediaLibrary:MaxBatchBytes"] = "4194304"
            }).Build();
        return new MediaLibraryService(env, configuration);
    }
}
