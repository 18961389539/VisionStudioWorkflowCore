using System;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Controls;
using ImageViewer.Drawing;

namespace ImageViewer.Plugins
{
    public sealed class RoiToolDescriptor
    {
        /// <summary>
        /// 以插件提供的绘制控制器声明一个绘制工具。
        /// Chinese: 激活时宿主调用 ImageViewer.StartDraw，因此新增可绘制类型无需改动主控件。
        /// English: Declares a drawing tool backed by a plugin-supplied draw controller. Activation calls
        /// ImageViewer.StartDraw, so adding a drawable type requires no change to the control.
        /// </summary>
        public RoiToolDescriptor(
            string header,
            IRoiDrawController drawController,
            int menuOrder = 0,
            Func<FrameworkElement>? createIcon = null,
            bool isMeasurement = false,
            bool isVisible = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(header);
            ArgumentNullException.ThrowIfNull(drawController);

            Header = header;
            DrawController = drawController;
            Activate = viewer => viewer.StartDraw(drawController, Header);
            MenuOrder = menuOrder;
            CreateIcon = createIcon;
            IsMeasurement = isMeasurement;
            IsVisible = isVisible;
        }

        /// <summary>
        /// 以自定义激活委托声明绘制工具。
        /// Chinese: 保留此重载以兼容既有外部插件程序集（插件程序集会被真实加载）。
        /// English: Declares a drawing tool from a custom activation delegate. Kept for compatibility
        /// with existing external plugin assemblies, which are loaded for real.
        /// </summary>
        public RoiToolDescriptor(
            string header,
            Action<Controls.ImageViewer> activate,
            int menuOrder = 0,
            Func<FrameworkElement>? createIcon = null,
            bool isMeasurement = false,
            bool isVisible = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(header);
            ArgumentNullException.ThrowIfNull(activate);

            Header = header;
            Activate = activate;
            MenuOrder = menuOrder;
            CreateIcon = createIcon;
            IsMeasurement = isMeasurement;
            IsVisible = isVisible;
        }

        public string Header { get; }

        public int MenuOrder { get; }

        public Func<FrameworkElement>? CreateIcon { get; }

        /// <summary>
        /// 激活该工具的回调。
        /// Chinese: 由绘制控制器派生，或由旧构造重载直接提供。
        /// English: Activation callback, derived from the draw controller or supplied directly by the
        /// legacy constructor overload.
        /// </summary>
        public Action<Controls.ImageViewer> Activate { get; }

        /// <summary>
        /// 该工具使用的绘制控制器；旧构造重载下为 null。
        /// Chinese: 供需要内省绘制行为的场景使用。
        /// English: The draw controller backing this tool, or null when the legacy overload was used.
        /// </summary>
        public IRoiDrawController? DrawController { get; }

        public bool IsMeasurement { get; }

        public bool IsVisible { get; }
    }
}
