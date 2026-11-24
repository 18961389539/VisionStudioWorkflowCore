using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// ONNX 输入归一化与分割叠加图的行为基线。两者原先都是逐像素托管/原生互操作，
/// 向量化后必须保持 BGR/NCHW 布局、mean/scale 语义和背景像素不变。
/// </summary>
public sealed class OnnxPreprocessingTests
{
    private static NodeDefinition Def(string json)
        => new("test", "inference.classification", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    [Fact]
    public void NormalizeInto_WritesChwPlanesWithoutSwapping()
    {
        using var image = new Mat(1, 2, MatType.CV_8UC3);
        image.Set(0, 0, new Vec3b(10, 20, 30));
        image.Set(0, 1, new Vec3b(40, 50, 60));
        var values = new float[6];

        OnnxInferenceHelpers.NormalizeInto(values, 0, image, 2, 1,
            Def("""{"scale":0.5,"meanB":1,"meanG":2,"meanR":3,"swapRB":false}"""));

        Assert.Equal(new[] { 4.5f, 19.5f, 9f, 24f, 13.5f, 28.5f }, values);
    }

    [Fact]
    public void NormalizeInto_SwapsChannelsAndHonoursTheBatchOffset()
    {
        using var image = new Mat(1, 2, MatType.CV_8UC3);
        image.Set(0, 0, new Vec3b(10, 20, 30));
        image.Set(0, 1, new Vec3b(40, 50, 60));
        var values = new float[12];

        OnnxInferenceHelpers.NormalizeInto(values, 6, image, 2, 1,
            Def("""{"scale":0.5,"meanB":1,"meanG":2,"meanR":3,"swapRB":true}"""));

        Assert.All(values[..6], value => Assert.Equal(0f, value));
        Assert.Equal(new[] { 14.5f, 29.5f, 9f, 24f, 3.5f, 18.5f }, values[6..]);
    }

    [Fact]
    public void LetterboxMapping_MapsPaddingAndInvertsInBothModes()
    {
        // 640x480 source into a 640x640 letterbox: scale 1, 80px bars top and bottom.
        var letterbox = new LetterboxMapping(sourceWidth: 640, sourceHeight: 480, inputWidth: 640, inputHeight: 640, letterbox: true);
        Assert.Equal(1, letterbox.ScaleX, 9);
        Assert.Equal(0, letterbox.PadX, 9);
        Assert.Equal(80, letterbox.PadY, 9);
        Assert.Equal(80, letterbox.ToInputY(0), 9);
        Assert.Equal(560, letterbox.ToInputY(480), 9);
        Assert.Equal(123.5, letterbox.ToSourceX(letterbox.ToInputX(123.5)), 9);
        Assert.Equal(234.5, letterbox.ToSourceY(letterbox.ToInputY(234.5)), 9);

        // Stretch scales each axis independently and round-trips through the inverse.
        var stretch = new LetterboxMapping(sourceWidth: 64, sourceHeight: 32, inputWidth: 640, inputHeight: 480, letterbox: false);
        Assert.Equal(10, stretch.ScaleX, 9);
        Assert.Equal(15, stretch.ScaleY, 9);
        Assert.Equal(10, stretch.ToInputX(1), 9);
        Assert.Equal(15, stretch.ToInputY(1), 9);
        Assert.Equal(32, stretch.ToSourceX(stretch.ToInputX(32)), 9);
        Assert.Equal(16, stretch.ToSourceY(stretch.ToInputY(16)), 9);

        Assert.Throws<InvalidOperationException>(() => new LetterboxMapping(0, 32, 64, 64, letterbox: true));
    }

    [Fact]
    public void SegmentationOverlayBlendsLabelledPixelsAndKeepsBackground()
    {
        using var source = new Mat(1, 2, MatType.CV_8UC3);
        source.Set(0, 0, new Vec3b(100, 110, 120));
        source.Set(0, 1, new Vec3b(200, 210, 220));
        using var mask = new Mat(1, 2, MatType.CV_8UC1, Scalar.All(0));
        mask.Set(0, 1, (byte)1);

        using var overlay = OnnxSegmentationNode.BuildOverlay(source, mask, maskClassOffset: 0);

        Assert.Equal(source.At<Vec3b>(0, 0), overlay.At<Vec3b>(0, 0));
        var color = InferenceStats.SegmentationColorBgr(2); // classId 1 -> palette slot 2
        var blended = overlay.At<Vec3b>(0, 1);
        Assert.Equal((byte)((200 + color.B) / 2), blended.Item0);
        Assert.Equal((byte)((210 + color.G) / 2), blended.Item1);
        Assert.Equal((byte)((220 + color.R) / 2), blended.Item2);
    }

    [Fact]
    public void SegmentationOverlayColoursOneClassIdIdenticallyAcrossMaskEncodings()
    {
        using var source = new Mat(1, 2, MatType.CV_8UC3, Scalar.All(0));
        using var semanticMask = new Mat(1, 2, MatType.CV_8UC1, Scalar.All(0));
        semanticMask.Set(0, 0, (byte)1); // class 1, maskClassOffset 0
        using var instanceMask = new Mat(1, 2, MatType.CV_8UC1, Scalar.All(0));
        instanceMask.Set(0, 1, (byte)2); // class 1, maskClassOffset 1

        using var semanticOverlay = OnnxSegmentationNode.BuildOverlay(source, semanticMask, maskClassOffset: 0);
        using var instanceOverlay = OnnxSegmentationNode.BuildOverlay(source, instanceMask, maskClassOffset: 1);

        Assert.Equal(semanticOverlay.At<Vec3b>(0, 0), instanceOverlay.At<Vec3b>(0, 1));
        var color = InferenceStats.SegmentationColorBgr(2); // classId 1 -> palette slot 2
        Assert.Equal(new Vec3b((byte)(color.B / 2), (byte)(color.G / 2), (byte)(color.R / 2)),
            instanceOverlay.At<Vec3b>(0, 1));
        Assert.Equal(default(Vec3b), instanceOverlay.At<Vec3b>(0, 0));
    }
}
