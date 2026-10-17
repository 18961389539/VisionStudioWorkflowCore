using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>Pure-managed YOLO26 layout decode / mask compose tests (no ONNX natives).</summary>
public sealed class Yolo26InferenceTests
{
    [Fact]
    public void DecodeClassMapAcceptsYolo26SemanticShapes()
    {
        float[] values = [0, 1, 1, 2, 0, 2];
        var (labels, h, w) = InferenceStats.DecodeClassMap(values, [1, 2, 3]);
        Assert.Equal(2, h); Assert.Equal(3, w);
        Assert.Equal(new byte[] { 0, 1, 1, 2, 0, 2 }, labels);

        var flat = InferenceStats.DecodeClassMap(values, [2, 3]);
        Assert.Equal(labels, flat.Labels);

        var nchw = InferenceStats.DecodeClassMap(values, [1, 1, 2, 3]);
        Assert.Equal(labels, nchw.Labels);
    }

    [Fact]
    public void DecodeClassMapRejectsLogitsLayout()
    {
        Assert.Throws<InvalidOperationException>(() => InferenceStats.DecodeClassMap(new float[8], [1, 2, 2, 2]));
    }

    [Fact]
    public void DecodeYoloSegSplitsBoxClassAndMaskCoeffs()
    {
        var values = new float[10];
        values[0] = 10; values[1] = 20; values[2] = 4; values[3] = 6;
        values[4] = 0.1f; values[5] = 0.9f;
        values[6] = 0.5f; values[7] = -0.5f; values[8] = 0.25f; values[9] = 0f;
        var cand = Assert.Single(InferenceStats.DecodeYoloSeg(values, [1, 10, 1], maskDim: 4));
        Assert.Equal(1, cand.ClassId);
        Assert.Equal(0.9, cand.Score, 5);
        Assert.Equal(10, cand.CenterX);
        Assert.Equal(4, cand.MaskCoeffs.Length);
        Assert.Equal(0.5f, cand.MaskCoeffs[0]);
    }

    [Fact]
    public void ComposeYoloSegMaskCropsAndThresholds()
    {
        float[] protos = [1f, 1f, 1f, 1f];
        float[] coeffs = [0f];
        var mask = InferenceStats.ComposeYoloSegMask(protos, 1, 2, 2, coeffs, 0, 0, 2, 2, 2, 2, 2, 2, maskThreshold: 0.5f);
        Assert.Equal(4, mask.Length);
        Assert.All(mask, b => Assert.Equal(255, b));

        var cropped = InferenceStats.ComposeYoloSegMask(protos, 1, 2, 2, coeffs, 0, 0, 1, 1, 2, 2, 2, 2, maskThreshold: 0.5f);
        Assert.Equal(255, cropped[0]);
        Assert.Equal(0, cropped[1]);
    }

    [Fact]
    public void DecodeYoloStillSupportsYolo26RawDetectionLayout()
    {
        float[] values = [5, 5, 2, 2, 0.2f, 0.8f];
        var cand = Assert.Single(InferenceStats.DecodeYolo(values, [1, 1, 6]));
        Assert.Equal(1, cand.ClassId);
        Assert.Equal(0.8, cand.Score, 5);
    }
}
