using System;
using ImageViewer.Core.Display;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class DisplayAdjustmentTests
{
    [Fact]
    public void Identity_IsIdentityAndLeavesEveryLevelUnchanged()
    {
        Assert.True(DisplayAdjustment.Identity.IsIdentity);

        for (int level = 0; level <= 255; level++)
        {
            Assert.Equal(level / 255d, DisplayAdjustment.Identity.Evaluate(level / 255d), 12);
        }
    }

    [Fact]
    public void AnyNonDefaultParameter_IsNotIdentity()
    {
        Assert.False((DisplayAdjustment.Identity with { Brightness = 0.01 }).IsIdentity);
        Assert.False((DisplayAdjustment.Identity with { Invert = true }).IsIdentity);
    }

    [Fact]
    public void Evaluate_AppliesWindowBrightnessContrastGammaThenInvert()
    {
        var adjustment = new DisplayAdjustment
        {
            WindowWidth = 0.5,
            WindowLevel = 0.5,
            Brightness = 0.1,
            Contrast = 0.5,
            Gamma = 2,
            Invert = true
        };

        // 0.4 → 窗口 0.3 → +亮度 0.4 → 对比度 0.35 → 伽马 0.35^(1/2) → 反色 1 - 0.591608。
        double expected = 1 - Math.Sqrt(0.35);
        Assert.Equal(expected, adjustment.Evaluate(0.4), 10);
    }

    [Fact]
    public void Evaluate_ClampsEachStepToTheUnitRange()
    {
        var bright = DisplayAdjustment.Identity with { Brightness = 1 };
        Assert.Equal(1d, bright.Evaluate(0.2));

        var inverted = DisplayAdjustment.Identity with { Brightness = -1 };
        Assert.Equal(0d, inverted.Evaluate(0.8));
    }

    [Fact]
    public void Evaluate_TreatsNonFiniteInputAsZero()
    {
        Assert.Equal(0d, DisplayAdjustment.Identity.Evaluate(double.NaN));
        Assert.Equal(0d, DisplayAdjustment.Identity.Evaluate(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(0, 0.5, 0, 0, 1)]
    [InlineData(double.NaN, 0.5, 0, 0, 1)]
    [InlineData(1, -0.1, 0, 0, 1)]
    [InlineData(1, 1.1, 0, 0, 1)]
    [InlineData(1, 0.5, 1.1, 0, 1)]
    [InlineData(1, 0.5, 0, -1.1, 1)]
    [InlineData(1, 0.5, 0, 0, 0)]
    [InlineData(1, 0.5, 0, 0, double.NaN)]
    public void TryValidate_RejectsOutOfDomainParameters(double width, double level, double brightness, double contrast, double gamma)
    {
        var adjustment = new DisplayAdjustment
        {
            WindowWidth = width,
            WindowLevel = level,
            Brightness = brightness,
            Contrast = contrast,
            Gamma = gamma
        };

        Assert.False(adjustment.TryValidate(out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<ArgumentOutOfRangeException>(adjustment.Validate);
    }

    [Fact]
    public void ToString_SummarizesNonDefaultParametersInvariantly()
    {
        Assert.Equal("none", DisplayAdjustment.Identity.ToString());

        string summary = (DisplayAdjustment.Identity with { WindowWidth = 0.5, Gamma = 2, Invert = true }).ToString();
        Assert.Contains("W=0.5", summary, StringComparison.Ordinal);
        Assert.Contains("Gamma=2", summary, StringComparison.Ordinal);
        Assert.Contains("invert", summary, StringComparison.Ordinal);
    }
}
