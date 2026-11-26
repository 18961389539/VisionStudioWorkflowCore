using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace VisionStudio.Wpf.Hosting;

/// <summary>
/// 桌面版宿主环境适配器：实现 <see cref="IWebHostEnvironment"/>，使 WPF 桌面应用可以在不启动
/// Kestrel Web 服务器的情况下直接复用 <c>VisionStudio.Api</c> 的完整服务注册与仓储初始化链。
/// </summary>
public sealed class DesktopWebHostEnvironment : IWebHostEnvironment
{
    public DesktopWebHostEnvironment(string contentRootPath, string environmentName = "Development")
    {
        ContentRootPath = Path.GetFullPath(contentRootPath);
        Directory.CreateDirectory(ContentRootPath);
        EnvironmentName = environmentName;
        WebRootPath = Path.Combine(ContentRootPath, "wwwroot");
        ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
        WebRootFileProvider = new NullFileProvider();
    }

    public string ApplicationName { get; set; } = "VisionStudio.Wpf";
    public IFileProvider WebRootFileProvider { get; set; }
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }

    /// <summary>
    /// 解析有效内容根路径：在源码仓库中运行时，与 <c>VisionStudio.Api</c> 对齐（指向
    /// <c>backend/src/VisionStudio.Api</c> 或仓库根），以便自动发现 <c>samples/camera_frames</c> 等内置资产；
    /// 打包发布后回落到应用程序基目录。
    /// </summary>
    public static string ResolveDefaultContentRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json")))
            return AppContext.BaseDirectory;
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var apiDir = Path.Combine(current.FullName, "backend", "src", "VisionStudio.Api");
            if (Directory.Exists(apiDir))
            {
                return apiDir;
            }
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 解析桌面端默认数据根：优先读取环境变量 <c>VISIONSTUDIO_DATA_ROOT</c>；
    /// 否则在源码开发布局下写入 <c>artifacts/wpf-data</c>，避免与同时调试的 Web API（<c>artifacts/dev-data</c>）争抢宿主文件锁。
    /// </summary>
    public static string ResolveDefaultDataRoot(string contentRootPath)
    {
        var envRoot = Environment.GetEnvironmentVariable("VISIONSTUDIO_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        var repoRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json"))
            ? null
            : TryFindRepoRoot(contentRootPath) ?? TryFindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            return Path.Combine(repoRoot, "artifacts", "wpf-data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionStudio", "Trial", "data");
    }

    public static string ResolveSamplesDirectory(string contentRootPath)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "samples");
        if (Directory.Exists(bundled))
        {
            return bundled;
        }
        var repoRoot = TryFindRepoRoot(contentRootPath);
        return repoRoot is not null ? Path.Combine(repoRoot, "samples") : bundled;
    }

    /// <summary>
    /// 尝试定位仓库根目录（包含 <c>samples</c> 与 <c>backend</c> 目录）。
    /// </summary>
    public static string? TryFindRepoRoot(string startPath)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startPath));
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples"))
                && Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
