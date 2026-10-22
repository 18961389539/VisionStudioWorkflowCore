using System.Text.Json;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

        if (feature.NeedsImage && !context.TryGetValue("image", out _))
            throw new InvalidOperationException($"JLVision region feature '{key}' requires an image input.");

        using var native = JLVisionRegions.ToNative(source);
        // 特征算子多数要求单个区域对象；多组件时先并成一个，connectAndHoles 这类保留组件。
        using var single = feature.KeepsComponents ? null : native.Union1();
        JlImage? image = null;
        try
        {
            if (feature.NeedsImage && context.TryGetValue("image", out var imageValue) && imageValue.Value is IVisionImage visionImage)
            {
                var mat = visionImage.NativeImage as OpenCvSharp.Mat;
                if (mat is null || mat.Empty())
                    throw new InvalidOperationException("jlvision.regionFeature received an empty image input.");
                image = JLVisionImages.FromMat(mat);
            }

            var args = new JlRegionFeatureArgs
            {
                Region = single ?? native,
                Image = image,
                Size = OperatorParameterForms.Number(node, "size", 5),
                Count = node.GetInt("count", 1),
                Row = OperatorParameterForms.Number(node, "row", 10),
                Column = OperatorParameterForms.Number(node, "column", 10),
                Min = OperatorParameterForms.Number(node, "min", 0),
                Max = OperatorParameterForms.Number(node, "max", 1),
                Mode = node.GetString("mode", string.Empty).Trim()
            };

            var result = feature.Run(args);
            var payload = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
            foreach (var (name, value) in result.Extra) payload[name] = value;

            using var overlaySurface = JLVisionNodeSupport.Canvas(source.Width, source.Height);
            JLVisionNodeSupport.DrawRegionRuns(overlaySurface, source, new OpenCvSharp.Scalar(80, 160, 255));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.Double(result.Value),
                ["values"] = VisionValue.String(JsonSerializer.Serialize(payload, JsonOptions)),
                ["region"] = VisionValue.Region(source),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["feature"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("feature", key), ("components", source.Components.Count),
                    ("value", result.Value)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["feature"] = key,
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
