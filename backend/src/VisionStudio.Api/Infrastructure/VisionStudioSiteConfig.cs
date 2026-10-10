using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace VisionStudio.Api.Infrastructure;

/// <summary>
/// F06：站点配置源（数据根 <c>site-config/appsettings.site.json</c>）。
///
/// 受支持的外部配置来源与优先级（低 → 高）：
///   内容根 appsettings.json → appsettings.{Environment}.json → **site-config/appsettings.site.json**
///   → 环境变量 → 命令行参数。
/// site-config 覆盖内容根文件（现场策略优先），但环境变量/命令行仍可对其做最终覆盖。
///
/// 本文件随系统备份导出（备份包 system/site-config 段），恢复数据根即恢复站点配置——
/// "备份的目录"与"实际生效的配置"不再脱节（此前该目录只是被复制、从未被加载）。
///
/// 敏感字段处理：站点文件可能包含连接串等敏感值，它们会进入备份包；若不希望导出，
/// 请把敏感项留在环境变量或密钥管理中——该文件缺席时相关配置回退到默认来源，
/// 恢复步骤：将 appsettings.site.json 放回数据根 site-config/ 后重启（无需其它操作）。
/// </summary>
public static class VisionStudioSiteConfig
{
    public const string FolderName = "site-config";
    public const string FileName = "appsettings.site.json";

    public static string ResolvePath(string contentRoot)
        => Path.Combine(VisionStudioDataRoot.Resolve(contentRoot), FolderName, FileName);

    /// <summary>
    /// 把站点文件源插入到"环境变量源之前"（覆盖 appsettings* 文件源、被环境变量/命令行覆盖）。
    /// 文件不存在时 optional 跳过（行为与未配置时完全一致）。
    /// </summary>
    public static void AddSource(IConfigurationBuilder configuration, string contentRoot)
    {
        var path = ResolvePath(contentRoot);
        // PhysicalFileProvider 要求其根目录存在（"可选文件"不等于"可选目录"）；预建空目录对运维
        // 也是放置位置提示，且对不存在文件的行为与未配置时完全一致。
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var source = new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(directory),
            Path = Path.GetFileName(path),
            Optional = true,
            ReloadOnChange = false
        };

        var sources = configuration.Sources;
        var envIndex = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource)
            {
                envIndex = i;
                break;
            }
        }
        if (envIndex >= 0) sources.Insert(envIndex, source);
        else sources.Add(source);
    }
}
