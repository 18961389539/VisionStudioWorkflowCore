using System.Windows.Input;

namespace ImageViewer.Drawing
{
    /// <summary>
    /// 绘制控制器：描述"如何绘制某一种 ROI"的入口。
    /// Chinese: 无状态，可被插件注册表单例持有；每次开始绘制时由宿主调用 <see cref="CreateSession"/> 取得独立会话。
    /// English: Stateless entry point describing how a drawable ROI is created. Registered once per
    /// tool (see RoiToolDescriptor) and shared; the host calls <see cref="CreateSession"/> to obtain a
    /// fresh, independent session for each draw.
    /// </summary>
    public interface IRoiDrawController
    {
        /// <summary>
        /// 进入绘制模式时使用的光标。
        /// Chinese: 例如拖拽类用 Cross，测量类用 Pen。
        /// English: Cursor applied when the draw mode is entered.
        /// </summary>
        Cursor Cursor { get; }

        /// <summary>
        /// 创建一个新的绘制会话，承载本次绘制的可变状态（进行中的 ROI、步进计数等）。
        /// Chinese: 每次开始绘制都应返回新实例，控制器本身不得持有绘制状态。
        /// English: Creates a new session holding this draw's mutable state. Must return a fresh
        /// instance each time; the controller itself must stay stateless.
        /// </summary>
        IRoiDrawSession CreateSession();
    }
}
