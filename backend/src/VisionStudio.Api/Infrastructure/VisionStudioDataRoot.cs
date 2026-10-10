namespace VisionStudio.Api.Infrastructure;

/// <summary>
/// 长期数据目录（data/）解析的单一来源：默认位于内容根下，可用 <see cref="EnvironmentVariable"/>
/// 环境变量重定向到版本包之外——升级切换发布目录时不迁移、也不静默新建数据库，
/// 数据与版本包彻底分离（任意发布目录/工作目录启动都指向同一数据根）。
/// </summary>
public static class VisionStudioDataRoot
{
    public const string EnvironmentVariable = "VISIONSTUDIO_DATA_ROOT";

    public static string Resolve(string contentRoot)
        => Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(contentRoot, "data");

    /// <summary>
    /// 媒体库根解析的单一来源（R05）：默认数据根/media；配置了 `MediaLibrary:RootPath` 时，
    /// 绝对路径原样使用，相对路径相对**数据根**（而非打包目录）解析。备份的 system/media 段
    /// 与媒体服务都通过本方法取根，确保"备份什么、还原到哪"始终一致。
    /// 兼容历史配置：相对路径若以 `data/` 开头（旧约定 = 内容根下 data 子目录），
    /// 该前缀会被剥离——数据根本身即 `data`，避免解析成 `&lt;dataRoot&gt;/data/...`。
    /// </summary>
    public static string ResolveMediaRoot(string contentRoot, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(Resolve(contentRoot), "media");
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        var relative = configured.Replace('\\', '/');
        if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) relative = relative["data/".Length..];
        return Path.Combine(Resolve(contentRoot), relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
