using System.Text.Json;
using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 轮廓特征节点：一条轮廓进，一个主数值（外加具名分量）出。
/// 与 <c>jlvision.regionFeature</c> 互补：XLD 特征不依赖区域填充，适合开放轮廓与亚像素边缘。
/// </summary>
public sealed class JLVisionContourFeatureNode : IVisionNodeExecutor
{
    public string Type => "jlvision.contourFeature";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contours = context.Require<IReadOnlyList<VisionContour>>("contour");
        if (contours.Count == 0)
            throw new InvalidOperationException("jlvision.contourFeature requires at least one input contour.");

        var key = node.GetString("feature", "areaCenter").Trim();
        var feature = JlContourFeatures.Resolve(key);
        OperatorParameterForms.Validate(node);
        var selectedIndex = AdvancedJl.Integer(node, "selectedIndex", 0, 0, contours.Count - 1);
        var selectedContour = contours[selectedIndex];
        var secondIndex = 0;

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row", 0), "row");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column", 0), "column");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row2", 10), "row2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column2", 10), "column2");
        if (feature.NeedsSecond && !context.TryGetValue("contour2", out _))
            throw new InvalidOperationException($"JLVision contour feature '{key}' requires a contour2 input.");

        JlXLDCont? second = null;
        try
        {
            if (feature.NeedsSecond)
            {
                var secondContours = context.Require<IReadOnlyList<VisionContour>>("contour2");
                if (secondContours.Count == 0) throw new InvalidOperationException("第二组轮廓为空，无法计算轮廓间特征。");
                secondIndex = AdvancedJl.Integer(node, "secondIndex", 0, 0, secondContours.Count - 1);
                second = ToXld(secondContours[secondIndex]);
            }

            using var primary = ToXld(selectedContour);
            var args = new JlContourFeatureArgs
            {
                Contour = primary,
                Second = second,
                Row = OperatorParameterForms.Number(node, "row", 0),
                Column = OperatorParameterForms.Number(node, "column", 0),
                Row2 = OperatorParameterForms.Number(node, "row2", 10),
                Column2 = OperatorParameterForms.Number(node, "column2", 10),
                Mode = node.GetString("mode", string.Empty).Trim()
            };

            var result = feature.Run(args);
            var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
            if (result.Extra is not null)
                foreach (var (name, value) in result.Extra) values[name] = value;

            var pointCount = selectedContour.Points.Count;
            var selectedContours = new[] { selectedContour };
            var viewport = JLVisionNodeSupport.PreviewViewport(selectedContour.Points);
            using var overlaySurface = JLVisionNodeSupport.Canvas(viewport.Width, viewport.Height);
            JLVisionNodeSupport.DrawPreviewContours(overlaySurface, selectedContours, new OpenCvSharp.Scalar(0, 255, 255), viewport);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.Double(result.Value),
                ["values"] = VisionValue.String(JsonSerializer.Serialize(values, JsonOptions)),
                ["contour"] = VisionValue.Contours(selectedContours),
                ["inputCount"] = VisionValue.Integer(contours.Count),
                ["selectedIndex"] = VisionValue.Integer(selectedIndex),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["pointCount"] = VisionValue.Integer(pointCount),
                ["feature"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("feature", key), ("value", result.Value), ("pointCount", pointCount), ("selectedIndex", selectedIndex), ("secondIndex", feature.NeedsSecond ? secondIndex : null), ("inputCount", contours.Count), ("previewTransform", viewport)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["feature"] = key,
                ["featureLabel"] = feature.Label,
                ["parametersUsed"] = feature.Note,
                ["value"] = result.Value,
                ["values"] = values
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision contour feature '{key}' failed: {ex.Message} (parameters: {feature.Note}).", ex);
        }
        finally
        {
            second?.Dispose();
        }
    }

    private static JlXLDCont ToXld(VisionContour contour)
    {
        return JlContourCodec.ToNative(new[] { contour });
    }
}
