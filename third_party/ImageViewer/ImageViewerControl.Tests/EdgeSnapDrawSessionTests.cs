using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Drawing;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 自动边缘吸附会话的异步交互测试。
    /// Chinese: 覆盖后台预览、最新位置合并、陈旧结果丢弃，以及点击仍以点击位置同步提交的语义。
    /// English: Covers background preview, latest-position coalescing, stale-result discard, and the
    /// pointer-down synchronous commit that keeps the click position authoritative.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    public class EdgeSnapDrawSessionTests
    {
        [Fact]
        public void PointerMove_WhenNoEdgeIsFound_ShowsCrossCursorAndRedrawsOnce()
        {
            var host = new FakeRoiDrawHost();
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(10, 10));

            Assert.Equal(1, host.SnapRequestCount);
            Assert.Same(Cursors.Cross, host.LastCursor);
            Assert.Equal(1, host.InvalidateCount);
            Assert.Empty(host.CommittedRois);
        }

        [Fact]
        public void PointerDown_ReusesPreviewForTheSamePointerPosition()
        {
            var host = new FakeRoiDrawHost
            {
                EdgeSnapFound = true,
                EdgeSnapPoint = new Point(11, 12),
                EdgeSnapScore = 42,
                EdgeSnapConfidence = 0.7
            };
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(10, 10));

            Assert.Same(Cursors.Hand, host.LastCursor);
            Assert.Equal(1, host.InvalidateCount);

            var e = LeftDown(10, 10);
            session.OnPointerDown(host, e);

            // 预览已经对应当前点击位置，点击不应再做一次同步搜索。
            Assert.Equal(0, host.CountCalls(nameof(IRoiDrawHost.TrySnapPointToEdge)));
            var roi = Assert.IsType<PointCoordinateMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(11, 12), roi.Position);
            Assert.Equal(42.0, roi.EdgeScore);
            Assert.Equal(0.7, roi.EdgeConfidence);
            Assert.True(roi.IsEdgeSnapped);
            Assert.Equal(1, host.EndDrawCount);
            Assert.True(e.Handled);
        }

        [Fact]
        public void PointerMove_WhileSearchIsInFlight_CoalescesToTheLatestPosition()
        {
            var first = new TaskCompletionSource<EdgeSnapResult>();
            var second = new TaskCompletionSource<EdgeSnapResult>();
            var host = new FakeRoiDrawHost { PostToUiThreadInline = false };
            host.SnapPointToEdgeAsyncHandler = (seed, _) =>
                host.SnapRequestCount == 1 ? first.Task : second.Task;
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(10, 10));
            session.OnPointerMove(host, Move(20, 20));

            // 同一时刻只允许一个后台搜索，第二次移动只记录最新位置。
            Assert.Equal(1, host.SnapRequestCount);

            first.SetResult(new EdgeSnapResult(true, new Point(10.5, 10.5), 20, 0.5));
            Assert.True(SpinWait.SpinUntil(() => host.PendingPostedActionCount > 0, TimeSpan.FromSeconds(5)));
            host.FlushPostedActions();

            // 指针已经移动，旧位置的结果必须丢弃并立即补算最新位置。
            Assert.Equal(2, host.SnapRequestCount);
            Assert.Equal(new Point(20, 20), host.SnapRequestPositions[1]);
            Assert.Equal(0, host.InvalidateCount);

            second.SetResult(new EdgeSnapResult(true, new Point(20.5, 20.5), 25, 0.8));
            Assert.True(SpinWait.SpinUntil(() => host.PendingPostedActionCount > 0, TimeSpan.FromSeconds(5)));
            host.FlushPostedActions();

            Assert.Same(Cursors.Hand, host.LastCursor);
            Assert.Equal(1, host.InvalidateCount);

            var e = LeftDown(20, 20);
            session.OnPointerDown(host, e);

            var roi = Assert.IsType<PointCoordinateMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(20.5, 20.5), roi.Position);
            Assert.True(e.Handled);
        }

        [Fact]
        public void PointerDown_WhileSearchIsInFlight_UsesSynchronousProbeAndCancelsThePreview()
        {
            var pending = new TaskCompletionSource<EdgeSnapResult>();
            var host = new FakeRoiDrawHost
            {
                PostToUiThreadInline = false,
                EdgeSnapFound = true,
                EdgeSnapPoint = new Point(30, 31),
                EdgeSnapScore = 33,
                EdgeSnapConfidence = 0.9
            };
            host.SnapPointToEdgeAsyncHandler = (_, _) => pending.Task;
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(30, 30));
            var e = LeftDown(30, 30);
            session.OnPointerDown(host, e);

            Assert.True(host.SnapRequestTokens[0].IsCancellationRequested);
            Assert.Equal(1, host.CountCalls(nameof(IRoiDrawHost.TrySnapPointToEdge)));
            var roi = Assert.IsType<PointCoordinateMeasureRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(30, 31), roi.Position);
            Assert.Equal(33.0, roi.EdgeScore);
            Assert.Equal(1, host.EndDrawCount);
            Assert.True(e.Handled);

            // 迟到的后台结果属于已取消的请求，不得再触发预览或第二次提交。
            pending.SetResult(new EdgeSnapResult(true, new Point(99, 99), 1, 1));
            Assert.Equal(0, host.PendingPostedActionCount);
            Assert.Single(host.CommittedRois);
            Assert.Equal(0, host.InvalidateCount);
        }

        [Fact]
        public void ModeExiting_CancelsInFlightSearchAndDiscardsLateResult()
        {
            var pending = new TaskCompletionSource<EdgeSnapResult>();
            var host = new FakeRoiDrawHost { PostToUiThreadInline = false };
            host.SnapPointToEdgeAsyncHandler = (_, _) => pending.Task;
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(5, 5));
            session.OnModeExiting(host);

            Assert.True(host.SnapRequestTokens[0].IsCancellationRequested);

            pending.SetResult(new EdgeSnapResult(true, new Point(6, 6), 10, 0.5));

            Assert.Equal(0, host.PendingPostedActionCount);
            Assert.Equal(0, host.InvalidateCount);
            Assert.Null(host.LastCursor);
        }

        [Fact]
        public void CaptureLost_ClearsPreviewImmediately()
        {
            var host = new FakeRoiDrawHost
            {
                EdgeSnapFound = true,
                EdgeSnapPoint = new Point(8, 9)
            };
            EdgeSnapDrawSession session = CreateSession();

            session.OnPointerMove(host, Move(8, 8));
            Assert.Equal(1, host.InvalidateCount);

            session.OnCaptureLost(host);

            Assert.Equal(2, host.InvalidateCount);
        }

        private static EdgeSnapDrawSession CreateSession()
            => new(static (position, score, confidence) => new PointCoordinateMeasureRoi
            {
                Position = new PointD(position.X, position.Y),
                EdgeScore = score,
                EdgeConfidence = confidence,
                IsEdgeSnapped = true
            });

        private static DrawPointerEvent Move(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: false, IsRightButtonPressed: false, ClickCount: 0);

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);
    }
}