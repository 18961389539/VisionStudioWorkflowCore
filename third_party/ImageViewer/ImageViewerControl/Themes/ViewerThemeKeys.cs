using System;
using System.Windows;
using System.Windows.Controls;

namespace ImageViewer.Controls
{
    /// <summary>
    /// 主题资源键。
    /// Chinese: 控件内所有颜色/样式都通过这些 ComponentResourceKey 引用，而不是把 <c>SolidColorBrush</c>
    /// 硬编码在控件自己的资源里。宿主可以在应用级（或窗口级）定义同名键来**逐键覆盖**主题，
    /// 也可以用 <see cref="ViewerThemes"/> 挂载整套明/暗/高对比字典。
    /// English: Component resource keys for every themeable brush. Hosts override individual keys by defining
    /// the same key at application or window scope, or swap whole dictionaries via <see cref="ViewerThemes"/>.
    /// </summary>
    public static class ViewerThemeKeys
    {
        /// <summary>画布背景（渐变）。</summary>
        public static ComponentResourceKey Backdrop => Make(nameof(Backdrop));

        /// <summary>浮动面板/卡片表面.</summary>
        public static ComponentResourceKey Surface => Make(nameof(Surface));

        /// <summary>次级表面（按钮、输入框底色）。</summary>
        public static ComponentResourceKey SurfaceAlt => Make(nameof(SurfaceAlt));

        /// <summary>半透明覆盖层背景。</summary>
        public static ComponentResourceKey SurfaceOverlay => Make(nameof(SurfaceOverlay));

        /// <summary>更淡的半透明覆盖层背景。</summary>
        public static ComponentResourceKey SurfaceOverlayAlt => Make(nameof(SurfaceOverlayAlt));

        /// <summary>内嵌区块（小标签底色）。</summary>
        public static ComponentResourceKey SurfaceInset => Make(nameof(SurfaceInset));

        /// <summary>面板/卡片描边。</summary>
        public static ComponentResourceKey PanelBorder => Make(nameof(PanelBorder));

        /// <summary>按钮/chip 描边。</summary>
        public static ComponentResourceKey ChipBorder => Make(nameof(ChipBorder));

        /// <summary>弹出层（子菜单、气泡）描边。</summary>
        public static ComponentResourceKey PopupBorder => Make(nameof(PopupBorder));

        /// <summary>内嵌区块描边。</summary>
        public static ComponentResourceKey InsetBorder => Make(nameof(InsetBorder));

        /// <summary>进度条底色。</summary>
        public static ComponentResourceKey ProgressTrack => Make(nameof(ProgressTrack));

        /// <summary>进度条描边。</summary>
        public static ComponentResourceKey ProgressBorder => Make(nameof(ProgressBorder));

        /// <summary>主强调色。</summary>
        public static ComponentResourceKey Accent => Make(nameof(Accent));

        /// <summary>强调色弱化填充（选中按钮底）。</summary>
        public static ComponentResourceKey AccentSoft => Make(nameof(AccentSoft));

        /// <summary>强调色描边。</summary>
        public static ComponentResourceKey AccentBorder => Make(nameof(AccentBorder));

        /// <summary>文本选择高亮。</summary>
        public static ComponentResourceKey Selection => Make(nameof(Selection));

        /// <summary>主文本。</summary>
        public static ComponentResourceKey TextPrimary => Make(nameof(TextPrimary));

        /// <summary>次级文本。</summary>
        public static ComponentResourceKey TextSecondary => Make(nameof(TextSecondary));

        /// <summary>弱化文本（快捷键提示等）。</summary>
        public static ComponentResourceKey TextMuted => Make(nameof(TextMuted));

        /// <summary>禁用文本。</summary>
        public static ComponentResourceKey TextDisabled => Make(nameof(TextDisabled));

        /// <summary>按钮悬停底色。</summary>
        public static ComponentResourceKey ButtonHover => Make(nameof(ButtonHover));

        /// <summary>按钮按下底色。</summary>
        public static ComponentResourceKey ButtonPressed => Make(nameof(ButtonPressed));

        /// <summary>列表项悬停底色。</summary>
        public static ComponentResourceKey ChipHover => Make(nameof(ChipHover));

        /// <summary>像素/吸附网格线。</summary>
        public static ComponentResourceKey GridLine => Make(nameof(GridLine));

        /// <summary>十字线。</summary>
        public static ComponentResourceKey Crosshair => Make(nameof(Crosshair));

        /// <summary>错误色。</summary>
        public static ComponentResourceKey Error => Make(nameof(Error));

        /// <summary>错误底色。</summary>
        public static ComponentResourceKey ErrorSoft => Make(nameof(ErrorSoft));

        /// <summary>错误描边。</summary>
        public static ComponentResourceKey ErrorBorder => Make(nameof(ErrorBorder));

        /// <summary>阴影颜色。</summary>
        public static ComponentResourceKey ShadowColor => Make(nameof(ShadowColor));

        /// <summary>阴影不透明度。</summary>
        public static ComponentResourceKey ShadowOpacity => Make(nameof(ShadowOpacity));

        /// <summary>列表项选中底。</summary>
        public static ComponentResourceKey ListItemSelected => Make(nameof(ListItemSelected));

        private static ComponentResourceKey Make(string id)
        {
            return new ComponentResourceKey(typeof(ViewerThemeKeys), id);
        }
    }

    /// <summary>
    /// 主题装载助手。
    /// Chinese: 宿主用它挂载整套内置主题字典，或注册自定义字典覆盖全部键。
    /// English: Helpers to mount a built-in theme dictionary (Dark / Light / HighContrast).
    /// </summary>
    public static class ViewerThemes
    {
        /// <summary>内置主题枚举。</summary>
        public enum Theme
        {
            /// <summary>深色（控件默认外观）。</summary>
            Dark,

            /// <summary>浅色。</summary>
            Light,

            /// <summary>高对比度（黑底亮黄）。</summary>
            HighContrast
        }

        private const string DarkSource = "pack://application:,,,/ImageViewerControl;component/Themes/ViewerTheme.Dark.xaml";
        private const string LightSource = "pack://application:,,,/ImageViewerControl;component/Themes/ViewerTheme.Light.xaml";
        private const string HighContrastSource = "pack://application:,,,/ImageViewerControl;component/Themes/ViewerTheme.HighContrast.xaml";

        /// <summary>判断某个字典来源是不是内置主题字典。</summary>
        /// English: True when the given source URI points at one of the built-in viewer theme dictionaries.
        public static bool IsBuiltInThemeSource(Uri source)
        {
            ArgumentNullException.ThrowIfNull(source);
            string value = source.OriginalString;
            return string.Equals(value, DarkSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LightSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, HighContrastSource, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>创建内置主题的资源字典。</summary>
        public static ResourceDictionary CreateDictionary(Theme theme)
        {
            string source = theme switch
            {
                Theme.Light => LightSource,
                Theme.HighContrast => HighContrastSource,
                _ => DarkSource
            };

            return new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) };
        }

        /// <summary>
        /// 把主题挂到某个元素（通常是 <see cref="Application"/>.Resources 或窗口）的资源字典上。
        /// Chinese: 已有同源字典会被移除，避免重复挂载导致资源膨胀。
        /// English: Removes any previously mounted viewer theme dictionary before adding the new one.
        /// </summary>
        public static void Apply(ResourceDictionary target, Theme theme)
        {
            ArgumentNullException.ThrowIfNull(target);

            Remove(target, DarkSource);
            Remove(target, LightSource);
            Remove(target, HighContrastSource);
            target.MergedDictionaries.Insert(0, CreateDictionary(theme));
        }

        private static void Remove(ResourceDictionary target, string source)
        {
            for (int index = target.MergedDictionaries.Count - 1; index >= 0; index--)
            {
                ResourceDictionary dictionary = target.MergedDictionaries[index];
                if (dictionary.Source != null &&
                    string.Equals(dictionary.Source.OriginalString, source, StringComparison.OrdinalIgnoreCase))
                {
                    target.MergedDictionaries.RemoveAt(index);
                }
            }
        }
    }
}
