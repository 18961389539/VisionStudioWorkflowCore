using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// P0：离线回放读盘必须落在宿主授权根目录内。这些用例锁定路径包含判断，
/// 防止 <c>__replayInputPath</c> 被用来读取数据目录之外的文件。
/// </summary>
public sealed class ReplayInputResolverTests
{
    private sealed class StubEnvironment(string contentRoot, string environmentName = "Testing") : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VisionStudio.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ConfiguredReplayInputResolver Resolver(
        string contentRoot,
        string? extraRoots = null,
        string? dataRoot = null,
        string? mediaRoot = null,
        string environmentName = "Testing",
        bool? allowUnrestricted = null)
    {
        var settings = new Dictionary<string, string?>();
        if (extraRoots is not null) settings[ConfiguredReplayInputResolver.ConfigurationKey] = extraRoots;
        if (dataRoot is not null) settings[VisionStudioDataRoot.ConfigurationKey] = dataRoot;
        if (mediaRoot is not null) settings["MediaLibrary:RootPath"] = mediaRoot;
        if (allowUnrestricted is not null)
            settings[ConfiguredReplayInputResolver.AllowUnrestrictedConfigurationKey] = allowUnrestricted.Value.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ConfiguredReplayInputResolver(configuration, new StubEnvironment(contentRoot, environmentName));
    }

    [Fact]
    public void DataAndMediaRoots_AreAllowedByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root);
        // 用解析后的数据根构造候选路径，避免 VISIONSTUDIO_DATA_ROOT 覆盖导致断言依赖环境。
        var dataRoot = Path.TrimEndingDirectorySeparator(resolver.AllowedRoots[0]);

        Assert.NotNull(resolver.Resolve(Path.Combine(dataRoot, "media", "frame.png")));
        Assert.NotNull(resolver.Resolve(Path.Combine(dataRoot, "traces", "replay.png")));
    }

    [Fact]
    public void PathsOutsideAllowedRoots_AreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "visionstudio-outside", "secret.png");
        var resolver = Resolver(root);

        Assert.Null(resolver.Resolve(outside));
        Assert.Null(resolver.Resolve("C:\\Windows\\win.ini"));
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefix_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var shared = Path.Combine(Path.GetTempPath(), "visionstudio-shared-replay", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root, shared);
        // 前缀相同的兄弟目录（<shared>-other）不能命中 <shared>：比较前必须补目录分隔符。
        var sibling = Path.Combine(Path.GetDirectoryName(shared)!, Path.GetFileName(shared) + "-other", "frame.png");

        Assert.Null(resolver.Resolve(sibling));
    }

    [Fact]
    public void ParentTraversal_IsNormalizedBeforeTheCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root);

        Assert.Null(resolver.Resolve(Path.Combine(root, "data", "..", "..", "escape.png")));
    }

    [Fact]
    public void MediaUri_MapsInsideMediaRootAndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-media-uri", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root);
        var mediaRoot = Path.TrimEndingDirectorySeparator(resolver.AllowedRoots[1]);
        var expected = Path.GetFullPath(Path.Combine(mediaRoot, "collection", "frame.png"));

        Assert.Equal(expected, resolver.Resolve("media://collection/frame.png"));
        Assert.Null(resolver.Resolve("media://../escape.png"));
    }

    [Fact]
    public void MediaUri_UsesConfiguredMediaRootWhenItDuplicatesAnotherAllowedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-media-root", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root, dataRoot: root, mediaRoot: root);

        Assert.Single(resolver.AllowedRoots);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "collection", "frame.png")),
            resolver.Resolve("media://collection/frame.png"));
    }

    [Fact]
    public void ExplicitAdditionalRoot_IsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var shared = Path.Combine(Path.GetTempPath(), "visionstudio-shared-replay", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root, shared);

        Assert.NotNull(resolver.Resolve(Path.Combine(shared, "frame.png")));
        Assert.Contains(resolver.AllowedRoots, entry => entry.StartsWith(Path.TrimEndingDirectorySeparator(shared), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Development_UsesDesktopFilePermissionsForAbsolutePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "visionstudio-desktop", "frame.png"));
        var resolver = Resolver(root, environmentName: "Development");

        Assert.Equal(outside, resolver.Resolve(outside));
    }

    [Fact]
    public void Production_DeniesUnconfiguredAbsolutePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "visionstudio-production", "frame.png"));
        var resolver = Resolver(root, environmentName: "Production");

        Assert.Null(resolver.Resolve(outside));
    }

    [Fact]
    public void Production_CanOptIntoDesktopFilePermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "visionstudio-production", "frame.png"));
        var resolver = Resolver(root, environmentName: "Production", allowUnrestricted: true);

        Assert.Equal(outside, resolver.Resolve(outside));
    }

    [Fact]
    public void RelativeAdditionalRoot_IsRejectedAtStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<InvalidOperationException>(() => Resolver(root, "relative/replay"));

        Assert.Contains("must be absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
