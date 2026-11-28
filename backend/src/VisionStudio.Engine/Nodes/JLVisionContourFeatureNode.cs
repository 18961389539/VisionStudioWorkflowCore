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

        var row = OperatorParameterForms.Number(node, "row", 0);
        var column = OperatorParameterForms.Number(node, "column", 0);
        var row2 = OperatorParameterForms.Number(node, "row2", 10);
        var column2 = OperatorParameterForms.Number(node, "column2", 10);
        CoordinateValidation.RequireFinite(row, "row");
        CoordinateValidation.RequireFinite(column, "column");
        CoordinateValidation.RequireFinite(row2, "row2");
        CoordinateValidation.RequireFinite(column2, "column2");

        NodeExecutorResult FeatureFailure(string reason) => new(
            new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(false), ["reason"] = VisionValue.String(reason),
                ["value"] = VisionValue.Double(0), ["values"] = VisionValue.String("{}"),
                ["pointCount"] = VisionValue.Integer(selectedContour.Points.Count),
                ["feature"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("found", false), ("reason", reason), ("feature", key),
                    ("selectedIndex", selectedIndex), ("pointCount", selectedContour.Points.Count)))
            },
            new Dictionary<string, object?>
            {
                ["found"] = false, ["reason"] = reason, ["feature"] = key, ["algorithmBackend"] = "jlvision"
            });

        // 模式必须在原生调用前校验，非法自由文本不能透传；退化参考线也不能悄悄算距离。
        var mode = node.GetString("mode", "").Trim();
        if (key == "distanceCc" && mode.Length > 0 && mode is not ("point_to_point" or "point_to_segment"))
            return ValueTask.FromResult(FeatureFailure("invalid-distance-mode"));
        if (key is "distanceLc" or "distanceSc" && Math.Abs(row2 - row) + Math.Abs(column2 - column) < 1e-9)
            return ValueTask.FromResult(FeatureFailure("zero-length-reference"));
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
                Row = row, Column = column, Row2 = row2, Column2 = column2,
                Mode = mode
            };

            var result = feature.Run(args);
            var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
            if (result.Extra is not null)
                foreach (var (name, value) in result.Extra) values[name] = value;

            var pointCount = selectedContour.Points.Count;
            // areaCenter 的主值是“点数”，不是面积：给出机器可读的 valueKind/unit 与显式面积特征名。
            // 原生 Extra 里的 row/column 可能是质心（areaCenter），不能被输入参数覆盖。
            values.TryAdd("row", row);
            values.TryAdd("column", column);
            values["pointCount"] = pointCount;
            var valueKind = key == "areaCenter" ? "pointCount"
                : key.Contains("area", StringComparison.OrdinalIgnoreCase) ? "area"
                : key.Contains("distance", StringComparison.OrdinalIgnoreCase) ? "length"
                : "count";
            var valueUnit = valueKind == "area" ? "px²" : valueKind == "length" ? "px" : "count";
            var selectedContours = new[] { selectedContour };
            var viewport = JLVisionNodeSupport.PreviewViewport(selectedContour.Points);
            using var overlaySurface = JLVisionNodeSupport.Canvas(viewport.Width, viewport.Height);
            JLVisionNodeSupport.DrawPreviewContours(overlaySurface, selectedContours, new OpenCvSharp.Scalar(0, 255, 255), viewport);
            var outputs = new Dictionary<string, VisionValue>
            {
                ["found"] = VisionValue.Boolean(true), ["reason"] = VisionValue.String("ok"),
                ["value"] = VisionValue.Double(result.Value),
                ["valueKind"] = VisionValue.String(valueKind), ["unit"] = VisionValue.String(valueUnit),
                ["areaFeature"] = VisionValue.String(key == "areaCenter" ? "area" : ""),
                ["values"] = VisionValue.String(JsonSerializer.Serialize(values, JsonOptions)),
                ["contour"] = VisionValue.Contours(selectedContours),
                ["inputCount"] = VisionValue.Integer(contours.Count),
                ["selectedIndex"] = VisionValue.Integer(selectedIndex),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["pointCount"] = VisionValue.Integer(pointCount),
                ["feature"] = VisionValue.String(key),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("found", true), ("feature", key), ("value", result.Value),
                    ("valueKind", valueKind), ("unit", valueUnit), ("pointCount", pointCount),
                    ("selectedIndex", selectedIndex), ("secondIndex", feature.NeedsSecond ? secondIndex : null),
                    ("inputCount", contours.Count), ("previewTransform", viewport)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["feature"] = key,
                ["featureLabel"] = feature.Label,
                ["valueKind"] = valueKind,
                ["unit"] = valueUnit,
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
