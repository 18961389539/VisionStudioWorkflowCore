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
    private sealed class StubEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VisionStudio.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ConfiguredReplayInputResolver Resolver(string contentRoot, string? extraRoots = null)
    {
        var settings = new Dictionary<string, string?>();
        if (extraRoots is not null) settings[ConfiguredReplayInputResolver.ConfigurationKey] = extraRoots;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ConfiguredReplayInputResolver(configuration, new StubEnvironment(contentRoot));
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
    public void ExplicitAdditionalRoot_IsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));
        var shared = Path.Combine(Path.GetTempPath(), "visionstudio-shared-replay", Guid.NewGuid().ToString("N"));
        var resolver = Resolver(root, shared);

        Assert.NotNull(resolver.Resolve(Path.Combine(shared, "frame.png")));
        Assert.Contains(resolver.AllowedRoots, entry => entry.StartsWith(Path.TrimEndingDirectorySeparator(shared), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RelativeAdditionalRoot_IsRejectedAtStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-replay-root", Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<InvalidOperationException>(() => Resolver(root, "relative/replay"));

        Assert.Contains("must be absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
