using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 通用 JLVision 区域算子节点：形态学、几何变换、选择排序、生成器等
/// <see cref="JlRegion"/> 算子，输入/输出都是平台区域类型。
/// <para>
/// 结构元不再要求用户预先画一个区域：节点按 Size 生成 size×size 的居中矩形结构元，
/// 需要参考点的算子（Erosion2/Dilation2）用 Row/Column 指定。
/// </para>
/// </summary>
public sealed class JLVisionRegionOpNode : IVisionNodeExecutor
{
    public string Type => "jlvision.regionOp";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.Require<VisionRegion>("region");
        var key = node.GetString("operator", "complement").Trim();
        var op = JlRegionOperations.Resolve(key);
        OperatorParameterForms.Validate(node);

        var regionSize = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 5), "size");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row", 10), "row");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column", 10), "column");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "phi", 0), "phi");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "length1", 10), "length1");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "length2", 10), "length2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "min", 0), "min");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "max", 1e9), "max");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "percent", 50), "percent");

        if (op.NeedsSecondRegion && !context.TryGetValue("region2", out _))
            throw new InvalidOperationException($"JLVision region operator '{key}' requires a region2 input.");
        if (op.NeedsImage && !context.TryGetValue("image", out _))
            throw new InvalidOperationException($"JLVision region operator '{key}' requires an image input.");

        using var native = JLVisionRegions.ToNative(source);
        JlRegion? second = null;
        JlRegion? structuringElement = null;
        JlImage? image = null;
        JlRegion? result = null;
        try
        {
            second = context.TryGetValue("region2", out var secondValue) && secondValue.Value is VisionRegion secondRegion
                ? JLVisionRegions.ToNative(secondRegion)
                : native;

            if (op.NeedsStructuringElement)
            {
                var size = Math.Clamp((int)Math.Round(OperatorParameterForms.Number(node, "size", 5)), 1, 511);
                structuringElement = new JlRegion();
                structuringElement.GenRectangle2(size / 2d, size / 2d, 0, size / 2d, size / 2d);
            }

            if (op.NeedsImage && context.TryGetValue("image", out var imageValue) && imageValue.Value is IVisionImage visionImage)
            {
                var mat = visionImage.NativeImage as OpenCvSharp.Mat;
                if (mat is null || mat.Empty())
                    throw new InvalidOperationException("jlvision.regionOp received an empty image input.");
                image = JLVisionImages.FromMat(mat);
            }

            var args = new JlRegionOperatorArgs
            {
                Region = native,
                Second = second,
                StructuringElement = structuringElement,
                Image = image,
                Size = OperatorParameterForms.Number(node, "size", 5),
                Count = node.GetInt("count", 1),
                Row = OperatorParameterForms.Number(node, "row", 10),
                Column = OperatorParameterForms.Number(node, "column", 10),
                Phi = OperatorParameterForms.Number(node, "phi", 0),
                Length1 = OperatorParameterForms.Number(node, "length1", 10),
                Length2 = OperatorParameterForms.Number(node, "length2", 10),
                Min = OperatorParameterForms.Number(node, "min", 0),
                Max = OperatorParameterForms.Number(node, "max", 1e9),
                Percent = OperatorParameterForms.Number(node, "percent", 50),
                Mode = node.GetString("mode", string.Empty).Trim()
            };

            result = op.Run(args);
            // 补集/平移/缩放等算子会把结果推出图像范围；平台区域要求 run 落在图像内，这里统一裁到画幅。
            using var domain = new JlRegion();
            domain.GenRectangle1(new JlTuple(0d), new JlTuple(0d), new JlTuple((double)(source.Height - 1)), new JlTuple((double)(source.Width - 1)));
            using var clipped = result.Intersection(domain);
            var output = JLVisionRegions.FromNative(clipped, source.Width, source.Height, cancellationToken);
            var area = 0L;
            foreach (var component in output.Components)
                foreach (var run in component.Runs)
                    area += run.EndColumn - run.StartColumn + 1;
            using var overlaySurface = JLVisionNodeSupport.Canvas(source.Width, source.Height);
            JLVisionNodeSupport.DrawRegionRuns(overlaySurface, output, new OpenCvSharp.Scalar(0, 200, 0));
            var diagnostics = JLVisionNodeSupport.Diagnostics(
                node.Type, "standard", 0, ("operator", key), ("inputComponents", source.Components.Count),
                ("outputComponents", output.Components.Count), ("area", area));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["region"] = VisionValue.Region(output),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["operator"] = VisionValue.String(key),
                ["components"] = VisionValue.Integer(output.Components.Count),
                ["area"] = VisionValue.Integer(area),
                ["diagnostics"] = VisionValue.String(diagnostics)
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["operator"] = key,
                ["operatorLabel"] = op.Label,
                ["parametersUsed"] = op.Note,
                ["inputComponents"] = source.Components.Count,
                ["outputComponents"] = output.Components.Count,
                ["structuringElement"] = op.NeedsStructuringElement
                    ? $"{Math.Clamp((int)Math.Round(OperatorParameterForms.Number(node, "size", 5)), 1, 511)}x{Math.Clamp((int)Math.Round(OperatorParameterForms.Number(node, "size", 5)), 1, 511)}"
                    : null
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision region operator '{key}' failed: {ex.Message} (parameters: {op.Note}).", ex);
        }
        finally
        {
            result?.Dispose();
            if (!ReferenceEquals(second, native)) second?.Dispose();
            structuringElement?.Dispose();
            image?.Dispose();
        }
    }
}
