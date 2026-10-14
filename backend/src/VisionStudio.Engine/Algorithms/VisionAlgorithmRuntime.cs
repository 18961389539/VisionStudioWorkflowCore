using System.Runtime.InteropServices;
using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

public sealed record VisionAlgorithmDiagnostics(string TraditionalBackend, string NativeVersion, string InferenceBackend, string InferenceVersion);

public static class VisionAlgorithmRuntime
{
    private static readonly Lazy<VisionAlgorithmDiagnostics> Diagnostics = new(() =>
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("JLVision requires Windows and an x64 process.");
        try
        {
            using var version = JLVisionExtendedOperators.Tuples(317, "get_system", 1, new JlTuple("version"))[0];
            var inferenceVersion = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
            return new("JLVision", version[0].S, "ONNX Runtime", inferenceVersion);
        }
        catch (Exception ex) { throw new InvalidOperationException($"JLVision runtime could not initialize: {ex.Message}", ex); }
    });
    public static VisionAlgorithmDiagnostics Initialize() => Diagnostics.Value;
    public static void ClearModelCache() => JLVisionModelCache.Clear();

    internal static string? BackendFor(string type)
    {
        if (type.StartsWith("inference.", StringComparison.OrdinalIgnoreCase)) return "ONNX Runtime";
        if (type.StartsWith("contour.", StringComparison.OrdinalIgnoreCase) || type.StartsWith("match.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) || type is "measure.blob" or "measure.blobs" or "measure.caliper" or "measure.caliperLine" or "measure.caliperCircle" or "measure.fitLine" or "measure.fitCircle" or "measure.fitEllipse" or "measure.rotatedRect" or "calibration.cameraIntrinsics") return "JLVision";
        if (type.StartsWith("image.", StringComparison.OrdinalIgnoreCase) && type is not ("image.acquire" or "image.synthetic")) return "JLVision";
        return null;
    }
}
