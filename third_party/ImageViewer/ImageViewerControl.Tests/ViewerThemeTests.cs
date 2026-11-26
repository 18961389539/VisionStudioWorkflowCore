using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Controls;
using Xunit;
using ImageViewerControl2D = ImageViewer.Controls.ImageViewer;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 主题可替换性守卫测试。
    /// Chinese: 这三组断言分别守住：① 控件自给自足（无宿主/无 Application 也能解析所有主题键）
    /// ② 切换内置主题真的改变了控件解析到的画刷颜色 ③ 宿主提供自定义字典能整体接管配色。
    /// English: Guards that the control renders standalone, that switching built-in themes actually changes resolved
    /// brushes, and that a host-supplied dictionary takes over.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Wpf")]
    public class ViewerThemeTests
    {
        private const string BackdropSolidKey = "ViewerBackdropSolidBrush";
        private const string AccentKey = "ViewerAccentBrush";
        private const string SurfaceKey = "ViewerSurfaceBrush";

        [Fact]
        public void Control_ResolvesEveryThemeBrush_WithoutAnApplication()
        {
            // 测试宿主没有 Application，控件必须靠 XAML 里合并的内置主题字典自给自足。
            Assert.Null(Application.Current);

            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();

                AssertBrushResolvable(viewer, BackdropSolidKey);
                AssertBrushResolvable(viewer, AccentKey);
                AssertBrushResolvable(viewer, SurfaceKey);
                AssertBrushResolvable(viewer, "ViewerTextPrimaryBrush");
                AssertBrushResolvable(viewer, "ViewerErrorBrush");
                AssertBrushResolvable(viewer, "ViewerGridLineBrush");
            });
        }

        [Fact]
        public void SwitchingBuiltInTheme_ChangesResolvedBrushColors()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();

                Color darkAccent = ResolveColor(viewer, AccentKey);

                viewer.Theme = ViewerThemes.Theme.Light;
                Color lightAccent = ResolveColor(viewer, AccentKey);

                viewer.Theme = ViewerThemes.Theme.HighContrast;
                Color highContrastAccent = ResolveColor(viewer, AccentKey);

                Assert.NotEqual(darkAccent, lightAccent);
                Assert.NotEqual(lightAccent, highContrastAccent);
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0x00), highContrastAccent);
            });
        }

        [Fact]
        public void SwitchingTheme_ReplacesTheDictionaryInsteadOfAccumulating()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();
                int initial = CountBuiltInThemeDictionaries(viewer);

                viewer.Theme = ViewerThemes.Theme.Light;
                viewer.Theme = ViewerThemes.Theme.HighContrast;
                viewer.Theme = ViewerThemes.Theme.Dark;

                Assert.Equal(initial, CountBuiltInThemeDictionaries(viewer));
            });
        }

        [Fact]
        public void HostSuppliedThemeDictionary_TakesOverCompletely()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();
                var hostDictionary = new ResourceDictionary
                {
                    [BackdropSolidKey] = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)),
                    [AccentKey] = new SolidColorBrush(Color.FromRgb(0xAB, 0xCD, 0xEF)),
                };

                viewer.ThemeDictionary = hostDictionary;

                Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), ResolveColor(viewer, BackdropSolidKey));
                Assert.Equal(Color.FromRgb(0xAB, 0xCD, 0xEF), ResolveColor(viewer, AccentKey));
            });
        }

        [Fact]
        public void ReplacingHostDictionary_Twice_KeepsTheLatestDictionaryWinning()
        {
            // 第一次替换后字典不再有内置 Source，靠 Source 识别会失败；必须靠"记录当前安装的字典"原位替换，
            // 否则新字典被插到索引 0，旧字典仍在前面的索引上继续生效。
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();
                var first = new ResourceDictionary { [AccentKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)) };
                var second = new ResourceDictionary { [AccentKey] = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)) };

                viewer.ThemeDictionary = first;
                Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), ResolveColor(viewer, AccentKey));

                viewer.ThemeDictionary = second;
                Assert.Equal(Color.FromRgb(0x22, 0x22, 0x22), ResolveColor(viewer, AccentKey));

                Assert.Equal(1, CountThemeDictionaries(viewer));
            });
        }

        [Fact]
        public void ClearingHostDictionary_RestoresBuiltInDefault()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();
                Color builtInAccent = ResolveColor(viewer, AccentKey);

                viewer.ThemeDictionary = new ResourceDictionary { [AccentKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)) };
                Assert.NotEqual(builtInAccent, ResolveColor(viewer, AccentKey));

                // ThemeDictionary = null 表示"改用 Theme 属性"，此时应回到内置默认外观。
                viewer.ThemeDictionary = null;

                Assert.Equal(builtInAccent, ResolveColor(viewer, AccentKey));
                Assert.Equal(1, CountThemeDictionaries(viewer));
            });
        }

        [Fact]
        public void SwitchingFromHostDictionaryToBuiltInTheme_ReplacesInsteadOfLayering()
        {
            WpfTestRunner.Run(() =>
            {
                var viewer = new ImageViewerControl2D();
                viewer.Theme = ViewerThemes.Theme.HighContrast;
                viewer.ThemeDictionary = new ResourceDictionary { [AccentKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)) };
                Assert.Equal(Color.FromRgb(0x11, 0x11, 0x11), ResolveColor(viewer, AccentKey));

                // 回到内置主题：高对比黄必须生效（旧自定义字典不能继续遮蔽它）。
                viewer.ThemeDictionary = null;

                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0x00), ResolveColor(viewer, AccentKey));
            });
        }

        [Fact]
        public void AdaptiveViewer_PropagatesThemeToNestedViewers()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer();

                viewer.Theme = ViewerThemes.Theme.Light;

                Assert.Equal(ViewerThemes.Theme.Light, viewer.Theme);
                var nested = Assert.IsType<ImageViewerControl2D>(viewer.ActiveView);
                Assert.Equal(ViewerThemes.Theme.Light, nested.Theme);
            });
        }

        [Fact]
        public void Volume3DViewer_ExposesThemeAndResolvesBrushes()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();

                AssertBrushResolvable(viewer, SurfaceKey);
                AssertBrushResolvable(viewer, "ViewerViewportBackgroundColor");

                viewer.Theme = ViewerThemes.Theme.HighContrast;
                Assert.Equal(Color.FromRgb(0x00, 0x00, 0x00), ResolveColor(viewer, "ViewerViewportBackgroundColor"));
            });
        }

        [Fact]
        public void AdaptiveViewer_ThemeDictionary_AppliesToItsOwnResources()
        {
            // 主题字典此前挂在子级 Grid.Resources 上，而切换代码改的是 UserControl.Resources，
            // 子级资源优先 → 切了也不生效。这里断言自适应控件自身解析到的画刷真的随主题变化。
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer();
                Color darkBackdrop = ResolveColor(viewer, BackdropSolidKey);

                viewer.Theme = ViewerThemes.Theme.Light;

                Assert.NotEqual(darkBackdrop, ResolveColor(viewer, BackdropSolidKey));
            });
        }

        [Fact]
        public void Volume3DViewer_Theme_AppliesToItsOwnResources()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new Volume3DViewer();
                Color darkBackdrop = ResolveColor(viewer, BackdropSolidKey);

                viewer.Theme = ViewerThemes.Theme.Light;

                Assert.NotEqual(darkBackdrop, ResolveColor(viewer, BackdropSolidKey));
            });
        }

        [Fact]
        public void VolumeViewer_ThemePropagatesToNestedSliceViewer()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new VolumeViewer();
                viewer.Theme = ViewerThemes.Theme.Light;

                Assert.Equal(ViewerThemes.Theme.Light, viewer.Theme);
                Assert.Equal(ViewerThemes.Theme.Light, viewer.SliceViewer.Theme);
            });
        }

        [Fact]
        public void AdaptiveViewer_ThemePropagatesToAxialViewerAndItsSliceViewer()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new AdaptiveImageViewer();
                viewer.Theme = ViewerThemes.Theme.Light;

                Assert.Equal(ViewerThemes.Theme.Light, viewer.VolumeViewer.Theme);
                Assert.Equal(ViewerThemes.Theme.Light, viewer.VolumeViewer.SliceViewer.Theme);
            });
        }

        [Fact]
        public void ThemeDictionary_ContainsEveryKeyUsedByControls()
        {
            // pack URI 只在运行时解析：静态编译不会发现键名/路径写错。这里逐个加载三个内置字典并核对键集。
            string[] requiredKeys =
            [
                "ViewerBackdropBrush", "ViewerBackdropSolidBrush", "ViewerSurfaceBrush", "ViewerSurfaceAltBrush",
                "ViewerSurfaceOverlayBrush", "ViewerSurfaceOverlayAltBrush", "ViewerSurfaceInsetBrush",
                "ViewerSurfaceOverlayMenuBrush", "ViewerMenuPopupBrush", "ViewerPanelBorderBrush", "ViewerChipBorderBrush",
                "ViewerMenuBorderBrush", "ViewerPopupBorderBrush", "ViewerInsetBorderBrush", "ViewerOverlayBorderBrush",
                "ViewerProgressTrackBrush", "ViewerProgressBorderBrush", "ViewerAccentBrush", "ViewerAccentSoftBrush",
                "ViewerAccentBorderBrush", "ViewerSelectionBrush", "ViewerTextPrimaryBrush", "ViewerTextSecondaryBrush",
                "ViewerTextMutedBrush", "ViewerTextDisabledBrush", "ViewerButtonHoverBrush", "ViewerButtonPressedBrush",
                "ViewerChipHoverBrush", "ViewerGridLineBrush", "ViewerCrosshairBrush", "ViewerSnapGridBrush",
                "ViewerErrorBrush", "ViewerErrorSoftBrush", "ViewerErrorBorderBrush", "ViewerWarningBrush",
                "ViewerInfoBrush", "ViewerShadowColor", "ViewerShadowOpacity", "ViewerListItemHoverBrush",
                "ViewerListItemSelectedBrush", "ViewerViewportBackgroundColor"
            ];

            foreach (ViewerThemes.Theme theme in new[]
                     {
                         ViewerThemes.Theme.Dark, ViewerThemes.Theme.Light, ViewerThemes.Theme.HighContrast
                     })
            {
                ResourceDictionary dictionary = ViewerThemes.CreateDictionary(theme);
                foreach (string key in requiredKeys)
                {
                    Assert.True(dictionary.Contains(key), $"主题 {theme} 缺少键 '{key}'。");
                }
            }
        }

        private static void AssertBrushResolvable(FrameworkElement element, string key)
        {
            object? value = element.TryFindResource(key);
            Assert.NotNull(value);
        }

        private static Color ResolveColor(FrameworkElement element, string key)
        {
            object? value = element.TryFindResource(key);
            return value switch
            {
                SolidColorBrush brush => brush.Color,
                Color color => color,
                _ => throw new InvalidOperationException($"Resource '{key}' is not a color: {value?.GetType().Name ?? "null"}")
            };
        }

        private static int CountBuiltInThemeDictionaries(FrameworkElement element)
        {
            return element.Resources.MergedDictionaries
                .Count(dictionary => dictionary.Source is not null && ViewerThemes.IsBuiltInThemeSource(dictionary.Source));
        }

        /// <summary>控件自身资源里挂着的主题字典总数（内置 + 宿主自定义）。</summary>
        private static int CountThemeDictionaries(FrameworkElement element)
        {
            return element.Resources.MergedDictionaries.Count;
        }
    }
}
