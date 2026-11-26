using System;
using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

/// <summary>
/// ROI 领域模型的"脱离 WPF"证明。
/// Chinese: 本测试项目只引用 ImageViewer.Core（net8.0/net10.0，无 WPF）。因此这些用例能跑通本身就证明
/// RoiBase / RoiColor 一族已经可以在无 UI 环境下被继承、实例化、克隆与断言——这是插件契约后续
/// 下沉 Core 的前置条件。
/// English: Proof that the ROI domain model no longer needs WPF. This test project references Core only
/// (net8.0/net10.0, no WPF), so these cases passing means RoiBase/RoiColor can be subclassed, instantiated and
/// asserted headlessly — the prerequisite for moving the plugin contract to Core later.
/// </summary>
public class RoiDomainModelTests
{
    [Fact]
    public void RoiBase_CanBeSubclassedAndUsedWithoutWpf()
    {
        var roi = new ProbeRoi { Label = "probe", Radius = 3.5 };
        Assert.Equal("ProbeRoi", roi.RoiTypeName);
        Assert.Equal("probe", roi.Label);
        Assert.Equal(3.5, roi.Radius);
        Assert.True(roi.IsVisible);
        Assert.Equal(RoiColors.Cyan, roi.StrokeColor);
    }

    [Fact]
    public void RoiBase_CloneAndApplyFrom_WorkHeadlessly()
    {
        var source = new ProbeRoi
        {
            Label = "source",
            Radius = 7.25,
            StrokeColor = RoiColors.Red,
            StrokeThickness = 4,
            IsVisible = false,
            IsLocked = true
        };

        ProbeRoi clone = Assert.IsType<ProbeRoi>(source.Clone());

        Assert.Equal("source", clone.Label);
        Assert.Equal(7.25, clone.Radius);
        Assert.Equal(RoiColors.Red, clone.StrokeColor);
        Assert.Equal(4, clone.StrokeThickness);
        Assert.False(clone.IsVisible);
        Assert.True(clone.IsLocked);
        Assert.False(clone.IsSelected);

        var target = new ProbeRoi();
        target.ApplyFrom(source);

        Assert.Equal(source.Label, target.Label);
        Assert.Equal(source.Radius, target.Radius);
        Assert.Equal(source.StrokeColor, target.StrokeColor);
    }

    [Fact]
    public void RoiBase_DisplayName_FallsBackToTypeNameWhenNoUiResolverIsInstalled()
    {
        var roi = new ProbeRoi { Label = "labeled" };

        Assert.Null(RoiDisplayNameResolver.Resolver);
        Assert.Equal("ProbeRoi", roi.DisplayTypeName);
        Assert.Equal("ProbeRoi: labeled", roi.DisplayName);
    }

    [Fact]
    public void RoiDisplayNameResolver_UsesInstalledResolverAndRestoresFallback()
    {
        var roi = new ProbeRoi();
        Func<RoiBase, string?> previous = RoiDisplayNameResolver.Resolver!;

        try
        {
            RoiDisplayNameResolver.Resolver = _ => "本地化名称";

            Assert.Equal("本地化名称", roi.DisplayTypeName);
            Assert.Equal("本地化名称", roi.DisplayName);

            RoiDisplayNameResolver.Resolver = null;

            Assert.Equal("ProbeRoi", roi.DisplayTypeName);
        }
        finally
        {
            RoiDisplayNameResolver.Resolver = previous;
        }
    }

    [Fact]
    public void RoiColors_ParseAndRoundTripWithoutWpf()
    {
        Assert.Equal(RoiColors.Cyan, RoiColor.Parse("Cyan")!.Value);
        Assert.Equal(new RoiColor(255, 0, 255, 255), RoiColor.Parse("#00FFFF")!.Value);
        // 8 位写法是 #AARRGGBB：A=00 时才是 "#0000FFFF"，与 6 位 "#00FFFF"（自动补 FF 不透明）不同。
        Assert.Equal(new RoiColor(0, 0, 255, 255), RoiColor.Parse("#0000FFFF")!.Value);
        Assert.Null(RoiColor.Parse("not-a-color"));
        Assert.Equal("#FF00FFFF", RoiColors.Cyan.ToString());
    }

    private sealed class ProbeRoi : RoiBase
    {
        private double _radius;

        public double Radius
        {
            get => _radius;
            set => SetProperty(ref _radius, value);
        }

        public override RoiBase Clone()
        {
            var clone = new ProbeRoi();
            clone.ApplyFrom(this);
            return clone;
        }

        public override void ApplyFrom(RoiBase source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source is not ProbeRoi probe)
            {
                throw new ArgumentException($"Expected {nameof(ProbeRoi)}.", nameof(source));
            }

            ApplyCommonState(source);
            Radius = probe.Radius;
        }
    }
}
