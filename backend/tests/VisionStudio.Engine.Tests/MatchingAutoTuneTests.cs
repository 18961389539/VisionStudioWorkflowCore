using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 自动调参（auto-v1）验收：原生 determine 的返回值必须能直接建成可用模型，
/// 且守卫（层数预算/角度步长/锁定项/极小模板）在任何输入下都生效。
/// </summary>
public sealed class MatchingAutoTuneTests
{
    [Fact]
    public void DetermineShape_ReturnsConcreteParameters()
    {
        using var template = CircleTemplate(160);

        var result = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        Assert.InRange(result.PyramidLevels, 1, MatchingAutoTune.MaxLevelsForTemplate(160, 160));
        Assert.InRange(result.AngleStepDeg, MatchingAutoTune.MinAngleStepDeg, MatchingAutoTune.MaxAngleStepDeg);
        Assert.NotNull(result.Contrast);
        Assert.InRange(result.MinContrast, 1, 255);
        Assert.False(string.IsNullOrWhiteSpace(result.Optimization));
        Assert.Contains("num_levels", result.Raw.Keys);
        Assert.Contains("angle_step", result.Raw.Keys);
        Assert.Equal(MatchingAutoTune.Version, "auto-v1");
    }

    [Fact]
    public void DetermineShape_Recommendation_BuildsUsableModel()
    {
        using var template = CircleTemplate(160);
        var result = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        using var image = JLVisionImages.FromMat(template);
        using var model = new JlShapeModel();
        using var levels = new JlTuple(result.PyramidLevels);
        using var step = new JlTuple(result.AngleStepDeg * Math.PI / 180d);
        using var optimization = new JlTuple(result.Optimization);
        using var contrast = new JlTuple(result.Contrast ?? 30);
        using var minContrast = new JlTuple(result.MinContrast);

        model.CreateShapeModel(image, levels, -Math.PI, 2 * Math.PI, step, optimization, "use_polarity", contrast, minContrast);
        model.GetShapeModelParams(
            out double _, out double _, out double actualStep,
            out double scaleMin, out double scaleMax, out double scaleStep,
            out string actualMetric, out int actualMinContrast);
        _ = scaleMin;
        _ = scaleMax;
        _ = scaleStep;

        Assert.Equal(result.AngleStepDeg * Math.PI / 180d, actualStep, 6);
        Assert.Equal("use_polarity", actualMetric);
        Assert.Equal(result.MinContrast, actualMinContrast);
    }

    [Fact]
    public void DetermineShape_IsDeterministic()
    {
        using var template = CircleTemplate(128);

        var first = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());
        var second = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        Assert.Equal(first.PyramidLevels, second.PyramidLevels);
        Assert.Equal(first.AngleStepDeg, second.AngleStepDeg);
        Assert.Equal(first.Contrast, second.Contrast);
        Assert.Equal(first.MinContrast, second.MinContrast);
    }

    [Fact]
    public void DetermineShape_RejectsTinyTemplate()
    {
        using var template = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(120));

        var error = Assert.Throws<InvalidOperationException>(
            () => MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest()));

        Assert.Contains("at least 16x16", error.Message);
    }

    [Fact]
    public void DetermineShape_RespectsLockedMinContrast()
    {
        using var template = CircleTemplate(160);

        var result = MatchingAutoTune.DetermineShape(
            template, new MatchingAutoTuneRequest(MinContrast: 17, LockedParameters: "min_contrast"));

        Assert.Equal(17, result.MinContrast);
        Assert.Contains(result.Warnings, w => w.Code == "min-contrast-locked");
    }

    [Fact]
    public void DetermineShape_ClampsLevelsToSizeBudget()
    {
        using var template = CircleTemplate(24);
        var budget = MatchingAutoTune.MaxLevelsForTemplate(24, 24);

        var result = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        Assert.InRange(result.PyramidLevels, 1, budget);
        Assert.Equal(3, budget);
    }

    [Fact]
    public void SpeedProfile_StaysWithinBudget_AndWarns()
    {
        using var template = CircleTemplate(160);

        var result = MatchingAutoTune.DetermineShape(
            template, new MatchingAutoTuneRequest(Profile: MatchingAutoTuneProfile.Speed));

        Assert.InRange(result.PyramidLevels, 1, MatchingAutoTune.MaxLevelsForTemplate(160, 160));
        Assert.InRange(result.AngleStepDeg, MatchingAutoTune.MinAngleStepDeg, MatchingAutoTune.MaxAngleStepDeg);
    }

    [Fact]
    public void AccuracyProfile_DoesNotCoarsenStep()
    {
        using var template = CircleTemplate(160);

        var balanced = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());
        var accurate = MatchingAutoTune.DetermineShape(
            template, new MatchingAutoTuneRequest(Profile: MatchingAutoTuneProfile.Accuracy));

        Assert.True(accurate.AngleStepDeg <= balanced.AngleStepDeg + 1e-9);
        Assert.InRange(accurate.AngleStepDeg, MatchingAutoTune.MinAngleStepDeg, MatchingAutoTune.MaxAngleStepDeg);
    }

    [Fact]
    public void DetermineNcc_ReturnsLevelsAndStep()
    {
        using var template = CircleTemplate(160);

        var result = MatchingAutoTune.DetermineNcc(template, new MatchingAutoTuneRequest(AngleStartDeg: 0, AngleRangeDeg: 180));

        Assert.InRange(result.PyramidLevels, 1, MatchingAutoTune.MaxLevelsForTemplate(160, 160));
        Assert.InRange(result.AngleStepDeg, MatchingAutoTune.MinAngleStepDeg, MatchingAutoTune.MaxAngleStepDeg);
        Assert.Contains("angle_step", result.Raw.Keys);
    }

    [Fact]
    public void Inspect_ReportsModelPoints_ForStructuredTemplate()
    {
        using var template = CircleTemplate(160);

        var result = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        Assert.InRange(result.Inspect.ModelPointCount, 1, 160 * 160);
        Assert.InRange(result.Inspect.CoverageRatio, 0, 1);
        Assert.NotEqual("unavailable", result.Inspect.Quality);
    }

    [Fact]
    public void ToNodeParameters_EmitsApplyPayload()
    {
        using var template = CircleTemplate(160);
        var result = MatchingAutoTune.DetermineShape(template, new MatchingAutoTuneRequest());

        var parameters = result.ToNodeParameters();

        Assert.Equal("auto", parameters["parameterSource"]);
        Assert.Equal(result.PyramidLevels, parameters["pyramidLevels"]);
        Assert.Equal(Math.Round(result.AngleStepDeg, 3), parameters["angleStepDeg"]);
        Assert.Equal(result.MinContrast, parameters["minContrast"]);
        // contrast 只作推荐值：节点尚无该参数，应用会触发既有配方重训。
        Assert.False(parameters.ContainsKey("contrast"));
    }

    private static Mat CircleTemplate(int size)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Circle(mat, new Point(size / 2, size / 2), Math.Max(3, size / 5), Scalar.All(220), -1, LineTypes.AntiAlias);
        return mat;
    }
}
