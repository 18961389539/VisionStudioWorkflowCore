using System;

namespace ImageViewer.Plugins
{
    /// <summary>
    /// ROI 插件契约版本。
    /// Chinese: 模块或程序集显式声明它编译时依赖的插件 ABI 版本；缺失时按 v1 处理，兼容 1.0 前的既有插件。
    /// English: Declares the plugin ABI version a module or assembly was compiled against. Missing means v1 for
    /// pre-1.0 compatibility; future host versions can reject unannotated plugins explicitly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class RoiPluginContractAttribute : Attribute
    {
        public RoiPluginContractAttribute(int version)
        {
            Version = version;
        }

        /// <summary>插件编译时依赖的契约版本。</summary>
        public int Version { get; }
    }

    /// <summary>当前宿主支持的 ROI 插件契约版本。</summary>
    public static class RoiPluginContract
    {
        /// <summary>1.0 前首个显式版本；之后每次破坏性变更递增。</summary>
        public const int CurrentVersion = 1;
    }

    /// <summary>插件声明的契约版本与宿主不一致。</summary>
    public sealed class RoiPluginContractMismatchException : Exception
    {
        public RoiPluginContractMismatchException(string moduleTypeName, int expectedVersion, int actualVersion)
            : base(
                $"ROI plugin '{moduleTypeName}' targets contract v{actualVersion}, but this host supports v{expectedVersion}. " +
                "Rebuild the plugin against the matching ImageViewerControl contract.")
        {
            ModuleTypeName = moduleTypeName;
            ExpectedVersion = expectedVersion;
            ActualVersion = actualVersion;
        }

        /// <summary>被拒绝的模块类型名。</summary>
        public string ModuleTypeName { get; }

        /// <summary>宿主支持的契约版本。</summary>
        public int ExpectedVersion { get; }

        /// <summary>插件声明的契约版本。</summary>
        public int ActualVersion { get; }
    }
}
