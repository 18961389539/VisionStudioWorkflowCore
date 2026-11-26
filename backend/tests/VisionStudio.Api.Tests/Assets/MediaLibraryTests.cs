using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Api.Media;
using VisionStudio.Abstractions;
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
        Assert.Empty(media.ListItems("bad", null).Items);
    }

    [Fact]
    public async Task Import_RejectsPngHeaderWithoutDecodableImage()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        var truncated = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
        using var bytes = new MemoryStream(truncated);

        await Assert.ThrowsAsync<ApiValidationException>(
            () => media.ImportAsync("bad", "Unlabeled", "truncated.png", bytes, bytes.Length, default));
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
        Assert.Equal(2, media.ListItems("repeat", "OK").Items.Count);
        Assert.Equal(TinyPng, File.ReadAllBytes(media.ResolveItemPath(first.RelativePath)));
    }

    [Fact]
    public async Task Import_PreservesRelativePathAndIsIdempotentWhenRequested()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var firstBytes = new MemoryStream(TinyPng);
        var first = await media.ImportAsync(
            "batch", "OK", "camera-a/day-1/sample.png", firstBytes, firstBytes.Length, default, idempotentRelativePath: true);
        using var secondBytes = new MemoryStream(TinyPng);
        var second = await media.ImportAsync(
            "batch", "OK", "camera-a/day-1/sample.png", secondBytes, secondBytes.Length, default, idempotentRelativePath: true);

        Assert.Equal("media://batch/OK/camera-a/day-1/sample.png", first.Source);
        Assert.Equal(first.RelativePath, second.RelativePath);
        Assert.Single(media.ListItems("batch", "OK").Items);
    }

    [Fact]
    public async Task LinkedDirectory_IsPersistedAndListedAsReadOnlySource()
    {
        using var env = new TempWebHostEnvironment();
        var resolver = new AllowAllResolver();
        var media = CreateMedia(env, resolver);
        var linkedRoot = Path.Combine(Path.GetTempPath(), "visionstudio-linked-media", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(linkedRoot, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(linkedRoot, "nested", "frame.png"), TinyPng);
        try
        {
            var linked = await media.LinkDirectoryAsync("training", linkedRoot);

            Assert.Equal(1, linked.ItemCount);
            Assert.Equal(linkedRoot, linked.Path);
            Assert.Contains(media.ListLinkedSources(), item => item.Name == "training" && item.ItemCount == 1);
            Assert.Contains(media.ListSources(), item => item.Kind == "linked" && item.Source == linkedRoot);
            Assert.True(await media.UnlinkDirectoryAsync("training"));
        }
        finally
        {
            Directory.Delete(linkedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IndexedSearchAndThumbnailMetadata_AreStableAcrossQueries()
    {
        using var env = new TempWebHostEnvironment();
        var configuration = CreateConfiguration();
        var database = new SqliteMetadataDatabase(env, configuration);
        var media = CreateMedia(env, configuration: configuration, database: database);
        using var bytes = new MemoryStream(TinyPng);
        var imported = await media.ImportAsync("indexed", "OK", "camera/frame-01.png", bytes, bytes.Length, default);

        var search = media.ListItems(null, null, search: "frame-01");
        Assert.Single(search.Items);
        var thumbnail = await media.ResolveThumbnailPathAsync(imported.RelativePath);
        Assert.True(File.Exists(thumbnail));
        var withDimensions = media.ListItems("indexed", "OK", minWidth: 1, maxWidth: 1);
        Assert.Single(withDimensions.Items);
        Assert.Equal(1, withDimensions.Items[0].Width);
        Assert.Equal(1, withDimensions.Items[0].Height);
    }

    [Fact]
    public async Task Import_StoresProvenanceHashAndDuplicateEvidence()
    {
        using var env = new TempWebHostEnvironment();
        var configuration = CreateConfiguration();
        var database = new SqliteMetadataDatabase(env, configuration);
        var media = CreateMedia(env, configuration: configuration, database: database);
        using var firstBytes = new MemoryStream(TinyPng);
        var first = await media.ImportAsync(
            "provenance", "OK", "camera-a/frame.png", firstBytes, firstBytes.Length, default, importedBy: "alice");
        using var secondBytes = new MemoryStream(TinyPng);
        var second = await media.ImportAsync(
            "provenance", "OK", "camera-b/frame.png", secondBytes, secondBytes.Length, default, importedBy: "alice");

        Assert.Equal("camera-a/frame.png", first.OriginPath);
        Assert.Equal("upload", first.SourceKind);
        Assert.Equal("alice", first.ImportedBy);
        Assert.NotNull(first.ImportedAt);
        Assert.Equal(64, first.Sha256?.Length);
        Assert.Equal(first.RelativePath, second.DuplicateOf);
    }

    [Fact]
    public async Task Manifest_DetectsContentChangesAndUnexpectedFiles()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var bytes = new MemoryStream(TinyPng);
        var item = await media.ImportAsync("manifest", "OK", "day-1/frame.png", bytes, bytes.Length, default);
        var manifest = await media.CreateManifestAsync("manifest");
        await File.AppendAllTextAsync(media.ResolveItemPath(item.RelativePath), "changed");
        using (var extra = new MemoryStream(TinyPng))
            await media.ImportAsync("manifest", "OK", "day-1/extra.png", extra, extra.Length, default);

        var verification = await media.VerifyManifestAsync("manifest", manifest);

        Assert.False(verification.Valid);
        Assert.Contains(verification.Issues, issue => issue.Path == item.RelativePath && issue.Kind == "content-changed");
        Assert.Contains(verification.Issues, issue => issue.Kind == "unexpected");
    }

    [Fact]
    public async Task IntegrityScan_DetectsDuplicatesAndIndexRepairsMissingRows()
    {
        using var env = new TempWebHostEnvironment();
        var configuration = CreateConfiguration();
        var database = new SqliteMetadataDatabase(env, configuration);
        var media = CreateMedia(env, configuration: configuration, database: database);
        for (var index = 0; index < 2; index++)
        {
            using var bytes = new MemoryStream(TinyPng);
            await media.ImportAsync("integrity", "OK", $"camera-{index}/frame.png", bytes, bytes.Length, default);
        }

        var report = await media.AuditIntegrityAsync();
        Assert.Equal(2, report.FileCount);
        Assert.Equal(1, report.DuplicateGroups);
        Assert.Contains(report.Issues, issue => issue.Kind == "duplicate-content");

        var maintenance = await media.RepairIndexAsync();
        Assert.Equal(0, maintenance.RemovedIndexRows);
        Assert.Equal(2, maintenance.Report.FileCount);
    }

    [Fact]
    public async Task IndexedCollections_KeepEmptyCollectionsVisible()
    {
        using var env = new TempWebHostEnvironment();
        var configuration = CreateConfiguration();
        var database = new SqliteMetadataDatabase(env, configuration);
        var media = CreateMedia(env, configuration: configuration, database: database);
        _ = media.ListCollections();

        var created = await media.CreateCollectionAsync("empty");

        Assert.Equal("empty", created.Name);
        Assert.Contains(media.ListCollections(), item => item.Name == "empty" && item.ItemCount == 0);
        Assert.Equal(1, media.Status().CollectionCount);
    }

    [Fact]
    public async Task BulkMoveAndDelete_ReportPartialResultsAndPreservePaths()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        using var firstBytes = new MemoryStream(TinyPng);
        var first = await media.ImportAsync("bulk-a", "OK", "camera/a.png", firstBytes, firstBytes.Length, default);
        using var secondBytes = new MemoryStream(TinyPng);
        var second = await media.ImportAsync("bulk-a", "OK", "camera/b.png", secondBytes, secondBytes.Length, default);

        var moved = await media.BulkUpdateAsync(new MediaBulkRequest(
            "move",
            [first.RelativePath, second.RelativePath],
            TargetCollection: "bulk-b",
            TargetLabel: "NG"));
        Assert.Equal(2, moved.Succeeded);
        Assert.All(moved.Items, item => Assert.StartsWith("bulk-b/NG/camera/", item.RelativePath));
        var deleted = await media.BulkUpdateAsync(new MediaBulkRequest(
            "delete",
            [moved.Items[0].RelativePath, "missing.png"]));
        Assert.Equal(1, deleted.Succeeded);
        Assert.Single(deleted.Failures);
        Assert.Equal(1, media.ListItems("bulk-b", "NG").Total);
    }

    [Fact]
    public async Task ListItems_PaginatesAndReportsFilteredTotal()
    {
        using var env = new TempWebHostEnvironment();
        var media = CreateMedia(env);
        for (var i = 1; i <= 5; i++)
        {
            using var bytes = new MemoryStream(TinyPng);
            await media.ImportAsync("page", "OK", $"sample-{i}.png", bytes, bytes.Length, default);
        }

        var first = media.ListItems("page", "OK", skip: 0, take: 2);
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);

        var middle = media.ListItems("page", "OK", skip: 2, take: 2);
        Assert.Equal(5, middle.Total);
        Assert.Equal(2, middle.Items.Count);

        var last = media.ListItems("page", "OK", skip: 4, take: 2);
        Assert.Equal(5, last.Total);
        Assert.Single(last.Items);

        var beyond = media.ListItems("page", "OK", skip: 99, take: 2);
        Assert.Equal(5, beyond.Total);
        Assert.Empty(beyond.Items);
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

    private static MediaLibraryService CreateMedia(
        TempWebHostEnvironment env,
        IReplayInputResolver? resolver = null,
        IConfiguration? configuration = null,
        SqliteMetadataDatabase? database = null)
    {
        configuration ??= CreateConfiguration();
        return new MediaLibraryService(env, configuration, resolver, database);
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MediaLibrary:RootPath"] = "data/media",
                ["MediaLibrary:MaxImportBytes"] = "1048576",
                ["MediaLibrary:MaxBatchFiles"] = "20",
                ["MediaLibrary:MaxBatchBytes"] = "4194304"
            }).Build();

    private sealed class AllowAllResolver : IReplayInputResolver
    {
        public string? Resolve(string requestedPath) =>
            Path.IsPathFullyQualified(requestedPath) ? Path.GetFullPath(requestedPath) : null;
    }
}
