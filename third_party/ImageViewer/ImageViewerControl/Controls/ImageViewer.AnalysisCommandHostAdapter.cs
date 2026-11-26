using System;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerAnalysisCommandHostAdapter : IImageViewerAnalysisCommandHost
    {
        private readonly IImageViewerAnalysisOptions _options;
        private readonly IImageViewerAnalysisOperations _operations;

        public ImageViewerAnalysisCommandHostAdapter(
            IImageViewerAnalysisOptions options,
            IImageViewerAnalysisOperations operations)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        public bool EnableAsyncAnalysis { get => _options.EnableAsyncAnalysis; set => _options.EnableAsyncAnalysis = value; }

        public bool PauseRealtimeHistogram { get => _options.PauseRealtimeHistogram; set => _options.PauseRealtimeHistogram = value; }

        public bool PauseRealtimeProfile { get => _options.PauseRealtimeProfile; set => _options.PauseRealtimeProfile = value; }

        public bool EnableImagePyramid { get => _options.EnableImagePyramid; set => _options.EnableImagePyramid = value; }

        public bool AutoSelectPyramidLevel { get => _options.AutoSelectPyramidLevel; set => _options.AutoSelectPyramidLevel = value; }

        public bool EnableTiledRendering { get => _options.EnableTiledRendering; set => _options.EnableTiledRendering = value; }

        public bool PrefetchAdjacentTiles { get => _options.PrefetchAdjacentTiles; set => _options.PrefetchAdjacentTiles = value; }

        public int TileCacheMaximumMegabytes { get => _options.TileCacheMaximumMegabytes; set => _options.TileCacheMaximumMegabytes = value; }

        public int TilePrefetchRadius { get => _options.TilePrefetchRadius; set => _options.TilePrefetchRadius = value; }

        public bool EnableGpuRendering { get => _options.EnableGpuRendering; set => _options.EnableGpuRendering = value; }

        public bool PreferShaderPseudoColor { get => _options.PreferShaderPseudoColor; set => _options.PreferShaderPseudoColor = value; }

        public bool AllowCpuPseudoColorFallback { get => _options.AllowCpuPseudoColorFallback; set => _options.AllowCpuPseudoColorFallback = value; }

        public void UpdateRenderedImage() => _operations.UpdateRenderedImage();

        public void RefreshAnalysis() => _operations.RefreshAnalysis();

        public void ClearAnalysisCache() => _operations.ClearAnalysisCache();

        public void ResetPyramidToBaseLevel() => _operations.ResetPyramidToBaseLevel();

        public void RebuildPyramidIfNeeded() => _operations.RebuildPyramidIfNeeded();

        public void SetPseudoColorPalette(PseudoColorPalette palette) => _operations.SetPseudoColorPalette(palette);

        public void ShowSmartDisplaySuggestion() => _operations.ShowSmartDisplaySuggestion();

        public void ShowDisplayAdjustment() => _operations.ShowDisplayAdjustment();

        public void ResetDisplayAdjustment() => _operations.ResetDisplayAdjustment();

        public void ShowRenderStatus() => _operations.ShowRenderStatus();
    }

    internal sealed class ImageViewerAnalysisOptionsAdapter : IImageViewerAnalysisOptions
    {
        private readonly ImageViewer _owner;

        public ImageViewerAnalysisOptionsAdapter(ImageViewer owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public bool EnableAsyncAnalysis { get => _owner.RuntimeOptions.EnableAsyncAnalysis; set => _owner.RuntimeOptions.EnableAsyncAnalysis = value; }
        public bool PauseRealtimeHistogram { get => _owner.RuntimeOptions.PauseRealtimeHistogram; set => _owner.RuntimeOptions.PauseRealtimeHistogram = value; }
        public bool PauseRealtimeProfile { get => _owner.RuntimeOptions.PauseRealtimeProfile; set => _owner.RuntimeOptions.PauseRealtimeProfile = value; }
        public bool EnableImagePyramid { get => _owner.RuntimeOptions.EnableImagePyramid; set => _owner.RuntimeOptions.EnableImagePyramid = value; }
        public bool AutoSelectPyramidLevel { get => _owner.RuntimeOptions.AutoSelectPyramidLevel; set => _owner.RuntimeOptions.AutoSelectPyramidLevel = value; }
        public bool EnableTiledRendering { get => _owner.RuntimeOptions.EnableTiledRendering; set => _owner.RuntimeOptions.EnableTiledRendering = value; }
        public bool PrefetchAdjacentTiles { get => _owner.RuntimeOptions.PrefetchAdjacentTiles; set => _owner.RuntimeOptions.PrefetchAdjacentTiles = value; }
        public int TileCacheMaximumMegabytes { get => _owner.RuntimeOptions.TileCacheMaximumMegabytes; set => _owner.RuntimeOptions.TileCacheMaximumMegabytes = value; }
        public int TilePrefetchRadius { get => _owner.RuntimeOptions.TilePrefetchRadius; set => _owner.RuntimeOptions.TilePrefetchRadius = value; }
        public bool EnableGpuRendering { get => _owner.EnableGpuRendering; set => _owner.EnableGpuRendering = value; }
        public bool PreferShaderPseudoColor { get => _owner.RuntimeOptions.PreferShaderPseudoColor; set => _owner.RuntimeOptions.PreferShaderPseudoColor = value; }
        public bool AllowCpuPseudoColorFallback { get => _owner.RuntimeOptions.AllowCpuPseudoColorFallback; set => _owner.RuntimeOptions.AllowCpuPseudoColorFallback = value; }
    }

    internal sealed class ImageViewerAnalysisOperationsAdapter : IImageViewerAnalysisOperations
    {
        private readonly ImageViewer _owner;
        private readonly ImageViewerAnalysisCoordinator _analysisController;
        private readonly ImageViewerDialogWorkflowService _dialogWorkflowService;

        public ImageViewerAnalysisOperationsAdapter(
            ImageViewer owner,
            ImageViewerAnalysisCoordinator analysisController,
            ImageViewerDialogWorkflowService dialogWorkflowService)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _analysisController = analysisController ?? throw new ArgumentNullException(nameof(analysisController));
            _dialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
        }

        public void UpdateRenderedImage() => _owner.UpdateRenderedImage();
        public void RefreshAnalysis() => _analysisController.HandleRefreshAnalysisRequested();
        public void ClearAnalysisCache() => _analysisController.HandleClearAnalysisCacheRequested();
        public void ResetPyramidToBaseLevel()
        {
            _analysisController.ClearRenderCache();
            _owner._analysisState.ResetPyramidToBaseLevel();
        }
        public void RebuildPyramidIfNeeded() => _owner.RebuildPyramidIfNeeded();
        public void SetPseudoColorPalette(PseudoColorPalette palette) => _owner.PseudoColorPalette = palette;
        public void ShowSmartDisplaySuggestion() => _owner.ShowSmartDisplaySuggestion(_dialogWorkflowService);
        public void ShowDisplayAdjustment() => _owner.ShowDisplayAdjustment();
        public void ResetDisplayAdjustment() => _owner.ResetDisplayAdjustment();
        public void ShowRenderStatus() => _dialogWorkflowService.ShowReadOnlyText(UiText.Get("DialogRenderStatusTitle"), _owner.BuildRenderStatusSummary());
    }
}
