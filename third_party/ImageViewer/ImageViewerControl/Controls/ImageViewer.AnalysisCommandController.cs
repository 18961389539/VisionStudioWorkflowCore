using System;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerAnalysisCommandController : ImageViewerCommandControllerBase<IImageViewerAnalysisCommandHost>
    {
        public ImageViewerAnalysisCommandController(IImageViewerAnalysisCommandHost host)
            : base(host)
        {
        }

        public void Execute(ImageViewerAnalysisCommand command)
        {
            switch (command)
            {
                case ImageViewerAnalysisCommand.ToggleAsyncAnalysis:
                    Host.EnableAsyncAnalysis = !Host.EnableAsyncAnalysis;
                    break;
                case ImageViewerAnalysisCommand.TogglePauseRealtimeHistogram:
                    Host.PauseRealtimeHistogram = !Host.PauseRealtimeHistogram;
                    break;
                case ImageViewerAnalysisCommand.TogglePauseRealtimeProfile:
                    Host.PauseRealtimeProfile = !Host.PauseRealtimeProfile;
                    break;
                case ImageViewerAnalysisCommand.RefreshAnalysis:
                    Host.RefreshAnalysis();
                    break;
                case ImageViewerAnalysisCommand.ToggleImagePyramid:
                    Host.EnableImagePyramid = !Host.EnableImagePyramid;
                    break;
                case ImageViewerAnalysisCommand.ToggleAutoSelectPyramidLevel:
                    Host.AutoSelectPyramidLevel = !Host.AutoSelectPyramidLevel;
                    break;
                case ImageViewerAnalysisCommand.ToggleTiledRendering:
                    Host.EnableTiledRendering = !Host.EnableTiledRendering;
                    break;
                case ImageViewerAnalysisCommand.TogglePrefetchAdjacentTiles:
                    Host.PrefetchAdjacentTiles = !Host.PrefetchAdjacentTiles;
                    break;
                case ImageViewerAnalysisCommand.ToggleGpuRendering:
                    Host.EnableGpuRendering = !Host.EnableGpuRendering;
                    Host.UpdateRenderedImage();
                    break;
                case ImageViewerAnalysisCommand.ClearPyramidCache:
                    Host.ResetPyramidToBaseLevel();
                    Host.RebuildPyramidIfNeeded();
                    break;
                case ImageViewerAnalysisCommand.ClearAnalysisCache:
                    Host.ClearAnalysisCache();
                    break;
                case ImageViewerAnalysisCommand.TogglePreferShaderPseudoColor:
                    Host.PreferShaderPseudoColor = !Host.PreferShaderPseudoColor;
                    break;
                case ImageViewerAnalysisCommand.ToggleAllowCpuPseudoColorFallback:
                    Host.AllowCpuPseudoColorFallback = !Host.AllowCpuPseudoColorFallback;
                    break;
                case ImageViewerAnalysisCommand.SetPseudoColorPaletteNone:
                    Host.SetPseudoColorPalette(PseudoColorPalette.None);
                    break;
                case ImageViewerAnalysisCommand.SetPseudoColorPaletteHot:
                    Host.SetPseudoColorPalette(PseudoColorPalette.Hot);
                    break;
                case ImageViewerAnalysisCommand.SetPseudoColorPaletteJet:
                    Host.SetPseudoColorPalette(PseudoColorPalette.Jet);
                    break;
                case ImageViewerAnalysisCommand.SetPseudoColorPaletteViridis:
                    Host.SetPseudoColorPalette(PseudoColorPalette.Viridis);
                    break;
                case ImageViewerAnalysisCommand.ShowSmartDisplaySuggestion:
                    Host.ShowSmartDisplaySuggestion();
                    break;
                case ImageViewerAnalysisCommand.ShowDisplayAdjustment:
                    Host.ShowDisplayAdjustment();
                    break;
                case ImageViewerAnalysisCommand.ResetDisplayAdjustment:
                    Host.ResetDisplayAdjustment();
                    break;
                case ImageViewerAnalysisCommand.ShowRenderStatus:
                    Host.ShowRenderStatus();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
    }
}