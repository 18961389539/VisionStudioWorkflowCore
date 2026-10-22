using JLVisionLib;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JLVision 阈值/分割节点：图像进，区域出。覆盖全局/自动/双阈值/快速/滞后/局部/方差/动态阈值与区域生长。
/// </summary>
public sealed class JLVisionThresholdNode : IVisionNodeExecutor
{
    public string Type => "jlvision.threshold";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image");
        if (source.Mat.Empty())
            throw new InvalidOperationException("jlvision.threshold requires a non-empty image.");

        var key = node.GetString("operator", "threshold").Trim();
        var op = JlThresholdOperations.Resolve(key);
        OperatorParameterForms.Validate(node);
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size", 3), "size");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "size2", 3), "size2");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "mult", 0), "mult");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "offset", 0), "offset");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "threshold", 128), "threshold");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "min", 0), "min");
        CoordinateValidation.RequireFinite(OperatorParameterForms.Number(node, "max", 128), "max");
        if (op.NeedsSecond && !context.TryGetValue("image2", out _))
            throw new InvalidOperationException($"JLVision threshold operator '{key}' requires an image2 input (the threshold image).");

        using var primary = JLVisionImages.FromMat(source.Mat);
        JlImage? secondary = null;
        JlRegion? region = null;
        try
        {
            var secondConnected = false;
            if (context.TryGetValue("image2", out var secondValue) && secondValue.Value is IVisionImage secondImage
                && secondImage.NativeImage is OpenCvSharp.Mat secondMat && !secondMat.Empty())
            {
                secondary = JLVisionImages.FromMat(secondMat);
                secondConnected = true;
            }
            if (op.NeedsSecond && !secondConnected)
                throw new InvalidOperationException($"JLVision threshold operator '{key}' requires a non-empty image2 input (the threshold image).");

            var args = new JlThresholdArgs
            {
                Image = primary,
                Secondary = secondary ?? primary,
                Size = OperatorParameterForms.Number(node, "size", 3),
                Size2 = OperatorParameterForms.Number(node, "size2", 3),
                Mult = OperatorParameterForms.Number(node, "mult", 0),
                Offset = OperatorParameterForms.Number(node, "offset", 0),
                Threshold = OperatorParameterForms.Number(node, "threshold", 128),
                Count = node.GetInt("count", 5),
                Min = OperatorParameterForms.Number(node, "min", 0),
                Max = OperatorParameterForms.Number(node, "max", 128),
                Mode = node.GetString("mode", string.Empty).Trim(),
                Mode2 = node.GetString("mode2", string.Empty).Trim()
            };

            region = op.Run(args);
            var output = JLVisionRegions.FromNative(region, source.Width, source.Height, cancellationToken);
            var area = 0L;
            foreach (var component in output.Components)
                foreach (var run in component.Runs)
                    area += run.EndColumn - run.StartColumn + 1;

            using var overlaySurface = JLVisionNodeSupport.Canvas(source.Width, source.Height);
            JLVisionNodeSupport.DrawRegionRuns(overlaySurface, output, new OpenCvSharp.Scalar(0, 220, 0));
            var outputs = new Dictionary<string, VisionValue>
            {
                ["region"] = VisionValue.Region(output),
                ["overlay"] = VisionValue.Image(VisionImage.Own(overlaySurface.Clone())),
                ["operator"] = VisionValue.String(key),
                ["area"] = VisionValue.Integer(area),
                ["components"] = VisionValue.Integer(output.Components.Count),
                ["diagnostics"] = VisionValue.String(JLVisionNodeSupport.Diagnostics(
                    node.Type, "standard", 0, ("operator", key), ("components", output.Components.Count),
                    ("area", area), ("secondInput", secondConnected)))
            };
            var summary = new Dictionary<string, object?>
            {
                ["algorithmBackend"] = "jlvision",
                ["operator"] = key,
                ["operatorLabel"] = op.Label,
                ["parametersUsed"] = op.Note,
                ["components"] = output.Components.Count,
                ["area"] = area,
                ["secondInputConnected"] = secondConnected
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        catch (JlOperatorException ex)
        {
            throw new InvalidOperationException(
                $"JLVision threshold operator '{key}' failed: {ex.Message} (parameters: {op.Note}).", ex);
        }
        finally
        {
            region?.Dispose();
            secondary?.Dispose();
        }
    }
}
