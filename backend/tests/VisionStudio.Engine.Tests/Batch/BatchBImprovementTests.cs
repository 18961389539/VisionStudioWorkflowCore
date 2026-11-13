using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>Pure-managed Batch B tests (no ONNX/OpenCV/JLVision natives required).</summary>
public sealed class BatchBImprovementTests
{
    [Fact]
    public void SegmentationClassesReportAreaFractionAndMeanScore()
    {
        byte[] labels = [0, 0, 1, 1, 1, 2];
        float[] scores = [0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f];
        var stats = InferenceStats.SegmentationClasses(labels, scores, 3);
        Assert.Equal(2, stats[0].PixelCount); Assert.Equal(3, stats[1].PixelCount);
        Assert.Equal(0.5, stats[1].Fraction, 6);
        Assert.Equal(0.6, stats[1].MeanScore, 5);
        Assert.Equal(1, stats[2].PixelCount);
    }

    [Fact]
    public void AnomalyMapReportsPeakAndAreaFraction()
    {
        float[] map = [0, 0, 0, 0, 10, 0, 0, 0, 4];
        var stat = InferenceStats.AnomalyMap(map, 3, 3, threshold: 3);
        Assert.Equal(10, stat.Peak); Assert.Equal(1, stat.PeakX); Assert.Equal(1, stat.PeakY);
        Assert.Equal(2d / 9, stat.AreaFraction, 6);
        var normalized = InferenceStats.NormalizeMap(map);
        Assert.Equal(255, normalized[4]); Assert.Equal(0, normalized[0]);
    }

    [Fact]
    public void YoloDecodeHandlesChannelsFirstLayout()
    {
        // [1, 6, 2]: cx,cy,w,h, class0, class1 for two anchors
        float[] values = [10, 20, 5, 8, 30, 40, 0.2f, 0.9f, 0.7f, 0.1f];
        // layout c*N+n: c0=[10,20], c1=[5,8], c2=[30,40], c3=[.2,.9]?? rebuild explicitly below
        values = [10, 20, 50, 60, 30, 40, 10, 12, 0.2f, 0.7f, 0.9f, 0.1f];
        var candidates = InferenceStats.DecodeYolo(values, [1, 6, 2]);
        Assert.Equal(2, candidates.Length);
        Assert.Equal(10, candidates[0].CenterX); Assert.Equal(50, candidates[0].CenterY);
        Assert.Equal(1, candidates[0].ClassId); Assert.Equal(0.9, candidates[0].Score, 5);
        Assert.Equal(0, candidates[1].ClassId);
    }

    [Fact]
    public void YoloDecodeRejectsBadShape()
        => Assert.Throws<InvalidOperationException>(() => InferenceStats.DecodeYolo(new float[8], [2, 4]));

    [Fact]
    public void PlanarViewReprojectionIsNearZeroForConsistentView()
    {
        var objectPoints = new List<List<double>>();
        var imagePoints = new List<List<double>>();
        // True homography: u = 2x + 10, v = 2y + 20 (scale+translate)
        for (var gy = 0; gy < 4; gy++)
        for (var gx = 0; gx < 5; gx++)
        {
            objectPoints.Add([gx * 10d, gy * 10d, 0]);
            imagePoints.Add([2 * gx * 10 + 10, 2 * gy * 10 + 20]);
        }
        var stat = CalibrationViewAnalysis.Analyze(objectPoints, imagePoints, 200, 200);
        Assert.NotNull(stat.ReprojectionRms);
        Assert.True(stat.ReprojectionRms < 0.01, $"rms={stat.ReprojectionRms}");
        Assert.True(stat.CoverageX > 0.3);
    }

    [Fact]
    public void NonPlanarViewSkipsReprojection()
    {
        var objectPoints = new List<List<double>> { new() { 0, 0, 0 }, new() { 10, 0, 5 }, new() { 0, 10, 2 }, new() { 10, 10, 9 } };
        var imagePoints = new List<List<double>> { new() { 10, 10 }, new() { 30, 10 }, new() { 10, 30 }, new() { 30, 30 } };
        var stat = CalibrationViewAnalysis.Analyze(objectPoints, imagePoints, 100, 100);
        Assert.Null(stat.ReprojectionRms);
    }

    [Fact]
    public void RegistrationRefinementFindsIntegerShift()
    {
        const int w = 40, h = 30;
        var image = new byte[w * h];
        for (var i = 0; i < image.Length; i++) image[i] = (byte)((i * 53 + (i / w) * 29) % 251);
        const int tw = 10, th = 8;
        var template = new byte[tw * th];
        // template taken from image at (12,9)
        for (var y = 0; y < th; y++) for (var x = 0; x < tw; x++) template[y * tw + x] = image[(9 + y) * w + 12 + x];
        var refined = RegistrationRefinement.Refine(image, w, template, tw, th, 12, 9);
        Assert.True(refined.PeakScore > 0.99, $"peak={refined.PeakScore}");
        Assert.True(Math.Abs(refined.OffsetX) < 0.51); Assert.True(Math.Abs(refined.OffsetY) < 0.51);
        var wrong = RegistrationRefinement.Ncc(image, w, template, tw, th, 20, 15);
        Assert.True(wrong < refined.PeakScore);
    }
}
