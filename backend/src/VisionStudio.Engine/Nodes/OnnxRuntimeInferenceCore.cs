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
    public static OnnxModelSession LoadNet(NodeDefinition node)
    {
        var path = node.GetString("modelPath", string.Empty).Trim();
        if (path.Length == 0) throw new InvalidOperationException($"Node '{node.Type}' requires modelPath to an ONNX model file.");
        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved)) throw new InvalidOperationException($"ONNX model '{resolved}' does not exist.");
        try { return new OnnxModelSession(resolved); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not load ONNX model '{resolved}': {ex.Message}", ex); }
    }

    public static (OnnxPreparedInput Blob, string OutputName, int Width, int Height) PrepareInput(OnnxModelSession net, Mat source, NodeDefinition node)
    {
        if (source.Empty()) throw new InvalidOperationException("ONNX inference requires a non-empty image.");
        if (source.Depth() != MatType.CV_8U) throw new InvalidOperationException("ONNX preprocessing requires byte pixels.");
        var width = node.GetInt("inputWidth", 224); var height = node.GetInt("inputHeight", 224);
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidOperationException("ONNX inputWidth and inputHeight must be between 1 and 4096.");
        var scale = node.GetDouble("scale", 1d / 255);
        var means = new[] { node.GetDouble("meanB", 0), node.GetDouble("meanG", 0), node.GetDouble("meanR", 0) };
        if (!double.IsFinite(scale) || means.Any(x => !double.IsFinite(x))) throw new InvalidOperationException("ONNX normalization scale and mean values must be finite.");
        using var bgr = new Mat();
        if (source.Channels() == 3) source.CopyTo(bgr);
        else JLVisionOperators.CvtColor(source, bgr, source.Channels() switch { 1 => ColorConversionCodes.GRAY2BGR, 4 => ColorConversionCodes.BGRA2BGR, _ => throw new InvalidOperationException("ONNX inference supports 1, 3, or 4 channel images.") });
        using var resized = new Mat();
        if (node.GetBool("crop", false))
        {
            var factor = Math.Max((double)width / bgr.Width, (double)height / bgr.Height);
            using var scaled = new Mat();
            JLVisionOperators.Resize(bgr, scaled, new Size(Math.Max(width, (int)Math.Round(bgr.Width * factor)), Math.Max(height, (int)Math.Round(bgr.Height * factor))));
            using var cropped = new Mat(scaled, new Rect((scaled.Width - width) / 2, (scaled.Height - height) / 2, width, height));
            cropped.CopyTo(resized);
        }
        else JLVisionOperators.Resize(bgr, resized, new Size(width, height));
        var pixels = checked(width * height);
        var values = new float[checked(3 * pixels)];
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
                values[channel * pixels + index] = (float)normalized;
            }
        }
        var tensor = new DenseTensor<float>(values, new[] { 1, 3, height, width });
        var metadata = net.Session.InputMetadata;
        if (metadata.Count != 1) throw new InvalidOperationException("Image inference requires a model with exactly one input tensor.");
        net.Input = tensor;
        return (new(tensor), node.GetString("outputName", string.Empty).Trim(), width, height);
    }

    public static OnnxOutputTensor Forward(OnnxModelSession net, string outputName, string modelPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (net.Input is null) throw new InvalidOperationException("ONNX image input has not been prepared.");
        var name = net.Session.InputMetadata.Keys.Single();
        var output = string.IsNullOrEmpty(outputName) ? net.Session.OutputMetadata.Keys.First() : outputName;
        if (!net.Session.OutputMetadata.ContainsKey(output)) throw new InvalidOperationException($"ONNX output '{output}' does not exist.");
        using var options = new RunOptions();
        using var cancellation = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var result = net.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, net.Input) }, new[] { output }, options);
            var tensor = result.Single().AsTensor<float>();
            var values = tensor.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new(values, tensor.Dimensions.ToArray());
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception ex) { throw new InvalidOperationException($"ONNX model '{modelPath}' inference failed for output '{output}': {ex.Message}", ex); }
    }
    public static float[] ReadFloatTensor(OnnxOutputTensor tensor, string contract)
    {
        if (tensor.Values.Length == 0) throw new InvalidOperationException($"ONNX output for {contract} is empty.");
        if (tensor.Values.Any(x => !float.IsFinite(x))) throw new InvalidOperationException($"ONNX output for {contract} contains a non-finite value.");
        return tensor.Values;
    }
    public static int[] Shape(OnnxOutputTensor tensor) => tensor.Dimensions;
    public static string ResolvePath(NodeDefinition node) => Path.GetFullPath(node.GetString("modelPath", string.Empty).Trim());
}
