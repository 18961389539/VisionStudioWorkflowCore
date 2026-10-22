using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Nodes;

internal sealed class OnnxModelSession : IDisposable
{
    private readonly JLVisionModelCache.Lease _lease;
    public InferenceSession Session { get; }
    public DenseTensor<float>? Input { get; set; }
    public OnnxModelSession(string path)
    {
        var file = new FileInfo(path);
        _lease = JLVisionModelCache.Acquire($"onnx:{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}", () => new InferenceSession(path));
        Session = (InferenceSession)_lease.Model;
    }
    public void Dispose() { Input = null; _lease.Dispose(); }
}
internal sealed record OnnxPreparedInput(DenseTensor<float> Tensor) : IDisposable { public void Dispose() { } }
internal sealed record OnnxOutputTensor(float[] Values, int[] Dimensions) : IDisposable { public void Dispose() { } }

/// <summary>ONNX Runtime inference. Image normalization preserves BGR/RGB, means and NCHW node contracts.</summary>
internal static class OnnxInferenceHelpers
{
    public static string[] ResolveLabels(NodeDefinition node)
    {
        var explicitText = node.GetString("labels", string.Empty).Trim();
        if (explicitText.Length > 0)
            return explicitText.Split(',', StringSplitOptions.TrimEntries);
        var packagePath = node.GetString("packagePath", string.Empty).Trim();
        if (packagePath.Length == 0) return [];
        var labelsCsv = VisionStudio.Engine.ExternalTrain.ExternalTrainPackage.ResolveRuntimePaths(packagePath).LabelsCsv;
        return string.IsNullOrWhiteSpace(labelsCsv) ? [] : labelsCsv.Split(',', StringSplitOptions.TrimEntries);
    }

    public static OnnxModelSession LoadNet(NodeDefinition node)
    {
        var packagePath = node.GetString("packagePath", string.Empty).Trim();
        if (packagePath.Length > 0)
        {
            var resolvedPackage = VisionStudio.Engine.ExternalTrain.ExternalTrainPackage.ResolveRuntimePaths(packagePath);
            if (resolvedPackage.ModelPath is null)
                throw new InvalidOperationException($"Node '{node.Type}' packagePath must point to an ONNX external-train package (classification/anomaly/segmentation/instance/pose/obb).");
            return LoadNetFromPath(resolvedPackage.ModelPath);
        }
        var path = node.GetString("modelPath", string.Empty).Trim();
        if (path.Length == 0) throw new InvalidOperationException($"Node '{node.Type}' requires modelPath to an ONNX model file (or packagePath to an external-train package).");
        return LoadNetFromPath(path);
    }

    public static OnnxModelSession LoadNetFromPath(string path)
    {
        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved)) throw new InvalidOperationException($"ONNX model '{resolved}' does not exist.");
        try { return new OnnxModelSession(resolved); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not load ONNX model '{resolved}': {ex.Message}", ex); }
    }

    public static (OnnxPreparedInput Blob, string OutputName, int Width, int Height) PrepareInput(OnnxModelSession net, Mat source, NodeDefinition node)
    {
        var (width, height) = ResolveInputSize(node);
        var values = new float[checked(3 * width * height)];
        NormalizeInto(values, 0, source, width, height, node);
        var tensor = new DenseTensor<float>(values, new[] { 1, 3, height, width });
        RequireSingleInput(net);
        net.Input = tensor;
        return (new(tensor), node.GetString("outputName", string.Empty).Trim(), width, height);
    }

    /// <summary>True when one run can process <paramref name="count"/> views (dynamic or matching batch dimension).</summary>
    public static bool SupportsBatch(OnnxModelSession net, int count)
    {
        if (count < 2) return false;
        var metadata = net.Session.InputMetadata;
        if (metadata.Count != 1) return false;
        var dimensions = metadata.Single().Value.Dimensions;
        return dimensions.Length == 4 && (dimensions[0] <= 0 || dimensions[0] == count);
    }

    /// <summary>
    /// Packs several views into one [N,3,H,W] tensor so test-time augmentation costs one session
    /// run instead of N. Callers must check <see cref="SupportsBatch"/> first; a model with a
    /// fixed batch dimension of one cannot be fed here.
    /// </summary>
    public static OnnxPreparedInput PrepareBatchInput(OnnxModelSession net, IReadOnlyList<Mat> sources, NodeDefinition node)
    {
        if (sources.Count == 0) throw new InvalidOperationException("ONNX batch inference requires at least one image.");
        var (width, height) = ResolveInputSize(node);
        var viewLength = checked(3 * checked(width * height));
        var values = new float[checked(sources.Count * viewLength)];
        for (var index = 0; index < sources.Count; index++)
            NormalizeInto(values, index * viewLength, sources[index], width, height, node);
        var tensor = new DenseTensor<float>(values, new[] { sources.Count, 3, height, width });
        RequireSingleInput(net);
        net.Input = tensor;
        return new(tensor);
    }

    private static (int Width, int Height) ResolveInputSize(NodeDefinition node)
    {
        var width = node.GetInt("inputWidth", 224); var height = node.GetInt("inputHeight", 224);
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidOperationException("ONNX inputWidth and inputHeight must be between 1 and 4096.");
        return (width, height);
    }

    private static void RequireSingleInput(OnnxModelSession net)
    {
        if (net.Session.InputMetadata.Count != 1) throw new InvalidOperationException("Image inference requires a model with exactly one input tensor.");
    }

    private static void NormalizeInto(float[] values, int offset, Mat source, int width, int height, NodeDefinition node)
    {
        if (source.Empty()) throw new InvalidOperationException("ONNX inference requires a non-empty image.");
        if (source.Depth() != MatType.CV_8U) throw new InvalidOperationException("ONNX preprocessing requires byte pixels.");
        var scale = node.GetDouble("scale", 1d / 255);
        var means = new[] { node.GetDouble("meanB", 0), node.GetDouble("meanG", 0), node.GetDouble("meanR", 0) };
        if (!double.IsFinite(scale) || means.Any(x => !double.IsFinite(x))) throw new InvalidOperationException("ONNX normalization scale and mean values must be finite.");
        using var bgr = new Mat();
        if (source.Channels() == 3) source.CopyTo(bgr);
        else JLVisionOperators.CvtColor(source, bgr, source.Channels() switch { 1 => ColorConversionCodes.GRAY2BGR, 4 => ColorConversionCodes.BGRA2BGR, _ => throw new InvalidOperationException("ONNX inference supports 1, 3, or 4 channel images.") });
        using var resized = new Mat();
        if (node.GetBool("crop", false))
        {
            if (node.GetBool("letterbox", false))
                throw new InvalidOperationException("ONNX preprocessing cannot enable crop and letterbox together.");
            var factor = Math.Max((double)width / bgr.Width, (double)height / bgr.Height);
            using var scaled = new Mat();
            JLVisionOperators.Resize(bgr, scaled, new Size(Math.Max(width, (int)Math.Round(bgr.Width * factor)), Math.Max(height, (int)Math.Round(bgr.Height * factor))));
            using var cropped = new Mat(scaled, new Rect((scaled.Width - width) / 2, (scaled.Height - height) / 2, width, height));
            cropped.CopyTo(resized);
        }
        else if (node.GetBool("letterbox", false))
        {
            // Ultralytics-style letterbox: preserve aspect, pad with 114 gray, centered.
            var factor = Math.Min((double)width / bgr.Width, (double)height / bgr.Height);
            var newW = Math.Max(1, (int)Math.Round(bgr.Width * factor));
            var newH = Math.Max(1, (int)Math.Round(bgr.Height * factor));
            using var scaled = new Mat();
            JLVisionOperators.Resize(bgr, scaled, new Size(newW, newH));
            resized.Create(height, width, MatType.CV_8UC3);
            resized.SetTo(new Scalar(114, 114, 114));
            var padX = (width - newW) / 2;
            var padY = (height - newH) / 2;
            using var roi = new Mat(resized, new Rect(padX, padY, newW, newH));
            scaled.CopyTo(roi);
        }
        else JLVisionOperators.Resize(bgr, resized, new Size(width, height));
        var pixels = checked(width * height);
        var swap = node.GetBool("swapRB", false);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var color = resized.At<Vec3b>(y, x);
            var index = y * width + x;
            for (var channel = 0; channel < 3; channel++)
            {
                var sourceChannel = swap ? 2 - channel : channel;
                var normalized = (color[sourceChannel] - means[sourceChannel]) * scale;
                if (!float.IsFinite((float)normalized)) throw new InvalidOperationException("ONNX normalized input contains a non-finite value.");
                values[offset + channel * pixels + index] = (float)normalized;
            }
        }
    }

    public static OnnxOutputTensor Forward(OnnxModelSession net, string outputName, string modelPath, CancellationToken cancellationToken = default)
    {
        var outputs = ForwardOutputs(net, string.IsNullOrEmpty(outputName) ? null : [outputName], modelPath, cancellationToken);
        return outputs[0];
    }

    /// <summary>
    /// Run the session and return one or more outputs. When <paramref name="outputNames"/> is null or empty,
    /// every graph output is returned in model order (needed for YOLO-Seg dual heads).
    /// </summary>
    public static OnnxOutputTensor[] ForwardOutputs(OnnxModelSession net, IReadOnlyList<string>? outputNames, string modelPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (net.Input is null) throw new InvalidOperationException("ONNX image input has not been prepared.");
        var name = net.Session.InputMetadata.Keys.Single();
        string[] requested;
        if (outputNames is null || outputNames.Count == 0)
            requested = net.Session.OutputMetadata.Keys.ToArray();
        else
        {
            requested = outputNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            if (requested.Length == 0) requested = net.Session.OutputMetadata.Keys.ToArray();
            foreach (var output in requested)
                if (!net.Session.OutputMetadata.ContainsKey(output))
                    throw new InvalidOperationException($"ONNX output '{output}' does not exist.");
        }
        if (requested.Length == 0) throw new InvalidOperationException("ONNX model has no outputs.");
        using var options = new RunOptions();
        using var cancellation = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var result = net.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, net.Input) }, requested, options);
            var list = new OnnxOutputTensor[requested.Length];
            for (var i = 0; i < requested.Length; i++)
            {
                var tensor = result[i].AsTensor<float>();
                list[i] = new(tensor.ToArray(), tensor.Dimensions.ToArray());
            }
            cancellationToken.ThrowIfCancellationRequested();
            return list;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) { throw new InvalidOperationException($"ONNX model '{modelPath}' inference failed: {ex.Message}", ex); }
    }
    public static float[] ReadFloatTensor(OnnxOutputTensor tensor, string contract)
    {
        if (tensor.Values.Length == 0) throw new InvalidOperationException($"ONNX output for {contract} is empty.");
        if (tensor.Values.Any(x => !float.IsFinite(x))) throw new InvalidOperationException($"ONNX output for {contract} contains a non-finite value.");
        return tensor.Values;
    }
    public static int[] Shape(OnnxOutputTensor tensor) => tensor.Dimensions;
    public static string ResolvePath(NodeDefinition node)
    {
        var packagePath = node.GetString("packagePath", string.Empty).Trim();
        if (packagePath.Length > 0)
        {
            var resolved = VisionStudio.Engine.ExternalTrain.ExternalTrainPackage.ResolveRuntimePaths(packagePath);
            if (resolved.ModelPath is not null) return resolved.ModelPath;
        }
        var path = node.GetString("modelPath", string.Empty).Trim();
        return path.Length > 0 ? Path.GetFullPath(path) : "(missing modelPath)";
    }
}
