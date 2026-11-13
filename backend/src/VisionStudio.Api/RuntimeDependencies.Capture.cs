using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed partial class RuntimeDependencyManifestService
{
    public async Task<RuntimeDependencyManifest> CaptureAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var expandedModules = moduleExpander is null ? new ExpandedWorkflowModules(workflow, []) : await moduleExpander.ExpandAsync(workflow, ct);
        workflow = expandedModules.Workflow;
        await ValidateCalibrationBindingsAsync(workflow, ct);
        var moduleDependencies = expandedModules.Dependencies
            .GroupBy(x => new { x.ModuleId, x.Version, x.ModuleHash })
            .Select(x => new WorkflowModuleRuntimeDependency(x.Key.ModuleId, x.Key.Version, x.Key.ModuleHash))
            .OrderBy(x => x.ModuleId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Version).ToArray();
        var pluginNodeTypes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var cameraIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var synchronizationGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var robotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var calibrationRefs = new HashSet<(string AssetId, int Version)>(new CalibrationRefComparer());
        var modelAssetRefs = new HashSet<(string AssetId, int Version)>(new ModelAssetRefComparer());

        foreach (var node in workflow.Nodes)
        {
            var registration = registry.Require(node.Type);
            if (!string.Equals(registration.Source, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                if (!pluginNodeTypes.TryGetValue(registration.Source, out var usedTypes))
                    pluginNodeTypes[registration.Source] = usedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedTypes.Add(node.Type);
            }

            CollectNodeResources(node, registration.Catalog, cameraIds, synchronizationGroupIds, deviceIds, robotIds);

            var assetId = ResolveString(node, registration.Catalog, "assetId");
            var assetVersion = ResolveInt(node, registration.Catalog, "assetVersion");
            if (!string.IsNullOrWhiteSpace(assetId) && assetVersion > 0)
                calibrationRefs.Add((assetId, assetVersion));

            if (string.Equals(node.Type, "match.shape", StringComparison.OrdinalIgnoreCase))
            {
                var modelAssetId = ResolveString(node, registration.Catalog, "modelAssetId");
                if (!string.IsNullOrWhiteSpace(modelAssetId))
                    modelAssetRefs.Add((modelAssetId!, ResolveInt(node, registration.Catalog, "modelAssetVersion")));
            }
            if (string.Equals(node.Type, "match.template", StringComparison.OrdinalIgnoreCase))
            {
                // 模板图资产与模型资产共用同一份 pin 列表（assetId/version/sha 与 kind 无关）。
                var templateAssetId = ResolveString(node, registration.Catalog, "templateAssetId");
                if (!string.IsNullOrWhiteSpace(templateAssetId))
                    modelAssetRefs.Add((templateAssetId!, ResolveInt(node, registration.Catalog, "templateAssetVersion")));
            }

            if (string.Equals(node.Type, "image.undistort", StringComparison.OrdinalIgnoreCase))
            {
                var intrinsicAssetId = ResolveString(node, registration.Catalog, "intrinsicsAssetId");
                var intrinsicAssetVersion = ResolveInt(node, registration.Catalog, "intrinsicsAssetVersion");
                if (!string.IsNullOrWhiteSpace(intrinsicAssetId) && intrinsicAssetVersion > 0)
                    calibrationRefs.Add((IntrinsicCalibrationReference(intrinsicAssetId!), intrinsicAssetVersion));
            }
            if (string.Equals(node.Type, "calibration.handEye2D", StringComparison.OrdinalIgnoreCase))
            {
                var handEyeAssetId = ResolveString(node, registration.Catalog, "handEyeAssetId");
                var handEyeAssetVersion = ResolveInt(node, registration.Catalog, "handEyeAssetVersion");
                if (!string.IsNullOrWhiteSpace(handEyeAssetId) && handEyeAssetVersion > 0)
                    calibrationRefs.Add((HandEyeCalibrationReference(handEyeAssetId!), handEyeAssetVersion));
            }
            if (string.Equals(node.Type, "calibration.handEye3D", StringComparison.OrdinalIgnoreCase))
            {
                var handEye3DAssetId = ResolveString(node, registration.Catalog, "handEye3DAssetId");
                var handEye3DAssetVersion = ResolveInt(node, registration.Catalog, "handEye3DAssetVersion");
                if (!string.IsNullOrWhiteSpace(handEye3DAssetId) && handEye3DAssetVersion > 0)
                    calibrationRefs.Add((HandEye3DCalibrationReference(handEye3DAssetId!), handEye3DAssetVersion));
            }
        }

        if (synchronizationGroupIds.Count > 0)
        {
            if (synchronizationGroups is null)
                throw new InvalidOperationException("Workflow references a camera synchronization group, but synchronization storage is unavailable.");
            foreach (var groupId in synchronizationGroupIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var group = await synchronizationGroups.GetAsync(groupId, ct);
                foreach (var cameraId in group.CameraIds) cameraIds.Add(cameraId);
            }
        }

        var engine = CaptureEngine();
        var pluginDependencies = CapturePlugins(pluginNodeTypes);
        var cameraDependencies = await CaptureCamerasAsync(cameraIds, ct);
        var deviceDependencies = await CaptureDevicesAsync(deviceIds, ct);
        var robotDependencies = await CaptureRobotsAsync(robotIds, ct);
        var calibrationDependencies = await CaptureCalibrationsAsync(calibrationRefs, ct);
        var modelAssetDependencies = await CaptureAssetPinsAsync(modelAssetRefs, ct);

        // ManifestHash deliberately excludes capture/display timestamps such as Device ProfileRevision.
        // SDK 2.0 plugin workflows advance to schema v5 so package-manifest + referenced tool versions become
        // production provenance. Legacy/module-free workflows preserve their exact historical canonical shapes.
        var hasWorkerPlugin = pluginDependencies.Any(x => string.Equals(x.IsolationMode, nameof(VisionPluginIsolationMode.WorkerProcess), StringComparison.OrdinalIgnoreCase));
        var hasSdk2Plugin = pluginDependencies.Any(x => (x.SdkApiVersion ?? 1) >= 2);
        // schema 8 只在出现模型资产锁定时使用：不含资产的流程继续走 3/4/5/7，历史 ManifestHash 不变。
        var manifestSchemaVersion = modelAssetDependencies.Length > 0 ? 8
            : hasWorkerPlugin ? 7 : hasSdk2Plugin ? 5 : moduleDependencies.Length > 0 ? 4 : 3;
        object canonical;
        if (manifestSchemaVersion == 3)
        {
            canonical = new
            {
                schemaVersion = 3,
                engine = new { engine.Name, engine.Version, engine.InformationalVersion, engine.BinaryHash },
                plugins = pluginDependencies.Select(x => new { x.Id, x.Version, x.AssemblyHash }).ToArray(),
                cameras = cameraDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint, synchronizationGroupHashes = x.SynchronizationGroupHashes ?? [] }).ToArray(),
                devices = deviceDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                robots = robotDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                calibrations = calibrationDependencies.Select(x => new { x.AssetId, x.Version, x.SnapshotHash }).ToArray()
            };
        }
        else if (manifestSchemaVersion == 4)
        {
            canonical = new
            {
                schemaVersion = 4,
                engine = new { engine.Name, engine.Version, engine.InformationalVersion, engine.BinaryHash },
                plugins = pluginDependencies.Select(x => new { x.Id, x.Version, x.AssemblyHash }).ToArray(),
                cameras = cameraDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint, synchronizationGroupHashes = x.SynchronizationGroupHashes ?? [] }).ToArray(),
                devices = deviceDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                robots = robotDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                calibrations = calibrationDependencies.Select(x => new { x.AssetId, x.Version, x.SnapshotHash }).ToArray(),
                modules = moduleDependencies.Select(x => new { x.ModuleId, x.Version, x.ModuleHash }).ToArray()
            };
        }
        else if (manifestSchemaVersion == 5)
        {
            canonical = new
            {
                schemaVersion = 5,
                engine = new { engine.Name, engine.Version, engine.InformationalVersion, engine.BinaryHash },
                plugins = pluginDependencies.Select(x => new
                {
                    x.Id, x.Version, x.AssemblyHash,
                    sdkApiVersion = x.SdkApiVersion ?? 1,
                    packageManifestHash = x.PackageManifestHash,
                    tools = (x.Tools ?? []).OrderBy(t => t.Type, StringComparer.OrdinalIgnoreCase).Select(t => new { t.Type, t.Version }).ToArray()
                }).ToArray(),
                cameras = cameraDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint, synchronizationGroupHashes = x.SynchronizationGroupHashes ?? [] }).ToArray(),
                devices = deviceDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                robots = robotDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                calibrations = calibrationDependencies.Select(x => new { x.AssetId, x.Version, x.SnapshotHash }).ToArray(),
                modules = moduleDependencies.Select(x => new { x.ModuleId, x.Version, x.ModuleHash }).ToArray()
            };
        }
        else if (manifestSchemaVersion == 8)
        {
            canonical = new
            {
                schemaVersion = 8,
                engine = new { engine.Name, engine.Version, engine.InformationalVersion, engine.BinaryHash },
                plugins = pluginDependencies.Select(x => new
                {
                    x.Id, x.Version, x.AssemblyHash,
                    sdkApiVersion = x.SdkApiVersion ?? 1,
                    packageManifestHash = x.PackageManifestHash,
                    isolationMode = x.IsolationMode,
                    workerProtocolVersion = x.WorkerProtocolVersion,
                    workerPoolSize = x.WorkerPoolSize,
                    workerImageTransport = x.WorkerImageTransport,
                    workerSharedMemoryThresholdBytes = x.WorkerSharedMemoryThresholdBytes,
                    tools = (x.Tools ?? []).OrderBy(t => t.Type, StringComparer.OrdinalIgnoreCase).Select(t => new { t.Type, t.Version }).ToArray()
                }).ToArray(),
                cameras = cameraDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint, synchronizationGroupHashes = x.SynchronizationGroupHashes ?? [] }).ToArray(),
                devices = deviceDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                robots = robotDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                calibrations = calibrationDependencies.Select(x => new { x.AssetId, x.Version, x.SnapshotHash }).ToArray(),
                modules = moduleDependencies.Select(x => new { x.ModuleId, x.Version, x.ModuleHash }).ToArray(),
                modelAssets = modelAssetDependencies.Select(x => new { x.AssetId, x.Version, x.Sha256 }).ToArray()
            };
        }
        else
        {
            canonical = new
            {
                schemaVersion = 7,
                engine = new { engine.Name, engine.Version, engine.InformationalVersion, engine.BinaryHash },
                plugins = pluginDependencies.Select(x => new
                {
                    x.Id, x.Version, x.AssemblyHash,
                    sdkApiVersion = x.SdkApiVersion ?? 1,
                    packageManifestHash = x.PackageManifestHash,
                    isolationMode = x.IsolationMode,
                    workerProtocolVersion = x.WorkerProtocolVersion,
                    workerPoolSize = x.WorkerPoolSize,
                    workerImageTransport = x.WorkerImageTransport,
                    workerSharedMemoryThresholdBytes = x.WorkerSharedMemoryThresholdBytes,
                    tools = (x.Tools ?? []).OrderBy(t => t.Type, StringComparer.OrdinalIgnoreCase).Select(t => new { t.Type, t.Version }).ToArray()
                }).ToArray(),
                cameras = cameraDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint, synchronizationGroupHashes = x.SynchronizationGroupHashes ?? [] }).ToArray(),
                devices = deviceDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                robots = robotDependencies.Select(x => new { x.Id, x.ConfigurationHash, hardwareFingerprint = x.Hardware?.Fingerprint }).ToArray(),
                calibrations = calibrationDependencies.Select(x => new { x.AssetId, x.Version, x.SnapshotHash }).ToArray(),
                modules = moduleDependencies.Select(x => new { x.ModuleId, x.Version, x.ModuleHash }).ToArray()
            };
        }
        var hash = HashCanonical(canonical);
        return new RuntimeDependencyManifest(
            manifestSchemaVersion,
            hash,
            DateTimeOffset.UtcNow,
            engine,
            pluginDependencies,
            cameraDependencies,
            deviceDependencies,
            robotDependencies,
            calibrationDependencies,
            moduleDependencies,
            modelAssetDependencies.Length > 0 ? modelAssetDependencies : null);
    }

    private static EngineRuntimeDependency CaptureEngine()
    {
        var assembly = typeof(VisionNodeRegistry).Assembly;
        var name = assembly.GetName();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? name.Version?.ToString()
            ?? "unknown";
        return new EngineRuntimeDependency(
            name.Name ?? "VisionStudio.Engine",
            name.Version?.ToString() ?? "unknown",
            info,
            FingerprintAssembly(assembly));
    }

    private IReadOnlyList<PluginRuntimeDependency> CapturePlugins(Dictionary<string, HashSet<string>> pluginNodeTypes)
    {
        if (pluginNodeTypes.Count == 0) return [];
        var loaded = plugins.Plugins
            .Where(x => x.Loaded)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var output = new List<PluginRuntimeDependency>();
        foreach (var pair in pluginNodeTypes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var id = pair.Key;
            if (!loaded.TryGetValue(id, out var plugin))
                throw new InvalidOperationException($"Workflow references plugin '{id}', but that plugin is not loaded.");
            if (string.IsNullOrWhiteSpace(plugin.AssemblySha256))
                throw new InvalidOperationException($"Plugin '{id}' has no binary hash and cannot be locked for production.");

            var tools = (plugin.Tools ?? [])
                .Where(x => pair.Value.Contains(x.Type))
                .OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
                .Select(x => new PluginToolRuntimeDependency(x.Type, x.ToolVersion ?? "legacy-unspecified"))
                .ToArray();
            if (plugin.SdkApiVersion >= 2 && tools.Length != pair.Value.Count)
                throw new InvalidOperationException($"SDK 2.0 plugin '{id}' is missing tool-version metadata for one or more referenced node types.");

            output.Add(new PluginRuntimeDependency(
                plugin.Id, plugin.Name, plugin.Version, plugin.AssemblySha256,
                plugin.SdkApiVersion,
                plugin.PackageManifestSha256,
                tools,
                plugin.IsolationMode == VisionPluginIsolationMode.WorkerProcess ? plugin.IsolationMode.ToString() : null,
                plugin.IsolationMode == VisionPluginIsolationMode.WorkerProcess ? plugin.WorkerProtocolVersion : null,
                plugin.IsolationMode == VisionPluginIsolationMode.WorkerProcess ? plugin.WorkerPoolSize : null,
                plugin.IsolationMode == VisionPluginIsolationMode.WorkerProcess ? plugin.WorkerImageTransport : null,
                plugin.IsolationMode == VisionPluginIsolationMode.WorkerProcess ? plugin.WorkerSharedMemoryThresholdBytes : null));
        }
        return output;
    }

    private static string PluginFingerprint(PluginRuntimeDependency plugin)
    {
        if ((plugin.SdkApiVersion ?? 1) < 2) return plugin.AssemblyHash;
        var tools = string.Join(",", (plugin.Tools ?? []).OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Type}@{x.Version}"));
        var isolation = string.IsNullOrWhiteSpace(plugin.IsolationMode) ? "" : $"|isolation={plugin.IsolationMode}";
        var worker = string.IsNullOrWhiteSpace(plugin.IsolationMode)
            ? ""
            : $"|workerProtocol={plugin.WorkerProtocolVersion}|pool={plugin.WorkerPoolSize}|imageTransport={plugin.WorkerImageTransport}|sharedThreshold={plugin.WorkerSharedMemoryThresholdBytes}";
        return $"{plugin.AssemblyHash}|sdk={plugin.SdkApiVersion}|manifest={plugin.PackageManifestHash}|tools={tools}{isolation}{worker}";
    }

    private static void CollectStringReference(NodeDefinition node, NodeCatalogItem catalog, string name, HashSet<string> output)
    {
        var value = ResolveString(node, catalog, name);
        if (!string.IsNullOrWhiteSpace(value)) output.Add(value);
    }

    /// <summary>
    /// 扩展安全契约：按目录**声明的资源请求**收集硬件资源 ID（声明承载资源 ID 的参数名），
    /// 不再按 cameraId/groupId/deviceId/robotId 参数名约定猜测插件的资源。
    /// 未声明资源的目录沿用参数名约定（兼容回退）；未知资源类别直接失败关闭（目录契约写错必须暴露）。
    /// </summary>
    private static void CollectNodeResources(
        NodeDefinition node,
        NodeCatalogItem catalog,
        HashSet<string> cameraIds,
        HashSet<string> synchronizationGroupIds,
        HashSet<string> deviceIds,
        HashSet<string> robotIds)
    {
        if (catalog.Resources is { Count: > 0 } declarations)
        {
            foreach (var declaration in declarations)
            {
                // 契约校验与取值无关：类别写错必须在任何取值下都失败关闭。
                var kind = declaration.Kind.Trim().ToLowerInvariant();
                if (kind is not ("camera" or "camerasyncgroup" or "device" or "robot"))
                    throw new InvalidOperationException(
                        $"Node catalog '{catalog.Type}' declares unknown resource kind '{declaration.Kind}' " +
                        "(expected camera | cameraSyncGroup | device | robot).");
                var value = ResolveDeclaredResourceValue(node, catalog, declaration.Parameter);
                if (string.IsNullOrWhiteSpace(value)) continue;
                switch (kind)
                {
                    case "camera": cameraIds.Add(value); break;
                    case "camerasyncgroup": synchronizationGroupIds.Add(value); break;
                    case "device": deviceIds.Add(value); break;
                    case "robot": robotIds.Add(value); break;
                }
            }
            return;
        }

        CollectStringReference(node, catalog, "cameraId", cameraIds);
        CollectStringReference(node, catalog, "groupId", synchronizationGroupIds);
        CollectStringReference(node, catalog, "deviceId", deviceIds);
        CollectStringReference(node, catalog, "robotId", robotIds);
    }

    /// <summary>
    /// 声明的资源参数以**声明本身为契约**：即使目录 Parameters 未逐一列出该参数也要读取节点取值
    ///（插件可以只声明资源而不把参数暴露为可编辑项）；缺省值仍按参数描述符解析。
    /// </summary>
    private static string? ResolveDeclaredResourceValue(NodeDefinition node, NodeCatalogItem catalog, string name)
        => NodeParameterValue.Resolve(node, catalog, name);

    private static string? ResolveString(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        // 资产参数必须先在目录里声明；未声明的键不参与依赖捕获。
        if (!catalog.Parameters.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
        return NodeParameterValue.Resolve(node, catalog, name);
    }

    private static int ResolveInt(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        var descriptor = catalog.Parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return 0;
        if (node.Parameters is not null && node.Parameters.TryGetValue(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)) return parsed;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
        }
        return descriptor.DefaultValue switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var i) => i,
            _ when int.TryParse(NodeParameterValue.FormatInvariant(descriptor.DefaultValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }
}
