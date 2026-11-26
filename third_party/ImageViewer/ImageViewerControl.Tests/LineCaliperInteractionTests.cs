using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

/// <summary>
/// Chinese: 线卡尺的黄框必须能用鼠标拖动改搜索范围，且端点拖动行为保持不变。
/// English: The line caliper's search region must be draggable by mouse, without changing the
/// endpoint behaviour of the reference line.
/// </summary>
public class LineCaliperInteractionTests
{
    private static (IRoiBehavior Behavior, LineCaliperMeasureRoi Roi) CreateTarget()
    {
        IRoiBehavior behavior = RoiInteractionService.CreateBuiltInBehaviorMap()[typeof(LineCaliperMeasureRoi)];
        var roi = new LineCaliperMeasureRoi
        {
            P1 = new PointD(100, 100),
            P2 = new PointD(200, 100),
            CaliperSearchRange = 18
        };

        return (behavior, roi);
    }

    [Fact]
    public void SearchRegionHandles_SitOnBothSidesOfTheReferenceLine()
    {
        (IRoiBehavior behavior, LineCaliperMeasureRoi roi) = CreateTarget();

        Assert.Equal(ResizeHandle.BottomCenter, HandleAt(behavior, roi, new Point(150, 118)));
        Assert.Equal(ResizeHandle.TopCenter, HandleAt(behavior, roi, new Point(150, 82)));
        Assert.Equal(ResizeHandle.P1, HandleAt(behavior, roi, new Point(100, 100)));
        Assert.Equal(ResizeHandle.P2, HandleAt(behavior, roi, new Point(200, 100)));
        Assert.Equal(ResizeHandle.None, HandleAt(behavior, roi, new Point(150, 60)));
    }

    [Fact]
    public void DraggingSearchRegionHandle_SetsSymmetricRangeAndKeepsEndpoints()
    {
        (IRoiBehavior behavior, LineCaliperMeasureRoi roi) = CreateTarget();

        behavior.Resize(roi, ResizeHandle.BottomCenter, 0, 24, new Point(150, 142), minimumRoiDimension: 0.1);

        Assert.Equal(42, roi.CaliperSearchRange);
        Assert.Equal(new PointD(100, 100), roi.P1);
        Assert.Equal(new PointD(200, 100), roi.P2);

        // The opposite handle applies the same symmetric distance.
        behavior.Resize(roi, ResizeHandle.TopCenter, 0, -6, new Point(150, 94), minimumRoiDimension: 0.1);

        Assert.Equal(6, roi.CaliperSearchRange);
        Assert.Equal(new PointD(100, 100), roi.P1);
    }

    [Fact]
    public void DraggingEndpoint_OnlyMovesThatEndpoint()
    {
        (IRoiBehavior behavior, LineCaliperMeasureRoi roi) = CreateTarget();

        behavior.Resize(roi, ResizeHandle.P2, 0, 0, new Point(260, 140), minimumRoiDimension: 0.1);

        Assert.Equal(new PointD(260, 140), roi.P2);
        Assert.Equal(new PointD(100, 100), roi.P1);
        Assert.Equal(18, roi.CaliperSearchRange);
    }

    [Fact]
    public void HitTest_InsideTheSearchRegion_SelectsTheCaliper()
    {
        (IRoiBehavior behavior, LineCaliperMeasureRoi roi) = CreateTarget();

        // Ten pixels away from the reference line: outside the segment tolerance, inside the region.
        Assert.True(behavior.HitTest(roi, new Point(150, 110), scale: 1, hitTestTolerance: 2));
        Assert.False(behavior.HitTest(roi, new Point(150, 140), scale: 1, hitTestTolerance: 2));
    }

    private static ResizeHandle HandleAt(IRoiBehavior behavior, LineCaliperMeasureRoi roi, Point point) =>
        behavior.GetHandleAt(
            roi,
            point,
            scale: 1,
            handleSize: 8,
            handleHitPadding: 4,
            infoTextOffset: 20,
            polygonVertexHitPadding: 4);
}
