using System.Windows;
using ImageViewer.Drawing;
using ImageViewer.Models;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 多步点击式测量工具的绘制行为测试。
    /// Chinese: 覆盖步进推进、未命中几何元素时拒绝启动、末步未命中时不提交、右键丢弃等既有语义。
    /// English: Behavior tests for the multi-step click tools, covering step advancement, refusing to
    /// start without a geometric hit, staying on the final step when the click misses, and right-button
    /// discard.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    public class StepClickDrawSessionTests
    {
        [Fact]
        public void AngleMeasure_ThreeClicks_CommitsVertexAndEndPoints()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.AngleMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerDown(host, LeftDown(30, 10));
            session.OnPointerDown(host, LeftDown(30, 30));

            var angle = Assert.IsType<AngleMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(10, 10), angle.P1);
            Assert.Equal(new PointD(30, 10), angle.Vertex);
            Assert.Equal(new PointD(30, 30), angle.P2);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void ArcMeasure_ThreeClicks_CommitsStartEndAndArcPoint()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.ArcMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerDown(host, LeftDown(40, 0));
            session.OnPointerDown(host, LeftDown(20, 20));

            var arc = Assert.IsType<ArcMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(0, 0), arc.StartPoint);
            Assert.Equal(new PointD(40, 0), arc.EndPoint);
            Assert.Equal(new PointD(20, 20), arc.ArcPoint);
        }

        [Fact]
        public void Concentricity_WithoutCircleHit_RefusesToStart()
        {
            var host = new FakeRoiDrawHost { HitTestResult = null };
            IRoiDrawSession session = BuiltInDrawControllers.Concentricity.CreateSession();

            session.OnPointerDown(host, LeftDown(50, 50));

            Assert.Null(session.ActiveRoi);
            Assert.Equal(0, host.CaptureCount);
            Assert.Empty(host.CommittedRois);
        }

        [Fact]
        public void Concentricity_ThreeClicksOnCircles_CommitsBothCircles()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Concentricity.CreateSession();

            host.HitTestResult = new CircleRoi { Center = new PointD(100, 100), Radius = 50 };
            session.OnPointerDown(host, LeftDown(100, 100));
            session.OnPointerDown(host, LeftDown(100, 100));

            host.HitTestResult = new CircleRoi { Center = new PointD(200, 200), Radius = 30 };
            session.OnPointerDown(host, LeftDown(200, 200));

            var roi = Assert.IsType<ConcentricityMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(100, 100), roi.Center1);
            Assert.Equal(50, roi.Radius1, 6);
            Assert.Equal(new PointD(200, 200), roi.Center2);
            Assert.Equal(30, roi.Radius2, 6);
        }

        [Fact]
        public void Parallelism_WithoutLineHit_RefusesToStart()
        {
            var host = new FakeRoiDrawHost { HitTestResult = new CircleRoi() };
            IRoiDrawSession session = BuiltInDrawControllers.Parallelism.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void Parallelism_ThreeClicksOnLines_CommitsBothLines()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Parallelism.CreateSession();

            host.HitTestResult = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(100, 0) };
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 0));

            host.HitTestResult = new LineMeasureRoi { P1 = new PointD(0, 50), P2 = new PointD(100, 50) };
            session.OnPointerDown(host, LeftDown(50, 50));

            var roi = Assert.IsType<ParallelismMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(0, 0), roi.Line1P1);
            Assert.Equal(new PointD(100, 0), roi.Line1P2);
            Assert.Equal(new PointD(0, 50), roi.Line2P1);
            Assert.Equal(new PointD(100, 50), roi.Line2P2);
        }

        [Fact]
        public void Parallelism_FinalClickWithoutHit_StaysOnStepAndDoesNotCommit()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Parallelism.CreateSession();

            host.HitTestResult = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(100, 0) };
            session.OnPointerDown(host, LeftDown(50, 0));
            session.OnPointerDown(host, LeftDown(50, 0));

            host.HitTestResult = null;
            session.OnPointerDown(host, LeftDown(50, 50));

            Assert.Empty(host.CommittedRois);
            Assert.NotNull(session.ActiveRoi);

            host.HitTestResult = new LineMeasureRoi { P1 = new PointD(0, 50), P2 = new PointD(100, 50) };
            session.OnPointerDown(host, LeftDown(50, 50));

            var roi = Assert.IsType<ParallelismMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(0, 50), roi.Line2P1);
        }

        [Fact]
        public void PointToLineDistance_SecondClickOnLine_CommitsResolvedLine()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.PointToLineDistance.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            host.HitTestResult = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(100, 0) };
            session.OnPointerDown(host, LeftDown(50, 0));

            var roi = Assert.IsType<PointToLineDistanceRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(10, 10), roi.Point);
            Assert.Equal(new PointD(0, 0), roi.LineP1);
            Assert.Equal(new PointD(100, 0), roi.LineP2);
        }

        [Fact]
        public void PointToLineDistance_UsesHitPointAnnotationAsAnchor()
        {
            var host = new FakeRoiDrawHost
            {
                HitTestResult = new PointAnnotationRoi { Position = new PointD(77, 88) }
            };
            IRoiDrawSession session = BuiltInDrawControllers.PointToLineDistance.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            var roi = Assert.IsType<PointToLineDistanceRoi>(session.ActiveRoi);
            Assert.Equal(new PointD(77, 88), roi.Point);
        }

        [Fact]
        public void StepClick_RightButton_DiscardsAndEndsDraw()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.AngleMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            DrawPointerEvent rightDown = new(new Point(20, 20), IsLeftButtonPressed: false, IsRightButtonPressed: true, ClickCount: 1);
            session.OnPointerDown(host, rightDown);

            Assert.Empty(host.CommittedRois);
            Assert.Null(session.ActiveRoi);
            Assert.Equal(1, host.EndDrawCount);
            Assert.True(rightDown.Handled);
        }

        [Fact]
        public void StepClick_MouseUpDoesNotAdvanceOrCommit()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.AngleMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnPointerUp(host, LeftUp(10, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
            Assert.NotNull(session.ActiveRoi);
        }

        [Fact]
        public void StepClick_IdleCursorReflectsSelectionAvailability()
        {
            var host = new FakeRoiDrawHost { HitTestResult = new CircleRoi() };
            IRoiDrawSession session = BuiltInDrawControllers.Concentricity.CreateSession();

            session.OnPointerMove(host, Move(10, 10));
            Assert.Equal(System.Windows.Input.Cursors.Hand, host.LastCursor);

            host.HitTestResult = null;
            session.OnPointerMove(host, Move(10, 10));
            Assert.Equal(System.Windows.Input.Cursors.Pen, host.LastCursor);
        }

        [Fact]
        public void StepClick_OnModeExiting_ClearsActiveRoi()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.AngleMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));
            session.OnModeExiting(host);

            Assert.Null(session.ActiveRoi);
        }

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent LeftUp(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent Move(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 0);
    }
}
