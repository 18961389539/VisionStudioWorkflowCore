using System.Windows;
using ImageViewer.Drawing;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Utils;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 单击落点式绘制工具的行为测试。
    /// Chinese: 覆盖点标注、文本标注（含取消后留驻）与外部落点（原始坐标、不吸附）的既有语义。
    /// English: Behavior tests for click-to-place tools, covering point annotation, text annotation
    /// (including staying in mode after cancel) and external placement (raw, unsnapped coordinate).
    /// </summary>
    public class ClickPlaceDrawSessionTests
    {
        [Fact]
        public void ClickPlace_ActiveRoiIsAlwaysNull()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.PointAnnotation.CreateSession();

            Assert.Null(session.ActiveRoi);

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Null(session.ActiveRoi);
        }

        [Fact]
        public void PointAnnotation_Click_CommitsWithGeneratedLabel()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.PointAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(12, 34));

            var point = Assert.IsType<PointAnnotationRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(12, 34), point.Position);
            Assert.Equal("P (12,34)", point.Label);
            Assert.Equal(1, host.EndDrawCount);
        }

        [Fact]
        public void PointAnnotation_UsesSnappedPosition()
        {
            var host = new FakeRoiDrawHost { SnapPointResult = new Point(0, 0) };
            IRoiDrawSession session = BuiltInDrawControllers.PointAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(12, 34));

            var point = Assert.IsType<PointAnnotationRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(0, 0), point.Position);
        }

        [Fact]
        public void TextAnnotation_WithText_CommitsTrimmedLabel()
        {
            var host = new FakeRoiDrawHost { TextInputResult = "  测量点 A  " };
            IRoiDrawSession session = BuiltInDrawControllers.TextAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            var text = Assert.IsType<TextAnnotationRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(10, 10), text.Position);
            Assert.Equal("测量点 A", text.Label);
        }

        [Fact]
        public void TextAnnotation_WhenCancelled_StaysInModeAndDoesNotCommit()
        {
            var host = new FakeRoiDrawHost { TextInputResult = null };
            IRoiDrawSession session = BuiltInDrawControllers.TextAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
            Assert.Contains(nameof(IRoiDrawHost.RequestTextInput), host.Calls);
        }

        [Fact]
        public void TextAnnotation_WhenWhitespaceOnly_StaysInModeAndDoesNotCommit()
        {
            var host = new FakeRoiDrawHost { TextInputResult = "   " };
            IRoiDrawSession session = BuiltInDrawControllers.TextAnnotation.CreateSession();

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
        }

        [Fact]
        public void ExternalPlacement_UsesRawPositionInsteadOfSnapped()
        {
            var host = new FakeRoiDrawHost { SnapPointResult = new Point(0, 0) };
            IRoiDrawSession session = CreateExternalPlacementSession(position => new PointAnnotationRoi { Position = position.ToPointD() });

            session.OnPointerDown(host, LeftDown(12, 34));

            var roi = Assert.IsType<PointAnnotationRoi>(Assert.Single(host.CommittedRois));
            Assert.Equal(new PointD(12, 34), roi.Position);
        }

        [Fact]
        public void ExternalPlacement_WhenFactoryReturnsNull_StaysInMode()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = CreateExternalPlacementSession(_ => null);

            session.OnPointerDown(host, LeftDown(10, 10));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
        }

        [Fact]
        public void ExternalPlacement_MarksEventHandled()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = CreateExternalPlacementSession(position => new PointAnnotationRoi { Position = position.ToPointD() });

            DrawPointerEvent down = LeftDown(10, 10);
            session.OnPointerDown(host, down);

            Assert.True(down.Handled);
        }

        [Fact]
        public void ClickPlace_RightButton_DoesNothing()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.PointAnnotation.CreateSession();

            session.OnPointerDown(host, new DrawPointerEvent(new Point(10, 10), IsLeftButtonPressed: false, IsRightButtonPressed: true, ClickCount: 1));

            Assert.Empty(host.CommittedRois);
            Assert.Equal(0, host.EndDrawCount);
        }

        [Fact]
        public void ClickPlace_HandledEvent_IsIgnored()
        {
            var host = new FakeRoiDrawHost();
            IRoiDrawSession session = BuiltInDrawControllers.PointAnnotation.CreateSession();

            DrawPointerEvent down = LeftDown(10, 10);
            down.Handled = true;
            session.OnPointerDown(host, down);

            Assert.Empty(host.CommittedRois);
        }

        private static IRoiDrawSession CreateExternalPlacementSession(System.Func<Point, RoiBase?> createRoi)
        {
            return new ClickPlaceDrawSession<RoiBase>(
                (_, position) => createRoi(position),
                useSnappedPosition: false,
                handlesEvent: true);
        }

        private static DrawPointerEvent LeftDown(double x, double y)
            => new(new Point(x, y), IsLeftButtonPressed: true, IsRightButtonPressed: false, ClickCount: 1);
    }
}
