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
        var (score, coverage, _, _, _, _) = ChamferShapeMatching.ScoreAt(dist, model, 80, 80, 0, 1, 2.5);
        Assert.True(score > .99 && coverage > .99, $"score={score} cov={coverage}");
    }

    [Fact]
    public void ScoreAt_PolarityRejectsOppositeGradient()
    {
        // Horizontal edge model with normals pointing +Y (brighter below in image coords).
        var model = Enumerable.Range(-20, 41).Select(x => new ChamferShapeMatching.ModelPoint(x, 0, 0, 1)).ToList();
        var edges = new bool[80, 80];
        for (var x = 20; x <= 60; x++) edges[40, x] = true;
        var dist = ChamferShapeMatching.DistanceTransform(edges);
        var gx = new float[80, 80]; var gyPos = new float[80, 80]; var gyNeg = new float[80, 80];
        for (var x = 20; x <= 60; x++) { gyPos[40, x] = 20; gyNeg[40, x] = -20; }

        var agree = ChamferShapeMatching.ScoreAt(dist, model, 40, 40, 0, 1, 2.5, ignorePolarity: false, gradX: gx, gradY: gyPos, polarityCosine: 0, minGradient: 5);
        var oppose = ChamferShapeMatching.ScoreAt(dist, model, 40, 40, 0, 1, 2.5, ignorePolarity: false, gradX: gx, gradY: gyNeg, polarityCosine: 0, minGradient: 5);
        var ignored = ChamferShapeMatching.ScoreAt(dist, model, 40, 40, 0, 1, 2.5, ignorePolarity: true, gradX: gx, gradY: gyNeg, polarityCosine: 0, minGradient: 5);

        Assert.True(agree.Score > 0.7, $"agree={agree.Score}");
        Assert.True(oppose.Score < agree.Score * 0.35, $"oppose={oppose.Score} agree={agree.Score}");
        Assert.True(ignored.Score > 0.7, $"ignored={ignored.Score}");
        Assert.True(agree.PolarityMatchFraction > 0.9);
        Assert.True(oppose.PolarityMatchFraction < 0.1);
    }

    [Fact]
    public void MeasureClutter_AndMaxClutterScoreRejectExtraEdges()
    {
        var model = RectangleModel(40, 24);
        var clean = Scene(160, 160, (model, 80, 80, 0, 1.0, 1.0));
        var cluttered = Scene(160, 160, (model, 80, 80, 0, 1.0, 1.0));
        // Sprinkle unexplained edges across the ROI.
        for (var y = 10; y < 150; y += 4)
        for (var x = 10; x < 150; x += 4)
            if (x < 50 || x > 110 || y < 50 || y > 110) cluttered[y, x] = true;

        var cleanClutter = ChamferShapeMatching.MeasureClutter(clean, model, 80, 80, 0, 1, 3, 0, 0, 160, 160);
        var dirtyClutter = ChamferShapeMatching.MeasureClutter(cluttered, model, 80, 80, 0, 1, 3, 0, 0, 160, 160);
        Assert.True(cleanClutter < 0.15, $"clean={cleanClutter}");
        Assert.True(dirtyClutter > 0.4, $"dirty={dirtyClutter}");

        var dist = ChamferShapeMatching.DistanceTransform(cluttered);
        var rejected = ChamferShapeMatching.Search(dist, model,
            new(AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5, MinScore: .5, MaxTargets: 2,
                ClutterEnabled: true, MaxClutterScore: 0.25, ScoreUsingClutter: false,
                ClutterRoiWidth: 160, ClutterRoiHeight: 160),
            sceneEdges: cluttered);
        Assert.Empty(rejected);

        var accepted = ChamferShapeMatching.Search(ChamferShapeMatching.DistanceTransform(clean), model,
            new(AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5, MinScore: .5, MaxTargets: 2,
                ClutterEnabled: true, MaxClutterScore: 0.25, ScoreUsingClutter: false,
                ClutterRoiWidth: 160, ClutterRoiHeight: 160),
            sceneEdges: clean);
        Assert.NotEmpty(accepted);
        Assert.True(accepted[0].ClutterScore <= 0.25);
    }

    [Fact]
    public void ScoreUsingClutter_LowersFinalScoreButKeepsNativeScore()
    {
        var model = RectangleModel(40, 24);
        var cluttered = Scene(160, 160, (model, 80, 80, 0, 1.0, 1.0));
        for (var x = 10; x < 150; x += 8)
        {
            cluttered[12, x] = true; cluttered[148, x] = true;
            cluttered[Math.Clamp(x, 0, 159), 12] = true; cluttered[Math.Clamp(x, 0, 159), 148] = true;
        }
        var dist = ChamferShapeMatching.DistanceTransform(cluttered);
        var without = ChamferShapeMatching.Search(dist, model,
            new(AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5, MinScore: .5, MaxTargets: 1,
                ClutterEnabled: true, MaxClutterScore: 1.0, ScoreUsingClutter: false,
                ClutterRoiX: 0, ClutterRoiY: 0, ClutterRoiWidth: 160, ClutterRoiHeight: 160),
            sceneEdges: cluttered);
        Assert.True(without.Count >= 1, "control search without scoreUsingClutter should hit");
        var hits = ChamferShapeMatching.Search(dist, model,
            new(AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5, MinScore: .2, MaxTargets: 1,
                ClutterEnabled: true, MaxClutterScore: 1.0, ScoreUsingClutter: true,
                ClutterRoiX: 0, ClutterRoiY: 0, ClutterRoiWidth: 160, ClutterRoiHeight: 160),
            sceneEdges: cluttered);
        Assert.True(hits.Count >= 1, $"scoreUsingClutter search empty; controlClutter={without[0].ClutterScore} controlScore={without[0].Score}");
        var hit = hits[0];
        Assert.True(hit.ClutterScore > 0.05, $"clutter={hit.ClutterScore}");
        Assert.True(hit.NativeScore > hit.Score + 1e-6, $"native={hit.NativeScore} final={hit.Score}");
        Assert.InRange(hit.Score, hit.NativeScore * (1 - hit.ClutterScore) - 1e-6, hit.NativeScore * (1 - hit.ClutterScore) + 1e-6);
    }
}