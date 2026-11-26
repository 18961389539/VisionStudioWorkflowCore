using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Display;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private string? _lastLargeImageProfileKey;

        private void OnRuntimeOptionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ImageViewerRuntimeOptions.EnableImagePyramid):
                    RebuildPyramidIfNeeded();
                    break;
                case nameof(ImageViewerRuntimeOptions.AutoSelectPyramidLevel):
                case nameof(ImageViewerRuntimeOptions.EnableTiledRendering):
                case nameof(ImageViewerRuntimeOptions.PrefetchAdjacentTiles):
                case nameof(ImageViewerRuntimeOptions.TileCacheMaximumMegabytes):
                case nameof(ImageViewerRuntimeOptions.TilePrefetchRadius):
                    UpdateRenderedImage();
                    break;
                case nameof(ImageViewerRuntimeOptions.EnableAsyncAnalysis):
                    _analysisController.HandleAsyncAnalysisChanged();
                    break;
                case nameof(ImageViewerRuntimeOptions.PauseRealtimeHistogram):
                    _analysisController.HandleRealtimeHistogramPauseChanged();
                    break;
                case nameof(ImageViewerRuntimeOptions.PauseRealtimeProfile):
                    _analysisController.HandleRealtimeProfilePauseChanged();
                    break;
                case nameof(ImageViewerRuntimeOptions.PreferShaderPseudoColor):
                case nameof(ImageViewerRuntimeOptions.AllowCpuPseudoColorFallback):
                    _analysisController.HandleRenderingOptionChanged();
                    break;
            }

            UpdateContextMenuState();
        }

        internal void UpdateRenderedImage()
        {
            if (!IsLoaded)
            {
                return;
            }

            _analysisController.UpdateRenderedImage();
        }

        internal void UpdateRenderedImageForDisplaySettings()
        {
            if (!IsLoaded)
            {
                return;
            }

            _analysisController.UpdateRenderedImageForDisplaySettings();
        }

        internal BitmapSource? GetAnalysisBitmapSource() => _analysisController.GetAnalysisBitmapSource();

        private Task PrepareAnalysisResourcesAsync(ImageSource? source)
        {
            if (source is BitmapSource bitmap)
            {
                string? profile = RuntimeOptions.ApplyLargeImageProfile(bitmap.PixelWidth, bitmap.PixelHeight);
                string profileKey = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}:{profile}";
                if (profile != null && !string.Equals(_lastLargeImageProfileKey, profileKey, System.StringComparison.Ordinal))
                {
                    ShowStatusHint(UiText.Get(profile), StatusHintKind.Info, durationMs: 4500);
                }

                _lastLargeImageProfileKey = profileKey;
            }
            else
            {
                _lastLargeImageProfileKey = null;
            }

            return _analysisController.PrepareAnalysisResourcesAsync(source);
        }

        private void RefreshAnalysisDisplays(bool force = false) => _ = BackgroundOperationObserver.ObserveAsync(_analysisController.RefreshAnalysisDisplays(force), "Refresh analysis displays");

        internal void RebuildPyramidIfNeeded() => _analysisController.RebuildPyramidIfNeeded();

        private void ClearAnalysisCaches() => _analysisController.ClearAnalysisCaches();

        internal string BuildRenderStatusSummary() => _analysisController.BuildRenderStatusSummary();

        internal void ShowSmartDisplaySuggestion(ImageViewerDialogWorkflowService dialogWorkflowService)
        {
            ArgumentNullException.ThrowIfNull(dialogWorkflowService);

            BitmapSource? bitmap = GetAnalysisBitmapSource();
            if (bitmap == null)
            {
                return;
            }

            DisplaySettingsSuggestion suggestion = DisplaySettingsSuggestionService.Suggest(bitmap);
            string pseudoColorRecommendation = suggestion.UsePseudoColor
                ? UiText.Get("SmartDisplaySuggestionPseudoColorRecommended")
                : UiText.Get("SmartDisplaySuggestionPseudoColorNotNeeded");
            string summary = UiText.Format(
                "SmartDisplaySuggestionSummary",
                suggestion.Contrast,
                suggestion.Brightness,
                pseudoColorRecommendation,
                suggestion.Reason);
            dialogWorkflowService.ShowReadOnlyText(UiText.Get("DialogSmartDisplaySuggestionTitle"), summary);
        }

        private void UpdatePseudoColorMenuState() => _analysisController.UpdatePseudoColorMenuState();

        /// <summary>
        /// 打开显示调节对话框。
        /// Chinese: 预览直接写回依赖属性（于是走的就是正常渲染路径，不存在"预览一套、应用另一套"），
        /// 取消时还原为打开前的值。"自动窗宽窗位"按钮按当前直方图的分位数给出建议，可把有效范围铺满显示。
        /// </summary>
        internal void ShowDisplayAdjustment()
        {
            DisplayAdjustment initial = DisplayAdjustment;
            var dialog = new DisplayAdjustmentDialog(initial, CreateAutoWindowLevel, value => DisplayAdjustment = value);
            if (Window.GetWindow(this) is { } owner)
            {
                dialog.Owner = owner;
            }

            DisplayAdjustment = dialog.ShowDialog() == true ? dialog.Result : initial;
        }

        /// <summary>还原显示调节（像素数据本来就没被改过）。</summary>
        internal void ResetDisplayAdjustment() => DisplayAdjustment = DisplayAdjustment.Identity;

        /// <summary>
        /// 从当前图像的强度直方图求自动窗宽窗位。
        /// Chinese: 读的是**原始**分析位图而不是显示位图——显示调节不该参与自身的取值，否则调一次就自我强化。
        /// </summary>
        private DisplayAdjustment? CreateAutoWindowLevel()
        {
            BitmapSource? bitmap = GetAnalysisBitmapSource();
            if (bitmap == null)
            {
                return null;
            }

            return DisplayTransfer.CreateAutoWindowLevel(ImageAnalysisService.CreateHistogram(bitmap, ImageViewerAnalysisCoordinator.HistogramBinCount));
        }

        private static void OnEnableGpuRenderingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer.UpdateRenderedImageForDisplaySettings();
            }
        }

        private static void OnPseudoColorPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer._analysisController.HandlePseudoColorPaletteChanged();
            }
        }

        /// <summary>
        /// 显示调节变化。
        /// Chinese: 和调色板一样只需重绘——调节参数已经在显示设置的缓存键里，不同取值天然是不同条目，
        /// 因此不必清缓存；直方图/剖面读的是原始强度图，也不受显示调节影响。
        /// 与调色板不同的是，<see cref="DisplayAdjustment"/> 会随会话保存并在打开会话时恢复，
        /// 所以它是文档状态的一部分：这里必须标记未保存，否则用户调完窗宽窗位直接关窗，改动会被静默丢弃。
        /// 加载会话时 <c>ApplySession</c> 会在设置显示参数之后再显式 MarkClean/MarkDirty，
        /// 因此这条标记不会让刚打开的会话变脏。
        /// </summary>
        private static void OnDisplayAdjustmentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer._analysisController.HandleRenderingOptionChanged();
                viewer._controlComposition?.SessionController.MarkDirty();
            }
        }

        private static void OnShowHistogramChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer._analysisController.HandleHistogramVisibilityChanged((bool)e.NewValue);
            }
        }

        private static void OnShowProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ImageViewer viewer)
            {
                viewer._analysisController.HandleProfileVisibilityChanged((bool)e.NewValue);
            }
        }
    }
}
