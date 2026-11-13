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
    /// <summary>Verifies immutable calibration pins before any workflow execution path.</summary>
    public async Task ValidateCalibrationBindingsAsync(WorkflowDefinition workflow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        foreach (var node in workflow.Nodes)
        {
            if (!registry.IsRegistered(node.Type)) continue;
            var catalog = registry.Require(node.Type).Catalog;
            if (string.Equals(node.Type, "calibration.planar", StringComparison.OrdinalIgnoreCase))
            {
                var assetId = ResolveString(node, catalog, "assetId");
                var version = ResolveInt(node, catalog, "assetVersion");
                var hasAssetId = !string.IsNullOrWhiteSpace(assetId);
                var hasVersion = version > 0;
                if (version < 0 || hasAssetId != hasVersion)
                    throw new InvalidOperationException($"Planar calibration node '{node.Id}' must specify both assetId and a positive assetVersion, or leave both unset.");
                if (hasAssetId) await ValidatePlanarCalibrationBindingAsync(node, assetId!, version, ct);
            }
            else if (string.Equals(node.Type, "image.undistort", StringComparison.OrdinalIgnoreCase))
            {
                var assetId = ResolveString(node, catalog, "intrinsicsAssetId");
                var version = ResolveInt(node, catalog, "intrinsicsAssetVersion");
                var hasAssetId = !string.IsNullOrWhiteSpace(assetId);
                var hasVersion = version > 0;
                if (version < 0 || hasAssetId != hasVersion)
                    throw new InvalidOperationException($"Image undistort node '{node.Id}' must specify both intrinsicsAssetId and a positive intrinsicsAssetVersion, or leave both unset.");
                if (!hasAssetId) continue;
                if (cameraIntrinsicAssets is null)
                    throw new InvalidOperationException("Workflow references a camera intrinsic asset, but intrinsic calibration storage is unavailable.");
                if (workflow.Edges.Any(edge => string.Equals(edge.TargetNodeId, node.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(edge.TargetPort, "calibrationJson", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Image undistort node '{node.Id}' cannot pin an intrinsic asset while calibrationJson is connected from another node. Pin a static model or remove the asset reference.");
                await cameraIntrinsicAssets.RequireModelMatchAsync(assetId!, version, node.GetString("calibrationJson", "{}"), ct);
            }
            else if (string.Equals(node.Type, "calibration.handEye2D", StringComparison.OrdinalIgnoreCase))
            {
                var assetId = ResolveString(node, catalog, "handEyeAssetId");
                var version = ResolveInt(node, catalog, "handEyeAssetVersion");
                var hasAssetId = !string.IsNullOrWhiteSpace(assetId);
                var hasVersion = version > 0;
                if (version < 0 || hasAssetId != hasVersion)
                    throw new InvalidOperationException($"Hand-eye node '{node.Id}' must specify both handEyeAssetId and a positive handEyeAssetVersion, or leave both unset.");
                if (!hasAssetId) continue;
                if (handEyeAssets is null || handEyeSolver is null)
                    throw new InvalidOperationException("Workflow references a hand-eye calibration asset, but hand-eye calibration storage is unavailable.");
                if (workflow.Edges.Any(edge => string.Equals(edge.TargetNodeId, node.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(edge.TargetPort, "samplesJson", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Hand-eye node '{node.Id}' cannot pin a hand-eye asset while samplesJson is connected from another node. Pin static samples or remove the asset reference.");
                var result = await handEyeSolver.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
                await handEyeAssets.RequireModelMatchAsync(assetId!, version, result.Outputs["calibrationJson"].Require<string>("calibrationJson"), ct);
            }
            else if (string.Equals(node.Type, "calibration.handEye3D", StringComparison.OrdinalIgnoreCase))
            {
                var assetId = ResolveString(node, catalog, "handEye3DAssetId");
                var version = ResolveInt(node, catalog, "handEye3DAssetVersion");
                var hasAssetId = !string.IsNullOrWhiteSpace(assetId);
                var hasVersion = version > 0;
                if (version < 0 || hasAssetId != hasVersion)
                    throw new InvalidOperationException($"3D hand-eye node '{node.Id}' must specify both handEye3DAssetId and a positive handEye3DAssetVersion, or leave both unset.");
                if (!hasAssetId) continue;
                if (handEye3DAssets is null || handEye3DSolver is null)
                    throw new InvalidOperationException("Workflow references a 3D hand-eye calibration asset, but 3D hand-eye calibration storage is unavailable.");
                if (workflow.Edges.Any(edge => string.Equals(edge.TargetNodeId, node.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(edge.TargetPort, "samplesJson", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"3D hand-eye node '{node.Id}' cannot pin a hand-eye asset while samplesJson is connected from another node. Pin static samples or remove the asset reference.");
                var result = await handEye3DSolver.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
                await handEye3DAssets.RequireModelMatchAsync(assetId!, version, result.Outputs["calibrationJson"].Require<string>("calibrationJson"), ct);
            }
        }
    }

    /// <summary>
    /// 捕获形状模型资产锁定项：version<=0（latest）在发布时就地落成具体版本号，
    /// 之后追加新版本不会改变已发布配方的锁定；资产/版本不存在直接拒绝发布。
    /// </summary>
    private async Task<ModelAssetRuntimeDependency[]> CaptureAssetPinsAsync(
        HashSet<(string AssetId, int Version)> references,
        CancellationToken ct)
    {
        if (references.Count == 0) return [];
        if (assets is null)
            throw new InvalidOperationException("Workflow references assets, but asset storage is unavailable.");
        var output = new List<ModelAssetRuntimeDependency>(references.Count);
        foreach (var reference in references.OrderBy(x => x.AssetId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Version))
        {
            var version = reference.Version;
            if (version <= 0)
            {
                var descriptor = await assets.GetAsync(reference.AssetId, ct)
                    ?? throw new InvalidOperationException($"Workflow references asset '{reference.AssetId}', but it does not exist.");
                version = descriptor.LatestVersion;
            }
            var info = await assets.GetVersionAsync(reference.AssetId, version, ct)
                ?? throw new InvalidOperationException($"Workflow references asset '{reference.AssetId}' v{version}, but that immutable version does not exist.");
            output.Add(new ModelAssetRuntimeDependency(reference.AssetId, info.Version, info.Sha256));
        }
        return [.. output];
    }

    private async Task<IReadOnlyList<CalibrationRuntimeDependency>> CaptureCalibrationsAsync(
        HashSet<(string AssetId, int Version)> references,
        CancellationToken ct)
    {
        var output = new List<CalibrationRuntimeDependency>();
        foreach (var reference in references.OrderBy(x => x.AssetId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Version))
        {
            if (TryGetIntrinsicCalibrationId(reference.AssetId, out var intrinsicAssetId))
            {
                if (cameraIntrinsicAssets is null)
                    throw new InvalidOperationException("Workflow references a camera intrinsic asset, but intrinsic calibration storage is unavailable.");
                var intrinsic = await cameraIntrinsicAssets.GetVersionAsync(intrinsicAssetId, reference.Version, ct)
                    ?? throw new InvalidOperationException($"Workflow references camera intrinsic calibration '{intrinsicAssetId}' v{reference.Version}, but that immutable version does not exist.");
                output.Add(new CalibrationRuntimeDependency(reference.AssetId, intrinsic.Version, intrinsic.SnapshotHash));
            }
            else if (TryGetHandEyeCalibrationId(reference.AssetId, out var handEyeAssetId))
            {
                if (handEyeAssets is null)
                    throw new InvalidOperationException("Workflow references a hand-eye calibration asset, but hand-eye calibration storage is unavailable.");
                var handEye = await handEyeAssets.GetVersionAsync(handEyeAssetId, reference.Version, ct)
                    ?? throw new InvalidOperationException($"Workflow references hand-eye calibration '{handEyeAssetId}' v{reference.Version}, but that immutable version does not exist.");
                output.Add(new CalibrationRuntimeDependency(reference.AssetId, handEye.Version, handEye.SnapshotHash));
            }
            else if (TryGetHandEye3DCalibrationId(reference.AssetId, out var handEye3DAssetId))
            {
                if (handEye3DAssets is null)
                    throw new InvalidOperationException("Workflow references a 3D hand-eye calibration asset, but 3D hand-eye calibration storage is unavailable.");
                var handEye3D = await handEye3DAssets.GetVersionAsync(handEye3DAssetId, reference.Version, ct)
                    ?? throw new InvalidOperationException($"Workflow references 3D hand-eye calibration '{handEye3DAssetId}' v{reference.Version}, but that immutable version does not exist.");
                output.Add(new CalibrationRuntimeDependency(reference.AssetId, handEye3D.Version, handEye3D.SnapshotHash));
            }
            else
            {
                var snapshot = await calibrations.GetVersionAsync(reference.AssetId, reference.Version, ct)
                    ?? throw new InvalidOperationException($"Workflow references calibration '{reference.AssetId}' v{reference.Version}, but that immutable version does not exist.");
                output.Add(new CalibrationRuntimeDependency(snapshot.AssetId, snapshot.Version, snapshot.SnapshotHash));
            }
        }
        return output;
    }

    private static string IntrinsicCalibrationReference(string assetId) => "camera-intrinsics:" + assetId;

    private static bool TryGetIntrinsicCalibrationId(string reference, out string assetId)
    {
        const string prefix = "camera-intrinsics:";
        if (reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetId = reference[prefix.Length..];
            return true;
        }
        assetId = string.Empty;
        return false;
    }

    private static string HandEyeCalibrationReference(string assetId) => "hand-eye-calibrations:" + assetId;

    private static bool TryGetHandEyeCalibrationId(string reference, out string assetId)
    {
        const string prefix = "hand-eye-calibrations:";
        if (reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetId = reference[prefix.Length..];
            return true;
        }
        assetId = string.Empty;
        return false;
    }

    private static string HandEye3DCalibrationReference(string assetId) => "hand-eye3d-calibrations:" + assetId;

    private static bool TryGetHandEye3DCalibrationId(string reference, out string assetId)
    {
        const string prefix = "hand-eye3d-calibrations:";
        if (reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetId = reference[prefix.Length..];
            return true;
        }
        assetId = string.Empty;
        return false;
    }

    private async Task ValidatePlanarCalibrationBindingAsync(NodeDefinition node, string assetId, int version, CancellationToken ct)
    {
        var snapshot = await calibrations.GetVersionAsync(assetId, version, ct)
            ?? throw new InvalidOperationException($"Planar calibration node '{node.Id}' references missing calibration '{assetId}' v{version}.");
        var workspace = snapshot.Workspace;
        var fieldsMatch =
            string.Equals(node.GetString("sourceFrame", "ImagePixel"), workspace.SourceFrame, StringComparison.Ordinal) &&
            string.Equals(node.GetString("targetFrame", "Workpiece"), workspace.TargetFrame, StringComparison.Ordinal) &&
            string.Equals(node.GetString("targetUnit", "mm"), workspace.TargetUnit, StringComparison.Ordinal) &&
            node.GetDouble("maxRmse", 0) == workspace.MaxRmse &&
            node.GetDouble("maxPointError", 0) == workspace.MaxPointError;

        List<CalibrationPair> actualPairs;
        try
        {
            actualPairs = JsonSerializer.Deserialize<List<CalibrationPair>>(node.GetString("pairsJson", "[]"), Json)
                ?? throw new JsonException("empty pair list");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Planar calibration node '{node.Id}' has invalid pairsJson while bound to '{assetId}' v{version}.", ex);
        }

        var expectedPairs = workspace.Points.Where(point => point.Enabled)
            .Select(point => new CalibrationPair(point.ImageX, point.ImageY, point.WorldX, point.WorldY));
        static IOrderedEnumerable<CalibrationPair> CanonicalOrder(IEnumerable<CalibrationPair> pairs)
            => pairs.OrderBy(pair => pair.ImageX).ThenBy(pair => pair.ImageY).ThenBy(pair => pair.WorldX).ThenBy(pair => pair.WorldY);
        var orderedExpected = CanonicalOrder(expectedPairs).ToArray();
        var orderedActual = CanonicalOrder(actualPairs).ToArray();
        var pairsMatch = orderedExpected.Length == orderedActual.Length && orderedExpected.SequenceEqual(orderedActual);
        if (!fieldsMatch || !pairsMatch)
            throw new InvalidOperationException($"Planar calibration node '{node.Id}' parameters do not match immutable calibration '{assetId}' v{version}. Reapply that version from the Calibration workspace before publishing or running with dependency validation.");
    }
}
