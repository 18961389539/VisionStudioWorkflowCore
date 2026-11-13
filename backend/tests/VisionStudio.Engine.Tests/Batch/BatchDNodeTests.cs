using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Batch D node tests (Windows natives: JLVision difference arithmetic).</summary>
public sealed class BatchDNodeTests
{
    [Fact]
    public async Task DifferenceReportsSimilarityAndChangeRegion()
    {
        using var source = VisionImage.Own(new Mat(20, 30, MatType.CV_8UC1, Scalar.All(100)));
        using var reference = VisionImage.Own(new Mat(20, 30, MatType.CV_8UC1, Scalar.All(100)));
        // Textured background: global SSIM against a flat reference is legitimately
        // low (zero reference variance), so similarity is checked on texture.
        for (var y = 0; y < 20; y++) for (var x = 0; x < 30; x++)
        {
            var v = (byte)(80 + (x * 7 + y * 13) % 60);
            source.Mat.Set(y, x, v); reference.Mat.Set(y, x, v);
        }
        using (var patch = new Mat(source.Mat, new Rect(5, 5, 4, 3))) patch.SetTo(Scalar.All(220));
        var node = new NodeDefinition("test", "image.difference", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mode":"absolute","threshold":10}"""));
        var result = await new ImageDifferenceNode().ExecuteAsync(new(new Dictionary<string, VisionValue>
        { ["image"] = VisionValue.Image(source), ["reference"] = VisionValue.Image(reference) }), node, CancellationToken.None);
        Assert.InRange(result.Outputs["similarity"].Require<double>("similarity"), 0.6, 1.0);
        Assert.Equal(12, result.Outputs["changedPixelCount"].Require<long>("changedPixelCount"));
        var region = result.Outputs["region"].Require<VisionRegion>("region");
        Assert.Single(region.Components);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();

        var identical = new NodeDefinition("test", "image.difference", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mode":"absolute","threshold":10}"""));
        var same = await new ImageDifferenceNode().ExecuteAsync(new(new Dictionary<string, VisionValue>
        { ["image"] = VisionValue.Image(reference), ["reference"] = VisionValue.Image(reference) }), identical, CancellationToken.None);
        Assert.Equal(1d, same.Outputs["similarity"].Require<double>("similarity"), 6);
        Assert.Equal(0, same.Outputs["changedPixelCount"].Require<long>("changedPixelCount"));
        (same.Outputs["image"].Value as IDisposable)?.Dispose();
    }
}
