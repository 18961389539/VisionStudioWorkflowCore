using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Services;

namespace ImageViewer.Plugins
{
    public sealed class RoiPluginDiscoveryOptions
    {
        /// <summary>
        /// 未显式配置插件目录时使用的子目录名。
        /// Chinese: 插件只从应用程序目录下的这个子目录加载——不再回退到应用程序目录本身，
        /// 否则会把宿主自己的一堆依赖 DLL 当成插件候选。
        /// English: The subdirectory used when no plugin directory is configured. Discovery never falls back to the
        /// application directory itself, which would treat the host's own dependencies as plugin candidates.
        /// </summary>
        public const string DefaultPluginDirectoryName = "plugins";

        public string? PluginDirectoryPath { get; set; }

        public List<string> DisabledAssemblyNames { get; set; } = new();

        /// <summary>
        /// 允许扫描的程序集名前缀。
        /// Chinese: 必填——为空时不加载也不扫描任何程序集（发现结果为空并记录一条警告），
        /// 而不是"为空即全放行"。这样"哪些 DLL 会被当成插件"始终是显式声明。
        /// English: Required. When empty, nothing is loaded or scanned (an empty result plus a warning is logged)
        /// instead of allowing everything, so the set of plugin assemblies is always explicit.
        /// </summary>
        public List<string> AllowedAssemblyNamePrefixes { get; set; } = new();

        public bool ScanLoadedAssemblies { get; set; }

        public bool FailOnModuleRegistrationError { get; set; }

        public List<string> DisabledModuleTypeNames { get; set; } = new();

        public List<string> DisabledPluginTypeKeys { get; set; } = new();

        public List<string> UnloadedPluginTypeKeys { get; set; } = new();

        public static RoiPluginDiscoveryOptions Load(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (!File.Exists(filePath))
            {
                return new RoiPluginDiscoveryOptions();
            }

            string json = File.ReadAllText(filePath);
            return System.Text.Json.JsonSerializer.Deserialize(json, ImageViewerJsonSerializationContext.Default.RoiPluginDiscoveryOptions) ?? new RoiPluginDiscoveryOptions();
        }

        public static async Task<RoiPluginDiscoveryOptions> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (!File.Exists(filePath))
            {
                return new RoiPluginDiscoveryOptions();
            }

            string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Deserialize(json, ImageViewerJsonSerializationContext.Default.RoiPluginDiscoveryOptions) ?? new RoiPluginDiscoveryOptions();
        }

        /// <summary>
        /// 解析实际扫描的插件目录。
        /// Chinese: 未配置 <see cref="PluginDirectoryPath"/> 时返回 <c>&lt;baseDirectory&gt;/plugins</c>；
        /// 目录不存在就等于没有插件，而不是退回 baseDirectory 去扫宿主的全部依赖。
        /// English: Returns <c>&lt;baseDirectory&gt;/plugins</c> when no path is configured; a missing directory simply
        /// means "no plugins" instead of falling back to scanning the host's whole application directory.
        /// </summary>
        public string ResolvePluginDirectory(string baseDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

            if (string.IsNullOrWhiteSpace(PluginDirectoryPath))
            {
                return Path.GetFullPath(Path.Combine(baseDirectory, DefaultPluginDirectoryName));
            }

            return Path.IsPathRooted(PluginDirectoryPath)
                ? PluginDirectoryPath
                : Path.GetFullPath(Path.Combine(baseDirectory, PluginDirectoryPath));
        }
    }
}
