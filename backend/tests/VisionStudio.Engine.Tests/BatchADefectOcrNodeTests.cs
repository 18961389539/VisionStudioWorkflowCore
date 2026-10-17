using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Batch A defect/OCR node tests. Native-dependent (OpenCV/JLVision/Tesseract);
/// on this Linux box they fail with DllNotFound like pre-existing image tests.</summary>
public sealed class BatchADefectOcrNodeTests : IDisposable
{
    private readonly List<IDisposable> _outputs = new();
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); }

    private async Task<NodeExecutorResult> RunCompare(VisionImage image, VisionImage reference, string json)
    {
        var node = new NodeDefinition("test", "defect.referenceCompare", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        var result = await new ReferenceCompareNode().ExecuteAsync(new(new Dictionary<string, VisionValue>
        { ["image"] = VisionValue.Image(image), ["reference"] = VisionValue.Image(reference) }), node, CancellationToken.None);
        _outputs.AddRange(result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>());
        return result;
    }

    private static VisionImage Image(byte value = 100, int width = 32, int height = 32) => VisionImage.Own(new Mat(height, width, MatType.CV_8UC1, Scalar.All(value)));

    [Fact]
    public async Task ReferenceCompareRegistrationToleratesShiftedReference()
    {
        using var source = Image(); using var reference = Image();
        // Defect block present in source at (10,10); reference is the clean image shifted by 1px
        // (simulated by a 1px bright frame difference) - registration should not create a frame defect.
        using (var patch = new Mat(source.Mat, new Rect(10, 10, 4, 4))) patch.SetTo(Scalar.All(140));
        for (var x = 0; x < 32; x++) reference.Mat.Set(0, x, (byte)112);
        var strict = await RunCompare(source, reference, """{"threshold":20,"minArea":2,"registrationTolerance":0}""");
        var aligned = await RunCompare(source, reference, """{"threshold":20,"minArea":2,"registrationTolerance":2}""");
        Assert.True(aligned.Outputs["area"].Require<double>("area") <= strict.Outputs["area"].Require<double>("area"));
        Assert.Equal(16d, aligned.Outputs["area"].Require<double>("area"));
        Assert.Equal("minor", aligned.Outputs["grade"].Require<string>("grade"));
        Assert.Contains("\"area\":16", aligned.Outputs["defects"].Require<string>("defects"));
    }

    [Fact]
    public async Task ReferenceCompareCompensationIgnoresGlobalBrightnessChange()
    {
        using var source = Image(130); using var reference = Image(100);
        var result = await RunCompare(source, reference, """{"threshold":20,"minArea":2,"compensation":"gainOffset"}""");
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("none", result.Outputs["grade"].Require<string>("grade"));
        var uncompensated = await RunCompare(source, reference, """{"threshold":20,"minArea":2}""");
        Assert.True(uncompensated.Outputs["found"].Require<bool>("found"));
        Assert.Equal("major", uncompensated.Outputs["grade"].Require<string>("grade")); // whole 32x32 frame differs: area 1024 >= majorArea 500
    }

    [Fact]
    public async Task ReferenceCompareMorphologyOpenRemovesThinNoise()
    {
        using var source = Image(); using var reference = Image();
        source.Mat.Set(5, 5, (byte)160); source.Mat.Set(5, 6, (byte)160);
        using (var patch = new Mat(source.Mat, new Rect(16, 16, 6, 6))) patch.SetTo(Scalar.All(160));
        var result = await RunCompare(source, reference, """{"threshold":20,"minArea":1,"morphologyOpen":3}""");
        Assert.Equal(36d, result.Outputs["area"].Require<double>("area"));
    }

    [Fact]
    public async Task ReferenceCompareRejectsInvalidGradingAndMorphology()
    {
        using var source = Image(); using var reference = Image();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCompare(source, reference, """{"majorArea":100,"criticalArea":50}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCompare(source, reference, """{"morphologyOpen":4}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCompare(source, reference, """{"compensation":"magic"}"""));
    }

    [Fact]
    public async Task OcrRejectsUnknownPsmAndOemDeterministically()
    {
        using var source = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(255));
        var vision = VisionImage.Own(source.Clone());
        var dir = Path.Combine(Path.GetTempPath(), "visionstudio-no-tessdata");
        async Task<string> Message(string psm)
        {
            var node = new NodeDefinition("test", "recognition.ocr", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { dataPath = dir, language = "eng", psm })));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new OcrNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(vision) }), node, CancellationToken.None));
            return ex.Message;
        }
        try { Assert.Contains("psm", await Message("magic")); }
        finally { vision.Dispose(); }
    }
}
