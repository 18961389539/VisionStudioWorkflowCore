using System.Security.Cryptography;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed partial class PluginManager
{
    private static void ValidateCandidate(PackageCandidate candidate)
    {
        if (!File.Exists(candidate.AssemblyPath))
            throw new InvalidOperationException($"Plugin entry assembly '{candidate.AssemblyPath}' was not found.");
        if (candidate.Manifest is null) return;
        var manifest = candidate.Manifest;
        if (manifest.SchemaVersion != VisionPluginSdk.PackageManifestSchemaVersion)
            throw new InvalidOperationException($"Unsupported plugin.json schemaVersion {manifest.SchemaVersion}; host expects {VisionPluginSdk.PackageManifestSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("plugin.json requires id, name and version.");
        if (manifest.MinimumSdkApiVersion > manifest.MaximumSdkApiVersion)
            throw new InvalidOperationException("plugin.json minimumSdkApiVersion cannot exceed maximumSdkApiVersion.");
        if (VisionPluginSdk.ApiVersion < manifest.MinimumSdkApiVersion || VisionPluginSdk.ApiVersion > manifest.MaximumSdkApiVersion)
            throw new InvalidOperationException($"Package '{manifest.Id}' supports SDK API {manifest.MinimumSdkApiVersion}..{manifest.MaximumSdkApiVersion}; host API is {VisionPluginSdk.ApiVersion}.");
        if (manifest.IsolationMode == VisionPluginIsolationMode.WorkerProcess && manifest.MinimumSdkApiVersion < 2)
            throw new InvalidOperationException("WorkerProcess isolation requires an SDK 2.0 manifest/plugin.");
        if (manifest.IsolationMode != VisionPluginIsolationMode.WorkerProcess &&
            (manifest.WorkerPoolSize is not null || manifest.WorkerSharedMemoryThresholdBytes is not null))
            throw new InvalidOperationException("workerPoolSize/workerSharedMemoryThresholdBytes are only valid for WorkerProcess plugins.");
        if (manifest.WorkerPoolSize is <= 0)
            throw new InvalidOperationException("workerPoolSize must be greater than zero.");
        if (manifest.WorkerSharedMemoryThresholdBytes is < 0)
            throw new InvalidOperationException("workerSharedMemoryThresholdBytes cannot be negative.");
    }

    private static void ValidateManifestAgainstPlugin(PluginPackageManifest? manifest, IVisionPlugin plugin, VisionPluginCompatibility compatibility)
    {
        if (manifest is null) return;
        if (plugin is not IVisionPluginV2)
            throw new InvalidOperationException($"Manifest package '{manifest.Id}' must implement IVisionPluginV2.");
        if (!string.Equals(manifest.Id, plugin.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"plugin.json id '{manifest.Id}' does not match IVisionPlugin descriptor id '{plugin.Descriptor.Id}'.");
        if (!string.Equals(manifest.Name, plugin.Descriptor.Name, StringComparison.Ordinal))
            throw new InvalidOperationException($"plugin.json name '{manifest.Name}' does not match IVisionPlugin descriptor name '{plugin.Descriptor.Name}'.");
        if (!string.Equals(manifest.Version, plugin.Descriptor.Version, StringComparison.Ordinal))
            throw new InvalidOperationException($"plugin.json version '{manifest.Version}' does not match IVisionPlugin descriptor version '{plugin.Descriptor.Version}'.");
        if (manifest.MinimumSdkApiVersion != compatibility.MinimumApiVersion || manifest.MaximumSdkApiVersion != compatibility.MaximumApiVersion)
            throw new InvalidOperationException("plugin.json SDK API range must match IVisionPluginV2.Compatibility.");
    }

    private static void ValidateDescriptor(VisionPluginDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id)) throw new InvalidOperationException("Plugin descriptor Id is required.");
        if (string.IsNullOrWhiteSpace(descriptor.Name)) throw new InvalidOperationException($"Plugin '{descriptor.Id}' descriptor Name is required.");
        if (string.IsNullOrWhiteSpace(descriptor.Version)) throw new InvalidOperationException($"Plugin '{descriptor.Id}' descriptor Version is required.");
    }

    private static void ValidateNodeSchema(string pluginId, VisionPluginNode node, bool requireToolIdentity) =>
        ValidateNodeSchema(pluginId, node.Catalog, node.ToolIdentity, requireToolIdentity);

    private static void ValidateNodeSchema(string pluginId, NodeCatalogItem catalog, VisionPluginToolIdentity? toolIdentity, bool requireToolIdentity)
    {
        if (string.IsNullOrWhiteSpace(catalog.Type)) throw new InvalidOperationException($"Plugin '{pluginId}' has a node with an empty Type.");
        if (string.IsNullOrWhiteSpace(catalog.DisplayName)) throw new InvalidOperationException($"Plugin node '{catalog.Type}' requires DisplayName.");
        if (requireToolIdentity && string.IsNullOrWhiteSpace(toolIdentity?.Version))
            throw new InvalidOperationException($"SDK 2.0 plugin node '{catalog.Type}' must declare ToolIdentity.Version.");

        EnsureUnique(catalog.Inputs.Select(x => x.Name), $"input port on '{catalog.Type}'");
        EnsureUnique(catalog.Outputs.Select(x => x.Name), $"output port on '{catalog.Type}'");
        EnsureUnique(catalog.Parameters.Select(x => x.Name), $"parameter on '{catalog.Type}'");
        var supported = new HashSet<string>(["number", "text", "textarea", "boolean", "select"], StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in catalog.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name) || string.IsNullOrWhiteSpace(parameter.Label))
                throw new InvalidOperationException($"Plugin node '{catalog.Type}' has a parameter without name/label.");
            if (!supported.Contains(parameter.Type))
                throw new InvalidOperationException($"Plugin node '{catalog.Type}' parameter '{parameter.Name}' uses unsupported editor type '{parameter.Type}'.");
            if (parameter.Min is not null && parameter.Max is not null && parameter.Min > parameter.Max)
                throw new InvalidOperationException($"Plugin node '{catalog.Type}' parameter '{parameter.Name}' has min > max.");
            if (string.Equals(parameter.Type, "select", StringComparison.OrdinalIgnoreCase) && (parameter.Options is null || parameter.Options.Count == 0))
                throw new InvalidOperationException($"Plugin node '{catalog.Type}' select parameter '{parameter.Name}' requires options.");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var duplicate = values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Duplicate {label} '{duplicate.Key}'.");
    }

    private static PackageCandidate ReadPackageCandidate(string directory)
    {
        var manifestPath = Path.Combine(directory, VisionPluginSdk.PackageManifestFileName);
        if (!File.Exists(manifestPath))
        {
            var name = Path.GetFileName(directory);
            return new PackageCandidate(directory, Path.Combine(directory, name + ".dll"), null, null);
        }

        var bytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(bytes, Json)
            ?? throw new InvalidOperationException($"Could not parse '{manifestPath}'.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new InvalidOperationException("plugin.json entryAssembly is required.");
        var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        if (!assemblyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("plugin.json entryAssembly must stay inside the plugin package directory.");
        var manifestHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new PackageCandidate(directory, assemblyPath, manifest, manifestHash);
    }

    private static PluginLoadInfo DisabledInfo(PackageCandidate candidate, PluginPackageManifest manifest) =>
        new(manifest.Id, manifest.Name, manifest.Version, candidate.AssemblyPath, 0, false,
            PackageFormat: "manifest-v2", PackageManifestSha256: candidate.ManifestSha256,
            SdkApiVersion: VisionPluginSdk.ApiVersion, MinimumSdkApiVersion: manifest.MinimumSdkApiVersion,
            MaximumSdkApiVersion: manifest.MaximumSdkApiVersion, Vendor: manifest.Vendor, Enabled: false,
            Warnings: ["Package is disabled in plugin.json. Enable it and rescan to activate it."], IsolationMode: manifest.IsolationMode);

    private static PluginLoadInfo FailedInfo(PackageCandidate candidate, string error)
    {
        var manifest = candidate.Manifest;
        var id = manifest?.Id ?? Path.GetFileName(candidate.Directory);
        return new(id, manifest?.Name ?? id, manifest?.Version ?? "unknown", candidate.AssemblyPath, 0, false, error,
            PackageFormat: manifest is null ? "legacy-v1" : "manifest-v2", PackageManifestSha256: candidate.ManifestSha256,
            SdkApiVersion: manifest is null ? 1 : VisionPluginSdk.ApiVersion,
            MinimumSdkApiVersion: manifest?.MinimumSdkApiVersion ?? 1,
            MaximumSdkApiVersion: manifest?.MaximumSdkApiVersion ?? 1,
            Vendor: manifest?.Vendor,
            Enabled: manifest?.Enabled ?? true,
            IsolationMode: manifest?.IsolationMode ?? VisionPluginIsolationMode.InProcess);
    }

    private static bool HasActivePackageChanged(PackageCandidate candidate, IReadOnlyList<PluginLoadInfo> active, out string? warning)
    {
        warning = null;
        if (candidate.Manifest is { Enabled: false })
        {
            warning = "Package is disabled on disk but remains active until host restart.";
            return true;
        }
        if (!File.Exists(candidate.AssemblyPath))
        {
            warning = "Active plugin entry assembly is missing on disk; restart is required to unload it.";
            return true;
        }
        var assemblyHash = ComputeSha256(candidate.AssemblyPath);
        if (active.Any(x => !string.Equals(x.AssemblySha256, assemblyHash, StringComparison.OrdinalIgnoreCase)))
        {
            warning = "Plugin binary changed on disk. V0.59 does not replace already active plugin code without restart; restart the host to activate the new binary.";
            return true;
        }
        if (active.Any(x => !string.Equals(x.PackageManifestSha256, candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase)))
        {
            warning = "plugin.json changed after activation. Restart the host to guarantee package metadata and active binary stay atomic.";
            return true;
        }
        return false;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
