using System.Windows;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 绘制会话收到的指针输入。
    /// Chinese: 封装绘制过程所需的指针信息：未吸附的图像坐标、按键状态与点击次数。
    /// English: Pointer payload passed to a draw session. Decoupled from WPF event args so sessions
    /// can be unit tested. <see cref="RawPosition"/> is the unsnapped image coordinate; sessions call
    /// <see cref="IRoiDrawHost.SnapPoint"/> when they need snapping.
    /// </summary>
    /// <remarks>
    /// 必须是引用类型：<see cref="Handled"/> 由会话就地写入，若为值类型，任何中间转发帧都会静默丢弃该写入。
    /// English: Must be a reference type. <see cref="Handled"/> is written in place by the session; as a
    /// value type any intermediate forwarding frame would silently drop the write.
    /// </remarks>
    public sealed record DrawPointerEvent(
        Point RawPosition,
        bool IsLeftButtonPressed,
        bool IsRightButtonPressed,
        int ClickCount)
    {
        /// <summary>
        /// 是否已被处理；会话置为 true 表示本次事件不再向下传递。
        /// Chinese: 由会话设置，宿主会写回到底层 WPF 事件。
        /// English: Set by the session to mark the underlying event as handled.
        /// </summary>
        public bool Handled { get; set; }
    }
}
