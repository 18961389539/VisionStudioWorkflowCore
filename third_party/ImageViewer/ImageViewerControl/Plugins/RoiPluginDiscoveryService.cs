using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ImageViewer.Abstractions;

namespace ImageViewer.Plugins
{
    public static class RoiPluginDiscoveryService
    {
        public static RoiPluginDiscoveryResult DiscoverAndRegister(RoiPluginRegistry registry, string? directoryPath = null, IImageViewerLogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(registry);
            var options = new RoiPluginDiscoveryOptions { PluginDirectoryPath = directoryPath };
            return DiscoverAndRegister(options, registry, logger);
        }

        public static RoiPluginDiscoveryResult DiscoverAndRegister(RoiPluginDiscoveryOptions? options, RoiPluginRegistry registry, IImageViewerLogger? logger = null)
        {
            var targetRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
            var effectiveOptions = options ?? new RoiPluginDiscoveryOptions();
            string scanDirectory = effectiveOptions.ResolvePluginDirectory(AppContext.BaseDirectory);
            return RegisterFromAssemblies(LoadAssemblies(scanDirectory, effectiveOptions, logger), targetRegistry, effectiveOptions, logger);
        }

        public static RoiPluginDiscoveryResult RegisterFromAssemblies(IEnumerable<Assembly> assemblies, RoiPluginRegistry registry, RoiPluginDiscoveryOptions? options = null, IImageViewerLogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(assemblies);

            var targetRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
            var effectiveOptions = options ?? new RoiPluginDiscoveryOptions();
            var disabledModuleTypeNames = new HashSet<string>(effectiveOptions.DisabledModuleTypeNames, StringComparer.OrdinalIgnoreCase);
            var registeredModuleTypeNames = new List<string>();
            var failures = new List<RoiPluginDiscoveryFailure>();

            var moduleTypes = assemblies
                .Distinct()
                .SelectMany(assembly => GetLoadableTypes(assembly, failures, logger))
                .Where(type =>
                    type is { IsAbstract: false, IsInterface: false } &&
                    typeof(IRoiPluginModule).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) != null &&
                    !disabledModuleTypeNames.Contains(type.FullName ?? type.Name))
                .OrderBy(type => type.FullName, StringComparer.Ordinal);

            foreach (var moduleType in moduleTypes)
            {
                RoiPluginDiscoveryFailure? failure = RegisterModule(
                    moduleType,
                    targetRegistry,
                    effectiveOptions,
                    logger);
                if (failure == null)
                {
                    registeredModuleTypeNames.Add(moduleType.FullName ?? moduleType.Name);
                }
                else
                {
                    failures.Add(failure);
                }
            }

            using (targetRegistry.BeginUpdate())
            {
                ApplyPluginFilters(targetRegistry, effectiveOptions, null);
            }

            var result = new RoiPluginDiscoveryResult(registeredModuleTypeNames, failures);
            if (effectiveOptions.FailOnModuleRegistrationError && result.HasFailures)
            {
                throw new AggregateException(
                    "One or more ROI plugin modules failed to register.",
                    result.Failures.Select(failure => failure.Exception));
            }

            return result;
        }

        private static RoiPluginDiscoveryFailure? RegisterModule(
            Type moduleType,
            RoiPluginRegistry registry,
            RoiPluginDiscoveryOptions options,
            IImageViewerLogger? logger)
        {
            RoiPluginDiscoveryFailure? contractFailure = ValidateContract(moduleType, logger);
            if (contractFailure != null)
            {
                return contractFailure;
            }

            var beforeKeys = new HashSet<string>(registry.RegisteredTypeKeys, StringComparer.OrdinalIgnoreCase);
            try
            {
                var module = (IRoiPluginModule)Activator.CreateInstance(moduleType)!;
                logger?.LogInfo(CreateLogMessage(moduleType, "Register", "Started"));

                // 整个模块的注册（含过滤器卸载）合并成一次变更通知，避免中间态被当成"类型已下线"而迁移一次状态。
                using (registry.BeginUpdate())
                {
                    module.Register(registry);
                    ApplyPluginFilters(registry, options, beforeKeys);
                }

                logger?.LogInfo(CreateLogMessage(moduleType, "Register", "Succeeded"));
                return null;
            }
            catch (Exception ex)
            {
                RemoveNewRegistrations(registry, beforeKeys);
                logger?.LogError(CreateLogMessage(moduleType, "Register", "Failed"), ex);
                return new RoiPluginDiscoveryFailure(moduleType.FullName ?? moduleType.Name, ex);
            }
        }

        private static RoiPluginDiscoveryFailure? ValidateContract(Type moduleType, IImageViewerLogger? logger)
        {
            var contract = moduleType.GetCustomAttribute<RoiPluginContractAttribute>(inherit: false)
                ?? moduleType.Assembly
                    .GetCustomAttributes(typeof(RoiPluginContractAttribute), inherit: false)
                    .OfType<RoiPluginContractAttribute>()
                    .FirstOrDefault();
            int actualVersion = contract?.Version ?? RoiPluginContract.CurrentVersion;
            if (actualVersion == RoiPluginContract.CurrentVersion)
            {
                return null;
            }

            var exception = new RoiPluginContractMismatchException(
                moduleType.FullName ?? moduleType.Name,
                RoiPluginContract.CurrentVersion,
                actualVersion);
            logger?.LogError(CreateLogMessage(moduleType, "Contract", "Failed"), exception);
            return new RoiPluginDiscoveryFailure(moduleType.FullName ?? moduleType.Name, exception);
        }

        private static void RemoveNewRegistrations(RoiPluginRegistry registry, HashSet<string> registeredBeforeModule)
        {
            using (registry.BeginUpdate())
            {
                foreach (string typeKey in registry.RegisteredTypeKeys
                    .Where(key => !registeredBeforeModule.Contains(key))
                    .ToArray())
                {
                    registry.Unregister(typeKey);
                }
            }
        }

        private static string CreateLogMessage(Type moduleType, string stage, string result)
        {
            string moduleName = moduleType.FullName ?? moduleType.Name;
            return $"PluginDiscovery module={moduleName} stage={stage} result={result}";
        }

        private static void ApplyPluginFilters(RoiPluginRegistry registry, RoiPluginDiscoveryOptions options, HashSet<string>? registeredBeforeModule)
        {
            var disabledKeys = new HashSet<string>(options.DisabledPluginTypeKeys, StringComparer.OrdinalIgnoreCase);
            disabledKeys.UnionWith(options.UnloadedPluginTypeKeys);

            if (disabledKeys.Count == 0)
            {
                return;
            }

            string[] keysToInspect = registeredBeforeModule == null
                ? registry.RegisteredTypeKeys.ToArray()
                : registry.RegisteredTypeKeys.Where(key => !registeredBeforeModule.Contains(key)).ToArray();

            foreach (var typeKey in keysToInspect)
            {
                if (disabledKeys.Contains(typeKey))
                {
                    registry.Unregister(typeKey);
                }
            }
        }

        private static IEnumerable<Assembly> LoadAssemblies(string directoryPath, RoiPluginDiscoveryOptions options, IImageViewerLogger? logger)
        {
            var disabledAssemblyNames = new HashSet<string>(options.DisabledAssemblyNames, StringComparer.OrdinalIgnoreCase);
            string[] allowedAssemblyNamePrefixes = options.AllowedAssemblyNamePrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .ToArray();

            if (allowedAssemblyNamePrefixes.Length == 0)
            {
                // 没有声明前缀就不加载、不扫描任何程序集：宁可"什么都没发现"，也不要把应用目录里的依赖当成插件候选。
                logger?.LogWarning(
                    $"ROI plugin discovery skipped: '{nameof(RoiPluginDiscoveryOptions.AllowedAssemblyNamePrefixes)}' is empty. "
                    + "Declare the plugin assembly name prefixes (for example \"Acme.RoiPlugins\") to enable discovery.");
                yield break;
            }

            var consideredAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            if (options.ScanLoadedAssemblies)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic)
                    {
                        continue;
                    }

                    string simpleName = assembly.GetName().Name ?? string.Empty;
                    if (!IsAllowedAssemblyName(simpleName, disabledAssemblyNames, allowedAssemblyNamePrefixes))
                    {
                        continue;
                    }

                    consideredAssemblies[assembly.FullName ?? simpleName] = assembly;
                }
            }

            foreach (var assembly in consideredAssemblies.Values)
            {
                yield return assembly;
            }

            if (!Directory.Exists(directoryPath))
            {
                yield break;
            }

            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                // 先用文件名过滤：非候选文件连元数据都不读，避免为了"看看是什么"而打开无关 DLL
                // （非托管 DLL 会因此留下一条"跳过非托管程序集"的警告，宿主依赖多时就是成片噪音）。
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                if (!IsAllowedAssemblyName(fileName, disabledAssemblyNames, allowedAssemblyNamePrefixes))
                {
                    continue;
                }

                AssemblyName assemblyName;
                try
                {
                    assemblyName = AssemblyName.GetAssemblyName(filePath);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Skipping non-.NET assembly '{filePath}': {ex.Message}");
                    continue;
                }

                // 再用元数据名二次校验：文件名可以随便改，真正决定身份的是程序集元数据。
                string simpleName = assemblyName.Name ?? fileName;
                if (!IsAllowedAssemblyName(simpleName, disabledAssemblyNames, allowedAssemblyNamePrefixes))
                {
                    continue;
                }

                if (consideredAssemblies.Values.Any(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName)))
                {
                    continue;
                }

                Assembly? assembly = null;
                try
                {
                    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Failed to load plugin assembly '{filePath}'.", ex);
                }

                if (assembly != null)
                {
                    consideredAssemblies[assembly.FullName ?? assembly.GetName().Name ?? filePath] = assembly;
                    yield return assembly;
                }
            }
        }

        /// <summary>
        /// 判定程序集是否可以进入插件扫描。
        /// Chinese: 必须命中至少一个允许前缀，且不在禁用名单里——禁用优先于允许。
        /// English: An assembly is considered only when it matches an allowed prefix and is not explicitly disabled;
        /// the disabled list wins over the allowed prefixes.
        /// </summary>
        internal static bool IsAllowedAssemblyName(
            string simpleName,
            IReadOnlySet<string> disabledAssemblyNames,
            IReadOnlyList<string> allowedAssemblyNamePrefixes)
        {
            if (string.IsNullOrWhiteSpace(simpleName) || disabledAssemblyNames.Contains(simpleName))
            {
                return false;
            }

            for (int index = 0; index < allowedAssemblyNamePrefixes.Count; index++)
            {
                if (simpleName.StartsWith(allowedAssemblyNamePrefixes[index], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Type[] GetLoadableTypes(
            Assembly assembly,
            List<RoiPluginDiscoveryFailure> failures,
            IImageViewerLogger? logger)
        {
            string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "unknown";
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Exception[] loaderExceptions = (ex.LoaderExceptions ?? []).OfType<Exception>().ToArray();
                if (loaderExceptions.Length == 0)
                {
                    failures.Add(new RoiPluginDiscoveryFailure($"assembly:{assemblyName}", ex));
                    logger?.LogError($"Failed to inspect plugin types in '{assemblyName}'.", ex);
                }
                else
                {
                    int index = 0;
                    foreach (Exception loaderException in loaderExceptions)
                    {
                        index++;
                        failures.Add(new RoiPluginDiscoveryFailure($"assembly:{assemblyName}:loader{index}", loaderException));
                        logger?.LogWarning(
                            $"Failed to load a type from plugin assembly '{assemblyName}': {loaderException.Message}");
                    }
                }

                return ex.Types.OfType<Type>().ToArray();
            }
            catch (Exception ex)
            {
                failures.Add(new RoiPluginDiscoveryFailure($"assembly:{assemblyName}", ex));
                logger?.LogError($"Failed to inspect plugin assembly '{assemblyName}'.", ex);
                return [];
            }
        }
    }
}
