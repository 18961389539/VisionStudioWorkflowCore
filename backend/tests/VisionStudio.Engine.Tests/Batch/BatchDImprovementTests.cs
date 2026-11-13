using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>Pure-managed Batch D tests (no natives required).</summary>
public sealed class BatchDImprovementTests
{
    [Fact]
    public void GlobalSsimIsOneForIdenticalAndLowerForDifferent()
    {
        var a = Enumerable.Range(0, 100).Select(i => (byte)(i % 256)).ToArray();
        Assert.Equal(1d, ImageDifferenceMetrics.GlobalSsim(a, a), 6);
        var inverted = a.Select(v => (byte)(255 - v)).ToArray();
        Assert.True(ImageDifferenceMetrics.GlobalSsim(a, inverted) < 0.5);
        Assert.Throws<InvalidOperationException>(() => ImageDifferenceMetrics.GlobalSsim(a, new byte[3]));
    }

    [Fact]
    public void RegionFromMaskSplitsDisconnectedObjects()
    {
        var mask = new byte[5 * 5];
        mask[0] = 255; mask[1] = 255; mask[6] = 255; // diagonal joins (8-connected)
        mask[24] = 255;
        var region = ImageDifferenceMetrics.RegionFromMask(mask, 5, 5);
        Assert.Equal(2, region.Components.Count);
        Assert.Equal(3, region.Components[0].Runs.Sum(r => r.EndColumn - r.StartColumn + 1));
    }

    [Fact]
    public void SegmentationClassBoxesReportGeometry()
    {
        byte[] labels = [0, 1, 1, 0, 1, 0];
        float[] scores = [0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f];
        var boxes = InferenceStats.SegmentationClassBoxes(labels, scores, 2, 3, 2);
        var one = boxes[1];
        Assert.Equal(3, one.PixelCount);
        Assert.Equal(1, one.X); Assert.Equal(0, one.Y);
        Assert.Equal(2, one.Width); Assert.Equal(2, one.Height);
        Assert.Equal(4d / 3, one.CentroidX, 6);
        Assert.Equal((0 + 0 + 1) / 3d, one.CentroidY, 6);
        Assert.NotEqual(InferenceStats.SegmentationColorBgr(1), InferenceStats.SegmentationColorBgr(2));
        Assert.Equal(InferenceStats.SegmentationColorBgr(1), InferenceStats.SegmentationColorBgr(12));
    }

    [Fact]
    public void DistortedPoseReprojectionUsesBrownConradyCoefficients()
    {
        // Synthetic view distorted with k1=0.1: without coefficients the RMS is
        // visibly non-zero; with them it returns to near zero.
        var fx = 100d; var fy = 100d; var cx = 100d; var cy = 100d;
        var objectPoints = new List<List<double>>();
        var imagePoints = new List<List<double>>();
        for (var gy = 0; gy < 4; gy++)
        for (var gx = 0; gx < 5; gx++)
        {
            double x = gx * 10, y = gy * 10;
            var camZ = 60d;
            var xn = x / camZ; var yn = y / camZ;
            var r2 = xn * xn + yn * yn;
            var xd = xn * (1 + 0.1 * r2); var yd = yn * (1 + 0.1 * r2);
            objectPoints.Add([x, y, 0]);
            imagePoints.Add([fx * xd + cx, fy * yd + cy]);
        }
        var plain = CalibrationViewAnalysis.AnalyzeWithCamera(objectPoints, imagePoints, 200, 200, fx, fy, cx, cy);
        var corrected = CalibrationViewAnalysis.AnalyzeWithCamera(objectPoints, imagePoints, 200, 200, fx, fy, cx, cy, new double[] { 0.1, 0, 0, 0 });
        Assert.NotNull(plain.ReprojectionRms); Assert.NotNull(corrected.ReprojectionRms);
        Assert.True(corrected.ReprojectionRms < plain.ReprojectionRms, $"corrected={corrected.ReprojectionRms} plain={plain.ReprojectionRms}");
        Assert.True(corrected.ReprojectionRms < 0.05, $"corrected={corrected.ReprojectionRms}");
    }

    [Fact]
    public void OcrBlacklistValidationAndConflict()
    {
        Assert.Equal("xyz", OcrTextParsing.ValidateBlacklist("xyz"));
        Assert.Throws<InvalidOperationException>(() => OcrTextParsing.ValidateBlacklist("a" + (char)1 + "b"));
        Assert.Throws<InvalidOperationException>(() => OcrTextParsing.EnsureNoWhitelistBlacklistConflict("abc", "cx"));
        OcrTextParsing.EnsureNoWhitelistBlacklistConflict("abc", "xyz");
    }
}
