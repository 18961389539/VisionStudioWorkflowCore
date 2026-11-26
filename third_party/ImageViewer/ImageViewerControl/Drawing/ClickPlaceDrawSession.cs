using System;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 单击落点式绘制会话：一次点击即创建并提交 ROI。
    /// Chinese: 用于点标注、文本标注与外部落点（如拟合椭圆）等无需拖拽的工具。
    /// English: Click-to-place draw session: a single click creates and commits the ROI. Used by tools
    /// that need no dragging (point annotation, text annotation, external placement such as fitted
    /// ellipse).
    /// </summary>
    /// <remarks>
    /// <c>createAt</c> 返回 null 表示放弃本次绘制并留在当前模式。
    /// Chinese: 文本标注取消输入即走此路径，与既有行为一致。
    /// English: A null from <c>createAt</c> abandons the draw and stays in the current mode, matching
    /// the existing behavior when the text-annotation input is cancelled.
    /// </remarks>
    public sealed class ClickPlaceDrawSession<T> : IRoiDrawSession
        where T : RoiBase
    {
        private readonly Func<IRoiDrawHost, Point, T?> _createAt;
        private readonly bool _useSnappedPosition;
        private readonly bool _handlesEvent;

        public ClickPlaceDrawSession(
            Func<IRoiDrawHost, Point, T?> createAt,
            bool useSnappedPosition = true,
            bool handlesEvent = false)
        {
            _createAt = createAt ?? throw new ArgumentNullException(nameof(createAt));
            _useSnappedPosition = useSnappedPosition;
            _handlesEvent = handlesEvent;
        }

        /// <summary>
        /// 单击落点工具不产生"进行中"的 ROI。
        /// Chinese: 要么一次点击就提交，要么放弃，因此没有可渲染的活动图形。
        /// English: Click-to-place tools never have an in-progress ROI: a click either commits or is
        /// abandoned, so there is nothing to render while active.
        /// </summary>
        public RoiBase? ActiveRoi => null;

        public void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e)
        {
            if (e.Handled || !e.IsLeftButtonPressed)
            {
                return;
            }

            Point position = _useSnappedPosition ? host.SnapPoint(e.RawPosition) : e.RawPosition;

            T? roi = _createAt(host, position);
            if (roi == null)
            {
                return;
            }

            host.Commit(roi);
            host.EndDraw();

            if (_handlesEvent)
            {
                e.Handled = true;
            }
        }

        public void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e)
        {
        }

        public void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e)
        {
        }

        public void OnCaptureLost(IRoiDrawHost host)
        {
        }

        public void OnModeExiting(IRoiDrawHost host)
        {
        }

        public void DrawOverlay(IRoiDrawHost host, RoiRenderContext context)
        {
        }
    }
}
