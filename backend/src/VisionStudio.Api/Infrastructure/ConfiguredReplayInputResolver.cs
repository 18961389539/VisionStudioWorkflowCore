using VisionStudio.Abstractions;

namespace VisionStudio.Api.Infrastructure;

/// <summary>
/// 离线回放读盘授权器：把 <c>__replayInputPath</c> 限制在数据根 / 媒体库根 / 显式配置的附加根之内。
/// <para>
/// 设计意图（R2 复查 P0）：回放路径由宿主注入，但 <c>NodeDefinition.Parameters</c> 来自可导入的流程文档，
/// 因此引擎不能信任它。<c>VisionStudio:ReplayInputRoots</c>（分号分隔）用于为现场留出额外根目录时显式放开，
/// 默认只允许产品自己的数据目录。
/// </para>
/// </summary>
public sealed class ConfiguredReplayInputResolver : IReplayInputResolver
{
    public const string ConfigurationKey = "VisionStudio:ReplayInputRoots";

    private readonly string[] _normalizedRoots;
    private readonly string _normalizedMediaRoot;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ConfiguredReplayInputResolver(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var dataRoot = VisionStudioDataRoot.Resolve(environment.ContentRootPath, configuration);
        var mediaRoot = VisionStudioDataRoot.ResolveMediaRoot(environment.ContentRootPath, configuration["MediaLibrary:RootPath"], configuration);
        var roots = new List<string>
        {
            dataRoot,
            mediaRoot
        };
        var configured = configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var entry in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathRooted(entry))
                    throw new InvalidOperationException($"{ConfigurationKey} entries must be absolute paths; '{entry}' is relative.");
                roots.Add(entry);
            }
        }

        _normalizedMediaRoot = NormalizeRoot(Path.GetFullPath(mediaRoot));
        _normalizedRoots = roots
            .Select(Path.GetFullPath)
            .Distinct(_comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(NormalizeRoot)
            .ToArray();
    }

    /// <summary>当前进程允许回放读取的根目录（已规范化，带尾部目录分隔符）。</summary>
    public IReadOnlyList<string> AllowedRoots => _normalizedRoots;

    public string? Resolve(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath)) return null;

        if (requestedPath.StartsWith("media://", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = requestedPath["media://".Length..];
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('?') || relativePath.Contains('#'))
                return null;

            relativePath = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
                return null;

            try
            {
                var mediaPath = Path.GetFullPath(Path.Combine(Path.TrimEndingDirectorySeparator(_normalizedMediaRoot), relativePath));
                if (!NormalizeRoot(mediaPath).StartsWith(_normalizedMediaRoot, _comparison))
                    return null;
                requestedPath = mediaPath;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // 末尾补分隔符后再比较，避免 <data>/media-other 命中 <data>/media。
        var candidate = NormalizeRoot(full);
        return _normalizedRoots.Any(root => candidate.StartsWith(root, _comparison)) ? full : null;
    }

    private static string NormalizeRoot(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed + Path.DirectorySeparatorChar;
    }
}
