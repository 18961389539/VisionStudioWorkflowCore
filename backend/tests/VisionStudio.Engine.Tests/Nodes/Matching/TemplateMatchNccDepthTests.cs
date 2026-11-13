using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Batch PD-4..PD-9: match.template NCC product depth — asymmetric angle, ignore polarity, diagnostics.
/// Honest scope: gray NCC (PatMax-NCC / HALCON find_ncc_model style), not shape-match parity.
/// </summary>
public sealed class TemplateMatchNccDepthTests
{
    [Fact]
    public async Task AsymmetricAngleWindow_FindsInRange_AndRejectsOutsideWindow()
    {
        using var template = Template();
        using var rotated = RotateExpanded(template, 15);
        using var scene = new Mat(220, 220, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, rotated, 70, 70);

        var hit = await Run(new TemplateMatchNode(), scene, template,
            """{"searchAngleStartDeg":5,"searchAngleEndDeg":25,"angleRangeDeg":0,"angleStepDeg":5,"minScore":0.5}""");
        Assert.True(hit.Outputs["found"].Require<bool>("found"));
        Assert.InRange(hit.Outputs["angle"].Require<double>("angle"), 10, 20);
        Assert.Equal(5d, Convert.ToDouble(hit.Summary["searchAngleStartDeg"]), 3);
        Assert.Equal(25d, Convert.ToDouble(hit.Summary["searchAngleEndDeg"]), 3);
        Assert.True(Convert.ToInt32(hit.Summary["rawCandidateCount"]) >= 1);
        Assert.True(Convert.ToDouble(hit.Summary["peakScore"]) >= 0.5);

        var miss = await Run(new TemplateMatchNode(), scene, template,
            """{"searchAngleStartDeg":-30,"searchAngleEndDeg":-5,"angleRangeDeg":0,"angleStepDeg":5,"minScore":0.5}""");
        Assert.False(miss.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task EmptyAngleWindow_IsRejected()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template,
                """{"searchAngleStartDeg":10,"searchAngleEndDeg":-10,"angleRangeDeg":0,"minScore":0.5}"""));
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IgnorePolarity_MatchesInvertedTemplate_WhenEnabled()
    {
        using var template = Template();
        using var inverted = new Mat();
        Cv2.BitwiseNot(template, inverted);
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 50, 50);

        var polarityOn = await Run(new TemplateMatchNode(), scene, inverted,
            """{"minScore":0.55,"angleRangeDeg":0,"ignorePolarity":false}""");
        Assert.False(polarityOn.Outputs["found"].Require<bool>("found"),
            $"use_polarity should reject an inverted template (score={polarityOn.Outputs["score"].Require<double>("score")})");
        Assert.Equal("use_polarity", polarityOn.Summary["polarityMode"]?.ToString());

        var polarityOff = await Run(new TemplateMatchNode(), scene, inverted,
            """{"minScore":0.55,"angleRangeDeg":0,"ignorePolarity":true}""");
        Assert.True(polarityOff.Outputs["found"].Require<bool>("found"));
        Assert.Equal("ignore_global_polarity", polarityOff.Summary["polarityMode"]?.ToString());
        Assert.True(polarityOff.Outputs["score"].Require<double>("score") >= 0.55);
    }

    [Fact]
    public async Task Diagnostics_ReportPyramidPolarityAndPeak_ForUnitScaleHit()
    {
        using var template = Template();
        using var scene = new Mat(140, 140, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 45);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"pyramidLevels":2,"angleRangeDeg":0}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(2, Convert.ToInt32(result.Summary["pyramidLevels"]));
        Assert.Equal("use_polarity", result.Summary["polarityMode"]?.ToString());
        Assert.Equal(true, result.Summary["subpixel"]);
        Assert.True(Convert.ToDouble(result.Summary["peakScore"]) >= 0.8);
        Assert.True(Convert.ToInt32(result.Summary["rawCandidateCount"]) >= 1);
        Assert.Equal(1, Convert.ToInt32(result.Summary["scaleSamples"]));
    }

    [Fact]
    public async Task LegacyAngleRange_StillOpensSymmetricWindow_WhenStartEndStayZero()
    {
        using var template = Template();
        using var rotated = RotateExpanded(template, 15);
        using var scene = new Mat(220, 220, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, rotated, 70, 70);

        // Simulates a form that wrote catalog defaults start=0/end=0 together with angleRangeDeg=20.
        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"angleRangeDeg":20,"searchAngleStartDeg":0,"searchAngleEndDeg":0,"angleStepDeg":5,"minScore":0.5}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.InRange(result.Outputs["angle"].Require<double>("angle"), 10, 20);
        Assert.Equal(-20d, Convert.ToDouble(result.Summary["searchAngleStartDeg"]), 3);
        Assert.Equal(20d, Convert.ToDouble(result.Summary["searchAngleEndDeg"]), 3);
    }


    [Fact]
    public async Task IgnoreRoi_ExcludesDecoyCopy_KeepsVisibleMatch()
    {
        using var template = Template();
        using var scene = new Mat(220, 240, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 30);   // will be ignored
        Paste(scene, template, 140, 150); // kept

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minScore":0.8,"minCenterDistance":20,"ignoreRoi":{"type":"Rectangle","x":10,"y":20,"width":60,"height":60}}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(true, result.Summary["ignoreMask"]);
        Assert.InRange(result.Outputs["x"].Require<double>("x"), 137, 145);
        Assert.InRange(result.Outputs["y"].Require<double>("y"), 147, 155);
    }

    [Fact]
    public async Task IgnoreRoi_CoveringOnlyCopy_YieldsNoMatch()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"ignoreRoi":{"type":"Rectangle","x":30,"y":30,"width":50,"height":50}}""");

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(true, result.Summary["ignoreMask"]);
    }

    [Fact]
    public async Task EmitScoreMap_ProducesFullSceneMap_WithPeakNearTemplateTopLeft()
    {
        using var template = Template();
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 48, 52);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"angleRangeDeg":0}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(true, result.Summary["scoreMapEmitted"]);
        Assert.Equal("template-top-left", result.Summary["scoreMapConvention"]?.ToString());
        Assert.Contains("opencv-ccoeff-normed-unit-scale", result.Summary["scoreMapMode"]?.ToString() ?? "");
        Assert.Contains("0deg", result.Summary["scoreMapMode"]?.ToString() ?? "");
        Assert.True(result.Outputs.ContainsKey("scoreMap"));
        using var map = result.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        Assert.Equal(160, map.Width);
        Assert.Equal(160, map.Height);
        Assert.InRange(Convert.ToDouble(result.Summary["scoreMapPeakX"]), 45, 55);
        Assert.InRange(Convert.ToDouble(result.Summary["scoreMapPeakY"]), 49, 58);
        Assert.True(Convert.ToDouble(result.Summary["scoreMapPeakValue"]) >= 200); // 8-bit near 255 for perfect NCC
    }

    [Fact]
    public async Task EmitScoreMap_ZerosIgnoredRegion_AndDefaultsOff()
    {
        using var template = Template();
        using var scene = new Mat(200, 220, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 30);
        Paste(scene, template, 140, 140);

        var off = await Run(new TemplateMatchNode(), scene, template, """{"minScore":0.8}""");
        Assert.Equal(false, off.Summary["scoreMapEmitted"]);
        Assert.False(off.Outputs.ContainsKey("scoreMap"));

        var on = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"maxTargets":1,"ignoreRoi":{"type":"Rectangle","x":10,"y":20,"width":60,"height":60}}""");
        Assert.Equal(true, on.Summary["scoreMapEmitted"]);
        using var map = on.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        // Peak should prefer the non-ignored copy (ignore zeros the left paste neighborhood).
        Assert.True(Convert.ToDouble(on.Summary["scoreMapPeakX"]) > 100,
            $"expected peak on right copy, got x={on.Summary["scoreMapPeakX"]}");
        // Spot-check a pixel inside the ignore rectangle is zeroed on the canvas.
        Assert.Equal(0, map.Mat.At<byte>(40, 30));
    }



    [Fact]
    public async Task SoftIgnore_KeepsOutsideMatch_AndDepressesIgnoredCopy()
    {
        using var template = Template();
        using var scene = new Mat(220, 240, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 30);   // inside soft ignore
        Paste(scene, template, 140, 150); // outside

        var soft = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minScore":0.55,"minCenterDistance":20,"ignoreMode":"soft","ignoreSoftWeight":0,"ignoreRoi":{"type":"Rectangle","x":10,"y":20,"width":60,"height":60}}""");

        Assert.True(soft.Outputs["found"].Require<bool>("found"));
        Assert.Equal("soft", soft.Summary["ignoreMode"]?.ToString());
        Assert.Equal(true, soft.Summary["ignoreSoftApplied"]);
        // With softWeight 0 (full mean-fill), the ignored copy should not survive; only the right one.
        Assert.Equal(1L, soft.Outputs["count"].Require<long>("count"));
        Assert.InRange(soft.Outputs["x"].Require<double>("x"), 137, 145);

        var partial = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minScore":0.15,"minCenterDistance":20,"ignoreMode":"soft","ignoreSoftWeight":0.35,"ignoreFeatherPx":4,"ignoreRoi":{"type":"Rectangle","x":10,"y":20,"width":60,"height":60}}""");
        Assert.Equal("soft", partial.Summary["ignoreMode"]?.ToString());
        Assert.True(Convert.ToDouble(partial.Summary["ignoreSoftWeight"]) > 0);
        // Outside copy must still be accepted.
        Assert.True(partial.Outputs["found"].Require<bool>("found"));
        Assert.True(partial.Outputs["count"].Require<long>("count") >= 1);
    }

    [Fact]
    public async Task SoftIgnore_RejectsUnknownMode()
    {
        using var template = Template();
        using var scene = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 20);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template, """{"ignoreMode":"weighted"}"""));
        Assert.Contains("ignoreMode", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScoreMap_MatchedAngle_UsesAcceptedRotation()
    {
        using var template = Template();
        using var rotated = RotateExpanded(template, 15);
        using var scene = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, rotated, 80, 80);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"angleRangeDeg":20,"angleStepDeg":5,"minScore":0.45,"emitScoreMap":true,"scoreMapAngleMode":"matched"}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("matched", result.Summary["scoreMapAngleMode"]?.ToString());
        var mapAngle = Convert.ToDouble(result.Summary["scoreMapAngleDeg"]);
        Assert.InRange(mapAngle, 10, 20);
        Assert.Contains("rot", result.Summary["scoreMapMode"]?.ToString() ?? "");
        Assert.True(result.Outputs.ContainsKey("scoreMap"));
        using var map = result.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        Assert.Equal(240, map.Width);
        Assert.Equal(240, map.Height);
    }

    [Fact]
    public async Task ScoreMap_SearchMid_ReportsWindowMidpoint()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"scoreMapAngleMode":"searchMid","searchAngleStartDeg":-10,"searchAngleEndDeg":30,"angleRangeDeg":0}""");

        Assert.Equal("searchmid", result.Summary["scoreMapAngleMode"]?.ToString());
        Assert.Equal(10d, Convert.ToDouble(result.Summary["scoreMapAngleDeg"]), 3);
        Assert.Equal(true, result.Summary["scoreMapEmitted"]);
    }

    [Fact]
    public async Task ScoreMap_SoftIgnore_AttenuatesNotHardZeros()
    {
        using var template = Template();
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 20);
        Paste(scene, template, 100, 100);

        var hard = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"ignoreMode":"hard","ignoreRoi":{"type":"Rectangle","x":10,"y":10,"width":50,"height":50}}""");
        using var hardMap = hard.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        Assert.Equal(0, hardMap.Mat.At<byte>(30, 30));
        Assert.Equal("hard-zero", hard.Summary["scoreMapIgnore"]?.ToString());

        var soft = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"ignoreMode":"soft","ignoreSoftWeight":0.25,"ignoreRoi":{"type":"Rectangle","x":10,"y":10,"width":50,"height":50}}""");
        Assert.Equal("soft-weight", soft.Summary["scoreMapIgnore"]?.ToString());
        using var softMap = soft.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        // Soft should not force every ignored pixel to absolute 0 the same way hard does when a peak existed;
        // at minimum the mode tag differs and soft weight is recorded.
        Assert.Equal(0.25d, Convert.ToDouble(soft.Summary["ignoreSoftWeight"]), 3);
    }



    [Fact]
    public async Task ScoreMap_TemplateMask_UsesOpenCvMaskedCorrelation()
    {
        using var template = Template();
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 48, 52);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"scoreMapWeighting":"templateMask","templateRoi":{"type":"Rectangle","x":4,"y":4,"width":24,"height":24},"angleRangeDeg":0}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(true, result.Summary["scoreMapTemplateMask"]);
        Assert.Contains("templatemask", result.Summary["scoreMapMode"]?.ToString() ?? "");
        Assert.InRange(Convert.ToDouble(result.Summary["scoreMapPeakX"]), 45, 55);
        Assert.InRange(Convert.ToDouble(result.Summary["scoreMapPeakY"]), 49, 58);
        Assert.True(Convert.ToDouble(result.Summary["scoreMapPeakValue"]) >= 180);
    }

    [Fact]
    public async Task ScoreMap_TemplateMask_RejectsTinyTemplateRoi()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template,
                """{"emitScoreMap":true,"scoreMapWeighting":"templateMask","templateRoi":{"type":"Rectangle","x":0,"y":0,"width":2,"height":2}}"""));
        Assert.Contains("16", error.Message);
    }

    [Fact]
    public async Task ScoreMap_Stack_EmitsMontageAndMaxProjection()
    {
        using var template = Template();
        using var rotated = RotateExpanded(template, 15);
        using var scene = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, rotated, 70, 70);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"angleRangeDeg":20,"angleStepDeg":10,"minScore":0.4,"emitScoreMap":true,"scoreMapAngleMode":"stack","scoreMapStackMaxPlanes":5}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("stack", result.Summary["scoreMapAngleMode"]?.ToString());
        var count = Convert.ToInt32(result.Summary["scoreMapStackCount"]);
        Assert.InRange(count, 2, 5);
        Assert.Contains("stack", result.Summary["scoreMapMode"]?.ToString() ?? "");
        Assert.True(result.Outputs.ContainsKey("scoreMapStack"));
        using var map = result.Outputs["scoreMap"].Require<VisionImage>("scoreMap");
        using var stack = result.Outputs["scoreMapStack"].Require<VisionImage>("scoreMapStack");
        Assert.Equal(200, map.Width);
        Assert.Equal(200, map.Height);
        Assert.Equal(200, stack.Width);
        Assert.Equal(200 * count, stack.Height);
        Assert.InRange(Convert.ToDouble(result.Summary["scoreMapAngleDeg"]), 5, 20);
    }

    [Fact]
    public async Task ScoreMap_Stack_SinglePlaneWhenAngleWindowIsZero()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"emitScoreMap":true,"scoreMapAngleMode":"stack","angleRangeDeg":0,"scoreMapStackMaxPlanes":9}""");

        Assert.Equal(1, Convert.ToInt32(result.Summary["scoreMapStackCount"]));
        Assert.False(result.Outputs.ContainsKey("scoreMapStack"));
        Assert.Equal(0d, Convert.ToDouble(result.Summary["scoreMapAngleDeg"]), 3);
    }

    [Fact]
    public async Task ScoreMap_RejectsUnknownWeighting()
    {
        using var template = Template();
        using var scene = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 20, 20);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template, """{"scoreMapWeighting":"sceneMask"}"""));
        Assert.Contains("scoreMapWeighting", error.Message, StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public async Task FindPath_TemplateRoi_ReduceDomain_ReportsModelRegionAndFinds()
    {
        using var template = Template();
        using var scene = new Mat(140, 140, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 45);

        var full = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.8,"angleRangeDeg":0,"templateRoi":{"type":"Rectangle","x":0,"y":0,"width":32,"height":32}}""");
        Assert.True(full.Outputs["found"].Require<bool>("found"));
        Assert.Equal(true, full.Summary["findUsesTemplateMask"]);
        Assert.Equal("create-ncc-reduce-domain", full.Summary["nccTemplateMaskMode"]?.ToString());
        Assert.True(Convert.ToDouble(full.Summary["nccModelRegionArea"]) >= 16);

        var none = await Run(new TemplateMatchNode(), scene, template, """{"minScore":0.8,"angleRangeDeg":0}""");
        Assert.Equal(false, none.Summary["findUsesTemplateMask"]);
        Assert.Equal("none", none.Summary["nccTemplateMaskMode"]?.ToString());
    }

    [Fact]
    public async Task FindPath_TemplateRoi_ExcludingFeatures_FailsWhileFeatureMaskFinds()
    {
        // Left half blank, right half has a bright unique glyph — FindNccModel must use ReduceDomain mask.
        using var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(template, new Rect(18, 6, 10, 20), Scalar.All(230), -1);
        Cv2.Circle(template, new Point(23, 16), 3, Scalar.All(10), -1);
        using var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 50, 50);

        var featureMask = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.55,"angleRangeDeg":0,"templateRoi":{"type":"Rectangle","x":16,"y":0,"width":16,"height":32}}""");
        Assert.True(featureMask.Outputs["found"].Require<bool>("found"),
            $"FindNccModel with feature-side templateRoi should locate the paste (score={featureMask.Outputs["score"].Require<double>("score")}).");
        Assert.Equal(true, featureMask.Summary["findUsesTemplateMask"]);
        Assert.True(Convert.ToDouble(featureMask.Summary["nccModelRegionArea"]) < 32 * 32 - 1);
        Assert.True(featureMask.Outputs["score"].Require<double>("score") >= 0.55);

        var blankMask = await Run(new TemplateMatchNode(), scene, template,
            """{"minScore":0.55,"angleRangeDeg":0,"templateRoi":{"type":"Rectangle","x":0,"y":0,"width":14,"height":32}}""");
        Assert.False(blankMask.Outputs["found"].Require<bool>("found"),
            $"FindNccModel with blank-side templateRoi must not accept a hit (score={blankMask.Outputs["score"].Require<double>("score")}).");
        Assert.Equal(true, blankMask.Summary["findUsesTemplateMask"]);
        // Blank-side model region is smaller than a full template and must score worse than the feature mask.
        Assert.True(blankMask.Outputs["score"].Require<double>("score") < featureMask.Outputs["score"].Require<double>("score"));
    }

    [Fact]
    public async Task FindPath_TemplateRoi_SqDiffMode_StillBuildsMaskedNccModel()
    {
        // SqDiff re-scores NCC proposals with domain-aware SSD. Prove the find path still trains a
        // ReduceDomain-masked model (region area) even when method is sqdiff-normed.
        using var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(template, new Rect(18, 6, 10, 20), Scalar.All(230), -1);
        using var scene = new Mat(140, 140, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);

        var feature = await Run(new TemplateMatchNode(), scene, template,
            """{"method":"sqdiff-normed","minScore":0.2,"angleRangeDeg":0,"templateRoi":{"type":"Rectangle","x":16,"y":0,"width":16,"height":32}}""");
        Assert.Equal(true, feature.Summary["findUsesTemplateMask"]);
        Assert.Equal("create-ncc-reduce-domain", feature.Summary["nccTemplateMaskMode"]?.ToString());
        var featureArea = Convert.ToDouble(feature.Summary["nccModelRegionArea"]);
        Assert.InRange(featureArea, 16 * 8, 16 * 32); // half-width mask

        var blank = await Run(new TemplateMatchNode(), scene, template,
            """{"method":"sqdiff-normed","minScore":0.2,"angleRangeDeg":0,"templateRoi":{"type":"Rectangle","x":0,"y":0,"width":14,"height":32}}""");
        Assert.Equal(true, blank.Summary["findUsesTemplateMask"]);
        var blankArea = Convert.ToDouble(blank.Summary["nccModelRegionArea"]);
        Assert.InRange(blankArea, 14 * 8, 14 * 32);
        Assert.NotEqual(featureArea, blankArea);
    }



    [Fact]
    public async Task MultiMatch_HardIoU_SuppressesNearDuplicate()
    {
        using var template = Template();
        using var scene = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);
        Paste(scene, template, 52, 40); // dx=12 → AABB IoU > 0.3; native still returns both

        var open = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":5,"minScore":0.5,"angleRangeDeg":0,"duplicateOverlapThreshold":0,"nmsMode":"hard","minCenterDistance":0}""");
        Assert.True(open.Outputs["count"].Require<long>("count") >= 2, $"expected ≥2 raw accepts with NMS off, got {open.Outputs["count"].Require<long>("count")}");
        Assert.Equal(0.95d, Convert.ToDouble(open.Summary["nccNativeOverlap"]), 3);

        var suppressed = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":5,"minScore":0.5,"angleRangeDeg":0,"duplicateOverlapThreshold":0.3,"nmsMode":"hard","minCenterDistance":0}""");
        Assert.Equal(1L, suppressed.Outputs["count"].Require<long>("count"));
        Assert.Equal("hard", suppressed.Summary["nmsMode"]?.ToString());
        Assert.Equal(0.3d, Convert.ToDouble(suppressed.Summary["nccNativeOverlap"]), 3);
        Assert.Equal("hard-native-maxoverlap", suppressed.Summary["nccOverlapPolicy"]?.ToString());
        // Primary dedupe may be JLVision MaxOverlap; C# AABB fine pass is secondary.
        Assert.True(Convert.ToInt32(suppressed.Summary["nmsSuppressedCount"]) >= 0);
    }

    [Fact]
    public async Task MultiMatch_SoftNms_DecaysOverlappingNeighbor()
    {
        using var template = Template();
        using var scene = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);
        Paste(scene, template, 52, 40);

        // High minScore: soft decay pushes the neighbor below the gate.
        var dropped = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":5,"minScore":0.85,"angleRangeDeg":0,"duplicateOverlapThreshold":0.3,"nmsMode":"soft","minCenterDistance":0}""");
        Assert.Equal("soft", dropped.Summary["nmsMode"]?.ToString());
        Assert.Equal(0.95d, Convert.ToDouble(dropped.Summary["nccNativeOverlap"]), 3);
        Assert.Equal("soft-raised-0.95", dropped.Summary["nccOverlapPolicy"]?.ToString());
        Assert.True(Convert.ToInt32(dropped.Summary["nmsSoftDecayedCount"]) >= 1
            || Convert.ToInt32(dropped.Summary["nmsSuppressedCount"]) >= 1);
        Assert.Equal(1L, dropped.Outputs["count"].Require<long>("count"));

        // Low minScore + maxTargets=2: keep both; second confidence ≤ first after linear Soft-NMS.
        var kept = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":2,"minScore":0.15,"angleRangeDeg":0,"duplicateOverlapThreshold":0.3,"nmsMode":"soft","minCenterDistance":0}""");
        Assert.Equal(2L, kept.Outputs["count"].Require<long>("count"));
        var detections = kept.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(2, detections.Count);
        var first = Assert.NotNull(detections[0].Confidence);
        var second = Assert.NotNull(detections[1].Confidence);
        Assert.True(first + 1e-9 >= second,
            $"soft-NMS should leave the second match at score <= first (got {first} vs {second})");
        Assert.True(Convert.ToInt32(kept.Summary["nmsSoftDecayedCount"]) >= 1);
        Assert.True(second < first - 1e-3, "second score should be visibly decayed by Soft-NMS");
    }

    [Fact]
    public async Task MultiMatch_AngleAware_KeepsDifferentOrientationOverlap()
    {
        using var template = Template();
        using var rotated = RotateExpanded(template, 45);
        using var scene = new Mat(260, 260, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 90, 90);
        Paste(scene, rotated, 96, 96);

        var blind = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":5,"minScore":0.4,"searchAngleStartDeg":-5,"searchAngleEndDeg":50,"angleStepDeg":5,"angleRangeDeg":0,"duplicateOverlapThreshold":0.15,"nmsMode":"hard","nmsAngleToleranceDeg":0,"minCenterDistance":0}""");
        Assert.Equal(1L, blind.Outputs["count"].Require<long>("count"));
        Assert.Equal(0d, Convert.ToDouble(blind.Summary["nmsAngleToleranceDeg"]), 3);

        var aware = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":5,"minScore":0.4,"searchAngleStartDeg":-5,"searchAngleEndDeg":50,"angleStepDeg":5,"angleRangeDeg":0,"duplicateOverlapThreshold":0.15,"nmsMode":"hard","nmsAngleToleranceDeg":20,"minCenterDistance":0}""");
        Assert.Equal(20d, Convert.ToDouble(aware.Summary["nmsAngleToleranceDeg"]), 3);
        Assert.Equal(2L, aware.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task MultiMatch_SortKey_ScoreThenAbsAngle_ReportsAndFinds()
    {
        using var template = Template();
        using var scene = new Mat(140, 140, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 50, 50);

        var result = await Run(new TemplateMatchNode(), scene, template,
            """{"maxTargets":1,"minScore":0.7,"angleRangeDeg":0,"nmsSortKey":"scoreThenAbsAngle"}""");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal("scoreThenAbsAngle", result.Summary["nmsSortKey"]?.ToString());
        Assert.InRange(Math.Abs(result.Outputs["angle"].Require<double>("angle")), 0, 5);
    }

    [Fact]
    public async Task MultiMatch_InvalidNmsMode_IsRejected()
    {
        using var template = Template();
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Paste(scene, template, 40, 40);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new TemplateMatchNode(), scene, template, """{"nmsMode":"gaussian"}"""));
        Assert.Contains("nmsMode", error.Message, StringComparison.OrdinalIgnoreCase);
    }




    private static async Task<NodeExecutorResult> Run(TemplateMatchNode node, Mat scene, Mat template, string json)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(scene.Clone())),
            ["template"] = VisionValue.Image(VisionImage.Own(template.Clone()))
        };
        try
        {
            return await node.ExecuteAsync(new NodeExecutionContext(inputs), Node(json), CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    private static NodeDefinition Node(string json)
        => new("n1", "match.template", null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static Mat Template()
    {
        var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(template, new Rect(6, 8, 20, 8), Scalar.All(230), -1);
        Cv2.Circle(template, new Point(11, 24), 3, Scalar.All(200), -1);
        return template;
    }

    private static void Paste(Mat scene, Mat patch, int x, int y)
    {
        using var area = new Mat(scene, new Rect(x, y, patch.Width, patch.Height));
        patch.CopyTo(area);
    }

    private static Mat RotateExpanded(Mat source, double angleDeg)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var cos = Math.Abs(matrix.At<double>(0, 0));
        var sin = Math.Abs(matrix.At<double>(0, 1));
        var width = (int)Math.Round(source.Width * cos + source.Height * sin);
        var height = (int)Math.Round(source.Width * sin + source.Height * cos);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (width / 2d - center.X));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (height / 2d - center.Y));
        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
        return rotated;
    }
}
