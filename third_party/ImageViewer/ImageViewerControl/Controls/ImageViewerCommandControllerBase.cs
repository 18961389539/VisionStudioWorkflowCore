using System;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 命令控制器公共基类：持有宿主引用并统一空参数保护。
    /// Chinese: 提供命令控制器所需的宿主引用与空参数校验，具体命令分发由子类实现。
    /// English: Base class for command controllers; stores the host reference and guards null arguments.
    /// </summary>
    internal abstract class ImageViewerCommandControllerBase<THost>
    {
        protected THost Host { get; }

        protected ImageViewerCommandControllerBase(THost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
        }
    }

    /// <summary>
    /// 菜单命令控制器基类：在命令执行后统一刷新上下文菜单状态。
    /// Chinese: 消除各菜单控制器"执行命令后调用 UpdateContextMenuState"的重复样板代码。
    /// English: Menu command controller base that refreshes context menu state after command execution.
    /// </summary>
    internal abstract class ImageViewerMenuCommandControllerBase<THost> : ImageViewerCommandControllerBase<THost>
    {
        private readonly Action _refreshMenuState;

        protected ImageViewerMenuCommandControllerBase(THost host, Action refreshMenuState)
            : base(host)
        {
            _refreshMenuState = refreshMenuState ?? throw new ArgumentNullException(nameof(refreshMenuState));
        }

        protected void RefreshMenuState()
        {
            _refreshMenuState();
        }
    }
}
