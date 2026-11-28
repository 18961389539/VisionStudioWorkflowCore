using System.Text.Json;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 通用 JLVision 区域特征节点：一个区域进，一个主数值 + 具名分量出。
/// 覆盖几何、矩、游程、灰度统计与纹理方向等原本只能写代码调用的特征算子。
/// </summary>
public sealed class JLVisionRegionFeatureNode : IVisionNodeExecutor
{
    public string Type => "jlvision.regionFeature";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionRegion>("region");
        var key = node.GetString("feature", "area").Trim();
        var feature = JlRegionFeatures.Resolve(key);
        OperatorParameterForms.Validate(node);

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 5), "size");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row", 10), "row");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column", 10), "column");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "min", 0), "min");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "max", 1), "max");

        var imageConnected = context.TryGetValue("image", out var imageValue);
        if (feature.NeedsImage && (!imageConnected || imageValue.Value is not IVisionImage imageForValidation
                                   || imageForValidation.NativeImage is not OpenCvSharp.Mat matForValidation
                                   || matForValidation.Empty()))
            throw new InvalidOperationException(
                $"JLVision region feature '{key}' requires an image input: non-empty image missing (connected={imageConnected}, featureNeedsImage={feature.NeedsImage}).");

        var count = node.GetInt("count", 1);
        if (count is < 1 or > 100000)
            throw new InvalidOperationException($"JLVision region feature '{key}' count must be 1..100000 (actual={count}).");
        var minValue = OperatorParameterForms.Number(node, "min", 0);
        var maxValue = OperatorParameterForms.Number(node, "max", 1);
        if (key == "grayHistoRange" && (!double.IsFinite(minValue) || !double.IsFinite(maxValue)
                                        || minValue >= maxValue || count is < 2 or > 65536))
            throw new InvalidOperationException(
                $"JLVision region feature '{key}' requires min < max and count in 2..65536 (actual min={minValue}, max={maxValue}, count={count}).");

        var pointRow = OperatorParameterForms.Number(node, "row", 10);
        var pointColumn = OperatorParameterForms.Number(node, "column", 10);
        if (key is "testRegionPoint" or "testRegionPoints" or "shapeHistoPoint")
        {
            if (pointRow != Math.Truncate(pointRow) || pointColumn != Math.Truncate(pointColumn))
                throw new InvalidOperationException(
                    $"JLVision region feature '{key}' requires integer point indices (actual row={pointRow}, column={pointColumn}).");
            if (pointRow < 0 || pointRow >= source.Height || pointColumn < 0 || pointColumn >= source.Width)
                throw new InvalidOperationException(
                    $"JLVision region feature '{key}' point ({pointRow},{pointColumn}) is outside the region canvas {source.Width}x{source.Height}.");
        }

        // 未知分量名过去会被原生层省略却仍返回 found=true：这里显式拒绝。
        var mode = node.GetString("mode", string.Empty).Trim();
        if (key == "regionFeatures")
        {
            string[] knownRegion = ["area", "row", "column", "width", "height", "circularity", "compactness", "convexity",
                "rectangularity", "euler_number", "eccentricity", "elliptic_axis", "roundness", "height_width_ratio",
                "smallest_rectangle1", "smallest_rectangle2", "inner_circle", "inner_rectangle1", "diameter_region",
                "moments_central", "moments_central_invar", "moments_second", "moments_second_invar", "moments_second_relative_invar",
                "moments_third", "moments_third_invar"];
            foreach (var part in (mode.Length == 0 ? "area" : mode).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!knownRegion.Contains(part, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"JLVision region feature 'regionFeatures' has unknown component name '{part}'.");
        }
        else if (key is "grayFeatures" or "shapeHistoAll" or "shapeHistoPoint")
        {
            string[] knownNamed = key == "grayFeatures"
                ? ["mean", "deviation", "entropy", "area", "min", "max", "range", "median", "skewness", "kurtosis"]
                : ["convexity", "circularity", "compactness", "rectangularity", "eccentricity", "orientation"];
            var requested = mode.Length == 0 ? (key == "grayFeatures" ? "mean" : "convexity") : mode;
            if (!knownNamed.Contains(requested, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"JLVision region feature '{key}' has unknown component name '{requested}'.");
        }

        using var native = JLVisionRegions.ToNative(source);
        // 特征算子多数要求单个区域对象；多组件时先并成一个，connectAndHoles 这类保留组件。
        using var single = feature.KeepsComponents ? null : native.Union1();
        JlImage? image = null;
        IVisionImage? imageSource = null;
        try
        {
            if (feature.NeedsImage && imageValue.Value is IVisionImage visionImage)
            {
                var mat = visionImage.NativeImage as OpenCvSharp.Mat;
                if (mat is null || mat.Empty())
                    throw new InvalidOperationException("jlvision.regionFeature received an empty image input.");
                imageSource = visionImage;
                image = JLVisionImages.FromMat(mat);
            }

            var args = new JlRegionFeatureArgs
            {
                Region = single ?? native,
                Image = image,
                Size = OperatorParameterForms.Number(node, "size", 5),
                Count = count,
                Row = pointRow,
                Column = pointColumn,
                Min = minValue,
                Max = maxValue,
                Mode = mode
            };

            var result = feature.Run(args);
            var payload = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
            foreach (var (name, value) in result.Extra) payload[name] = value;
            payload["schemaVersion"] = 1;
            payload["regionWidth"] = source.Width;
            payload["regionHeight"] = source.Height;
            payload["imageWidth"] = imageSource?.Width ?? 0;
            payload["imageHeight"] = imageSource?.Height ?? 0;

            using var overlaySurface = JLVisionNodeSupport.Canvas(source.Width, source.Height);
            JLVisionNodeSupport.DrawRegionRuns(overlaySurface, source, new OpenCvSharp.Scalar(80, 160, 255));
            var valueUnit = key.Contains("gray", StringComparison.OrdinalIgnoreCase) ? "gray"
                : key.Contains("area", StringComparison.OrdinalIgnoreCase) ? "px²"
                : key.Contains("width", StringComparison.OrdinalIgnoreCase) || key.Contains("height", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("diameter", StringComparison.OrdinalIgnoreCase) || key.Contains("circle", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("rectangle", StringComparison.OrdinalIgnoreCase) ? "px" : "count";
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["reason"] = VisionValue.String("ok"),
                ["value"] = VisionValue.Double(result.Value),
                ["valuesSchemaVersion"] = VisionValue.Integer(1), ["unit"] = VisionValue.String(valueUnit),
                ["imageWidth"] = VisionValue.Integer(imageSource?.Width ?? 0),
                ["imageHeight"] = VisionValue.Integer(imageSource?.Height ?? 0),
                ["values"] = VisionValue.String(JsonSerializer.Serialize(payload, JsonOptions)),
                ["region"] = VisionValue.Region(source),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["feature"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("found", true), ("feature", key), ("components", source.Components.Count),
                    ("value", result.Value), ("valuesSchemaVersion", 1), ("unit", valueUnit),
                    ("regionWidth", source.Width), ("regionHeight", source.Height),
                    ("imageWidth", imageSource?.Width ?? 0), ("imageHeight", imageSource?.Height ?? 0)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["feature"] = key,
                ["valuesSchemaVersion"] = 1,
                ["unit"] = valueUnit,
                ["featureLabel"] = feature.Label,
                ["parametersUsed"] = feature.Note,
                ["value"] = result.Value,
                ["components"] = source.Components.Count,
                ["values"] = payload
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision region feature '{key}' failed: {ex.Message} (parameters: {feature.Note}).", ex);
        }
        finally
        {
            image?.Dispose();
        }
    }
}
