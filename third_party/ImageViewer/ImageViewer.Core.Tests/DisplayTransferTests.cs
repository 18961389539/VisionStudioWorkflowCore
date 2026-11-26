using System;
using ImageViewer.Core.Display;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class DisplayTransferTests
{
    private static readonly double[] InvertedLevels = [1d, 0.75, 0.5, 0.25, 0d];

    [Fact]
    public void CreateNormalizedLut_WithIdentity_EqualsInputLevels()
    {
        double[] lut = DisplayTransfer.CreateNormalizedLut(DisplayAdjustment.Identity);

        Assert.Equal(DisplayTransfer.DefaultLutEntryCount, lut.Length);
        for (int index = 0; index < lut.Length; index++)
        {
            Assert.Equal(index / 255d, lut[index], 12);
        }
    }

    [Fact]
    public void CreateNormalizedLut_WithInvert_IsMirrored()
    {
        double[] lut = DisplayTransfer.CreateNormalizedLut(DisplayAdjustment.Identity with { Invert = true }, 5);

        Assert.Equal(InvertedLevels, lut);
    }

    [Fact]
    public void CreateNormalizedLut_RejectsTooFewEntries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayTransfer.CreateNormalizedLut(DisplayAdjustment.Identity, 1));
    }

    [Fact]
    public void CreateAutoWindowLevel_TargetsThePopulatedRange()
    {
        var histogram = new int[256];
        for (int bin = 100; bin <= 150; bin++)
        {
            histogram[bin] = 10;
        }

        DisplayAdjustment adjustment = DisplayTransfer.CreateAutoWindowLevel(histogram);

        Assert.True(adjustment.WindowWidth > 0 && adjustment.WindowWidth <= 1, $"window width {adjustment.WindowWidth}");
        Assert.InRange(adjustment.WindowLevel, 0.35, 0.65);
        Assert.Equal(0d, adjustment.Brightness);
        Assert.Equal(0d, adjustment.Contrast);
        Assert.Equal(1d, adjustment.Gamma);
        Assert.False(adjustment.Invert);
    }

    [Fact]
    public void CreateAutoWindowLevel_DegenerateRangeFallsBackToOneDisplayCode()
    {
        var histogram = new int[256];
        histogram[100] = 500;

        DisplayAdjustment adjustment = DisplayTransfer.CreateAutoWindowLevel(histogram);

        Assert.Equal(1 / 255d, adjustment.WindowWidth, 12);
        Assert.Equal(100 / 255d, adjustment.WindowLevel, 12);
    }

    [Fact]
    public void CreateAutoWindowLevel_EmptyHistogramReturnsIdentity()
    {
        Assert.Same(DisplayAdjustment.Identity, DisplayTransfer.CreateAutoWindowLevel(new int[256]));
    }

    [Fact]
    public void CreateAutoWindowLevel_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => DisplayTransfer.CreateAutoWindowLevel(new int[1]));
        Assert.Throws<ArgumentException>(() => DisplayTransfer.CreateAutoWindowLevel([1, -1, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransfer.CreateAutoWindowLevel([1, 1, 1], lowPercentile: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayTransfer.CreateAutoWindowLevel([1, 1, 1], lowPercentile: 0.9, highPercentile: 0.5));
    }
}
