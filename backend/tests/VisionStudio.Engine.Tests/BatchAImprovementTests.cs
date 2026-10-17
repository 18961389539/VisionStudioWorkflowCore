using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>Pure-managed Batch A tests: they run on Linux without OpenCV/JLVision/Tesseract natives.</summary>
public sealed class BatchAImprovementTests
{
    [Fact]
    public void LineFitRecoversSlopeAndRejectsOutlier()
    {
        var points = Enumerable.Range(0, 21).Select(i => new VisionPoint2D(i * 5, 10 + i * 2.5)).ToList();
        points.Add(new VisionPoint2D(50, 200)); // gross outlier
        var fit = GeometryFitting.FitLine(points);
        Assert.Equal(22, fit.PointCount);
        Assert.True(fit.InlierCount >= 21, $"inliers={fit.InlierCount}");
        Assert.True(fit.RmsError < 0.6, $"rms={fit.RmsError}");
        Assert.InRange(fit.AngleDeg, 26, 27.2);
        Assert.True(fit.Line.Length > 110);
    }

    [Fact]
    public void LineFitRejectsCoincidentPoints()
        => Assert.Throws<InvalidOperationException>(() => GeometryFitting.FitLine([new(3, 3), new(3, 3)]));

    [Fact]
    public void CircleFitRecoversCenterRadiusAndRejectsOutlier()
    {
        var points = Enumerable.Range(0, 36).Select(i =>
        {
            var a = i * Math.PI / 18;
            return new VisionPoint2D(100 + 40 * Math.Cos(a), 80 + 40 * Math.Sin(a));
        }).ToList();
        points.Add(new VisionPoint2D(0, 0));
        var fit = GeometryFitting.FitCircle(points);
        Assert.InRange(fit.Circle.X, 99.5, 100.5);
        Assert.InRange(fit.Circle.Y, 79.5, 80.5);
        Assert.InRange(fit.Circle.Radius, 39.5, 40.5);
        Assert.True(fit.RmsError < 0.5, $"rms={fit.RmsError}");
        Assert.True(fit.InlierCount >= 36);
    }

    [Fact]
    public void CircleFitRejectsCollinearPoints()
        => Assert.Throws<InvalidOperationException>(() => GeometryFitting.FitCircle([new(0, 0), new(1, 1), new(2, 2), new(3, 3)]));

    [Fact]
    public void OcrTsvWordsAreScaledBackToSourceCoordinates()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
                           "5\t1\t1\t1\t1\t1\t20\t40\t60\t20\t92.5\tHELLO\n" +
                           "5\t1\t1\t1\t1\t2\t90\t40\t50\t20\t-1\t\n" +
                           "1\t1\t1\t0\t0\t0\t0\t0\t200\t100\t-1\t\n";
        var words = OcrTextParsing.ParseTsvWords(tsv, scale: 2, offsetX: 10, offsetY: 5);
        var word = Assert.Single(words);
        Assert.Equal("HELLO", word.Text);
        Assert.Equal(0.925, word.Confidence, 3);
        Assert.Equal(20, word.X); Assert.Equal(25, word.Y);
        Assert.Equal(30, word.Width); Assert.Equal(10, word.Height);
    }

    [Fact]
    public void OcrWhitelistRejectsControlCharacters()
        => Assert.Throws<InvalidOperationException>(() => OcrTextParsing.ValidateWhitelist("ABC\n123"));

    [Fact]
    public void RegistrationFindsKnownShift()
    {
        const int w = 24, h = 24;
        var reference = new byte[w * h];
        for (var i = 0; i < reference.Length; i++) reference[i] = (byte)((i * 37 + (i / w) * 11) % 251);
        var image = new byte[w * h];
        // image is reference shifted by (+2,+1): image(x,y) = reference(x-2, y-1)
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            image[y * w + x] = x >= 2 && y >= 1 ? reference[(y - 1) * w + (x - 2)] : (byte)0;
        var shift = ReferenceAlignment.SearchShift(image, reference, w, h, 3);
        Assert.Equal(2, shift.ShiftX); Assert.Equal(1, shift.ShiftY);
        Assert.True(shift.MeanAbsoluteDifference < 20);
    }

    [Fact]
    public void GainOffsetFitRecoversIlluminationChange()
    {
        var reference = Enumerable.Range(0, 201).Select(i => (byte)i).ToArray();
        var image = reference.Select(v => (byte)Math.Clamp(1.2 * v + 10, 0, 255)).ToArray();
        var (gain, offset) = ReferenceAlignment.FitGainOffset(image, reference);
        Assert.InRange(gain, 1.1, 1.3);
        Assert.InRange(offset, 4, 16);
    }
}

public sealed class BatchAFitNodeTests
{
    private static NodeDefinition Definition(string type) => new("test", type, null, null, new Dictionary<string, System.Text.Json.JsonElement>());

    [Fact]
    public async Task FitLineNodeReportsResidualsWithoutNativeLibraries()
    {
        var inputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 10; i++) inputs[$"point{i + 1}"] = VisionValue.Point(new VisionPoint2D(i * 10, 5 + i * 5));
        inputs["point11"] = VisionValue.Point(new VisionPoint2D(50, 500)); // outlier
        var result = await new VisionStudio.Engine.Nodes.FitLineNode().ExecuteAsync(new(inputs), Definition("measure.fitLine"), CancellationToken.None);
        Assert.True(result.Outputs["rmsError"].Require<double>("rmsError") < 1);
        Assert.Equal(10, result.Outputs["inlierCount"].Require<long>("inlierCount"));
    }

    [Fact]
    public async Task FitCircleNodeReportsResidualsWithoutNativeLibraries()
    {
        var inputs = new Dictionary<string, VisionValue>();
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            inputs[$"point{i + 1}"] = VisionValue.Point(new VisionPoint2D(50 + 30 * Math.Cos(a), 50 + 30 * Math.Sin(a)));
        }
        var result = await new VisionStudio.Engine.Nodes.FitCircleNode().ExecuteAsync(new(inputs), Definition("measure.fitCircle"), CancellationToken.None);
        Assert.InRange(result.Outputs["radius"].Require<double>("radius"), 29.5, 30.5);
        Assert.True(result.Outputs["rmsError"].Require<double>("rmsError") < 0.5);
    }
}
