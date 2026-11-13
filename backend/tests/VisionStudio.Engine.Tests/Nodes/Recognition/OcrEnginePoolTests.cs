using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class OcrEnginePoolTests
{
    [Fact]
    public async Task AlternatingConfigurations_ReuseBothWarmEngines()
    {
        var tessdata = Environment.GetEnvironmentVariable("VISIONSTUDIO_TESSDATA");
        if (string.IsNullOrWhiteSpace(tessdata)) return;
        using var source = new Mat(180, 900, MatType.CV_8UC1, Scalar.All(255));
        Cv2.PutText(source, "VISION 123", new Point(35, 125), HersheyFonts.HersheySimplex, 3.0, Scalar.All(0), 6, LineTypes.AntiAlias);
        using var image = VisionImage.Own(source.Clone());
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) });
        var executor = new OcrNode();

        async Task<NodeExecutorResult> Recognize(string mode)
        {
            var parameters = JsonSerializer.SerializeToElement(new { dataPath = tessdata, language = "eng", oem = mode, psm = "singleline" });
            var node = new NodeDefinition("ocr", "recognition.ocr", null, null,
                parameters.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));
            return await executor.ExecuteAsync(context, node, default);
        }

        await Recognize("default");
        await Recognize("lstm");
        var first = await Recognize("default");
        var second = await Recognize("lstm");

        Assert.Equal(true, first.Summary["engineReused"]);
        Assert.Equal(true, second.Summary["engineReused"]);
        Assert.Contains("VISION", first.Outputs["text"].Require<string>("text"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISION", second.Outputs["text"].Require<string>("text"), StringComparison.OrdinalIgnoreCase);
    }
}
