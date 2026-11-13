using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Batch F: session document round-trip, pose hints, outlier helpers.</summary>
public sealed class BatchFCalibrationWizardTests
{
    [Fact]
    public void SessionDocumentRoundTripsViewsAndSolveSummary()
    {
        var store = new CalibrationIntrinsicsWizard.SessionStore();
        var session = store.Create(new CalibrationIntrinsicsWizard.BoardConfig("chessboard", 3, 2, 10, MinBlurVariance: 10, MinCoverage: 0.1));
        session.SetImageSize(100, 80);

        for (var i = 0; i < 4; i++)
        {
            var view = MakeView(i * 3, i * 2);
            var quality = CalibrationViewQuality.Evaluate(view.ImagePoints, 100, 80, 6, 150, 10, 0.1);
            session.AddView(view, quality, $"v{i}");
        }
        session.SetEnabled(1, false);
        var model = new CameraCalibrationModel(100, 80, [[500, 0, 50], [0, 500, 40], [0, 0, 1]], [0, 0, 0, 0, 0], 0.42);
        session.StoreSolveResult(model, new { rms = 0.42, outlierViewIndex = 2 });

        var document = session.ExportDocument();
        Assert.Equal(CalibrationIntrinsicsWizard.SessionDocument.CurrentVersion, document.FormatVersion);
        Assert.Equal(4, document.Views.Count);
        Assert.NotNull(document.Model);

        var json = JsonSerializer.Serialize(document);
        var restoredDoc = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        })!;
        var restored = store.CreateFromDocument(restoredDoc with { SessionId = "restored-session" });
        var snap = restored.Snapshot();
        Assert.Equal("restored-session", snap.SessionId);
        Assert.Equal(4, snap.Views.Count);
        Assert.Equal(3, snap.EnabledCount);
        Assert.False(snap.Views.Single(v => v.Index == 1).Enabled);
        Assert.NotNull(snap.Model);
        Assert.Equal(0.42, snap.Model!.Rms, 3);
    }

    [Fact]
    public void PoseHintsSuggestEmptyCornersAndTilt()
    {
        // All centroids clustered near image center -> corner hints appear.
        var centered = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<double>)new double[] { 45 + (i % 3), 40 + (i / 3) }).ToList();
        var hints = CalibrationPoseHints.Suggest([(centered, true)], 100, 80, targetViews: 8);
        Assert.Contains(hints, h => h.Code.StartsWith("cover-", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Code is "add-tilt" or "more-tilt");
        Assert.True(hints.Any(h => h.Code == "more-views") || hints.Count >= 4);

        var empty = CalibrationPoseHints.Suggest([], 100, 80);
        Assert.Contains(empty, h => h.Code == "start-center");
    }

    [Fact]
    public void PerspectiveSkewIsHigherForTrapezoidThanRectangle()
    {
        // Rectangle-ish grid
        var rect = new List<IReadOnlyList<double>>();
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 4; c++)
            rect.Add([20 + c * 20.0, 20 + r * 20.0]);
        var skewRect = CalibrationPoseHints.PerspectiveSkew(rect);

        // Trapezoid: top narrower than bottom
        var trap = new List<IReadOnlyList<double>>();
        for (var r = 0; r < 3; r++)
        {
            var inset = (2 - r) * 12.0;
            for (var c = 0; c < 4; c++)
                trap.Add([20 + inset + c * ((80 - 2 * inset) / 3.0), 20 + r * 25.0]);
        }
        var skewTrap = CalibrationPoseHints.PerspectiveSkew(trap);
        Assert.True(skewTrap > skewRect, $"trap={skewTrap} rect={skewRect}");
    }

    private static CameraCalibrationView MakeView(double ox, double oy)
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
}
