using System;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

/// <summary>
/// Chinese: 信息面板必须解释"为什么没检出"，否则用户只看到一条横跨 ROI 的占位线。
/// English: The info panel must explain a failed detection instead of silently showing only the
/// operator-drawn placeholder length.
/// </summary>
public class RoiInfoServiceTests
{
    [Fact]
    public void BuildInfo_DualEdgeCaliperNotDetected_ExplainsHowToFixIt()
    {
        var roi = new CaliperMeasureRoi
        {
            P1 = new PointD(100, 50),
            P2 = new PointD(300, 50),
            Label = "W"
        };

        string text = RoiInfoService.BuildInfo(
            roi,
            bitmap: null,
            pixelSize: 1,
            physicalUnit: "px",
            RoiPluginRegistry.CreateBuiltIn(),
            includeStatistics: false);

        Assert.Contains("未检出边缘对", text, StringComparison.Ordinal);
        Assert.Contains("跨过目标两侧边缘", text, StringComparison.Ordinal);
    }
}
