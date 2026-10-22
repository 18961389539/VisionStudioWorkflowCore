using JLVisionLib;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

internal static class FlatFieldCorrection
{
    internal static ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = context.Require<VisionImage>("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("Flat-field correction requires a non-empty image.");
        var mode = ResultFileWriter.Choice(node, "referenceMode", "gainOffset", "gainOffset", "gain", "offset");
        var darkPolicy = ResultFileWriter.Choice(node, "darkPolicy", "skip", "skip", "fail");
        var outputType = ResultFileWriter.Choice(node, "outputType", "byte", "byte", "real");
        var low = Number(node, "clipLow", 0, 0, 20); var high = Number(node, "clipHigh", 0, 0, 20);
        var target = Number(node, "targetMean", 0, 0, 65535);
        var minimum = Number(node, "minGain", 1, 0.000001, 65535);
        var maximum = Number(node, "maxGain", 0, 0, 1e6);
        var maxStd = Number(node, "maxCorrectedStd", 0, 0, 65535);
        var positive = node.GetBool("requirePositiveGain", true);
        VisionImage? dark = null, bright = null;
        if (mode != "gain")
        {
            if (context.TryGetValue("dark", out var darkValue)) dark = darkValue.Require<VisionImage>("dark");
            else if (darkPolicy == "fail") throw new InvalidOperationException("This correction mode requires a dark reference or darkPolicy=skip.");
        }
        if (mode != "offset")
        {
            if (!context.TryGetValue("bright", out var brightValue)) throw new InvalidOperationException("Gain correction requires a bright reference.");
            bright = brightValue.Require<VisionImage>("bright");
        }
        foreach (var reference in new[] { dark, bright })
            if (reference is not null) { DefectNodeHelpers.SameSize(source, reference); DefectNodeHelpers.SameGrayDepth(source, reference); }
        using var image = Prepare(source, low, high, token);
        using var darkImage = dark is null ? image.ScaleImage(0d, 0d) : Prepare(dark, low, high, token);
        using var brightImage = bright is null ? image.ScaleImage(0d, 1d) : Prepare(bright, low, high, token);
        using var gain = mode == "offset" ? image.ScaleImage(0d, 1d) : brightImage.SubImage(darkImage, 1d, 0d);
        using var domain = gain.GetDomain();
        var gainStats = DefectNodeHelpers.Statistics(gain, domain);
        if (mode != "offset")
        {
            if (maximum > 0 && maximum <= minimum) throw new InvalidOperationException("maxGain must be 0 or greater than minGain.");
            using var gainMat = JLVisionImages.ToMat(gain); gainMat.GetArray(out float[] pixels);
            foreach (var value in pixels)
            {
                token.ThrowIfCancellationRequested();
                if (!float.IsFinite(value) || (positive ? value <= minimum : Math.Abs(value) <= minimum))
                    throw new InvalidOperationException("Flat-field gain violates finite/positive/minimum-magnitude protection.");
                if (maximum > 0 && Math.Abs(value) > maximum) throw new InvalidOperationException("Flat-field gain exceeds maxGain.");
            }
        }
        var autoTarget = target == 0;
        if (autoTarget) target = mode == "offset" ? DefectNodeHelpers.Statistics(darkImage, domain).Mean : gainStats.Mean;
        using var signal = image.SubImage(darkImage, 1d, 0d);
        using var corrected = mode == "offset" ? signal.ScaleImage(1, target) : signal.DivImage(gain, target, 0);
        using var output = corrected.ConvertImageType(outputType);
        var stats = DefectNodeHelpers.Statistics(output, domain);
        if (maxStd > 0 && stats.Deviation > maxStd) throw new InvalidOperationException("Corrected output exceeds maxCorrectedStd.");
        token.ThrowIfCancellationRequested();
        var diagnostics = NodeCompletionGuards.BuildDiagnostics(node.Type, NodeCompletionGuards.DiagnosticsLevel(node), 0, 1,
            ("referenceMode", mode), ("darkSkipped", dark is null), ("clipLow", low), ("clipHigh", high),
            ("gainMean", gainStats.Mean), ("targetMean", target), ("autoTarget", autoTarget), ("outputType", outputType),
            ("correctedMean", stats.Mean), ("correctedStd", stats.Deviation));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(output))), ["gainMean"] = VisionValue.Double(gainStats.Mean),
            ["correctedMean"] = VisionValue.Double(stats.Mean), ["correctedMin"] = VisionValue.Double(stats.Min),
            ["correctedMax"] = VisionValue.Double(stats.Max), ["correctedStd"] = VisionValue.Double(stats.Deviation), ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?> { ["referenceMode"] = mode, ["targetMean"] = target, ["outputType"] = outputType,
            ["gainMean"] = gainStats.Mean, ["correctedMean"] = stats.Mean, ["diagnostics"] = diagnostics }));
    }

    private static JlImage Prepare(VisionImage source, double low, double high, CancellationToken token)
    {
        using var gray = DefectNodeHelpers.Gray(source, true);
        using var mat = JLVisionImages.ToMat(gray); mat.GetArray(out float[] pixels);
        if (pixels.Any(value => !float.IsFinite(value))) throw new InvalidOperationException("Correction references require finite pixels.");
        if (low == 0 && high == 0) return gray.CopyImage();
        var sorted = pixels.Order().ToArray();
        var lower = sorted[(int)Math.Floor((sorted.Length - 1) * low / 100)];
        var upper = sorted[(int)Math.Ceiling((sorted.Length - 1) * (1 - high / 100))];
        for (var i = 0; i < pixels.Length; i++) { if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); pixels[i] = Math.Clamp(pixels[i], lower, upper); }
        mat.SetArray(pixels);
        return JLVisionImages.FromMat(mat);
    }

    private static double Number(NodeDefinition node, string name, double fallback, double min, double max)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value < min || value > max) throw new InvalidOperationException($"{name} must be finite and in {min}..{max}.");
        return value;
    }
}
