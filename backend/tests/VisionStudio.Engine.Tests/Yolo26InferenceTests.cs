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

    [Fact]
    public void DecodeYoloReadsChannelsFirstAndChannelsLastRawHeadsIdentically()
    {
        const int count = 3;
        const int channels = 6;
        float At(int n, int c) => c switch
        {
            0 => 10 + n, 1 => 20 + n, 2 => 2, 3 => 4, 4 => 0.1f, _ => 0.9f
        };
        var channelsFirst = new float[channels * count];
        var channelsLast = new float[count * channels];
        for (var n = 0; n < count; n++)
        for (var c = 0; c < channels; c++)
        {
            channelsFirst[c * count + n] = At(n, c);
            channelsLast[n * channels + c] = At(n, c);
        }

        var first = InferenceStats.DecodeYolo(channelsFirst, [1, channels, count]);
        var last = InferenceStats.DecodeYolo(channelsLast, [1, count, channels]);
        Assert.Equal(first.Length, last.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(first[i].CenterX, last[i].CenterX, 5);
            Assert.Equal(first[i].CenterY, last[i].CenterY, 5);
            Assert.Equal(first[i].ClassId, last[i].ClassId);
            Assert.Equal(first[i].Score, last[i].Score, 5);
        }
        Assert.Equal(1, first[0].ClassId);
        Assert.Equal(0.9, first[0].Score, 5);
    }

    [Fact]
    public void RawDecodersRejectAmbiguousSquareTensorsUnlessOrderIsExplicit()
    {
        var raw = new float[7 * 7];
        for (var n = 0; n < 7; n++)
        {
            raw[0 * 7 + n] = 1; raw[1 * 7 + n] = 2; raw[2 * 7 + n] = 3; raw[3 * 7 + n] = 4;
            raw[4 * 7 + n] = 0.75f; raw[5 * 7 + n] = 0.2f; raw[6 * 7 + n] = 0.4f;
        }
        Assert.Throws<InvalidOperationException>(() => InferenceStats.DecodeYolo(raw, [1, 7, 7]));
        var explicitOrder = InferenceStats.DecodeYolo(raw, [1, 7, 7], tensorOrder: "channelsFirst");
        Assert.Equal(7, explicitOrder.Length);
        Assert.Equal(0.75, explicitOrder[0].Score, 5);

        var seg = new float[6 * 6];
        for (var n = 0; n < 6; n++)
        {
            seg[0 * 6 + n] = 4; seg[1 * 6 + n] = 5; seg[2 * 6 + n] = 6; seg[3 * 6 + n] = 7;
            seg[4 * 6 + n] = 0.8f; seg[5 * 6 + n] = 0.25f;
        }
        Assert.Throws<InvalidOperationException>(() => InferenceStats.DecodeYoloSeg(seg, [1, 6, 6], maskDim: 1));
        Assert.Equal(6, InferenceStats.DecodeYoloSeg(seg, [1, 6, 6], maskDim: 1, tensorOrder: "channelsFirst").Length);

        var pose = new float[7 * 7];
        for (var n = 0; n < 7; n++)
        {
            pose[0 * 7 + n] = 1; pose[1 * 7 + n] = 2; pose[2 * 7 + n] = 3; pose[3 * 7 + n] = 4;
            pose[4 * 7 + n] = 0.6f; pose[5 * 7 + n] = 8; pose[6 * 7 + n] = 9;
        }
        Assert.Throws<InvalidOperationException>(() => InferenceStats.DecodeYoloPose(pose, [1, 7, 7], numKeypoints: 1, keypointDims: 2));
        Assert.Equal(7, InferenceStats.DecodeYoloPose(pose, [1, 7, 7], numKeypoints: 1, keypointDims: 2, tensorOrder: "channelsFirst").Length);
    }

    [Fact]
    public void AnomalyOutputSelectionUsesShapeNotOrder()
    {
        var mapThenScore = InferenceStats.SelectAnomalyOutputs(new IReadOnlyList<int>[] { [1, 1, 32, 32], [1, 1] });
        Assert.Equal(1, mapThenScore.ScoreIndex);
        Assert.Equal(0, mapThenScore.MapIndex);

        var scoreOnly = InferenceStats.SelectAnomalyOutputs(new IReadOnlyList<int>[] { [1, 1] });
        Assert.Equal(0, scoreOnly.ScoreIndex);
        Assert.Equal(-1, scoreOnly.MapIndex);

        var mapOnly = InferenceStats.SelectAnomalyOutputs(new IReadOnlyList<int>[] { [1, 1, 8, 8] });
        Assert.Equal(-1, mapOnly.ScoreIndex);
        Assert.Equal(0, mapOnly.MapIndex);
        Assert.Throws<InvalidOperationException>(() => InferenceStats.SelectAnomalyOutputs(Array.Empty<IReadOnlyList<int>>()));
    }

    [Fact]
    public void ComposeYoloSegMaskLetterboxMapsThroughPadding()
    {
        // Proto 2x2 all high after sigmoid(0*1)=0.5 -> use positive coeff so sigmoid > 0.5
        float[] protos = [1f, 1f, 1f, 1f];
        float[] coeffs = [2f]; // sigmoid(2) ~ 0.88
        // Source 2x2 letterboxed into 4x4 input -> pad 1 on each side.
        // Fill entire input-space mask; after letterbox remap only the center 2x2 of source should sample the content region.
        var mask = InferenceStats.ComposeYoloSegMask(
            protos, 1, 2, 2, coeffs,
            boxLeft: 0, boxTop: 0, boxRight: 4, boxBottom: 4,
            inputWidth: 4, inputHeight: 4, outWidth: 2, outHeight: 2,
            maskThreshold: 0.5f, letterbox: true);
        Assert.Equal(4, mask.Length);
        Assert.All(mask, b => Assert.Equal(255, b));
    }

    [Fact]
    public void DecodeYoloPoseSplitsBoxClassAndKeypoints()
    {
        // channels: cx,cy,w,h + 1 class + 2 kpts * 3 = 4+1+6 = 11, count=1, channels-first
        var values = new float[11];
        values[0] = 100; values[1] = 200; values[2] = 40; values[3] = 60;
        values[4] = 0.91f;
        // kpt0
        values[5] = 10; values[6] = 20; values[7] = 0.9f;
        // kpt1
        values[8] = 30; values[9] = 40; values[10] = 0.8f;
        var cand = Assert.Single(InferenceStats.DecodeYoloPose(values, [1, 11, 1], numKeypoints: 2, keypointDims: 3));
        Assert.Equal(0, cand.ClassId);
        Assert.Equal(0.91, cand.Score, 5);
        Assert.Equal(100, cand.CenterX);
        Assert.Equal(6, cand.Keypoints.Length);
        Assert.Equal(10, cand.Keypoints[0]);
        Assert.Equal(0.8, cand.Keypoints[5], 5);
    }

    [Fact]
    public void DecodeYoloPoseXyxyAcceptsYolo26EndToEndRows()
    {
        // [1,1,12] = x1,y1,x2,y2,score,class + 2*(x,y,conf)
        float[] values =
        [
            10, 20, 50, 80, 0.88f, 0,
            12, 22, 0.95f,
            48, 78, 0.7f
        ];
        var cand = Assert.Single(InferenceStats.DecodeYoloPoseXyxy(values, [1, 1, 12], numKeypoints: 2, keypointDims: 3));
        Assert.Equal(0, cand.ClassId);
        Assert.Equal(0.88, cand.Score, 5);
        Assert.Equal(30, cand.CenterX); // (10+50)/2
        Assert.Equal(50, cand.CenterY); // (20+80)/2
        Assert.Equal(12, cand.Keypoints[0]);
        Assert.Equal(0.7, cand.Keypoints[5], 5);
    }

    [Fact]
    public void Coco17SkeletonHasExpectedEdgeCount()
    {
        Assert.Equal(19, InferenceStats.Coco17Skeleton.Length);
        Assert.Contains(InferenceStats.Coco17Skeleton, e => e.A == 5 && e.B == 6);
    }
}
