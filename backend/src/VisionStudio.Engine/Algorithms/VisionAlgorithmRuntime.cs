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

    /// <summary>
    /// Default algorithmBackend label for a node type when the executor did not set one.
    /// Short vocabulary: jlvision | opencv | managed | zxing | tesseract | onnx | hybrid.
    /// Domain-specific keys (calibrationBackend, recognitionBackend, matchingBackend, inferenceBackend)
    /// are set by the node when relevant. Returns null for non-algorithm / device / flow nodes.
    /// </summary>
    internal static string? BackendFor(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (type.StartsWith("inference.", StringComparison.OrdinalIgnoreCase)) return "onnx";
        if (type.Equals("recognition.barcode", StringComparison.OrdinalIgnoreCase)) return "zxing";
        if (type.Equals("recognition.ocr", StringComparison.OrdinalIgnoreCase)) return "tesseract";
        if (type.Equals("recognition.aruco", StringComparison.OrdinalIgnoreCase)) return "opencv";
        if (type is "color.classify" or "image.denoise" or "image.stitch" or "match.feature") return "opencv";
        if (type.Equals("calibration.charuco", StringComparison.OrdinalIgnoreCase)) return "opencv";
        if (type.Equals("script.csharp", StringComparison.OrdinalIgnoreCase)) return "roslyn";
        if (type.Equals("calibration.handEye3D", StringComparison.OrdinalIgnoreCase)) return "opencv";
        if (type.Equals("calibration.dualCameraExtrinsics", StringComparison.OrdinalIgnoreCase)) return "opencv";
        if (type.Equals("calibration.handEye2D", StringComparison.OrdinalIgnoreCase)) return "managed";
        if (type.Equals("defect.referenceCompare", StringComparison.OrdinalIgnoreCase)) return "hybrid";
        // cameraIntrinsics / undistort are model-dependent (jlvision vs opencv fisheye); nodes set calibrationBackend.
        if (type is "measure.grayHistogram" or "measure.caliperWidth" or "measure.caliperArray" or "measure.contourThickness" or "measure.metrology" or "jlvision.imageOp" or "jlvision.regionOp" or "jlvision.regionFeature" or "jlvision.geometry" or "jlvision.matrixOp" or "jlvision.homMat" or "jlvision.contourOp" or "jlvision.threshold" or "jlvision.contourFeature" or "jlvision.measure1d") return "jlvision";
        if (type is "measure.roundness" or "measure.straightness" or "measure.contourDeviation" or "measure.regionIntensity" or "measure.colorDifference" or "calibration.detectBoard" || type.StartsWith("defect.", StringComparison.OrdinalIgnoreCase) || type.StartsWith("region.", StringComparison.OrdinalIgnoreCase) || type.StartsWith("contour.", StringComparison.OrdinalIgnoreCase) || type.StartsWith("match.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) || type is "measure.blob" or "measure.blobs" or "measure.caliper" or "measure.caliperLine" or "measure.caliperCircle" or "measure.fitLine" or "measure.fitCircle" or "measure.fitEllipse" or "measure.rotatedRect" ) return "jlvision";
        if (type.StartsWith("image.", StringComparison.OrdinalIgnoreCase) && type is not ("image.acquire" or "image.synthetic")) return "jlvision";
        return null;
    }

    /// <summary>Stamp recognitionBackend / matchingBackend / inferenceBackend / calibrationBackend when absent.</summary>
    internal static IReadOnlyDictionary<string, object?> EnsureDomainBackendLabels(string type, IReadOnlyDictionary<string, object?> summary)
    {
        if (string.IsNullOrWhiteSpace(type)) return summary;
        ArgumentNullException.ThrowIfNull(summary);
        string? domainKey = null;
        string? domainValue = null;
        if (type.StartsWith("inference.", StringComparison.OrdinalIgnoreCase))
        { domainKey = "inferenceBackend"; domainValue = "onnx"; }
        else if (type.Equals("recognition.barcode", StringComparison.OrdinalIgnoreCase))
        { domainKey = "recognitionBackend"; domainValue = "zxing"; }
        else if (type.Equals("recognition.ocr", StringComparison.OrdinalIgnoreCase))
        { domainKey = "recognitionBackend"; domainValue = "tesseract"; }
        else if (type.Equals("recognition.aruco", StringComparison.OrdinalIgnoreCase))
        { domainKey = "recognitionBackend"; domainValue = "opencv"; }
        else if (type.Equals("calibration.charuco", StringComparison.OrdinalIgnoreCase))
        { domainKey = "calibrationBackend"; domainValue = "opencv"; }
        else if (type.Equals("script.csharp", StringComparison.OrdinalIgnoreCase))
        { domainKey = "scriptBackend"; domainValue = "csharp"; }
        else if (type.Equals("calibration.handEye3D", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("calibration.dualCameraExtrinsics", StringComparison.OrdinalIgnoreCase))
        { domainKey = "calibrationBackend"; domainValue = "opencv"; }
        else if (type.Equals("calibration.handEye2D", StringComparison.OrdinalIgnoreCase))
        { domainKey = "calibrationBackend"; domainValue = "managed"; }
        else if (type.StartsWith("match.", StringComparison.OrdinalIgnoreCase))
        {
            domainKey = "matchingBackend";
            if (summary.TryGetValue("algorithmBackend", out var ab) && ab is string s &&
                s.Equals("managed", StringComparison.OrdinalIgnoreCase))
                domainValue = "managed";
            else if (summary.TryGetValue("algorithmBackend", out var ob) && ob is string o &&
                o.Equals("opencv", StringComparison.OrdinalIgnoreCase))
                domainValue = "opencv";
            else
                domainValue = "jlvision";
        }
        if (domainKey is null || domainValue is null || summary.ContainsKey(domainKey)) return summary;
        return new Dictionary<string, object?>(summary) { [domainKey] = domainValue };
    }

}
