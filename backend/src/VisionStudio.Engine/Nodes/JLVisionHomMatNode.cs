using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 二维齐次变换节点：由平移/旋转/缩放/斜切/镜像参数构造 HomMat2D，
/// 输出 3x3 矩阵 JSON 与平台 <see cref="VisionTransform2D"/>，并可顺带算出某点的变换结果。
/// </summary>
public sealed class JLVisionHomMatNode : IVisionNodeExecutor
{
    public string Type => "jlvision.homMat";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = node.GetString("operator", "translate").Trim();
        var op = JlHomMatOperations.Resolve(key);
        OperatorParameterForms.Validate(node);

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "tx", 0), "tx");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "ty", 0), "ty");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "phi", 0), "phi");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "theta", 0), "theta");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "sx", 1), "sx");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "sy", 1), "sy");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "px", 0), "px");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "py", 0), "py");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "qx", 10), "qx");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "qy", 10), "qy");

        var args = new JlHomMatArgs
        {
            Tx = OperatorParameterForms.Number(node, "tx", 0),
            Ty = OperatorParameterForms.Number(node, "ty", 0),
            Phi = OperatorParameterForms.Number(node, "phi", 0),
            Theta = OperatorParameterForms.Number(node, "theta", 0),
            Sx = OperatorParameterForms.Number(node, "sx", 1),
            Sy = OperatorParameterForms.Number(node, "sy", 1),
            Px = OperatorParameterForms.Number(node, "px", 0),
            Py = OperatorParameterForms.Number(node, "py", 0),
            Qx = OperatorParameterForms.Number(node, "qx", 10),
            Qy = OperatorParameterForms.Number(node, "qy", 10),
            Mode = node.GetString("mode", string.Empty).Trim()
        };

        JlHomMatResult result;
        try
        {
            result = op.Run(args);
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision hom-mat operator '{key}' failed: {ex.Message} (parameters: {op.Note}).", ex);
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
        if (result.Extra is not null)
            foreach (var (name, value) in result.Extra) values[name] = value;

        var outputs = new Dictionary<string, VisionValue>
        {
            ["value"] = VisionValue.Double(result.Value),
            ["matrix"] = VisionValue.String(result.Matrix is null ? "null" : JlMatrixOperations.ToJson(result.Matrix)),
            ["values"] = VisionValue.String(JsonSerializer.Serialize(values, JsonOptions)),
            ["overlay"] = VisionValue.Image(VisionImage.Own(CloneOverlay(result.Matrix))),
            ["points"] = VisionValue.Contours(new[] { PlotSamples(result.Matrix) }),
            ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                node.Type, "standard", 0, ("operator", key), ("value", result.Value)))
        };
        if (result.Matrix is not null)
            outputs["transform"] = VisionValue.Transform(JlHomMatOperations.ToPlatform(result.Matrix, "image", "world"));

        var summary = new Dictionary<string, object?>
        {
            ["algorithmBackend"] = "jlvision",
            ["operator"] = key,
            ["operatorLabel"] = op.Label,
            ["parametersUsed"] = op.Note,
            ["value"] = result.Value,
            ["matrix"] = result.Matrix is null ? null : (object)values,
            ["values"] = values
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    /// <summary>沿 x 轴采样的单位点经结果矩阵变换后的坐标（矩阵为空时返回恒等采样）。</summary>
    private static List<VisionPoint2D> SamplePoints(double[,]? matrix)
    {
        var samples = new List<VisionPoint2D>();
        foreach (var x in new[] { 0d, 20d, 40d, 60d })
        {
            var ux = x;
            var uy = 0d;
            if (matrix is not null)
            {
                ux = matrix[0, 0] * x + matrix[0, 2];
                uy = matrix[1, 0] * x + matrix[1, 2];
            }
            if (double.IsFinite(ux) && double.IsFinite(uy))
                samples.Add(new VisionPoint2D(ux, uy));
        }
        return samples;
    }

    /// <summary>把采样点包成一条平台轮廓，供下游可视化/拟合复用。</summary>
    private static VisionContour PlotSamples(double[,]? matrix)
        => new(0, SamplePoints(matrix), -1, false, 0, 0, 0,
            new VisionRectangle2D(0, 0, 0, 0), new VisionCircle(0, 0, 0));

    /// <summary>在画布上画出采样点折线，作为变换结果的调试预览。</summary>
    private static Mat CloneOverlay(double[,]? matrix)
    {
        using var canvas = BuildOverlay(matrix);
        return canvas.Clone();
    }

    private static Mat BuildOverlay(double[,]? matrix)
    {
        var samples = SamplePoints(matrix);
        var maxX = samples.Select(p => p.X).DefaultIfEmpty(10).Max();
        var maxY = samples.Select(p => p.Y).DefaultIfEmpty(10).Max();
        var width = Math.Max(32, (int)Math.Ceiling(Math.Max(1, maxX)) + 4);
        var height = Math.Max(32, (int)Math.Ceiling(Math.Max(1, maxY)) + 4);
        var canvas = JLVisionNodeSupport.Canvas(width, height);
        var points = samples.Select(p => new Point(
            (int)Math.Clamp(p.X, 0, width - 1), (int)Math.Clamp(p.Y, 0, height - 1))).ToArray();
        if (points.Length >= 2)
            Cv2.Polylines(canvas, new[] { points }, false, new Scalar(0, 255, 255), 1, LineTypes.AntiAlias);
        foreach (var point in points)
            Cv2.Circle(canvas, point, 2, new Scalar(0, 200, 0), -1);
        return canvas;
    }
}
