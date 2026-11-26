using ImageViewer.Services;
using ImageViewer.Localization;

namespace ImageViewer.Controls
{
    internal static class ImageViewerRenderStatusFormatter
    {
        public static string Format(ImageViewerRenderStatus status)
        {
            string sourceSize = status.SourcePixelWidth.HasValue && status.SourcePixelHeight.HasValue
                ? $"{status.SourcePixelWidth.Value} x {status.SourcePixelHeight.Value}"
                : UiText.Get("RenderStatusNotAvailable");
            // "有没有在做显示处理"不能只看调色板：只有窗宽窗位/伽马时同样在跑 LUT，
            // 报成 Original 会让人以为像素没被动过。
            bool isDisplayProcessingActive = status.PseudoColorPalette != PseudoColorPalette.None ||
                !status.DisplaySettings.Adjustment.IsIdentity;
            string renderMode = status.IsShaderPseudoColorActive
                ? UiText.Get("RenderStatusModeShaderEffect")
                : (isDisplayProcessingActive ? UiText.Get("RenderStatusModeCpuFallback") : UiText.Get("RenderStatusModeOriginal"));

            string pyramidState = status.EnableImagePyramid
                ? UiText.FormatInvariant("RenderStatusPyramidOn", status.PyramidLevelCount, status.PyramidBuildDuration.TotalMilliseconds)
                : UiText.Get("RenderStatusPyramidOff");

            return UiText.FormatInvariant("RenderStatusLineImage", sourceSize) + "\n" +
                   UiText.FormatInvariant("RenderStatusLinePyramid", pyramidState) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineAutoLevel", status.AutoSelectPyramidLevel) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineTiledRendering", status.EnableTiledRendering) + "\n" +
                   UiText.FormatInvariant("RenderStatusLinePrefetchAdjacentTiles", status.PrefetchAdjacentTiles) + "\n" +
                     UiText.FormatInvariant("RenderStatusLineTileCacheLimit", status.TileCacheMaximumMegabytes) + "\n" +
                     UiText.FormatInvariant("RenderStatusLineTilePrefetchRadius", status.TilePrefetchRadius) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineFrame", status.LastRenderFrame.Left, status.LastRenderFrame.Top, status.LastRenderFrame.Width, status.LastRenderFrame.Height, status.LastRenderFrame.ScaleFactor, status.LastRenderFrame.IsTiled) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineGpuCache", status.EnableGpuRendering) + "\n" +
                   UiText.FormatInvariant("RenderStatusLinePseudoColor", status.PseudoColorPalette, renderMode) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineDisplayAdjustment", status.DisplaySettings.Adjustment) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineAsyncAnalysis", status.EnableAsyncAnalysis) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineHistogram", status.PauseRealtimeHistogram, status.LastHistogramDuration.TotalMilliseconds) + "\n" +
                   UiText.FormatInvariant("RenderStatusLineProfile", status.PauseRealtimeProfile, status.LastProfileDuration.TotalMilliseconds);
        }
    }
}