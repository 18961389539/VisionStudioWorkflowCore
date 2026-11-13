using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Calibration wizard / quality gate tests (Batch E).</summary>
public sealed class BatchECalibrationWizardTests
{
    [Fact]
    public void LaplacianVarianceIsHigherOnSharpEdgesThanBlur()
    {
        using var sharp = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(sharp, new Rect(16, 16, 32, 32), Scalar.All(255), -1);
        using var blurry = new Mat();
        Cv2.GaussianBlur(sharp, blurry, new Size(15, 15), 0);
        var sharpScore = CalibrationViewQuality.LaplacianVariance(sharp);
        var blurScore = CalibrationViewQuality.LaplacianVariance(blurry);
        Assert.True(sharpScore > blurScore * 2, $"sharp={sharpScore} blur={blurScore}");
    }

    [Fact]
    public void QualityEvaluateFailsIncompleteAndLowCoverage()
    {
        var points = Enumerable.Range(0, 10).Select(i => (IReadOnlyList<double>)new double[] { 10 + i, 10 }).ToList();
        var incomplete = CalibrationViewQuality.Evaluate(points, 200, 200, 42, blurVariance: 200, minBlurVariance: 0, minCoverage: 0);
        Assert.Equal(CalibrationViewQuality.Verdict.Fail, incomplete.Verdict);
        Assert.Equal("incomplete-grid", incomplete.Reason);

        var full = Enumerable.Range(0, 42).Select(i => (IReadOnlyList<double>)new double[] { 20 + (i % 7), 20 + (i / 7) }).ToList();
        var lowCov = CalibrationViewQuality.Evaluate(full, 200, 200, 42, blurVariance: 200, minBlurVariance: 0, minCoverage: 0.5);
        Assert.Equal(CalibrationViewQuality.Verdict.Fail, lowCov.Verdict);
        Assert.Equal("low-coverage", lowCov.Reason);

        var missing = CalibrationViewQuality.Evaluate(null, 200, 200, 42, 50);
        Assert.Equal(CalibrationViewQuality.Verdict.Fail, missing.Verdict);
        Assert.Equal("board-not-found", missing.Reason);
    }

    [Fact]
    public void WizardSessionAccumulatesAcceptsRejectsAndBuildsRequest()
    {
        var store = new CalibrationIntrinsicsWizard.SessionStore();
        var session = store.Create(new CalibrationIntrinsicsWizard.BoardConfig("chessboard", 3, 2, 10, MinBlurVariance: 10, MinCoverage: 0.1));
        session.SetImageSize(100, 80);

        CameraCalibrationView MakeView(double ox, double oy)
        {
            var obj = new List<List<double>>();
            var img = new List<List<double>>();
            for (var r = 0; r < 2; r++)
            for (var c = 0; c < 3; c++)
            {
                obj.Add([c * 10.0, r * 10.0, 0]);
                img.Add([20 + c * 20 + ox, 15 + r * 20 + oy]);
            }
            return new CameraCalibrationView(obj, img);
        }

        for (var i = 0; i < 5; i++)
        {
            var view = MakeView(i * 3, i * 2);
            var quality = CalibrationViewQuality.Evaluate(view.ImagePoints, 100, 80, 6, blurVariance: 150, minBlurVariance: 10, minCoverage: 0.1);
            session.AddView(view, quality, $"v{i}");
        }

        var snap = session.Snapshot();
        Assert.Equal(5, snap.Views.Count);
        Assert.Equal(5, snap.EnabledCount);

        session.SetEnabled(1, false);
        Assert.Equal(4, session.Snapshot().EnabledCount);

        session.RemoveView(2);
        var request = session.BuildRequest();
        Assert.Equal(100, request.ImageWidth);
        Assert.Equal(80, request.ImageHeight);
        Assert.Equal(3, request.Views.Count);

        Assert.Throws<InvalidOperationException>(() =>
            store.Create(new CalibrationIntrinsicsWizard.BoardConfig("chessboard", 3, 2, 10)).BuildRequest());
    }

    [Fact]
    public void WizardRejectsBoardChangeAfterViewsAndDuplicateSessionLookup()
    {
        var store = new CalibrationIntrinsicsWizard.SessionStore();
        var session = store.Create(new CalibrationIntrinsicsWizard.BoardConfig("circles", 4, 3, 5));
        session.SetImageSize(200, 200);
        var view = new CameraCalibrationView(
            Enumerable.Range(0, 12).Select(i => new List<double> { i % 4 * 5.0, i / 4 * 5.0, 0 }).ToList(),
            Enumerable.Range(0, 12).Select(i => new List<double> { 10 + (i % 4) * 30.0, 10 + (i / 4) * 40.0 }).ToList());
        var q = CalibrationViewQuality.Evaluate(view.ImagePoints, 200, 200, 12, 200);
        session.AddView(view, q);
        Assert.Throws<InvalidOperationException>(() => session.UpdateBoard(session.Board with { Columns = 5 }));
        Assert.True(store.Remove(session.SessionId));
        Assert.Throws<KeyNotFoundException>(() => store.Get(session.SessionId));
    }

    [Fact]
    public async Task HandEyeReportsPerSampleResidualsAndRejectsDuplicateRobotPoses()
    {
        var x = new Pose(12, -4, 0.2);
        var y = new Pose(100, 30, -0.1);
        var robots = new[] { new Pose(0, 0, -0.4), new Pose(40, 10, -0.1), new Pose(80, -20, 0.25), new Pose(-10, 50, 0.55), new Pose(30, 70, 0.9) };
        var samples = robots.Select(r =>
        {
            var vision = Compose(Inverse(x), Compose(Inverse(r), y));
            return new { robot = new { x = r.X, y = r.Y, thetaDeg = r.Theta * 180 / Math.PI }, vision = new { x = vision.X, y = vision.Y, thetaDeg = vision.Theta * 180 / Math.PI } };
        }).ToArray();
        var json = JsonSerializer.Serialize(new { samples });
        var result = await new PlanarHandEyeCalibrationNode().ExecuteAsync(
            new(new Dictionary<string, VisionStudio.Abstractions.VisionValue>()),
            new VisionStudio.Abstractions.NodeDefinition("n", "calibration.handEye2D", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { mode = "eyeInHand", samplesJson = json }))!),
            default);
        Assert.True(result.Outputs.ContainsKey("sampleResidualsJson"));
        Assert.True(result.Outputs.ContainsKey("maxTranslationError"));
        Assert.True(result.Outputs.ContainsKey("worstSampleIndex"));
        var residuals = JsonDocument.Parse(result.Outputs["sampleResidualsJson"].Require<string>("sampleResidualsJson"));
        Assert.Equal(5, residuals.RootElement.GetArrayLength());

        var dup = samples.Concat([samples[0]]).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PlanarHandEyeCalibrationNode().ExecuteAsync(
                new(new Dictionary<string, VisionStudio.Abstractions.VisionValue>()),
                new VisionStudio.Abstractions.NodeDefinition("n", "calibration.handEye2D", null, null,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { mode = "eyeInHand", samplesJson = JsonSerializer.Serialize(new { samples = dup }) }))!),
                default));
    }

    private readonly record struct Pose(double X, double Y, double Theta);
    private static Pose Compose(Pose a, Pose b)
    {
        var c = Math.Cos(a.Theta); var s = Math.Sin(a.Theta);
        return new(a.X + c * b.X - s * b.Y, a.Y + s * b.X + c * b.Y, a.Theta + b.Theta);
    }
    private static Pose Inverse(Pose p)
    {
        var c = Math.Cos(p.Theta); var s = Math.Sin(p.Theta);
        return new(-c * p.X - s * p.Y, s * p.X - c * p.Y, -p.Theta);
    }
}
