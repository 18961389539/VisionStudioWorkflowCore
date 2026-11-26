using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 绘制会话：承载单次绘制的可变状态（进行中的 ROI、步进计数、拖拽起点等）。
    /// Chinese: 由 <see cref="IRoiDrawController.CreateSession"/> 创建，宿主把指针事件转发进来。
    /// English: Holds the mutable state of a single draw (in-progress ROI, step counter, drag origin).
    /// The host forwards pointer events to it and reads <see cref="ActiveRoi"/> for rendering.
    /// </summary>
    public interface IRoiDrawSession
    {
        /// <summary>
        /// 当前进行中的 ROI；没有可渲染内容时返回 null。
        /// Chinese: polygon/polyline 在尚未落点时必须返回 null，否则会出现空图形预览。
        /// English: The in-progress ROI, or null when there is nothing to render yet (e.g. a polygon
        /// or polyline with no points placed so far).
        /// </summary>
        RoiBase? ActiveRoi { get; }

        void OnPointerDown(IRoiDrawHost host, DrawPointerEvent e);

        void OnPointerMove(IRoiDrawHost host, DrawPointerEvent e);

        void OnPointerUp(IRoiDrawHost host, DrawPointerEvent e);

        /// <summary>
        /// 鼠标捕获丢失时的兜底收尾。
        /// Chinese: 自由手绘折线依赖它结束绘制；其他工具通常丢弃进行中的图形。
        /// English: Fallback finish invoked when mouse capture is lost. Freehand polyline relies on it
        /// to end the draw; other tools typically discard the in-progress shape.
        /// </summary>
        void OnCaptureLost(IRoiDrawHost host);

        /// <summary>
        /// 宿主即将退出当前绘制模式（切换工具 / Esc / 右键 / 失焦）。
        /// Chinese: 语义由会话自决——polygon/polyline 在此提交（点数足够时），圆环则丢弃。
        /// English: The host is about to leave the current draw mode. Whether to commit or discard is
        /// up to the session: polygon/polyline commit when they have enough points, while a ring
        /// discards.
        /// </summary>
        void OnModeExiting(IRoiDrawHost host);

        /// <summary>
        /// 绘制"画到一半"的预览（橡皮筋、闭合虚线、闭合高亮手柄、实时数值文本）。
        /// Chinese: 由宿主在刷新活动图层时调用，必须是纯绘制、不得触发再次刷新。
        /// English: Draws the in-progress preview. Called by the host while refreshing the active
        /// overlay layer; must be pure drawing and must not trigger another refresh.
        /// </summary>
        void DrawOverlay(IRoiDrawHost host, RoiRenderContext context);
    }
}
