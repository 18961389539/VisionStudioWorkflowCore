using System.Windows;
using ImageViewer.Drawing;
using ImageViewer.Models;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 拖拽式绘制会话的行为测试。
    /// Chinese: 直接驱动生产环境声明的会话（BuiltInDrawControllers），无需 WPF 事件。
    /// English: Behavior tests for the drag draw session, driving the production declarations from
    /// BuiltInDrawControllers without WPF input events.
    /// </summary>
    public class DragDrawSessionTests
    {
        [Fact]
        public void Circle_WhenDraggedAndReleased_CommitsRoiWithDraggedRadius()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerMove(host, Move(30, 10));
            session.OnPointerUp(host, LeftUp(30, 10));

            var circle = Assert.IsType<CircleRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(10, 10), circle.Center);
            Assert.Equal(20, circle.Radius, 6);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Circle_WhenDraggedBelowMinimumSize_DoesNotCommit()
        {
            var host = new FakeRoiDrawHost { MinimumDrawableSize = 5 };
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerMove(host, Move(12, 10));
            session.OnPointerUp(host, LeftUp(12, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Circle_UsesSnappedCoordinates()
        {
            var host = new FakeRoiDrawHost { SnapPointResult = new Point(0, 0) };
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(3, 7));

            var circle = Assert.IsType<CircleRoi>(session.ActiveRoi);
            Assert.Equal(new PointD(0, 0), circle.Center);
        }

        [Fact]
        public void Drag_WithRightButton_DoesNotStartDrawing()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, new DrawPointerEvent(new Point(10, 10), IsLeftButtonPressed: false, IsRightButtonPressed: true, ClickCount: 1));

            Assert.Null(session.ActiveRoi);
            Assert.Equal(0, host.CaptureCount);
        }

        [Fact]
        public void Drag_WhenCaptureFails_DoesNotCreateRoi()
        {
            var host = new FakeRoiDrawHost { CaptureResult = false };
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void Drag_SecondDownWhileDrawing_IsIgnored()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerDown(host, LeftDown(50, 50));

            Assert.Equal(1, host.CaptureCount);
            var circle = Assert.IsType<CircleRoi>(session.ActiveRoi);
            Assert.Equal(new PointD(10, 10), circle.Center);
        }

        [Fact]
        public void Drag_WhenEventAlreadyHandled_IsIgnored()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            DrawPointerEvent down = LeftDown(10, 10);
            down.Handled = true;
            session.OnPointerDown(host, down);

            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void Drag_OnCaptureLost_EndsDrawWithoutCommitting()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnCaptureLost(host);

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Drag_OnModeExiting_ClearsActiveRoi()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnModeExiting(host);

            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void LineMeasure_DragKeepsStartPointAndMovesEndPoint()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.LineMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerMove(host, Move(40, 25));
            session.OnPointerUp(host, LeftUp(40, 25));

            var line = Assert.IsType<LineMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(10, 10), line.P1);
            Assert.Equal(new PointD(40, 25), line.P2);
        }

        [Fact]
        public void LineMeasure_WhenDraggedBelowMinimumSize_DoesNotCommit()
        {
            var host = new FakeRoiDrawHost { MinimumDrawableSize = 5 };
            IRoiDrawSession session = BuiltInDrawControllers.LineMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerMove(host, Move(11, 10));
            session.OnPointerUp(host, LeftUp(11, 10));

            Assert.Empty(host.CommittedRois);
        }

        [Fact]
        public void Drag_MouseUpWithoutActiveRoi_StillEndsDraw()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerUp(host, LeftUp(10, 10));

            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Drag_MouseMoveWithoutActiveRoi_DoesNotRefresh()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Circle.CreateSession();

            session.OnPointerMove(host, Move(10, 10));

            Assert.Equal(0, host.InvalidateCount);
        }

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent LeftUp(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent Move(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 0);
    }
}
