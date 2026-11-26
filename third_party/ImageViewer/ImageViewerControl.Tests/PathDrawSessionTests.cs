using System.Linq;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Drawing;
using ImageViewer.Models;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 路径式绘制工具（多边形 / 折线 / 自由手绘）的行为测试。
    /// Chinese: 覆盖退出模式时提交与丢弃的差异、闭合判定、最少点数门槛与拖拽累积。
    /// English: Behavior tests for the path tools, covering the commit-vs-discard difference on mode
    /// exit, closing detection, the minimum point threshold, and drag accumulation.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    public class PathDrawSessionTests
    {
        [Fact]
        public void Polygon_BeforeFirstClick_ActiveRoiIsNull()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            Assert.Null(session.ActiveRoi);

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.NotNull(session.ActiveRoi);
        }

        [Fact]
        public void Polygon_ThreeClicksThenRightClick_CommitsClosedPolygon()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnPointerDown(host, RightDown(50, 50));

            var polygon = Assert.IsType<PolygonRoi>(Assert.Single(host.CommittedRois));
            Assert.True(polygon.IsClosed);
            Assert.Equal(3, polygon.Points.Count);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Polygon_TwoClicksThenRightClick_DiscardsBelowMinimumPoints()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, RightDown(50, 0));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Polygon_ModeExitWithEnoughPoints_Commits()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnModeExiting(host);

            var polygon = Assert.IsType<PolygonRoi>(Assert.Single(host.CommittedRois));
            Assert.True(polygon.IsClosed);
        }

        [Fact]
        public void Polygon_ClickNearFirstPoint_ClosesAndCommits()
        {
            var host = new FakeRoiDrawHost { HitTestTolerance = 5, Scale = 1 };
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnPointerDown(host, LeftDown(2, 2));

            var polygon = Assert.IsType<PolygonRoi>(Assert.Single(host.CommittedRois));
            Assert.True(polygon.IsClosed);
            Assert.Equal(3, polygon.Points.Count);
        }

        [Fact]
        public void Polygon_MoveNearFirstPoint_ShowsCloseCursor()
        {
            var host = new FakeRoiDrawHost { HitTestTolerance = 5, Scale = 1 };
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 50));

            session.OnPointerMove(host, Move(2, 2));
            Assert.Equal(Cursors.Hand, host.LastCursor);

            session.OnPointerMove(host, Move(200, 200));
            Assert.Equal(Cursors.Pen, host.LastCursor);
        }

        [Fact]
        public void Polyline_TwoClicksThenRightClick_CommitsOpenPolyline()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnPointerDown(host, RightDown(50, 50));

            var polyline = Assert.IsType<PolylineRoi>(Assert.Single(host.CommittedRois));
            Assert.False(polyline.IsFreehand);
            Assert.Equal(2, polyline.Points.Count);
        }

        [Fact]
        public void Polyline_ModeExit_DiscardsInsteadOfCommitting()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnModeExiting(host);

            Assert.Empty(host.CommittedRois);
        }

        [Fact]
        public void Polyline_DoubleClick_Finishes()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(50, 50));
            session.OnPointerDown(host, new DrawPointerEvent(new Point(50, 50), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 2));

            var polyline = Assert.IsType<PolylineRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(2, polyline.Points.Count);
        }

        [Fact]
        public void Freehand_DragAccumulatesPointsAndCommitsOnRelease()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.FreehandPolyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(5, 0));
            session.OnPointerMove(host, Move(5.5, 0));
            session.OnPointerMove(host, Move(10, 0));
            session.OnPointerUp(host, LeftUp(10, 0));

            var polyline = Assert.IsType<PolylineRoi>(Assert.Single(host.CommittedRois));
            Assert.True(polyline.IsFreehand);
            Assert.Equal(3, polyline.Points.Count);
        }

        [Fact]
        public void Freehand_RightButtonDoesNotFinish()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.FreehandPolyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(10, 0));
            session.OnPointerDown(host, RightDown(10, 0));

            Assert.Empty(host.CommittedRois);
            Assert.NotNull(session.ActiveRoi);
            Assert.Equal(0, host.EndDrawCount);
        }

        [Fact]
        public void Freehand_CaptureLost_Commits()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.FreehandPolyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(10, 0));
            session.OnCaptureLost(host);

            Assert.Single(host.CommittedRois);
        }

        [Fact]
        public void Freehand_SinglePointOnRelease_Discards()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.FreehandPolyline.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerUp(host, LeftUp(0, 0));

            Assert.Empty(host.CommittedRois);
        }

        [Fact]
        public void Path_RightButtonBeforeAnyPoint_DoesNotCommit()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            session.OnPointerDown(host, RightDown(10, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Path_DrawOverlayWithoutPoints_DrawsNothing()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Polygon.CreateSession();

            Assert.Empty(host.Calls);

            session.OnPointerMove(host, Move(10, 10));

            Assert.Equal(0, host.InvalidateCount);
        }

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent RightDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: true, ClickCount: 1);

        private static DrawPointerEvent LeftUp(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent Move(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 0);
    }
}
