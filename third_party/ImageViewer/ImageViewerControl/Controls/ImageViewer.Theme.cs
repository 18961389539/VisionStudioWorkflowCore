using System;
using System.Windows;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        /// <summary>
        /// 控件主题。
        /// Chinese: 默认 <see cref="ViewerThemes.Theme.Dark"/>（历史外观）。控件内部所有颜色都是 <c>DynamicResource</c>，
        /// 所以改这个属性会即时换肤，且不影响宿主其它部分。
        /// English: Theme of the control. Every color inside is referenced via DynamicResource, so switching this value
        /// re-skins the control instantly without touching the rest of the host.
        /// </summary>
        public static readonly DependencyProperty ThemeProperty =
            DependencyProperty.Register(
                nameof(Theme),
                typeof(ViewerThemes.Theme),
                typeof(ImageViewer),
                new PropertyMetadata(ViewerThemes.Theme.Dark, OnThemeChanged));

        /// <summary>
        /// 自定义主题字典。
        /// Chinese: 非 null 时优先于 <see cref="Theme"/>，用于宿主完全自定义配色（键名需与内置主题一致）。
        /// English: When non-null, takes precedence over <see cref="Theme"/> for full host customization.
        /// </summary>
        public static readonly DependencyProperty ThemeDictionaryProperty =
            DependencyProperty.Register(
                nameof(ThemeDictionary),
                typeof(ResourceDictionary),
                typeof(ImageViewer),
                new PropertyMetadata(null, OnThemeChanged));

        /// <summary>控件主题（默认深色）。</summary>
        public ViewerThemes.Theme Theme
        {
            get => (ViewerThemes.Theme)GetValue(ThemeProperty);
            set => SetValue(ThemeProperty, value);
        }

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public ResourceDictionary? ThemeDictionary
        {
            get => (ResourceDictionary?)GetValue(ThemeDictionaryProperty);
            set => SetValue(ThemeDictionaryProperty, value);
        }

        private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer.ApplyTheme();
            }
        }

        /// <summary>
        /// 把当前 <see cref="Theme"/>/<see cref="ThemeDictionary"/> 解析出的字典装进控件资源。
        /// English: Mounts the dictionary resolved from Theme/ThemeDictionary into the control's own resources.
        /// </summary>
        private void ApplyTheme()
        {
            if (!IsInitialized)
            {
                // XAML 尚未加载：属性可能是在 InitializeComponent 之前用代码设置，或仅在 XAML 上声明。
                // bootstrap 会在 InitializeComponent 之后补一次 ApplyTheme。
                return;
            }

            ViewerThemeSwitcher.Apply(this, ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
        }
    }
}
