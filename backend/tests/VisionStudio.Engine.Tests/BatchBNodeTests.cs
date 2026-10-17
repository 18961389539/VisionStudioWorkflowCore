using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Batch B node tests. The intrinsics view gate runs before any native call and
/// passes on Linux; image/ONNX tests need natives and fail with DllNotFound here like
/// all pre-existing image tests (re-run on Windows).</summary>
public sealed class BatchBNodeTests
{
    private static string ViewsJson(double perturbation)
    {
        var views = new List<object>();
        for (var v = 0; v < 3; v++)
        {
            var objectPoints = new List<double[]>();
            var imagePoints = new List<double[]>();
            for (var gy = 0; gy < 4; gy++)
            for (var gx = 0; gx < 5; gx++)
            {
                objectPoints.Add([gx * 10d, gy * 10d, 0]);
                double u = 2 * gx * 10 + 10 + v * 5;
                double w = 2 * gy * 10 + 20 + v * 3;
                if (v == 1 && gx == 4 && gy == 3) u += perturbation; // corrupted corner in view 1
                imagePoints.Add([u, w]);
            }
            views.Add(new { objectPoints, imagePoints });
        }
        return JsonSerializer.Serialize(new { imageWidth = 200, imageHeight = 200, views });
    }

    [Fact]
    public async Task IntrinsicsPerViewGateRejectsCorruptedViewBeforeNativeCalibration()
    {
        var node = new NodeDefinition("test", "calibration.cameraIntrinsics", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { maxViewRms = 0.5 })));
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue>
        { ["calibrationDataJson"] = VisionValue.String(ViewsJson(perturbation: 30)) });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CameraIntrinsicCalibrationNode().ExecuteAsync(context, node, CancellationToken.None));
        Assert.Contains("view 1", ex.Message);
        Assert.Contains("reprojection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferenceRejectsInvalidScaleAndMode()
    {
        using var source = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(100)));
        using var reference = VisionImage.Own(new Mat(16, 16, MatType.CV_8UC1, Scalar.All(90)));
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source), ["reference"] = VisionValue.Image(reference) };
        var badScale = new NodeDefinition("test", "image.difference", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"scale":0}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new ImageDifferenceNode().ExecuteAsync(new(inputs), badScale, CancellationToken.None));
        var normalized = new NodeDefinition("test", "image.difference", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"mode":"normalized","threshold":0.1}"""));
        var result = await new ImageDifferenceNode().ExecuteAsync(new(inputs), normalized, CancellationToken.None);
        Assert.True(result.Outputs.ContainsKey("aboveThresholdFraction"));
        var output = result.Outputs["image"].Require<VisionImage>("image");
        // (100-90)/(100+90) ~= 0.0526 < 0.1 everywhere
        Assert.Equal(0d, result.Outputs["aboveThresholdFraction"].Require<double>("aboveThresholdFraction"));
        output.Dispose();
    }

    [Fact]
    public async Task BlobsRejectsUnknownSortAndFilterBySize()
    {
        using var source = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(source, new Rect(10, 10, 60, 20), Scalar.All(255), -1);
        Cv2.Rectangle(source, new Rect(100, 40, 20, 20), Scalar.All(255), -1);
        var vision = VisionImage.Own(source.Clone());
        try
        {
            var badSort = new NodeDefinition("test", "measure.blobs", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"sortBy":"magic"}"""));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new MultiBlobNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(vision) }), badSort, CancellationToken.None));
            var filtered = new NodeDefinition("test", "measure.blobs", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"minArea":10,"minWidth":50}"""));
            var result = await new MultiBlobNode().ExecuteAsync(new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(vision) }), filtered, CancellationToken.None);
            Assert.Equal(1, result.Outputs["count"].Require<long>("count"));
        }
        finally { vision.Dispose(); }
    }
}
