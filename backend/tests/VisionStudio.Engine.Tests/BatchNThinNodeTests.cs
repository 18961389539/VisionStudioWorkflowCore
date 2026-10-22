using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class BatchNThinNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task PerspectiveAndWarpAffine_EmitDiagnostics()
    {
        using var source = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(80));
        Cv2.Rectangle(source, new Rect(5, 5, 30, 30), Scalar.All(200), -1);
        var persp = await new PerspectiveCorrectionNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
            Def("image.perspective", """{"x1":5,"y1":5,"x2":35,"y2":5,"x3":35,"y3":35,"x4":5,"y4":35,"width":60,"height":60}"""), CancellationToken.None);
        Assert.Contains("image.perspective", (string)persp.Outputs["diagnostics"].Value!);
        Assert.Equal(60, ((VisionImage)persp.Outputs["image"].Value!).Width);

        var identity = VisionTransform2DMath.Rigid("ImagePixel", "ImagePixel", "px", 0, 0, 0);
        var warp = await new ImageWarpAffineNode().ExecuteAsync(Ctx(new()
        {
            ["image"] = Img(source),
            ["transform"] = VisionValue.Transform(identity)
        }), Def("image.warpAffine"), CancellationToken.None);
        Assert.Contains("image.warpAffine", (string)warp.Outputs["diagnostics"].Value!);
        Assert.True(warp.Outputs.ContainsKey("validRegion"));
    }

    [Fact]
    public async Task IlluminationAndSharpen_EmitDiagnostics()
    {
        using var source = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(100));
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                source.Set(y, x, (byte)Math.Clamp(40 + x * 3, 0, 255));
        var illum = await new IlluminationNormalizeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
            Def("image.illuminationNormalize", """{"windowSize":15,"targetMean":128}"""), CancellationToken.None);
        Assert.Contains("image.illuminationNormalize", (string)illum.Outputs["diagnostics"].Value!);
        var sharp = await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
            Def("image.sharpen", """{"amount":1.5}"""), CancellationToken.None);
        Assert.Contains("image.sharpen", (string)sharp.Outputs["diagnostics"].Value!);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ImageSharpenNode().ExecuteAsync(Ctx(new() { ["image"] = Img(source) }),
                Def("image.sharpen", """{"amount":99}"""), CancellationToken.None));
    }

    [Fact]
    public async Task FitEllipse_DiagnosticsAndStrictIndex()
    {
        using var image = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(image, new Point(60, 60), new Size(40, 25), 20, 0, 360, Scalar.All(255), -1);
        var extracted = await new ContourExtractNode().ExecuteAsync(Ctx(new() { ["image"] = Img(image) }),
            Def("contour.extract", """{"mode":"external"}"""), CancellationToken.None);
        var contours = extracted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var fit = await new FitEllipseNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
            Def("measure.fitEllipse"), CancellationToken.None);
        Assert.Contains("measure.fitEllipse", (string)fit.Outputs["diagnostics"].Value!);
        Assert.True((double)fit.Outputs["axisRatio"].Value! >= 1.0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new FitEllipseNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
                Def("measure.fitEllipse", """{"selectBy":"index","index":99}"""), CancellationToken.None));
    }

    [Fact]
    public async Task TextMatchAndFrameset_EmitDiagnostics()
    {
        var match = await new TextMatchNode().ExecuteAsync(Ctx(new()
        {
            ["text"] = VisionValue.String("ABC"),
            ["expected"] = VisionValue.String("ABC")
        }), Def("text.match"), CancellationToken.None);
        Assert.True((bool)match.Outputs["pass"].Value!);
        Assert.Contains("text.match", (string)match.Outputs["diagnostics"].Value!);

        using var top = VisionImage.Own(new Mat(8, 10, MatType.CV_8UC1, Scalar.All(1)));
        using var set = new VisionFrameSet(
            "sync-1", "device-ptp", 1.0, true,
            new Dictionary<string, IVisionImage> { ["top"] = top },
            new Dictionary<string, VisionFrameSetFrameInfo> { ["top"] = new("top", 1, DateTimeOffset.UtcNow, 10, 1, "Mono8") });
        var pick = await new FrameSetImageNode().ExecuteAsync(Ctx(new() { ["frameSet"] = VisionValue.FrameSet(set) }),
            Def("frameset.image", """{"cameraId":"top"}"""), CancellationToken.None);
        Assert.Contains("frameset.image", (string)pick.Outputs["diagnostics"].Value!);
    }

    [Fact]
    public void DeviceValidation_RejectsEmptyIds()
    {
        Assert.Throws<InvalidOperationException>(() => DeviceNodeValidation.RequireId("  ", "deviceId"));
        Assert.Equal("cam-1", DeviceNodeValidation.RequireId(" cam-1 ", "deviceId"));
    }
}
