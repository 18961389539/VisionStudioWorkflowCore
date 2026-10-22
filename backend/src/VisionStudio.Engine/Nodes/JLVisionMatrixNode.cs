using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 矩阵节点：输入 JSON 二维数组，输出主值 + 结果矩阵 JSON。
/// 覆盖转置/求逆/行列式/加减乘/逐元素/范数/求和/极值/子矩阵/重复/线性求解/对称特征值/正交分解。
/// </summary>
public sealed class JLVisionMatrixNode : IVisionNodeExecutor
{
    public string Type => "jlvision.matrixOp";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = node.GetString("operator", "transpose").Trim();
        var op = JlMatrixOperations.Resolve(key);
        OperatorParameterForms.Validate(node);

        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "scalar", 1), "scalar");

        var matrixAJson = key == "create" ? "[[0]]" : context.TryGetValue("matrixA", out var inputA)
            ? inputA.Require<string>("matrixA") : node.GetString("matrixA", "[[1,0],[0,1]]").Trim();
        CoordinateValidation.RequireNonEmpty(matrixAJson, "matrixA");
        using var a = JlMatrixOperations.ParseMatrix(matrixAJson, "matrixA");
        using var b = op.NeedsB
            ? JlMatrixOperations.ParseMatrix(context.TryGetValue("matrixB", out var inputB) ? inputB.Require<string>("matrixB") : node.GetString("matrixB", "[[1,0],[0,1]]"), "matrixB")
            : null;

        if (op.NeedsB && b is null)
            throw new InvalidOperationException($"Matrix operator '{key}' requires matrixB.");

        var args = new JlMatrixArgs
        {
            A = a,
            B = b,
            Scalar = OperatorParameterForms.Number(node, "scalar", 1),
            Row = node.GetInt("row", 0),
            Column = node.GetInt("column", 0),
            Rows = node.GetInt("rows", 2),
            Columns = node.GetInt("columns", 2),
            Mode = node.GetString("mode", string.Empty).Trim()
        };

        JlMatrixResult result;
        try
        {
            JlMatrixOperations.ValidateOutputBudget(key, args);
            result = op.Run(args);
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision matrix operator '{key}' failed: {ex.Message} (parameters: {op.Note}).", ex);
        }

        if (!double.IsFinite(result.Value) || result.Matrix is { } valuesMatrix && valuesMatrix.Cast<double>().Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("矩阵计算返回非有限数值；请检查除零、负数开方或奇异矩阵。");
        var matrixJson = result.Matrix is null ? "null" : JlMatrixOperations.ToJson(result.Matrix);
        var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["value"] = result.Value };
        if (result.Extra is not null)
            foreach (var (name, value) in result.Extra) values[name] = value;

        using var overlaySurface = BuildHeatmap(result.Matrix, result.Value);
        var outputs = new Dictionary<string, VisionValue>
        {
            ["value"] = VisionValue.Double(result.Value),
            ["matrix"] = VisionValue.String(matrixJson),
            ["values"] = VisionValue.String(JsonSerializer.Serialize(values, JsonOptions)),
            ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
            ["plot"] = VisionValue.Contours(new[] { BuildPlot(result.Matrix, result.Value) }),
            ["rows"] = VisionValue.Integer(result.Matrix?.GetLength(0) ?? 0),
            ["columns"] = VisionValue.Integer(result.Matrix?.GetLength(1) ?? 0),
            ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                node.Type, "standard", 0, ("operator", key), ("value", result.Value),
                ("resultRows", result.Matrix?.GetLength(0) ?? 0), ("resultColumns", result.Matrix?.GetLength(1) ?? 0),
                ("previewRows", Math.Min(128, result.Matrix?.GetLength(0) ?? 1)), ("previewColumns", Math.Min(128, result.Matrix?.GetLength(1) ?? 1)),
                ("previewTruncated", result.Matrix is { } preview && (preview.GetLength(0) > 128 || preview.GetLength(1) > 128)),
                ("plotSampleLimit", 64), ("plotCoordinates", "调试曲线按幅值归一化，非原始几何坐标")))
        };
        var summary = new Dictionary<string, object?>
        {
            ["algorithmBackend"] = "jlvision",
            ["operator"] = key,
            ["operatorLabel"] = op.Label,
            ["parametersUsed"] = op.Note,
            ["value"] = result.Value,
            ["resultRows"] = result.Matrix?.GetLength(0),
            ["resultColumns"] = result.Matrix?.GetLength(1),
            ["values"] = values
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    /// <summary>把结果矩阵渲染成灰度热力图（按最大绝对值归一化），标量结果用 1x1 单元表示。</summary>
    private static Mat BuildHeatmap(double[,]? matrix, double fallback)
    {
        // Preview at most 128×128 cells; the matrix output retains every element.
        var rows = Math.Clamp(matrix?.GetLength(0) ?? 1, 1, 128);
        var columns = Math.Clamp(matrix?.GetLength(1) ?? 1, 1, 128);
        const int cell = 16;
        var maxAbs = 0d;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var magnitude = Math.Abs(matrix is null ? fallback : matrix[r, c]);
                if (double.IsFinite(magnitude) && magnitude > maxAbs) maxAbs = magnitude;
            }
        if (maxAbs <= 0) maxAbs = 1;
        var canvas = JLVisionNodeSupport.Canvas(columns * cell, rows * cell);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var value = matrix is null ? fallback : matrix[r, c];
                var level = double.IsFinite(value) ? (byte)Math.Clamp(Math.Abs(value) / maxAbs * 255d, 0, 255) : (byte)0;
                Cv2.Rectangle(canvas, new Rect(c * cell, r * cell, cell - 1, cell - 1), new Scalar(level, level, level), -1);
            }
        return canvas;
    }

    /// <summary>把矩阵元素按行展开成一条折线轮廓，作为数值走向的调试曲线。</summary>
    private static VisionContour BuildPlot(double[,]? matrix, double fallback)
    {
        var values = new List<double>();
        if (matrix is null)
        {
            values.Add(fallback);
        }
        else
        {
            var rows = matrix.GetLength(0);
            var columns = matrix.GetLength(1);
            for (var r = 0; r < rows && values.Count < 64; r++)
                for (var c = 0; c < columns && values.Count < 64; c++)
                    values.Add(matrix[r, c]);
        }
        var points = new List<VisionPoint2D>();
        var magnitude = Math.Max(1, values.Select(Math.Abs).DefaultIfEmpty(1).Max());
        for (var i = 0; i < values.Count && i < 64; i++)
        {
            var y = 200d - values[i] / magnitude * 200d;
            points.Add(new VisionPoint2D(i * 8d, y));
        }
        if (points.Count == 1)
            return new VisionContour(0, points, -1, false, 0, 0, 0, new(points[0].X, points[0].Y, 0, 0), new(points[0].X, points[0].Y, 0))
                { Closed = false, Unit = "preview-px", CoordinateFrame = "debug:matrixPlot" };
        var source = new VisionContour(0, points, -1, false, 0, 0, 0, default, default) { Closed = false };
        using var native = JlContourCodec.ToNative(new[] { source });
        return JlContourCodec.FromNative(native, CancellationToken.None)[0] with { Unit = "preview-px", CoordinateFrame = "debug:matrixPlot" };
    }
}
