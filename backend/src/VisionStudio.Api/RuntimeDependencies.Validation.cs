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
            CollectNodeResources(node, registration.Catalog, cameraIds, synchronizationGroupIds, deviceIds, robotIds);
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
        CompareById(expected.ModelAssets ?? [], current.ModelAssets ?? [], x => $"{x.AssetId}:v{x.Version}", x => x.Sha256, "ModelAsset", drifts);
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
}
