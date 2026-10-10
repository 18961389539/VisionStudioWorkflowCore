using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Provenance;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Api;

public sealed record EngineRuntimeDependency(
    string Name,
    string Version,
    string InformationalVersion,
    string BinaryHash);

public sealed record PluginToolRuntimeDependency(string Type, string Version);

public sealed record PluginRuntimeDependency(
    string Id,
    string Name,
    string Version,
    string AssemblyHash,
    int? SdkApiVersion = null,
    string? PackageManifestHash = null,
    IReadOnlyList<PluginToolRuntimeDependency>? Tools = null,
    string? IsolationMode = null,
    int? WorkerProtocolVersion = null,
    int? WorkerPoolSize = null,
    string? WorkerImageTransport = null,
    int? WorkerSharedMemoryThresholdBytes = null);

public sealed record CameraRuntimeDependency(
    string Id,
    string Driver,
    string? Source,
    string ConfigurationHash,
    CameraSettings Settings,
    string? SourceFingerprint = null,
    HardwareProvenanceSnapshot? Hardware = null,
    IReadOnlyList<string>? SynchronizationGroupHashes = null);

public sealed record DeviceRuntimeDependency(
    string Id,
    string Driver,
    string Protocol,
    string Endpoint,
    string ConfigurationHash,
    string? ProfileRevision,
    HardwareProvenanceSnapshot? Hardware = null);

public sealed record RobotRuntimeDependency(
    string Id,
    string Driver,
    string Vendor,
    string Model,
    string BaseFrame,
    string Unit,
    string ConfigurationHash,
    HardwareProvenanceSnapshot? Hardware = null);

public sealed record CalibrationRuntimeDependency(
    string AssetId,
    int Version,
    string SnapshotHash);

public sealed record WorkflowModuleRuntimeDependency(
    string ModuleId,
    int Version,
    string ModuleHash);

public sealed record RuntimeDependencyManifest(
    int SchemaVersion,
    string ManifestHash,
    DateTimeOffset CapturedAt,
    EngineRuntimeDependency Engine,
    IReadOnlyList<PluginRuntimeDependency> Plugins,
    IReadOnlyList<CameraRuntimeDependency> Cameras,
    IReadOnlyList<DeviceRuntimeDependency> Devices,
    IReadOnlyList<RobotRuntimeDependency> Robots,
    IReadOnlyList<CalibrationRuntimeDependency> Calibrations,
    IReadOnlyList<WorkflowModuleRuntimeDependency>? Modules = null);

public sealed record RuntimeDependencyDrift(
    string Kind,
    string Id,
    string Expected,
    string? Actual,
    string Message);

public sealed record RuntimeDependencyValidation(
    bool Compatible,
    string ExpectedManifestHash,
    string CurrentManifestHash,
    IReadOnlyList<RuntimeDependencyDrift> Drifts,
    RuntimeDependencyManifest CurrentManifest);

/// <summary>
/// Asset ids referenced by one workflow, resolved without capturing a full manifest.
/// Used to arbitrate ad-hoc/debug runs against a running Production Runtime.
/// </summary>
public sealed record WorkflowRuntimeReferences(
    IReadOnlyList<string> Cameras,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> Robots);

/// <summary>
/// Captures the exact runtime dependencies referenced by one Workflow. The resulting ManifestHash is
/// semantic: CapturedAt and display-only timestamps are excluded, so unchanged runtime dependencies
/// produce the same hash across repeated publications.
/// </summary>
/// <summary>恢复前设备状态核对结果；不通过时必须禁止自动恢复新周期。</summary>
public sealed record DeviceStateVerification(bool Ok, IReadOnlyList<string> Issues)
{
    public string Summary => Issues.Count == 0 ? "all devices verified" : string.Join("; ", Issues);
}

public sealed class RuntimeDependencyManifestService(
    VisionNodeRegistry registry,
    PluginManager plugins,
    CameraManager cameras,
    DeviceManager devices,
    DeviceProfileStore deviceProfiles,
    RobotManager robots,
    CalibrationAssetStore calibrations,
    HardwareProvenanceService hardwareProvenance,
    WorkflowModuleExpander? moduleExpander = null,
    CameraSynchronizationGroupStore? synchronizationGroups = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 副作用故障恢复前的设备状态核对：锁定清单中的每个设备/机器人当前必须处于"可安全重跑"
    /// 的状态——不只是能解析、不只是非 Faulted。设备必须真正 Connected（Disconnected/Connecting/
    /// Reconnecting 都无法确认上一周期的写入是否落地）；机器人必须 Connected、非 Busy、握手无 Error
    /// 且无未完成的在途命令。核对不通过时禁止恢复——重新启动自动周期前必须完成核对并留下记录。
    ///
    /// Get 返回的是管理器状态快照，不含 PLC 动作完成标志或机器人命令确认 ID；因此对"设备动作结果
    /// 不确定"的场景采取保守拒绝：任何非 Connected、Busy 或握手异常都视为不可恢复。
    /// </summary>
    public DeviceStateVerification VerifyLockedDevices(RuntimeDependencyManifest manifest)
        => VerifyLockedDevices(
            manifest.Devices.Select(x => x.Id).ToArray(),
            manifest.Robots.Select(x => x.Id).ToArray());

    /// <summary>
    /// F02：按 ID 清单核对设备/机器人状态（与 <see cref="VerifyLockedDevices(RuntimeDependencyManifest)"/>
    /// 同一判定标准）。持久化的"故障时锁定清单"可能不属于当前发布流程——核对必须无条件针对该清单，
    /// 否则切换到无设备的新流程时，空清单会必然通过。
    /// </summary>
    public DeviceStateVerification VerifyLockedDevices(IReadOnlyList<string> deviceIds, IReadOnlyList<string> robotIds)
    {
        var issues = new List<string>();
        foreach (var deviceId in deviceIds)
        {
            try
            {
                var descriptor = devices.Get(deviceId);
                if (descriptor.ConnectionState != DeviceConnectionState.Connected)
                    issues.Add($"device '{deviceId}' is {descriptor.ConnectionState}, not Connected — the previous cycle's writes cannot be confirmed ({descriptor.Error ?? "no detail"})");
            }
            catch (Exception ex)
            {
                issues.Add($"device '{deviceId}' cannot be resolved: {ex.Message}");
            }
        }
        foreach (var robotId in robotIds)
        {
            try
            {
                var descriptor = robots.Get(robotId);
                if (descriptor.ConnectionState != RobotConnectionState.Connected)
                {
                    issues.Add($"robot '{robotId}' is {descriptor.ConnectionState}, not Connected — the previous cycle's motion cannot be confirmed ({descriptor.Error ?? "no detail"})");
                    continue;
                }
                if (descriptor.Busy)
                    issues.Add($"robot '{robotId}' is still Busy with command {descriptor.LastCommandId} — an in-flight motion was not confirmed stopped");
                if (descriptor.HandshakeState == RobotHandshakeState.Faulted || descriptor.Handshake.Error)
                    issues.Add($"robot '{robotId}' reports a handshake Error (state {descriptor.HandshakeState}, command {descriptor.LastCommandId})");
                // 未完成命令：握手停在 Executing/TargetAccepted 说明上一条命令没有被确认收尾。
                if (descriptor.HandshakeState is RobotHandshakeState.Executing or RobotHandshakeState.TargetAccepted)
                    issues.Add($"robot '{robotId}' has an unconfirmed command {descriptor.LastCommandId} in state {descriptor.HandshakeState}");
                if (!string.IsNullOrWhiteSpace(descriptor.Error))
                    issues.Add($"robot '{robotId}' reports error: {descriptor.Error}");
            }
            catch (Exception ex)
            {
                issues.Add($"robot '{robotId}' cannot be resolved: {ex.Message}");
            }
        }
        return new DeviceStateVerification(issues.Count == 0, issues);
    }

    public async Task<RuntimeDependencyManifest> CaptureAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var expandedModules = moduleExpander is null ? new ExpandedWorkflowModules(workflow, []) : await moduleExpander.ExpandAsync(workflow, ct);
        workflow = expandedModules.Workflow;
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

        foreach (var node in workflow.Nodes)
        {
            var registration = registry.Require(node.Type);
            if (!string.Equals(registration.Source, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                if (!pluginNodeTypes.TryGetValue(registration.Source, out var usedTypes))
                    pluginNodeTypes[registration.Source] = usedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedTypes.Add(node.Type);
            }

            CollectStringReference(node, registration.Catalog, "cameraId", cameraIds);
            CollectStringReference(node, registration.Catalog, "groupId", synchronizationGroupIds);
            CollectStringReference(node, registration.Catalog, "deviceId", deviceIds);
            CollectStringReference(node, registration.Catalog, "robotId", robotIds);

            var assetId = ResolveString(node, registration.Catalog, "assetId");
            var assetVersion = ResolveInt(node, registration.Catalog, "assetVersion");
            if (!string.IsNullOrWhiteSpace(assetId) && assetVersion > 0)
                calibrationRefs.Add((assetId, assetVersion));
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

        // ManifestHash deliberately excludes capture/display timestamps such as Device ProfileRevision.
        // SDK 2.0 plugin workflows advance to schema v5 so package-manifest + referenced tool versions become
        // production provenance. Legacy/module-free workflows preserve their exact historical canonical shapes.
        var hasWorkerPlugin = pluginDependencies.Any(x => string.Equals(x.IsolationMode, nameof(VisionPluginIsolationMode.WorkerProcess), StringComparison.OrdinalIgnoreCase));
        var hasSdk2Plugin = pluginDependencies.Any(x => (x.SdkApiVersion ?? 1) >= 2);
        var manifestSchemaVersion = hasWorkerPlugin ? 7 : hasSdk2Plugin ? 5 : moduleDependencies.Length > 0 ? 4 : 3;
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
            moduleDependencies);
    }

    /// <summary>
    /// Lightweight reference scan (no manifest capture): resolves the camera/device/robot ids referenced
    /// by a workflow so callers can arbitrate runs against a locked Production Runtime manifest.
    /// Unknown node types are skipped - the run itself reports validation errors.
    /// </summary>
    public async Task<WorkflowRuntimeReferences> ExtractReferencesAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        try
        {
            if (moduleExpander is not null)
                workflow = (await moduleExpander.ExpandAsync(workflow, ct)).Workflow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Arbitration stays best-effort: an invalid module reference is reported by the run itself.
        }

        var cameraIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var synchronizationGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var robotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in workflow.Nodes)
        {
            if (!registry.IsRegistered(node.Type)) continue;
            var registration = registry.Require(node.Type);
            CollectStringReference(node, registration.Catalog, "cameraId", cameraIds);
            CollectStringReference(node, registration.Catalog, "groupId", synchronizationGroupIds);
            CollectStringReference(node, registration.Catalog, "deviceId", deviceIds);
            CollectStringReference(node, registration.Catalog, "robotId", robotIds);
        }

        if (synchronizationGroupIds.Count > 0 && synchronizationGroups is not null)
        {
            foreach (var groupId in synchronizationGroupIds)
            {
                try
                {
                    var group = await synchronizationGroups.GetAsync(groupId, ct);
                    foreach (var cameraId in group.CameraIds) cameraIds.Add(cameraId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Missing synchronization groups are a run-time validation error; skip for arbitration.
                }
            }
        }

        return new WorkflowRuntimeReferences(
            cameraIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            deviceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            robotIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<RuntimeDependencyValidation> ValidateAsync(
        RuntimeDependencyManifest expected,
        WorkflowDefinition workflow,
        CancellationToken ct)
    {
        var current = await CaptureAsync(workflow, ct);
        if (string.Equals(expected.ManifestHash, current.ManifestHash, StringComparison.OrdinalIgnoreCase))
            return new RuntimeDependencyValidation(true, expected.ManifestHash, current.ManifestHash, [], current);

        var drifts = new List<RuntimeDependencyDrift>();
        if (expected.SchemaVersion < 3)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "ManifestSchema", "runtime", $"v{expected.SchemaVersion}", $"v{current.SchemaVersion}",
                expected.SchemaVersion < 2
                    ? "Published Job predates hardware provenance and camera synchronization provenance. Re-publish the Job."
                    : "Published Job predates multi-camera synchronization provenance. Re-publish the Job to lock synchronization-group configuration."));
        }
        if (current.Modules is { Count: > 0 } && expected.SchemaVersion < 4)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "ManifestSchema", "workflow-modules", $"v{expected.SchemaVersion}", $"v{current.SchemaVersion}",
                "Published Job references reusable Workflow Modules but its manifest predates module-version provenance. Re-publish the Job."));
        }
        if (current.Plugins.Any(x => (x.SdkApiVersion ?? 1) >= 2) && expected.SchemaVersion < 5)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "ManifestSchema", "plugin-sdk", $"v{expected.SchemaVersion}", $"v{current.SchemaVersion}",
                "Published Job references SDK 2.0 plugin tools but its manifest predates package/tool-version provenance. Re-publish the Job."));
        }
        if (current.Plugins.Any(x => string.Equals(x.IsolationMode, nameof(VisionPluginIsolationMode.WorkerProcess), StringComparison.OrdinalIgnoreCase)) && expected.SchemaVersion < 6)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "ManifestSchema", "plugin-worker", $"v{expected.SchemaVersion}", $"v{current.SchemaVersion}",
                "Published Job references a process-isolated plugin but its manifest predates isolation-mode provenance. Re-publish the Job."));
        }
        if (current.Plugins.Any(x => string.Equals(x.IsolationMode, nameof(VisionPluginIsolationMode.WorkerProcess), StringComparison.OrdinalIgnoreCase)) && expected.SchemaVersion < 7)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "ManifestSchema", "plugin-worker-runtime", $"v{expected.SchemaVersion}", $"v{current.SchemaVersion}",
                "Published Job references a process-isolated plugin but its manifest predates worker protocol/pool/image-transport provenance. Re-publish the Job under V0.57+ because worker protocol v3 adds execution timing telemetry."));
        }

        if (!string.Equals(expected.Engine.BinaryHash, current.Engine.BinaryHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.Engine.InformationalVersion, current.Engine.InformationalVersion, StringComparison.Ordinal))
        {
            drifts.Add(new RuntimeDependencyDrift(
                "Engine",
                expected.Engine.Name,
                $"{expected.Engine.InformationalVersion} / {expected.Engine.BinaryHash}",
                $"{current.Engine.InformationalVersion} / {current.Engine.BinaryHash}",
                "VisionStudio.Engine binary/version changed after Job publication."));
        }

        CompareById(expected.Plugins, current.Plugins, x => x.Id, PluginFingerprint, "Plugin", drifts);
        CompareById(expected.Cameras, current.Cameras, x => x.Id, x => x.ConfigurationHash, "Camera", drifts);
        CompareById(expected.Devices, current.Devices, x => x.Id, x => x.ConfigurationHash, "Device", drifts);
        CompareById(expected.Robots, current.Robots, x => x.Id, x => x.ConfigurationHash, "Robot", drifts);
        CompareHardware(expected.Cameras, current.Cameras, x => x.Id, x => x.Hardware, "CameraHardware", drifts);
        CompareHardware(expected.Devices, current.Devices, x => x.Id, x => x.Hardware, "DeviceHardware", drifts);
        CompareHardware(expected.Robots, current.Robots, x => x.Id, x => x.Hardware, "RobotHardware", drifts);
        CompareById(expected.Calibrations, current.Calibrations, x => $"{x.AssetId}:v{x.Version}", x => x.SnapshotHash, "Calibration", drifts);
        CompareById(expected.Modules ?? [], current.Modules ?? [], x => $"{x.ModuleId}:v{x.Version}", x => x.ModuleHash, "WorkflowModule", drifts);

        if (drifts.Count == 0)
        {
            drifts.Add(new RuntimeDependencyDrift(
                "Manifest", "runtime", expected.ManifestHash, current.ManifestHash,
                "Runtime manifest changed even though no category-level difference was identified."));
        }
        return new RuntimeDependencyValidation(false, expected.ManifestHash, current.ManifestHash, drifts, current);
    }

    public async Task ValidateOrThrowAsync(RuntimeDependencyManifest expected, WorkflowDefinition workflow, CancellationToken ct)
    {
        RuntimeDependencyValidation validation;
        try
        {
            validation = await ValidateAsync(expected, workflow, ct);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or InvalidDataException or IOException)
        {
            throw new ApiConflictException($"Runtime dependency validation failed: {ex.Message}", ex);
        }

        if (validation.Compatible) return;
        var detail = string.Join("; ", validation.Drifts.Take(5).Select(x => $"{x.Kind} '{x.Id}': {x.Message}"));
        if (validation.Drifts.Count > 5) detail += $"; +{validation.Drifts.Count - 5} more drift(s)";
        throw new ApiConflictException(
            $"Published Job dependency manifest no longer matches the current runtime. Expected {expected.ManifestHash}, current {validation.CurrentManifestHash}. {detail}");
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

    private async Task<IReadOnlyList<CameraRuntimeDependency>> CaptureCamerasAsync(HashSet<string> cameraIds, CancellationToken ct)
    {
        var output = new List<CameraRuntimeDependency>();
        foreach (var id in cameraIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            CameraDescriptor descriptor;
            try { descriptor = cameras.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references camera '{id}', but it is not registered.", ex); }
            var sourceProvenance = cameras.Require(id) is ICameraSourceProvenanceProvider provenanceProvider
                ? provenanceProvider.GetSourceProvenance()
                : null;
            var config = new
            {
                descriptor.Id,
                descriptor.Name,
                descriptor.Driver,
                descriptor.Source,
                sourceFingerprint = sourceProvenance?.ContentSha256,
                sourceItemCount = sourceProvenance?.ItemCount,
                sourceBytes = sourceProvenance?.TotalBytes,
                settings = descriptor.Settings,
                capabilities = descriptor.Capabilities,
                commissioningProfileHash = cameras.GetCommissioningProfileHash(descriptor.Id)
            };
            output.Add(new CameraRuntimeDependency(
                descriptor.Id,
                descriptor.Driver,
                descriptor.Source,
                HashCanonical(config),
                descriptor.Settings,
                sourceProvenance?.ContentSha256,
                await hardwareProvenance.CaptureForProductionAsync("camera", descriptor.Id, ct),
                synchronizationGroups is null ? [] : await synchronizationGroups.GetConfigurationHashesForCameraAsync(descriptor.Id, ct)));
        }
        return output;
    }

    private async Task<IReadOnlyList<DeviceRuntimeDependency>> CaptureDevicesAsync(HashSet<string> deviceIds, CancellationToken ct)
    {
        if (deviceIds.Count == 0) return [];
        var profiles = (await deviceProfiles.ListAsync(ct)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var output = new List<DeviceRuntimeDependency>();
        foreach (var id in deviceIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            DeviceDescriptor descriptor;
            try { descriptor = devices.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references device '{id}', but it is not registered.", ex); }

            if (profiles.TryGetValue(id, out var profile))
            {
                var stableProfile = new
                {
                    profile.Id,
                    profile.Kind,
                    profile.Name,
                    profile.Host,
                    profile.Port,
                    profile.TimeoutMs,
                    profile.UnitId,
                    profile.RegisterOrder,
                    profile.CpuType,
                    profile.Rack,
                    profile.Slot,
                    tags = (profile.Tags ?? []).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    settings = (profile.Settings ?? new DeviceRuntimeSettings()).Normalize()
                };
                output.Add(new DeviceRuntimeDependency(
                    descriptor.Id,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    HashCanonical(stableProfile),
                    profile.UpdatedAt?.ToUniversalTime().ToString("O"),
                    await hardwareProvenance.CaptureForProductionAsync("device", descriptor.Id, ct)));
            }
            else
            {
                var stableRuntime = new
                {
                    descriptor.Id,
                    descriptor.Name,
                    descriptor.Vendor,
                    descriptor.Model,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    settings = descriptor.Settings,
                    capabilities = descriptor.Capabilities,
                    tags = descriptor.Tags.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray()
                };
                output.Add(new DeviceRuntimeDependency(
                    descriptor.Id,
                    descriptor.Driver,
                    descriptor.Protocol,
                    descriptor.Endpoint,
                    HashCanonical(stableRuntime),
                    null,
                    await hardwareProvenance.CaptureForProductionAsync("device", descriptor.Id, ct)));
            }
        }
        return output;
    }

    private async Task<IReadOnlyList<RobotRuntimeDependency>> CaptureRobotsAsync(HashSet<string> robotIds, CancellationToken ct)
    {
        var output = new List<RobotRuntimeDependency>();
        foreach (var id in robotIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            RobotDescriptor descriptor;
            try { descriptor = robots.Get(id); }
            catch (KeyNotFoundException ex) { throw new InvalidOperationException($"Workflow references robot '{id}', but it is not registered.", ex); }
            var stable = new
            {
                descriptor.Id,
                descriptor.Name,
                descriptor.Vendor,
                descriptor.Model,
                descriptor.Driver,
                descriptor.BaseFrame,
                descriptor.Unit,
                settings = descriptor.Settings,
                capabilities = descriptor.Capabilities
            };
            output.Add(new RobotRuntimeDependency(
                descriptor.Id,
                descriptor.Driver,
                descriptor.Vendor,
                descriptor.Model,
                descriptor.BaseFrame,
                descriptor.Unit,
                HashCanonical(stable),
                await hardwareProvenance.CaptureForProductionAsync("robot", descriptor.Id, ct)));
        }
        return output;
    }

    private async Task<IReadOnlyList<CalibrationRuntimeDependency>> CaptureCalibrationsAsync(
        HashSet<(string AssetId, int Version)> references,
        CancellationToken ct)
    {
        var output = new List<CalibrationRuntimeDependency>();
        foreach (var reference in references.OrderBy(x => x.AssetId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Version))
        {
            var snapshot = await calibrations.GetVersionAsync(reference.AssetId, reference.Version, ct)
                ?? throw new InvalidOperationException($"Workflow references calibration '{reference.AssetId}' v{reference.Version}, but that immutable version does not exist.");
            output.Add(new CalibrationRuntimeDependency(snapshot.AssetId, snapshot.Version, snapshot.SnapshotHash));
        }
        return output;
    }

    private static void CollectStringReference(NodeDefinition node, NodeCatalogItem catalog, string name, HashSet<string> output)
    {
        var value = ResolveString(node, catalog, name);
        if (!string.IsNullOrWhiteSpace(value)) output.Add(value);
    }

    private static string? ResolveString(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        var descriptor = catalog.Parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return null;
        if (node.Parameters is not null && node.Parameters.TryGetValue(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null
            };
        }
        return descriptor.DefaultValue?.ToString();
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
            _ when int.TryParse(descriptor.DefaultValue?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string FingerprintAssembly(Assembly assembly)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
            {
                using var stream = File.OpenRead(assembly.Location);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }
        catch (Exception) when (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location)) { }
        return "mvid:" + assembly.ManifestModule.ModuleVersionId.ToString("N");
    }

    private static string HashCanonical(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void CompareById<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> current,
        Func<T, string> id,
        Func<T, string> fingerprint,
        string kind,
        List<RuntimeDependencyDrift> output)
    {
        var expectedMap = expected.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        var currentMap = current.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expectedMap)
        {
            if (!currentMap.TryGetValue(key, out var actual))
            {
                output.Add(new RuntimeDependencyDrift(kind, key, fingerprint(value), null, $"Expected {kind.ToLowerInvariant()} is no longer available."));
                continue;
            }
            var expectedHash = fingerprint(value);
            var actualHash = fingerprint(actual);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                output.Add(new RuntimeDependencyDrift(kind, key, expectedHash, actualHash, $"{kind} configuration/binary changed after publication."));
        }
        foreach (var (key, value) in currentMap)
        {
            if (!expectedMap.ContainsKey(key))
                output.Add(new RuntimeDependencyDrift(kind, key, "<not present>", fingerprint(value), $"Current Workflow resolves an unexpected {kind.ToLowerInvariant()} dependency."));
        }
    }

    private static void CompareHardware<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> current,
        Func<T, string> id,
        Func<T, HardwareProvenanceSnapshot?> hardware,
        string kind,
        List<RuntimeDependencyDrift> output)
    {
        var expectedMap = expected.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        var currentMap = current.ToDictionary(id, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expectedMap)
        {
            if (!currentMap.TryGetValue(key, out var actual)) continue;
            var expectedHardware = hardware(value);
            var currentHardware = hardware(actual);
            if (expectedHardware is null)
            {
                output.Add(new RuntimeDependencyDrift(kind, key, "<not captured>", currentHardware?.Fingerprint,
                    "Published Job has no hardware provenance for this asset. Re-publish after reviewing hardware identity."));
                continue;
            }
            if (currentHardware is null || !string.Equals(expectedHardware.Fingerprint, currentHardware.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                output.Add(new RuntimeDependencyDrift(kind, key, expectedHardware.Fingerprint, currentHardware?.Fingerprint,
                    "Physical/controller provenance changed after publication."));
            }
        }
    }

    private sealed class CalibrationRefComparer : IEqualityComparer<(string AssetId, int Version)>
    {
        public bool Equals((string AssetId, int Version) x, (string AssetId, int Version) y)
            => x.Version == y.Version && string.Equals(x.AssetId, y.AssetId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string AssetId, int Version) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.AssetId), obj.Version);
    }
}
