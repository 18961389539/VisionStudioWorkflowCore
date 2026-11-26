using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class ProfileMeasurementToolsTests
{
    [Fact]
    public void Straightness_UsesPerpendicularLeastSquaresResidualRange()
    {
        StraightnessResult result = ProfileMeasurementTools.EvaluateStraightness(
        [new(0, 1), new(1, 0), new(2, 1)]);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(1, result.PeakToValley, 10);
        Assert.Equal(3, result.SignedResiduals.Count);
        Assert.Equal(1, result.Normal.Length, 10);
    }

    [Fact]
    public void Straightness_RejectsInsufficientAndCoincidentPoints()
    {
        Assert.False(ProfileMeasurementTools.EvaluateStraightness([new(0, 0)]).Success);
        StraightnessResult coincident = ProfileMeasurementTools.EvaluateStraightness([new(2, 3), new(2, 3)]);
        Assert.False(coincident.Success);
        Assert.False(string.IsNullOrWhiteSpace(coincident.Error));
        Assert.True(double.IsNaN(coincident.PeakToValley));
        Assert.False(ProfileMeasurementTools.EvaluateStraightness([new(2, 3), new(2, 3), new(2, 3)]).Success);
    }

    [Fact]
    public void Circularity_RecoversCircleAndRejectsCollinearPoints()
    {
        CircularityResult result = ProfileMeasurementTools.EvaluateCircularity(
        [new(3, 2), new(1, 4), new(-1, 2), new(1, 0)]);

        Assert.True(result.Success);
        Assert.Equal(1, result.Center.X, 10);
        Assert.Equal(2, result.Center.Y, 10);
        Assert.Equal(2, result.Radius, 10);
        Assert.Equal(0, result.PeakToValley, 10);
        Assert.False(ProfileMeasurementTools.EvaluateCircularity([new(0, 0), new(1, 1), new(2, 2)]).Success);
    }

    [Fact]
    public void ContourDeviation_ReportsNearestOffsetAndDirectedSign()
    {
        PointD[] reference = [new(0, 0), new(4, 0)];
        ContourDeviationResult signed = ProfileMeasurementTools.EvaluateContourDeviation(
            [new(1, 2), new(3, -1)], reference);
        ContourDeviationResult unsigned = ProfileMeasurementTools.EvaluateContourDeviation(
            [new(1, 2), new(3, -1)], reference, signedOffsets: false);

        Assert.True(signed.Success);
        Assert.Equal([2d, -1d], signed.Deviations);
        Assert.Equal([2d, 1d], unsigned.Deviations);
        Assert.Equal(3, signed.PeakToValley);
        Assert.False(ProfileMeasurementTools.EvaluateContourDeviation([new(0, 0)], [new(1, 1)]).Success);
    }

    [Fact]
    public void NormalGaps_SamplesEndpointsAndReturnsWitnessPairsAndStatistics()
    {
        GapProfileResult result = ProfileMeasurementTools.MeasureNormalGaps(
            [new(0, 0), new(4, 0)], [new(0, 3), new(4, 3)], sampleSpacing: 2);

        Assert.True(result.Success);
        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(new PointD(2, 0), result.Samples[1].SourcePoint);
        Assert.Equal(new PointD(2, 3), result.Samples[1].OpposingPoint);
        Assert.All(result.Samples, sample => Assert.Equal(3, sample.Gap, 10));
        Assert.Equal(3, result.Minimum, 10);
        Assert.Equal(3, result.Maximum, 10);
        Assert.Equal(3, result.Mean, 10);
        Assert.Equal(0, result.StandardDeviation, 10);
    }

    [Fact]
    public void NormalGaps_FailsWhenAStationCannotIntersectTheOtherPolyline()
    {
        GapProfileResult result = ProfileMeasurementTools.MeasureNormalGaps(
            [new(0, 0), new(4, 0)], [new(2, 2), new(3, 2)], 2);

        Assert.False(result.Success);
        Assert.Contains("intersection", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Samples);
        Assert.True(double.IsNaN(result.Mean));
    }

    [Fact]
    public void Pitch_ComputesConsecutiveSpacingAndRejectsDuplicateAdjacentPoints()
    {
        PitchResult result = ProfileMeasurementTools.EvaluatePitch([new(0, 0), new(3, 4), new(3, 10)]);

        Assert.True(result.Success);
        Assert.Equal([5d, 6d], result.Pitches);
        Assert.Equal(5, result.Minimum);
        Assert.Equal(6, result.Maximum);
        Assert.Equal(5.5, result.Mean);
        Assert.Equal(0.5, result.StandardDeviation);
        Assert.False(ProfileMeasurementTools.EvaluatePitch([new(1, 1), new(1, 1)]).Success);
    }

    [Fact]
    public void Tools_RejectNonFiniteInputAndInvalidSpacing()
    {
        Assert.False(ProfileMeasurementTools.EvaluateStraightness([new(0, 0), new(double.NaN, 1)]).Success);
        Assert.False(ProfileMeasurementTools.MeasureNormalGaps(
            [new(0, 0), new(1, 0)], [new(0, 1), new(1, 1)], double.NaN).Success);
    }
}
