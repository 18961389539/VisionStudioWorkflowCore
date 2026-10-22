using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 通用 JLVision 图像算子节点：用 <c>operator</c> 选择底层 <see cref="JlImage"/> 算子，
/// 把之前只能写代码调用的逐像素/滤波/形态学/位运算能力暴露成可配置节点。
/// <para>
/// 设计取舍：所有算子共用一组参数（size/size2/mult/offset/threshold/count/mode），
/// 每个算子在目录描述里标注自己取哪几个。这样新增算子只加一行表项，不需要新节点类型。
/// </para>
/// </summary>
public sealed class JLVisionImageOpNode : IVisionNodeExecutor
{
    public string Type => "jlvision.imageOp";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty())
            throw new InvalidOperationException("jlvision.imageOp requires a non-empty image.");

        var key = node.GetString("operator", "abs").Trim();
        var op = JlImageOperations.Resolve(key);

        OperatorParameterForms.Validate(node);
        if (ImageOperationGeometry.IsSpatial(node))
            return ValueTask.FromResult(ImageOperationGeometry.Execute(source, node, key, cancellationToken));

        // 参数守卫：把所有共用数值参数先做有限性校验，避免原生算子拿到 NaN/Inf 才报错。
        CoordinateValidation.RequireNonEmpty(key, "operator");
        var size = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 3), "size");
        var size2 = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size2", 3), "size2");
        var mult = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "mult", 1), "mult");
        var offset = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "offset", 0), "offset");
        var threshold = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "threshold", 128), "threshold");

        if (op.Pixels == JlPixelKind.Color && source.Mat.Channels() != 3)
            throw new InvalidOperationException(
                $"JLVision operator '{key}' requires a 3-channel image; the input has {source.Mat.Channels()} channel(s).");
        if (op.Pixels is JlPixelKind.Real or JlPixelKind.UnitReal && source.Mat.Depth() != OpenCvSharp.MatType.CV_32F)
            throw new InvalidOperationException(
                $"JLVision operator '{key}' requires a real (CV_32F) image; the input is {source.Mat.Type()}. " +
                "Chain a convertType=real step first (this node exposes convertType as well).");

        using var third = key is "compose3" or "rgb3ToGray" ? JLVisionImages.FromMat(context.RequireImage("image3").Mat) : null;
        if (third is not null)
        {
            var second = context.RequireImage("image2"); var thirdSource = context.RequireImage("image3");
            if (source.Mat.Channels() != 1 || second.Mat.Channels() != 1 || thirdSource.Mat.Channels() != 1 ||
                source.Mat.Type() != second.Mat.Type() || source.Mat.Type() != thirdSource.Mat.Type() ||
                source.Width != second.Width || source.Height != second.Height || source.Width != thirdSource.Width || source.Height != thirdSource.Height)
                throw new InvalidOperationException("通道合成需要三张尺寸和像素类型相同的单通道图像。");
        }
        using var primary = JLVisionImages.FromMat(source.Mat);
        JlImage? ownedSecondary = null;
        JlImage? ownedStructuringElement = null;
        JlImage secondary;
        var secondConnected = false;
        if (context.TryGetValue("image2", out var secondValue) && secondValue.Value is IVisionImage secondImage)
        {
            var secondMat = secondImage.NativeImage as OpenCvSharp.Mat;
            if (secondMat is null || secondMat.Empty())
                throw new InvalidOperationException("jlvision.imageOp received an empty image2 input.");
            ownedSecondary = JLVisionImages.FromMat(secondMat);
            secondary = ownedSecondary;
            secondConnected = true;
        }
        else if (op.NeedsStructuringElement)
        {
            // 灰度形态学没有结构元就没有意义：按 size 生成一个平坦矩形结构元，避免用户被迫再造一张图。
            var seSize = Math.Clamp((int)Math.Round(OperatorParameterForms.Number(node, "size", 3)), 1, 63);
            ownedStructuringElement = new JlImage();
            ownedStructuringElement.GenImageConst("byte", seSize, seSize);
            secondary = ownedStructuringElement;
        }
        else
        {
            secondary = primary;
        }

        var args = new JlImageOperatorArgs
        {
            Image = primary,
            Secondary = secondary,
            Third = third,
            LutJson = node.GetString("lutJson", System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 256))),
            Size = size,
            Row = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "row", 0), "row"),
            Column = CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "column", 0), "column"),
            Size2 = size2,
            Mult = mult,
            Offset = offset,
            Threshold = threshold,
            Count = Math.Max(0, node.GetInt("count", 1)),
            Mode = node.GetString("mode", "byte").Trim()
        };

        JlImage? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = op.Run(args);
            if (result is null)
                throw new InvalidOperationException($"JLVision operator '{key}' returned no image.");

            using var mat = JLVisionImages.ToMat(result);
            using var preview = JLVisionNodeSupport.Preview(mat);
            var diagnostics = JLVisionNodeSupport.Diagnostics(
                node.Type, "standard", NodeCompletionGuards.CountWarnings(secondConnected ? false : op.NeedsStructuringElement),
                ("operator", key), ("inputWidth", source.Width), ("inputHeight", source.Height),
                ("outputWidth", mat.Width), ("outputHeight", mat.Height));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(VisionImage.Own(mat.Clone())),
                ["overlay"] = VisionValue.Image(VisionImage.Own(preview.Clone())),
                ["operator"] = VisionValue.String(key),
                ["width"] = VisionValue.Integer(mat.Width),
                ["height"] = VisionValue.Integer(mat.Height),
                ["diagnostics"] = VisionValue.String(diagnostics)
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["operator"] = key,
                ["operatorLabel"] = op.Label,
                ["parametersUsed"] = op.Note,
                ["secondInputConnected"] = secondConnected,
                ["structuringElement"] = op.NeedsStructuringElement
                    ? (secondConnected ? "input" : "generated")
                    : null,
                ["inputWidth"] = source.Width,
                ["inputHeight"] = source.Height,
                ["outputWidth"] = mat.Width,
                ["outputHeight"] = mat.Height,
                ["pixelType"] = mat.Type().ToString()
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision operator '{key}' failed: {ex.Message} (check pixel type and parameters: {op.Note}).", ex);
        }
        finally
        {
            result?.Dispose();
            ownedSecondary?.Dispose();
            ownedStructuringElement?.Dispose();
        }
    }
}
