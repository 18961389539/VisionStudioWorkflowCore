using ImageViewer.Core.Measurements;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class MeasurementStatisticsTests
{
    [Fact]
    public void FromSamples_FiltersInvalidValuesAndComputesStableStatistics()
    {
        MeasurementStatistics result = MeasurementStatistics.FromSamples([double.NaN, 12, 10, -1, 11, 0]);

        Assert.Equal([10d, 11d, 12d], result.Samples);
        Assert.Equal(3, result.SampleCount);
        Assert.Equal(10, result.Minimum);
        Assert.Equal(12, result.Maximum);
        Assert.Equal(11, result.Mean);
        Assert.Equal(11, result.Median);
        Assert.Equal(Math.Sqrt(2d / 3d), result.StandardDeviation, 10);
    }

    [Fact]
    public void EmptySamples_ReturnsSharedEmptyStatistics()
    {
        MeasurementStatistics first = MeasurementStatistics.FromSamples([]);
        MeasurementStatistics second = MeasurementStatistics.FromSamples([double.NaN, 0, -2]);

        Assert.Same(MeasurementStatistics.Empty, first);
        Assert.Same(MeasurementStatistics.Empty, second);
        Assert.False(first.IsMeasured);
        Assert.Equal(0, first.Range);
    }
}
