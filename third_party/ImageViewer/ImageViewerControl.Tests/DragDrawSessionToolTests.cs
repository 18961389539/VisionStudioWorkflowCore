using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Drawing;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Rendering;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 各内置拖拽工具的绘制行为测试。
    /// Chinese: 覆盖圆环的单段拖拽 + 确认点击、斑块/卡尺的提交前检测顺序、克隆式预览等既有语义。
    /// English: Per-tool behavior tests for the built-in drag tools, covering the ring's single drag plus
    /// confirmation click, pre-commit analysis ordering, and the clone-based preview path.
    /// </summary>
    public class DragDrawSessionToolTests
    {
        [Fact]
        public void RotatedRect_Drag_SetsSizeAndCenter()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.RotatedRect.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 20));
            session.OnPointerMove(host, Move(40, 60));
            session.OnPointerUp(host, LeftUp(40, 60));

            var rect = Assert.IsType<RotatedRect>(Assert.Single(host.CommittedRois));
            Assert.Equal(30, rect.Width, 6);
            Assert.Equal(40, rect.Height, 6);
            Assert.Equal(new PointD(25, 40), rect.Center);
        }

        [Fact]
        public void Ellipse_Drag_SetsRadiiAndCenter()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ellipse.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 20));
            session.OnPointerMove(host, Move(40, 60));
            session.OnPointerUp(host, LeftUp(40, 60));

            var ellipse = Assert.IsType<EllipseRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(15, ellipse.RadiusX, 6);
            Assert.Equal(20, ellipse.RadiusY, 6);
            Assert.Equal(new PointD(25, 40), ellipse.Center);
        }

        [Fact]
        public void BlobAnalysis_RunsAnalysisBeforeCommit()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.BlobAnalysis.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 20));
            session.OnPointerMove(host, Move(40, 60));
            session.OnPointerUp(host, LeftUp(40, 60));

            Assert.Equal(1, host.CountCalls(nameof(IRoiDrawHost.TryApplyAnalysis)));
            Assert.True(host.IndexOfCall(nameof(IRoiDrawHost.TryApplyAnalysis)) < host.IndexOfCall(nameof(IRoiDrawHost.Commit)));
            Assert.IsType<BlobAnalysisRoi>(Assert.Single(host.CommittedRois));
        }

        [Fact]
        public void Ring_FirstRelease_EntersConfirmationWithoutCommitting()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(100, 0));

            // Single-drag preview already shows the final proportions: inner radius follows the outer one.
            var preview = Assert.IsType<RingRoi>(session.ActiveRoi);
            Assert.Equal(100, preview.OuterRadius, 6);
            Assert.Equal(50, preview.InnerRadius, 6);

            session.OnPointerUp(host, LeftUp(100, 0));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
            // Capture stays with the host: releasing it would raise LostMouseCapture and cancel the
            // pending ring, which is exactly the bug this assertion guards against.
            Assert.Equal(0, host.ReleaseCaptureCount);

            var ring = Assert.IsType<RingRoi>(session.ActiveRoi);
            Assert.Equal(100, ring.OuterRadius, 6);
            Assert.Equal(50, ring.InnerRadius, 6);
        }

        [Fact]
        public void Ring_FirstRelease_SurvivesHostDroppingCapture()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            // Replay the real control: rootGrid.ReleaseMouseCapture raises LostMouseCapture, which
            // the host forwards to the session as OnCaptureLost.
            host.ReleaseCaptureCallback = () => session.OnCaptureLost(host);

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(100, 0));
            session.OnPointerUp(host, LeftUp(100, 0));

            Assert.Equal(0, host.ReleaseCaptureCount);
            Assert.Equal(0, host.EndDrawCount);
            var ring = Assert.IsType<RingRoi>(session.ActiveRoi);
            Assert.Equal(50, ring.InnerRadius, 6);
        }

        [Fact]
        public void Ring_ConfirmationClick_CommitsWithHalfInnerRadius()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(100, 0));
            session.OnPointerUp(host, LeftUp(100, 0));
            session.OnPointerUp(host, LeftUp(100, 0));

            var ring = Assert.IsType<RingRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(100, ring.OuterRadius, 6);
            Assert.Equal(50, ring.InnerRadius, 6);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void Ring_IgnoresPointerWhileAwaitingConfirmation()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(100, 0));
            session.OnPointerUp(host, LeftUp(100, 0));

            // The geometry is frozen while waiting for the confirmation click.
            session.OnPointerMove(host, Move(20, 0));

            var ring = Assert.IsType<RingRoi>(session.ActiveRoi);
            Assert.Equal(50, ring.InnerRadius, 6);
        }

        [Fact]
        public void Ring_WhenOuterRadiusTooSmall_DiscardsOnRelease()
        {
            var host = new FakeRoiDrawHost { MinimumDrawableSize = 5 };
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(2, 0));
            session.OnPointerUp(host, LeftUp(2, 0));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void Ring_RightButton_CancelsDrawing()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(100, 0));
            session.OnPointerDown(host, new DrawPointerEvent(new Point(100, 0), IsLeftButtonPressed: false, IsRightButtonPressed: true, ClickCount: 1));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(1, host.EndDrawCount);
            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void CaliperMeasure_RunsAnalysisWhileDraggingBeyondMinimumLength()
        {
            var host = new FakeRoiDrawHost { MinimumLineLength = 1 };
            IRoiDrawSession session = BuiltInDrawControllers.CaliperMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(50, 0));

            Assert.Contains(nameof(IRoiDrawHost.TryApplyAnalysis), host.Calls);
        }

        [Fact]
        public void CaliperMeasure_WhenDraggedBelowMinimumLength_ClearsDetectionInsteadOfAnalyzing()
        {
            var host = new FakeRoiDrawHost { MinimumLineLength = 100 };
            IRoiDrawSession session = BuiltInDrawControllers.CaliperMeasure.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(50, 0));

            Assert.DoesNotContain(nameof(IRoiDrawHost.TryApplyAnalysis), host.Calls);
        }

        [Fact]
        public void LineCaliper_AnalyzesCloneInsteadOfTheLiveRoi()
        {
            var host = new FakeRoiDrawHost { MinimumLineLength = 1 };
            IRoiDrawSession session = BuiltInDrawControllers.LineCaliper.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(50, 0));
            session.OnPointerUp(host, LeftUp(50, 0));

            var analyzed = Assert.Single(host.AnalyzedRois);
            var committed = Assert.Single(host.CommittedRois);
            Assert.NotSame(committed, analyzed);
            Assert.Equal(new PointD(0, 0), ((LineCaliperMeasureRoi)committed).P1);
            Assert.Equal(new PointD(50, 0), ((LineCaliperMeasureRoi)committed).P2);
        }

        [Fact]
        public void CircularCaliper_RunsAnalysisOnMoveAndBeforeCommit()
        {
            var host = new FakeRoiDrawHost { MinimumLineLength = 1 };
            IRoiDrawSession session = BuiltInDrawControllers.CircularCaliper.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(30, 0));
            session.OnPointerUp(host, LeftUp(30, 0));

            Assert.Equal(2, host.CountCalls(nameof(IRoiDrawHost.TryApplyAnalysis)));
            Assert.IsType<CircularCaliperMeasureRoi>(Assert.Single(host.CommittedRois));
        }

        [Fact]
        public void ArcCaliper_CreatesArcRoi()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.ArcCaliper.CreateSession();

            session.OnPointerDown(host, LeftDown(0, 0));
            session.OnPointerMove(host, Move(30, 0));
            session.OnPointerUp(host, LeftUp(30, 0));

            Assert.IsType<ArcCaliperMeasureRoi>(Assert.Single(host.CommittedRois));
        }

        [Fact]
        public void ArrowAnnotation_DragCommitsLineWithArrowGeometry()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.ArrowAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(5, 5));
            session.OnPointerMove(host, Move(25, 5));
            session.OnPointerUp(host, LeftUp(25, 5));

            var arrow = Assert.IsType<ArrowAnnotationRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(5, 5), arrow.P1);
            Assert.Equal(new PointD(25, 5), arrow.P2);
        }

        [Fact]
        public void Ring_AwaitingConfirmation_DrawsConfirmHint()
        {
            WpfTestRunner.Run(() =>
            {
                var host = new FakeRoiDrawHost();
                IRoiDrawSession session = BuiltInDrawControllers.Ring.CreateSession();
                var overlayCanvas = new Canvas();
                var screenOverlayCanvas = new Canvas();
                RoiRenderContext context = CreateContext(overlayCanvas, screenOverlayCanvas);

                session.OnPointerDown(host, LeftDown(0, 0));
                session.OnPointerMove(host, Move(100, 0));

                // While dragging there is nothing to confirm yet.
                session.DrawOverlay(host, context);
                Assert.Empty(screenOverlayCanvas.Children);

                session.OnPointerUp(host, LeftUp(100, 0));
                session.DrawOverlay(host, context);

                var hint = Assert.Single(screenOverlayCanvas.Children.OfType<TextBlock>());
                Assert.Equal(UiText.Get("RoiDrawClickToConfirm"), hint.Text);
            });
        }

        private static RoiRenderContext CreateContext(Canvas overlayCanvas, Canvas screenOverlayCanvas)
        {
            return new RoiRenderContext(
                overlayCanvas,
                screenOverlayCanvas,
                static point => point,
                scale: 1,
                pixelSize: 1,
                physicalUnit: "px",
                showCaliperScores: false,
                handleSize: 8,
                infoTextOffset: 20,
                angleArcRadius: 20,
                pointAnnotationSize: 10,
                polygonResizeHandlePadding: 4,
                polygonCloseHighlightPadding: 6);
        }

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent LeftUp(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 1);

        private static DrawPointerEvent Move(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 0);
    }
}
