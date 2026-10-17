using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>Pure-managed Batch C tests (no natives required).</summary>
public sealed class BatchCImprovementTests
{
    [Fact]
    public void SubpixelRefineMovesIntegerEdgeTowardTrueEdge()
    {
        const int w = 40, h = 20;
        // Smooth vertical step edge centred at x=20.4: gray = sigmoid-like ramp
        var gray = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            gray[y * w + x] = (byte)Math.Clamp(Math.Round(255 / (1 + Math.Exp(-(x - 20.4) / 1.2))), 0, 255);
        var refined = SubpixelEdgeRefinement.Refine(gray, w, h, 20, 10);
        Assert.InRange(refined.X, 19.9, 20.9);
        Assert.Equal(10, refined.Y, 3);
    }

    [Fact]
    public void AngularCoverageDetectsPartialArc()
    {
        var points = Enumerable.Range(0, 19).Select(i =>
        {
            var a = i * Math.PI / 18; // half circle 0..180
            return new VisionPoint2D(50 + 20 * Math.Cos(a), 50 + 20 * Math.Sin(a));
        }).ToArray();
        var coverage = SubpixelEdgeRefinement.AngularCoverageDeg(points, 50, 50);
        Assert.InRange(coverage, 170, 190);
    }

    [Fact]
    public void OcrBoxCharsFlipBottomOriginAndScale()
    {
        const string box = "A 10 5 30 25 0\nB 35 5 50 25 0\nbad line\n";
        var chars = OcrTextParsing.ParseBoxChars(box, scale: 2, offsetX: 3, offsetY: 1, processedImageHeight: 200);
        Assert.Equal(2, chars.Length);
        Assert.Equal("A", chars[0].Text);
        Assert.Equal(8, chars[0].X); // 10/2+3
        Assert.Equal((200 - 25) / 2d + 1, chars[0].Y, 3);
        Assert.Equal(10, chars[0].Width); Assert.Equal(10, chars[0].Height);
    }

    [Fact]
    public void OtsuThresholdSeparatesBimodalMap()
    {
        var values = Enumerable.Repeat(0.1f, 90).Concat(Enumerable.Repeat(0.9f, 10)).ToArray();
        var threshold = InferenceStats.OtsuThreshold(values);
        Assert.InRange(threshold, 0.09, 0.89);
        Assert.Equal(0.42, InferenceStats.OtsuThreshold([0.42f, 0.42f]), 6);
    }

    [Fact]
    public void YoloV5DecodeMultipliesObjectness()
    {
        // [1,7,1]: cx,cy,w,h,objectness,class0,class1
        float[] values = [50, 50, 20, 20, 0.5f, 0.2f, 0.8f];
        var candidate = Assert.Single(InferenceStats.DecodeYolo(values, [1, 7, 1], useObjectness: true));
        Assert.Equal(0.4, candidate.Score, 5);
        Assert.Equal(1, candidate.ClassId);
        var v8 = Assert.Single(InferenceStats.DecodeYolo(values, [1, 7, 1]));
        Assert.Equal(0.8, v8.Score, 5); // without objectness, channel 4 onward are class scores
        Assert.Equal(2, v8.ClassId);
    }

    [Fact]
    public void CameraPoseReprojectionMatchesSyntheticView()
    {
        // Board at Z=0 viewed with K fx=fy=200 cx=cy=100, pose: tilt around X by 20 deg, t=(0,0,300 in board units -> use mm-like scale)
        var fx = 200d; var fy = 200d; var cx = 100d; var cy = 100d;
        var angle = 20 * Math.PI / 180;
        var objectPoints = new List<List<double>>();
        var imagePoints = new List<List<double>>();
        for (var gy = 0; gy < 4; gy++)
        for (var gx = 0; gx < 5; gx++)
        {
            double x = gx * 10, y = gy * 10;
            var camY = Math.Cos(angle) * y - Math.Sin(angle) * 0;
            var camZ = Math.Sin(angle) * y + Math.Cos(angle) * 0 + 300;
            var camX = x;
            objectPoints.Add([x, y, 0]);
            imagePoints.Add([fx * camX / camZ + cx, fy * camY / camZ + cy]);
        }
        var stat = CalibrationViewAnalysis.AnalyzeWithCamera(objectPoints, imagePoints, 200, 200, fx, fy, cx, cy);
        Assert.NotNull(stat.ReprojectionRms);
        Assert.True(stat.ReprojectionRms < 0.05, $"rms={stat.ReprojectionRms}");
    }
}

public sealed class BatchCVariationTests
{
    [Fact]
    public void LocalStdDevIsZeroOnFlatAndPositiveOnTexture()
    {
        var flat = new byte[25]; Array.Fill(flat, (byte)100);
        Assert.All(ReferenceAlignment.LocalStdDev(flat, 5, 5, 3), v => Assert.Equal(0, v));
        var textured = new byte[25];
        for (var i = 0; i < textured.Length; i++) textured[i] = (byte)(i % 2 == 0 ? 0 : 255);
        Assert.All(ReferenceAlignment.LocalStdDev(textured, 5, 5, 3), v => Assert.True(v > 100));
        Assert.Throws<InvalidOperationException>(() => ReferenceAlignment.LocalStdDev(flat, 5, 5, 4));
    }
}
