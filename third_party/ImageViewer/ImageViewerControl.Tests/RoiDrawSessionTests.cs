using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using ImageViewer.Controls;
using ImageViewer.Drawing;
using ImageViewer.Models;
using ImageViewer.Rendering;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 绘制会话插件化的宿主侧接线测试。
    /// English: Host-side wiring tests for the pluginized draw-session infrastructure.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    public class RoiDrawSessionTests
    {
        [Fact]
        public void StartDraw_CreatesSessionFromController()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var controller = new ProbeDrawController();

                viewer.StartDraw(controller);

                Assert.Equal(1, controller.CreateSessionCount);
            });
        }

        [Fact]
        public void ExitCurrentMode_NotifiesActiveSessionOnce()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var controller = new ProbeDrawController();

                viewer.StartDraw(controller);
                viewer.ExitCurrentMode();
                viewer.ExitCurrentMode();

                Assert.Equal(1, controller.Session.ModeExitCount);
            });
        }

        [Fact]
        public void StartDraw_WhileDrawing_ExitsPreviousSessionFirst()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var first = new ProbeDrawController();
                var second = new ProbeDrawController();

                viewer.StartDraw(first);
                viewer.StartDraw(second);

                Assert.Equal(1, first.Session.ModeExitCount);
                Assert.Equal(0, second.Session.ModeExitCount);
            });
        }

        [Fact]
        public void ExitCurrentMode_WithoutActiveSession_DoesNotThrow()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();

                viewer.ExitCurrentMode();
            });
        }

        [Fact]
        public void AllBuiltInDrawingTools_CarryADrawController()
        {
            var registry = ImageViewer.Plugins.RoiPluginRegistry.CreateBuiltIn();

            var tools = registry.GetDrawingTools().ToList();

            Assert.NotEmpty(tools);
            Assert.All(tools, tool =>
            {
                Assert.NotNull(tool.DrawController);
                Assert.NotNull(tool.Activate);
            });
        }

        [Fact]
        public void BuiltInToolActivate_StartsDrawModeAndExitRestoresCursor()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var tool = ImageViewer.Plugins.RoiPluginRegistry.CreateBuiltIn().GetDrawingTools().First();

                tool.Activate(viewer);
                Assert.Equal(tool.DrawController!.Cursor, viewer.rootGrid.Cursor);

                viewer.ExitCurrentMode();
                Assert.Equal(Cursors.Arrow, viewer.rootGrid.Cursor);
            });
        }

        /// <summary>
        /// 记录生命周期调用的探针控制器/会话。
        /// English: Probe controller/session recording lifecycle calls.
        /// </summary>
        private sealed class ProbeDrawController : IRoiDrawController
        {
            public ProbeDrawController()
            {
                Session = new ProbeDrawSession();
            }

            public int CreateSessionCount { get; private set; }

            public ProbeDrawSession Session { get; }

            public Cursor Cursor => Cursors.Cross;

            public IRoiDrawSession CreateSession()
            {
                CreateSessionCount++;
                return Session;
            }
        }

        private sealed class ProbeDrawSession : IRoiDrawSession
        {
            public int ModeExitCount { get; private set; }

            public int CaptureLostCount { get; private set; }

            public int DownCount { get; private set; }

            public RoiBase? ActiveRoi { get; set; }

            public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e) => DownCount++;

            public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
            {
            }

            public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
            {
            }

            public void OnCaptureLost(IRoiDrawHost host) => CaptureLostCount++;

            public void OnModeExiting(IRoiDrawHost host) => ModeExitCount++;

            public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
            {
            }
        }
    }
}
