using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Pure-managed chamfer shape matching: no Mat/native code, so these run on
/// Linux. Scene edges are synthesized by transforming the model with the same
/// image-coordinate rotation convention as the matcher (see RoiTransform).
/// </summary>
public sealed class ChamferShapeMatchingTests
{
    private static List<ChamferShapeMatching.ModelPoint> RectangleModel(double w = 60, double h = 40)
    {
        var pts = new List<ChamferShapeMatching.ModelPoint>();
        for (var x = -w / 2; x <= w / 2; x += 2) { pts.Add(new(x, -h / 2)); pts.Add(new(x, h / 2)); }
        for (var y = -h / 2; y <= h / 2; y += 2) { pts.Add(new(-w / 2, y)); pts.Add(new(w / 2, y)); }
        // Asymmetric internal stub: a plain rectangle perimeter aliases under
        // rotation/scale (see matcher limits), real templates carry asymmetry.
        for (var t2 = 0d; t2 <= 1; t2 += .1) pts.Add(new(w / 4 * t2, -h / 4 * t2));
        return pts;
    }

    private static bool[,] Scene(int w, int h, params (IReadOnlyList<ChamferShapeMatching.ModelPoint> Model, double Cx, double Cy, double Angle, double Scale, double KeepFraction)[] objects)
    {
        var edges = new bool[h, w];
        foreach (var o in objects)
        {
            var rad = o.Angle * Math.PI / 180; var cos = Math.Cos(rad); var sin = Math.Sin(rad);
            var keep = (int)(o.Model.Count * o.KeepFraction);
            foreach (var p in o.Model.Take(keep))
            {
                var x = (int)Math.Round(o.Cx + (cos * p.X + sin * p.Y) * o.Scale);
                var y = (int)Math.Round(o.Cy + (-sin * p.X + cos * p.Y) * o.Scale);
                if (x >= 0 && y >= 0 && x < w && y < h) edges[y, x] = true;
            }
        }
        return edges;
    }

    [Fact]
    public void DistanceTransform_IsZeroOnEdgesAndGrowsAway()
    {
        var edges = new bool[9, 9]; edges[4, 4] = true;
        var d = ChamferShapeMatching.DistanceTransform(edges);
        Assert.Equal(0, d[4, 4]); Assert.True(d[4, 6] > 1.9 && d[4, 6] < 2.1); Assert.True(d[0, 0] > 5);
    }

    [Fact]
    public void Search_FindsTranslatedRotatedScaledObject()
    {
        var model = RectangleModel();
        var dist = ChamferShapeMatching.DistanceTransform(Scene(200, 200, (model, 120, 90, 30, 1.2, 1.0)));
        var hits = ChamferShapeMatching.Search(dist, model, new(AngleMinDeg: -60, AngleMaxDeg: 60, AngleStepDeg: 5, ScaleMin: .8, ScaleMax: 1.4, ScaleStep: .1, MinScore: .6, MaxTargets: 3));
        Assert.NotEmpty(hits);
        // Best hit must be the true pose; a symmetric perimeter can also alias
        // at other angles/scales above 0.6 (documented chamfer limit), so this
        // asserts ranking, not uniqueness.
        var hit = hits[0];
        Assert.InRange(hit.X, 117, 123); Assert.InRange(hit.Y, 87, 93);
        Assert.InRange(Math.Abs(hit.AngleDeg - 30), 0, 4); Assert.InRange(hit.Scale, 1.1, 1.3);
        Assert.True(hit.Score > .8, $"score={hit.Score}");
    }

    [Fact]
    public void Search_PartialOcclusionStillMatchesWithReducedCoverage()
    {
        var model = RectangleModel();
        var dist = ChamferShapeMatching.DistanceTransform(Scene(200, 200, (model, 100, 100, 0, 1.0, 0.55)));
        var hits = ChamferShapeMatching.Search(dist, model, new(AngleMinDeg: -10, AngleMaxDeg: 10, AngleStepDeg: 5, MinScore: .45, MaxTargets: 2));
        var hit = Assert.Single(hits);
        Assert.InRange(hit.Coverage, .4, .75); Assert.True(hit.Score >= .45);
    }

    [Fact]
    public void Search_EmptySceneFindsNothing_AndMultiTargetWorks()
    {
        var model = RectangleModel();
        var empty = ChamferShapeMatching.Search(ChamferShapeMatching.DistanceTransform(new bool[120, 120]), model, new(MinScore: .6));
        Assert.Empty(empty);
        var dist = ChamferShapeMatching.DistanceTransform(Scene(240, 240, (model, 60, 60, 0, 1.0, 1.0), (model, 180, 180, 0, 1.0, 1.0)));
        var hits = ChamferShapeMatching.Search(dist, model, new(AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5, MinScore: .6, MaxTargets: 5, MinCenterDistance: 30));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void BuildModel_SubsamplesAndScoreAtPerfectPoseIsOne()
    {
        var model = RectangleModel();
        Assert.Equal(20, ChamferShapeMatching.BuildModel(model, 20).Length);
        var dist = ChamferShapeMatching.DistanceTransform(Scene(160, 160, (model, 80, 80, 0, 1.0, 1.0)));
        var (score, coverage, _) = ChamferShapeMatching.ScoreAt(dist, model, 80, 80, 0, 1, 2.5);
        Assert.True(score > .99 && coverage > .99, $"score={score} cov={coverage}");
    }
}
