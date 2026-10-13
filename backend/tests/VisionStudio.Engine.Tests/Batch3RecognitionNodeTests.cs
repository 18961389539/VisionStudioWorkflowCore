using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using ZXing;
using ZXing.Common;

namespace VisionStudio.Engine.Tests;

public sealed class Batch3RecognitionNodeTests
{
    [Theory]
    [InlineData("QR_CODE", "VISION-QR-42", 0)]
    [InlineData("CODE_128", "VISION-128-42", 1)]
    public async Task BarcodeDecodesGeneratedRealSymbols(string formatName, string value, int mode)
    {
        var format = Enum.Parse<BarcodeFormat>(formatName);
        var matrix = new MultiFormatWriter().encode(value, format, 520, 180);
        using var source = new Mat(matrix.Height, matrix.Width, MatType.CV_8UC1);
        for (var y = 0; y < matrix.Height; y++)
        for (var x = 0; x < matrix.Width; x++) source.Set(y, x, matrix[x, y] ? (byte)0 : (byte)255);
        var vision = VisionImage.Own(source.Clone());
        try
        {
            var result = await Run(new BarcodeDecodeNode(), "recognition.barcode", new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(vision) }, $"{{\"mode\":\"{(mode == 0 ? "qr" : "1d")}\"}}");
            Assert.Equal(value, result.Outputs["text"].Require<string>("text"));
            Assert.Equal(formatName, result.Outputs["format"].Require<string>("format"));
            Assert.True(source.At<byte>(0, 0) is 0 or 255); // upstream image remains usable
        }
        finally { vision.Dispose(); }
    }

    [Fact]
    public async Task BarcodeRejectsUnknownMode()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(255));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new BarcodeDecodeNode(), "recognition.barcode", Inputs(source), "{\"mode\":\"fallback\"}"));
        Assert.Contains("Unsupported barcode mode", ex.Message);
    }

    [Theory]
    [InlineData("+eng")]
    [InlineData("eng+")]
    [InlineData("eng++chi_sim")]
    [InlineData("eng+bad lang")]
    public async Task OcrRejectsMalformedLanguageList(string language)
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(255));
        var dir = Path.Combine(Path.GetTempPath(), "visionstudio-no-tessdata");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new OcrNode(), "recognition.ocr", Inputs(source), JsonSerializer.Serialize(new { dataPath = dir, language })));
        Assert.Contains("language", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OcrRequiresConfiguredLanguageModel()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(255));
        var dir = Path.Combine(Path.GetTempPath(), "visionstudio-missing-model", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new OcrNode(), "recognition.ocr", Inputs(source), JsonSerializer.Serialize(new { dataPath = dir, language = "eng" })));
            Assert.Contains("missing", ex.Message);
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public async Task OcrRunsRealTesseractWhenLocalTessdataIsConfigured()
    {
        var tessdata = Environment.GetEnvironmentVariable("VISIONSTUDIO_TESSDATA");
        if (string.IsNullOrWhiteSpace(tessdata)) return;
        using var source = new Mat(180, 900, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(source, "VISION 123", new Point(35, 125), HersheyFonts.HersheySimplex, 3.0, Scalar.All(0), 6, LineTypes.AntiAlias);
        var result = await Run(new OcrNode(), "recognition.ocr", Inputs(source), JsonSerializer.Serialize(new { dataPath = tessdata, language = "eng" }));
        var text = result.Outputs["text"].Require<string>("text");
        Assert.Contains("VISION", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClassificationUsesActualOnnxOutputAndPreservesInput()
    {
        using var source = new Mat(40, 60, MatType.CV_8UC3, new Scalar(20, 90, 220));
        var result = await RunOnnx(new OnnxClassificationNode(), "inference.classification", TinyModels.Classification,
            source, new { inputWidth = 8, inputHeight = 8, applySoftmax = false });
        Assert.Equal(2, checked((int)result.Outputs["classIndex"].Require<long>("classIndex")));
        Assert.Equal(220, source.At<Vec3b>(0, 0).Item2);
    }

    [Fact]
    public async Task DetectionUsesStrictContractAndMapsInputPixelsBackToSource()
    {
        using var source = new Mat(40, 60, MatType.CV_8UC3, Scalar.All(128));
        var result = await RunOnnx(new OnnxDetectionNode(), "inference.detection", TinyModels.Detection,
            source, new { inputWidth = 8, inputHeight = 8, confidenceThreshold = 0.4 });
        Assert.Equal(1, checked((int)result.Outputs["count"].Require<long>("count")));
        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(15, detections[0].X, 1);
        Assert.Equal(10, detections[0].Y, 1);
        Assert.Equal(30, detections[0].Width, 1);
        Assert.Equal(20, detections[0].Height, 1);
        Assert.InRange(detections[0].Confidence!.Value, .49, .51);
        Assert.Equal(2, detections[0].ClassId);
    }

    [Fact]
    public async Task SegmentationProducesResizedArgmaxMask()
    {
        using var source = new Mat(40, 60, MatType.CV_8UC3, new Scalar(20, 90, 220));
        var result = await RunOnnx(new OnnxSegmentationNode(), "inference.segmentation", TinyModels.Segmentation,
            source, new { inputWidth = 8, inputHeight = 8 });
        var mask = ((VisionImage)result.Outputs["mask"].Value!).Mat;
        Assert.Equal(new Size(60, 40), mask.Size());
        Assert.Equal((byte)2, mask.At<byte>(20, 30));
    }

    [Fact]
    public async Task AnomalyUsesScalarOnnxInference()
    {
        using var source = new Mat(40, 60, MatType.CV_8UC3, Scalar.All(128));
        var result = await RunOnnx(new OnnxAnomalyNode(), "inference.anomaly", TinyModels.Anomaly,
            source, new { inputWidth = 8, inputHeight = 8, threshold = 0.5 });
        Assert.True(result.Outputs["isAnomaly"].Require<bool>("isAnomaly"));
        Assert.InRange(result.Outputs["score"].Require<double>("score"), .49, .51);
    }

    [Fact]
    public async Task DetectionAndSegmentationRejectCropBecauseCoordinatesCannotBeInverted()
    {
        using var source = new Mat(24, 30, MatType.CV_8UC3, Scalar.All(100));
        var det = await Assert.ThrowsAsync<InvalidOperationException>(() => RunOnnx(new OnnxDetectionNode(), "inference.detection", TinyModels.Detection, source, new { inputWidth = 8, inputHeight = 8, crop = true }));
        Assert.Contains("crop=true", det.Message);
        var seg = await Assert.ThrowsAsync<InvalidOperationException>(() => RunOnnx(new OnnxSegmentationNode(), "inference.segmentation", TinyModels.Segmentation, source, new { inputWidth = 8, inputHeight = 8, crop = true }));
        Assert.Contains("crop=true", seg.Message);
    }

    [Fact]
    public async Task MissingOnnxModelFailsExplicitly()
    {
        using var source = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(0));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new OnnxClassificationNode(), "inference.classification", Inputs(source), "{\"modelPath\":\"Z:\\\\not-a-model.onnx\"}"));
        Assert.Contains("does not exist", ex.Message);
    }

    private static Dictionary<string, VisionValue> Inputs(Mat source) => new() { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) };
    private static NodeDefinition Definition(string type, string json) => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, string type, Dictionary<string, VisionValue> inputs, string json)
        => ExecuteAndDisposeInputs(executor, new NodeExecutionContext(inputs), Definition(type, json));

    private static async Task<NodeExecutorResult> ExecuteAndDisposeInputs(IVisionNodeExecutor executor, NodeExecutionContext context, NodeDefinition node)
    {
        try { return await executor.ExecuteAsync(context, node, CancellationToken.None); }
        finally { foreach (var value in context.Inputs.Values) (value.Value as VisionImage)?.Dispose(); }
    }

    private static async Task<NodeExecutorResult> RunOnnx(IVisionNodeExecutor executor, string type, byte[] model, Mat source, object parameters)
    {
        var directory = Path.Combine(Path.GetTempPath(), "visionstudio-onnx-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "model.onnx");
        await File.WriteAllBytesAsync(path, model);
        try
        {
            var json = JsonSerializer.Serialize(new { modelPath = path, inputWidth = 8, inputHeight = 8, parameters });
            // Flatten the additional parameters into the node's top-level parameter object.
            var all = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            var nested = all.Remove("parameters", out var p) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetRawText())! : new();
            foreach (var pair in nested) all[pair.Key] = pair.Value;
            var context = new NodeExecutionContext(Inputs(source));
            try { return await executor.ExecuteAsync(context, new NodeDefinition("test", type, null, null, all), CancellationToken.None); }
            finally { foreach (var value in context.Inputs.Values) (value.Value as VisionImage)?.Dispose(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static class TinyModels
    {
        // ONNX protobufs below are tiny real graphs produced from the public ONNX schema: global average pool/flatten,
        // identity segmentation logits, scalar reduce-mean, and a detector head whose confidence is image-dependent.
        public static readonly byte[] Classification = Convert.FromBase64String("CAcSElZpc2lvblN0dWRpbyB0ZXN0czqQAQoiCgVpbnB1dBIGcG9vbGVkIhFHbG9iYWxBdmVyYWdlUG9vbAomCgZwb29sZWQSBnNjb3JlcyIHRmxhdHRlbioLCgRheGlzGAGgAQISB2ZpeHR1cmVaHwoFaW5wdXQSFgoUCAESEAoCCAEKAggDCgIICAoCCAhiGAoGc2NvcmVzEg4KDAgBEggKAggBCgIIA0IECgAQCw==");
        public static readonly byte[] Segmentation = Convert.FromBase64String("CAcSElZpc2lvblN0dWRpbyB0ZXN0czpjChcKBWlucHV0EgRtYXNrIghJZGVudGl0eRIHZml4dHVyZVofCgVpbnB1dBIWChQIARIQCgIIAQoCCAMKAggICgIICGIeCgRtYXNrEhYKFAgBEhAKAggBCgIIAwoCCAgKAggIQgQKABAL");
        public static readonly byte[] Anomaly = Convert.FromBase64String("CAcSElZpc2lvblN0dWRpbyB0ZXN0czqqAQoiCgVpbnB1dBIGcG9vbGVkIhFHbG9iYWxBdmVyYWdlUG9vbAo5CgZwb29sZWQSBXNjb3JlIgpSZWR1Y2VNZWFuKgsKBGF4ZXNAAaABByoPCghrZWVwZGltcxgBoAECEgdmaXh0dXJlWh8KBWlucHV0EhYKFAgBEhAKAggBCgIIAwoCCAgKAggIYh8KBXNjb3JlEhYKFAgBEhAKAggBCgIIAQoCCAEKAggBQgQKABAL");
        public static readonly byte[] Detection = Convert.FromBase64String("CAcSElZpc2lvblN0dWRpbyB0ZXN0czrcAgoiCgVpbnB1dBIGcG9vbGVkIhFHbG9iYWxBdmVyYWdlUG9vbAo5CgZwb29sZWQSBXNjb3JlIgpSZWR1Y2VNZWFuKgsKBGF4ZXNAAaABByoPCghrZWVwZGltcxgBoAECChsKBXNjb3JlCgVzY2FsZRIGc2NhbGVkIgNNdWwKGgoGc2NhbGVkCgZvZmZzZXQSA3JvdyIDQWRkCicKA3JvdxIKZGV0ZWN0aW9ucyIHRmxhdHRlbioLCgRheGlzGAGgAQISB2ZpeHR1cmUqJwgBCAYQASIYAAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAQgVzY2FsZSooCAEIBhABIhgAAIA+AACAPgAAQD8AAEA/AAAAAAAAAEBCBm9mZnNldFofCgVpbnB1dBIWChQIARIQCgIIAQoCCAMKAggICgIICGIcCgpkZXRlY3Rpb25zEg4KDAgBEggKAggBCgIIBkIECgAQCw==");
    }
}
