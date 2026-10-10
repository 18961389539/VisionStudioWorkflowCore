using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>
/// 长期数据目录解析：默认指内容根下的 data/；VISIONSTUDIO_DATA_ROOT 环境变量优先
/// （env 分支不在此处测试——进程级环境变量会与并行测试互相干扰，其行为由部署脚本演练覆盖）。
/// </summary>
public sealed class VisionStudioDataRootTests
{
    [Fact]
    public void Resolve_DefaultsToContentRootData()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "visionstudio-content-root");
        var resolved = VisionStudioDataRoot.Resolve(contentRoot);
        Assert.Equal(Path.Combine(contentRoot, "data"), resolved);
    }

    // R05：媒体根解析的单一来源——默认 data/media；绝对路径原样；相对配置路径相对**数据根**解析
    // （而非打包目录），使备份 system/media 段与 MediaLibraryService 始终指向同一处。
    [Fact]
    public void ResolveMediaRoot_DefaultsToDataRootMedia()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "visionstudio-content-root");
        var resolved = VisionStudioDataRoot.ResolveMediaRoot(contentRoot, null);
        Assert.Equal(Path.Combine(contentRoot, "data", "media"), resolved);
        Assert.Equal(resolved, VisionStudioDataRoot.ResolveMediaRoot(contentRoot, "   "));
    }

    [Fact]
    public void ResolveMediaRoot_KeepsAbsoluteOverride()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "visionstudio-content-root");
        var absolute = Path.Combine(Path.GetTempPath(), "mounted-media");
        Assert.Equal(Path.GetFullPath(absolute), VisionStudioDataRoot.ResolveMediaRoot(contentRoot, absolute));
    }

    [Fact]
    public void ResolveMediaRoot_RelativeOverrideResolvesUnderDataRoot_NotPackage()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "visionstudio-package");
        var resolved = VisionStudioDataRoot.ResolveMediaRoot(contentRoot, "shared/media");
        // 关键：相对路径绝不能落在打包目录下（contentRoot/shared/media），必须在数据根下。
        Assert.Equal(Path.Combine(contentRoot, "data", "shared", "media"), resolved);
        Assert.NotEqual(Path.Combine(contentRoot, "shared", "media"), resolved);
    }
}
