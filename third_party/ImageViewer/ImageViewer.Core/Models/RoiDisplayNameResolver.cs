#pragma warning disable CS1591
using System;

namespace ImageViewer.Models
{
    /// <summary>
    /// ROI 显示名解析入口。
    /// Chinese: Core 只负责"取一次显示名"，具体文案由 UI 层安装——控件程序集把 <see cref="Resolver"/> 指向
    /// 它自己的本地化查找（<c>RoiDisplayNameLocalizer</c> + <c>UiText</c>）。未安装时回退到
    /// <see cref="RoiBase.RoiTypeName"/>，因此 Core 可以脱离 WPF 独立运行与测试。
    /// English: Core asks for a display name; the UI assembly installs the localized lookup. Without an installed
    /// resolver it falls back to the CLR type name, which keeps Core usable (and testable) without WPF.
    /// </summary>
    public static class RoiDisplayNameResolver
    {
        public static Func<RoiBase, string?>? Resolver { get; set; }

        public static string GetDisplayName(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return Resolver?.Invoke(roi) ?? roi.RoiTypeName;
        }
    }
}
#pragma warning restore CS1591
