using System;
using ImageViewer.Core.Spc;
using Xunit;

namespace ImageViewer.Core.Tests;

[Trait("Category", "Unit")]
public sealed class ProcessCapabilityTests
{
    [Fact]
    public void Calculate_ComputesMeanAndBothSigmas()
    {
        double[] samples = [9, 10, 11];

        ProcessCapability capability = ProcessCapability.Calculate(samples);

        Assert.Equal(3, capability.SampleCount);
        Assert.Equal(10d, capability.Mean, 12);
        // 移动极差 1、1 → σwithin = 1/1.128；整体 σ 为样本标准差 1。
        Assert.Equal(1 / 1.128, capability.SigmaWithin, 12);
        Assert.Equal(1d, capability.SigmaOverall, 12);
        Assert.False(capability.HasSpecificationLimits);
        Assert.Null(capability.Cp);
        Assert.Null(capability.Cpk);
        Assert.Null(capability.Pp);
        Assert.Null(capability.Ppk);
    }

    [Fact]
    public void Calculate_WithBothLimits_ComputesCpAndPp()
    {
        double[] samples = [9, 10, 11];

        ProcessCapability capability = ProcessCapability.Calculate(samples, lowerSpecificationLimit: 7, upperSpecificationLimit: 13);

        Assert.True(capability.HasSpecificationLimits);
        Assert.Equal(6d / (6 * (1 / 1.128)), capability.Cp!.Value, 10);
        Assert.Equal(6d / (6 * 1), capability.Pp!.Value, 10);
        // 数据居中时 Cpk = Cp、Ppk = Pp。
        Assert.Equal(capability.Cp!.Value, capability.Cpk!.Value, 10);
        Assert.Equal(capability.Pp!.Value, capability.Ppk!.Value, 10);
    }

    [Fact]
    public void Calculate_WithOneLimit_LeavesCpAndPpNullButComputesCpkAndPpk()
    {
        double[] samples = [9, 10, 11];

        ProcessCapability lowerOnly = ProcessCapability.Calculate(samples, lowerSpecificationLimit: 7);
        Assert.Null(lowerOnly.Cp);
        Assert.NotNull(lowerOnly.Cpk);
        Assert.Equal(3d / (3 * (1 / 1.128)), lowerOnly.Cpk!.Value, 10);
        Assert.Null(lowerOnly.Pp);
        Assert.NotNull(lowerOnly.Ppk);
        Assert.Equal(3d / (3 * 1), lowerOnly.Ppk!.Value, 10);

        ProcessCapability upperOnly = ProcessCapability.Calculate(samples, upperSpecificationLimit: 13);
        Assert.Null(upperOnly.Cp);
        Assert.Equal(lowerOnly.Cpk!.Value, upperOnly.Cpk!.Value, 12);
        Assert.Null(upperOnly.Pp);
        Assert.Equal(lowerOnly.Ppk!.Value, upperOnly.Ppk!.Value, 12);
    }

    [Fact]
    public void Calculate_WhenOffCenter_TakesTheNearerLimit()
    {
        double[] samples = [9, 10, 11];

        ProcessCapability capability = ProcessCapability.Calculate(samples, lowerSpecificationLimit: 7, upperSpecificationLimit: 12);

        // 距上限 2、距下限 3 → 取 2/(3σwithin)。
        Assert.Equal(2d / (3 * (1 / 1.128)), capability.Cpk!.Value, 10);
        Assert.Equal(2d / (3 * 1), capability.Ppk!.Value, 10);
        Assert.True(capability.Cpk < capability.Cp);
    }

    [Fact]
    public void Calculate_WithZeroSpread_ReturnsNullIndices()
    {
        double[] samples = [5, 5, 5];

        ProcessCapability capability = ProcessCapability.Calculate(samples, lowerSpecificationLimit: 4, upperSpecificationLimit: 6);

        Assert.Equal(5d, capability.Mean, 12);
        Assert.Equal(0d, capability.SigmaWithin);
        Assert.Equal(0d, capability.SigmaOverall);
        Assert.Null(capability.Cp);
        Assert.Null(capability.Cpk);
        Assert.Null(capability.Pp);
        Assert.Null(capability.Ppk);
    }

    [Fact]
    public void Calculate_SkipsNonFiniteSamples()
    {
        double[] samples = [1, double.NaN, 2, double.PositiveInfinity, 3];

        ProcessCapability capability = ProcessCapability.Calculate(samples);

        Assert.Equal(3, capability.SampleCount);
        Assert.Equal(2d, capability.Mean, 12);
    }

    [Fact]
    public void Calculate_RequiresAtLeastTwoFiniteSamples()
    {
        double[] samples = [1, double.NaN];

        Assert.Throws<ArgumentException>(() => ProcessCapability.Calculate(samples));
        Assert.Throws<ArgumentNullException>(() => ProcessCapability.Calculate(null!));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Calculate_RejectsNonFiniteLimits(double lower, double upper)
    {
        double[] samples = [1, 2, 3];

        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessCapability.Calculate(samples, lower, upper));
    }

    [Fact]
    public void Calculate_RejectsInvertedLimits()
    {
        double[] samples = [1, 2, 3];

        Assert.Throws<ArgumentException>(() => ProcessCapability.Calculate(samples, lowerSpecificationLimit: 5, upperSpecificationLimit: 5));
    }

    [Fact]
    public void Empty_HasNoSamplesOrIndices()
    {
        Assert.Equal(0, ProcessCapability.Empty.SampleCount);
        Assert.False(ProcessCapability.Empty.HasSpecificationLimits);
        Assert.Null(ProcessCapability.Empty.Cpk);
        Assert.Null(ProcessCapability.Empty.Ppk);
    }
}
