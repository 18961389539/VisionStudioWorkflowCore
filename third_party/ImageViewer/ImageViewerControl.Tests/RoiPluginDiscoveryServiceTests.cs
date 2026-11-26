using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 插件发现边界守卫。
    /// Chinese: 发现只扫显式声明的目录与程序集名前缀——不声明前缀就不扫任何程序集，未配置目录时只用
    /// <c>&lt;基目录&gt;/plugins</c> 而**不**回退到应用程序目录本身（否则会把宿主依赖当成插件候选）。
    /// English: Discovery is scoped to an explicit directory and explicit assembly-name prefixes; without prefixes it
    /// scans nothing, and an unconfigured directory resolves to &lt;base&gt;/plugins rather than the application directory.
    /// </summary>
    public class RoiPluginDiscoveryServiceTests
    {
        [Fact]
        public void RegisterFromAssemblies_WithoutRegistry_Throws()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
                RoiPluginDiscoveryService.RegisterFromAssemblies([Assembly.GetExecutingAssembly()], registry: null!));

            Assert.Equal("registry", ex.ParamName);
        }

        [Fact]
        public void RegisterFromAssemblies_ContinuesAfterModuleFailure()
        {
            FailingRoiPluginModule.Reset();
            HealthyRoiPluginModule.Reset();

            RoiPluginDiscoveryResult result = RoiPluginDiscoveryService.RegisterFromAssemblies(
                [Assembly.GetExecutingAssembly()],
                new RoiPluginRegistry());

            Assert.True(FailingRoiPluginModule.WasCalled);
            Assert.True(HealthyRoiPluginModule.WasCalled);
            Assert.True(result.HasFailures);
            Assert.Contains(result.Failures, failure => failure.ModuleTypeName.Contains(nameof(FailingRoiPluginModule), StringComparison.Ordinal));
        }

        [Fact]
        public void RegisterFromAssemblies_RejectsUnsupportedContractVersionBeforeRegister()
        {
            ContractMismatchRoiPluginModule.Reset();
            HealthyRoiPluginModule.Reset();

            RoiPluginDiscoveryResult result = RoiPluginDiscoveryService.RegisterFromAssemblies(
                [Assembly.GetExecutingAssembly()],
                new RoiPluginRegistry());

            Assert.False(ContractMismatchRoiPluginModule.WasCalled);
            Assert.True(HealthyRoiPluginModule.WasCalled);
            Assert.Contains(
                result.Failures,
                failure => failure.Exception is RoiPluginContractMismatchException mismatch &&
                           mismatch.ModuleTypeName.Contains(nameof(ContractMismatchRoiPluginModule), StringComparison.Ordinal) &&
                           mismatch.ActualVersion == RoiPluginContract.CurrentVersion + 1);
        }

        [Fact]
        public void RegisterFromAssemblies_StrictMode_ThrowsAfterCollectingFailures()
        {
            RoiPluginDiscoveryOptions options = new() { FailOnModuleRegistrationError = true };

            Assert.Throws<AggregateException>(() =>
                RoiPluginDiscoveryService.RegisterFromAssemblies(
                    [Assembly.GetExecutingAssembly()],
                    new RoiPluginRegistry(),
                    options));
        }

        [Fact]
        public void ResolvePluginDirectory_WithoutConfiguration_UsesPluginsSubdirectory()
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "ImageViewerPluginRoot");

            string resolved = new RoiPluginDiscoveryOptions().ResolvePluginDirectory(baseDirectory);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(baseDirectory, RoiPluginDiscoveryOptions.DefaultPluginDirectoryName)),
                resolved);

            // 关键：绝不回退到应用程序目录本身。
            Assert.NotEqual(Path.GetFullPath(baseDirectory), resolved);
            Assert.Equal(Path.GetFullPath(baseDirectory), Path.GetDirectoryName(resolved));
        }

        [Fact]
        public void ResolvePluginDirectory_HonorsRelativeAndRootedPaths()
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "ImageViewerPluginRoot");
            string rootedPath = Path.Combine(Path.GetTempPath(), "ImageViewerPluginsElsewhere");

            var relative = new RoiPluginDiscoveryOptions { PluginDirectoryPath = "ext/roi" };
            var rooted = new RoiPluginDiscoveryOptions { PluginDirectoryPath = rootedPath };

            Assert.Equal(Path.GetFullPath(Path.Combine(baseDirectory, "ext", "roi")), relative.ResolvePluginDirectory(baseDirectory));
            Assert.Equal(rootedPath, rooted.ResolvePluginDirectory(baseDirectory));
        }

        [Fact]
        public void DiscoverAndRegister_WithoutAllowedPrefixes_DoesNotScanAnything()
        {
            var registry = new RoiPluginRegistry();
            var logger = new RecordingLogger();

            // 目录刻意指向"存在且含 DLL"的位置：没有前缀时连目录都不该被打开。
            RoiPluginDiscoveryOptions options = new()
            {
                PluginDirectoryPath = AppContext.BaseDirectory,
                ScanLoadedAssemblies = false
            };

            RoiPluginDiscoveryResult result = RoiPluginDiscoveryService.DiscoverAndRegister(options, registry, logger);

            Assert.Empty(result.RegisteredModuleTypeNames);
            Assert.Empty(registry.Plugins);
            Assert.Contains(logger.Warnings, warning => warning.Contains("AllowedAssemblyNamePrefixes", StringComparison.Ordinal));
        }

        [Fact]
        public void DiscoverAndRegister_SkipsDirectoryFilesThatDoNotMatchTheConfiguredPrefixes()
        {
            string pluginDirectory = Path.Combine(Path.GetTempPath(), $"iv-plugins-{Guid.NewGuid():N}");
            Directory.CreateDirectory(pluginDirectory);
            try
            {
                // 非 .NET 文件：一旦被打开就会留下"跳过非托管程序集"的警告，因此可以精确观察前缀是否生效。
                File.WriteAllText(Path.Combine(pluginDirectory, "Acme.RoiPlugins.Native.dll"), "not a managed assembly");

                var nonMatchingLogger = new RecordingLogger();
                RoiPluginDiscoveryService.DiscoverAndRegister(
                    new RoiPluginDiscoveryOptions
                    {
                        PluginDirectoryPath = pluginDirectory,
                        AllowedAssemblyNamePrefixes = { "Other.Prefix" }
                    },
                    new RoiPluginRegistry(),
                    nonMatchingLogger);

                Assert.DoesNotContain(nonMatchingLogger.Warnings, warning => warning.Contains("Skipping non-.NET assembly", StringComparison.Ordinal));

                var matchingLogger = new RecordingLogger();
                RoiPluginDiscoveryService.DiscoverAndRegister(
                    new RoiPluginDiscoveryOptions
                    {
                        PluginDirectoryPath = pluginDirectory,
                        AllowedAssemblyNamePrefixes = { "Acme.RoiPlugins" }
                    },
                    new RoiPluginRegistry(),
                    matchingLogger);

                Assert.Contains(matchingLogger.Warnings, warning => warning.Contains("Skipping non-.NET assembly", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(pluginDirectory, recursive: true);
            }
        }

        [Fact]
        public void DiscoverAndRegister_LoadsModulesFromTheConfiguredPluginsDirectory()
        {
            string pluginDirectory = Path.Combine(Path.GetTempPath(), $"iv-plugins-{Guid.NewGuid():N}");
            Directory.CreateDirectory(pluginDirectory);
            try
            {
                // 复制测试程序集：文件名与程序集元数据名都要命中前缀——发现先用文件名做廉价过滤，
                // 再用元数据名二次校验（改名不改元数据，所以两边都得对）。
                string pluginAssemblyPath = Path.Combine(pluginDirectory, "ImageViewerControl.Tests.Probe.dll");
                File.Copy(Assembly.GetExecutingAssembly().Location, pluginAssemblyPath);

                var registry = new RoiPluginRegistry();
                RoiPluginDiscoveryResult result = RoiPluginDiscoveryService.DiscoverAndRegister(
                    new RoiPluginDiscoveryOptions
                    {
                        PluginDirectoryPath = pluginDirectory,
                        AllowedAssemblyNamePrefixes = { "ImageViewerControl.Tests" }
                    },
                    registry);

                Assert.Contains(
                    result.RegisteredModuleTypeNames,
                    name => name.Contains(nameof(HealthyRoiPluginModule), StringComparison.Ordinal));
                Assert.Contains(
                    result.Failures,
                    failure => failure.ModuleTypeName.Contains(nameof(FailingRoiPluginModule), StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(pluginDirectory, recursive: true);
            }
        }

        [Fact]
        public void IsAllowedAssemblyName_RequiresAPrefixAndLetsDisableWin()
        {
            IReadOnlySet<string> disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Acme.RoiPlugins.Legacy" };
            string[] prefixes = ["Acme.RoiPlugins"];

            Assert.True(RoiPluginDiscoveryService.IsAllowedAssemblyName("Acme.RoiPlugins", disabled, prefixes));
            Assert.True(RoiPluginDiscoveryService.IsAllowedAssemblyName("acme.roiplugins.calipers", disabled, prefixes));
            Assert.False(RoiPluginDiscoveryService.IsAllowedAssemblyName("Acme.RoiPlugins.Legacy", disabled, prefixes));
            Assert.False(RoiPluginDiscoveryService.IsAllowedAssemblyName("ImageViewer.Core", disabled, prefixes));
            Assert.False(RoiPluginDiscoveryService.IsAllowedAssemblyName("Acme.RoiPlugins", disabled, []));
            Assert.False(RoiPluginDiscoveryService.IsAllowedAssemblyName(string.Empty, disabled, prefixes));
        }

        private sealed class RecordingLogger : ImageViewer.Abstractions.IImageViewerLogger
        {
            public List<string> Warnings { get; } = [];

            public void LogInfo(string message)
            {
            }

            public void LogWarning(string message) => Warnings.Add(message);

            public void LogError(string message, Exception? exception = null) => Warnings.Add(message);
        }

        [RoiPluginContract(RoiPluginContract.CurrentVersion + 1)]
        public sealed class ContractMismatchRoiPluginModule : IRoiPluginModule
        {
            public static bool WasCalled { get; private set; }

            public static void Reset() => WasCalled = false;

            public void Register(RoiPluginRegistry registry) => WasCalled = true;
        }

        public sealed class FailingRoiPluginModule : IRoiPluginModule
        {
            public static bool WasCalled { get; private set; }

            public static void Reset() => WasCalled = false;

            public void Register(RoiPluginRegistry registry)
            {
                WasCalled = true;
                throw new InvalidOperationException("Expected test failure.");
            }
        }

        public sealed class HealthyRoiPluginModule : IRoiPluginModule
        {
            public static bool WasCalled { get; private set; }

            public static void Reset() => WasCalled = false;

            public void Register(RoiPluginRegistry registry) => WasCalled = true;
        }
    }
}
