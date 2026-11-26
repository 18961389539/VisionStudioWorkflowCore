using System;

namespace ImageViewer.Controls
{
    internal interface IImageViewerMenuCommandTag<TCommand>
        where TCommand : struct, Enum
    {
        TCommand Command { get; }
    }

    public abstract class ImageViewerMenuCommandTag
    {
    }

    public sealed class ImageViewerViewMenuCommandTag : ImageViewerMenuCommandTag, IImageViewerMenuCommandTag<ImageViewerViewCommand>
    {
        private readonly ImageViewerViewCommand _command;

        internal ImageViewerViewMenuCommandTag(ImageViewerViewCommand command)
        {
            _command = command;
        }

        ImageViewerViewCommand IImageViewerMenuCommandTag<ImageViewerViewCommand>.Command => _command;
    }

    public sealed class ImageViewerAnalysisMenuCommandTag : ImageViewerMenuCommandTag, IImageViewerMenuCommandTag<ImageViewerAnalysisCommand>
    {
        private readonly ImageViewerAnalysisCommand _command;

        internal ImageViewerAnalysisMenuCommandTag(ImageViewerAnalysisCommand command)
        {
            _command = command;
        }

        ImageViewerAnalysisCommand IImageViewerMenuCommandTag<ImageViewerAnalysisCommand>.Command => _command;
    }

    public sealed class ImageViewerRoiMenuCommandTag : ImageViewerMenuCommandTag, IImageViewerMenuCommandTag<ImageViewerRoiMenuCommand>
    {
        private readonly ImageViewerRoiMenuCommand _command;

        internal ImageViewerRoiMenuCommandTag(ImageViewerRoiMenuCommand command)
        {
            _command = command;
        }

        ImageViewerRoiMenuCommand IImageViewerMenuCommandTag<ImageViewerRoiMenuCommand>.Command => _command;
    }

    public sealed class ImageViewerFileMenuCommandTag : ImageViewerMenuCommandTag, IImageViewerMenuCommandTag<ImageViewerFileMenuCommand>
    {
        private readonly ImageViewerFileMenuCommand _command;

        internal ImageViewerFileMenuCommandTag(ImageViewerFileMenuCommand command)
        {
            _command = command;
        }

        ImageViewerFileMenuCommand IImageViewerMenuCommandTag<ImageViewerFileMenuCommand>.Command => _command;
    }

    public sealed class ImageViewerFeatureMenuCommandTag : ImageViewerMenuCommandTag, IImageViewerMenuCommandTag<ImageViewerFeatureMenuCommand>
    {
        private readonly ImageViewerFeatureMenuCommand _command;

        internal ImageViewerFeatureMenuCommandTag(ImageViewerFeatureMenuCommand command)
        {
            _command = command;
        }

        ImageViewerFeatureMenuCommand IImageViewerMenuCommandTag<ImageViewerFeatureMenuCommand>.Command => _command;
    }

    public static class ImageViewerViewMenuTags
    {
        public static ImageViewerViewMenuCommandTag TogglePixelGrid { get; } = new(ImageViewerViewCommand.TogglePixelGrid);
        public static ImageViewerViewMenuCommandTag ToggleCrosshair { get; } = new(ImageViewerViewCommand.ToggleCrosshair);
        public static ImageViewerViewMenuCommandTag ToggleCaliperScores { get; } = new(ImageViewerViewCommand.ToggleCaliperScores);
        public static ImageViewerViewMenuCommandTag ToggleInfoPanel { get; } = new(ImageViewerViewCommand.ToggleInfoPanel);
        public static ImageViewerViewMenuCommandTag ToggleHistogram { get; } = new(ImageViewerViewCommand.ToggleHistogram);
        public static ImageViewerViewMenuCommandTag ToggleProfile { get; } = new(ImageViewerViewCommand.ToggleProfile);
        public static ImageViewerViewMenuCommandTag ToggleScaleBar { get; } = new(ImageViewerViewCommand.ToggleScaleBar);
        public static ImageViewerViewMenuCommandTag ToggleRoiList { get; } = new(ImageViewerViewCommand.ToggleRoiList);
        public static ImageViewerViewMenuCommandTag ToggleToolbar { get; } = new(ImageViewerViewCommand.ToggleToolbar);
        public static ImageViewerViewMenuCommandTag ToggleSnapGrid { get; } = new(ImageViewerViewCommand.ToggleSnapGrid);
        public static ImageViewerViewMenuCommandTag ToggleSnapToGrid { get; } = new(ImageViewerViewCommand.ToggleSnapToGrid);
        public static ImageViewerViewMenuCommandTag FitToView { get; } = new(ImageViewerViewCommand.FitToView);
        public static ImageViewerViewMenuCommandTag FillToView { get; } = new(ImageViewerViewCommand.FillToView);
        public static ImageViewerViewMenuCommandTag ActualSize { get; } = new(ImageViewerViewCommand.ActualSize);
        public static ImageViewerViewMenuCommandTag ZoomIn { get; } = new(ImageViewerViewCommand.ZoomIn);
        public static ImageViewerViewMenuCommandTag ZoomOut { get; } = new(ImageViewerViewCommand.ZoomOut);
        public static ImageViewerViewMenuCommandTag ZoomToSelection { get; } = new(ImageViewerViewCommand.ZoomToSelection);
        public static ImageViewerViewMenuCommandTag ResetView { get; } = new(ImageViewerViewCommand.ResetView);
        public static ImageViewerViewMenuCommandTag ShowFullImage { get; } = new(ImageViewerViewCommand.ShowFullImage);
        public static ImageViewerViewMenuCommandTag RotateLeft { get; } = new(ImageViewerViewCommand.RotateLeft);
        public static ImageViewerViewMenuCommandTag RotateRight { get; } = new(ImageViewerViewCommand.RotateRight);
        public static ImageViewerViewMenuCommandTag FlipHorizontal { get; } = new(ImageViewerViewCommand.FlipHorizontal);
        public static ImageViewerViewMenuCommandTag FlipVertical { get; } = new(ImageViewerViewCommand.FlipVertical);
    }

    public static class ImageViewerAnalysisMenuTags
    {
        public static ImageViewerAnalysisMenuCommandTag ToggleAsyncAnalysis { get; } = new(ImageViewerAnalysisCommand.ToggleAsyncAnalysis);
        public static ImageViewerAnalysisMenuCommandTag TogglePauseRealtimeHistogram { get; } = new(ImageViewerAnalysisCommand.TogglePauseRealtimeHistogram);
        public static ImageViewerAnalysisMenuCommandTag TogglePauseRealtimeProfile { get; } = new(ImageViewerAnalysisCommand.TogglePauseRealtimeProfile);
        public static ImageViewerAnalysisMenuCommandTag RefreshAnalysis { get; } = new(ImageViewerAnalysisCommand.RefreshAnalysis);
        public static ImageViewerAnalysisMenuCommandTag ToggleImagePyramid { get; } = new(ImageViewerAnalysisCommand.ToggleImagePyramid);
        public static ImageViewerAnalysisMenuCommandTag ToggleAutoSelectPyramidLevel { get; } = new(ImageViewerAnalysisCommand.ToggleAutoSelectPyramidLevel);
        public static ImageViewerAnalysisMenuCommandTag ToggleTiledRendering { get; } = new(ImageViewerAnalysisCommand.ToggleTiledRendering);
        public static ImageViewerAnalysisMenuCommandTag TogglePrefetchAdjacentTiles { get; } = new(ImageViewerAnalysisCommand.TogglePrefetchAdjacentTiles);
        public static ImageViewerAnalysisMenuCommandTag ToggleGpuRendering { get; } = new(ImageViewerAnalysisCommand.ToggleGpuRendering);
        public static ImageViewerAnalysisMenuCommandTag ClearPyramidCache { get; } = new(ImageViewerAnalysisCommand.ClearPyramidCache);
        public static ImageViewerAnalysisMenuCommandTag ClearAnalysisCache { get; } = new(ImageViewerAnalysisCommand.ClearAnalysisCache);
        public static ImageViewerAnalysisMenuCommandTag TogglePreferShaderPseudoColor { get; } = new(ImageViewerAnalysisCommand.TogglePreferShaderPseudoColor);
        public static ImageViewerAnalysisMenuCommandTag ToggleAllowCpuPseudoColorFallback { get; } = new(ImageViewerAnalysisCommand.ToggleAllowCpuPseudoColorFallback);
        public static ImageViewerAnalysisMenuCommandTag SetPseudoColorPaletteNone { get; } = new(ImageViewerAnalysisCommand.SetPseudoColorPaletteNone);
        public static ImageViewerAnalysisMenuCommandTag SetPseudoColorPaletteHot { get; } = new(ImageViewerAnalysisCommand.SetPseudoColorPaletteHot);
        public static ImageViewerAnalysisMenuCommandTag SetPseudoColorPaletteJet { get; } = new(ImageViewerAnalysisCommand.SetPseudoColorPaletteJet);
        public static ImageViewerAnalysisMenuCommandTag SetPseudoColorPaletteViridis { get; } = new(ImageViewerAnalysisCommand.SetPseudoColorPaletteViridis);
        public static ImageViewerAnalysisMenuCommandTag ShowSmartDisplaySuggestion { get; } = new(ImageViewerAnalysisCommand.ShowSmartDisplaySuggestion);
        public static ImageViewerAnalysisMenuCommandTag ShowDisplayAdjustment { get; } = new(ImageViewerAnalysisCommand.ShowDisplayAdjustment);
        public static ImageViewerAnalysisMenuCommandTag ResetDisplayAdjustment { get; } = new(ImageViewerAnalysisCommand.ResetDisplayAdjustment);
        public static ImageViewerAnalysisMenuCommandTag ShowRenderStatus { get; } = new(ImageViewerAnalysisCommand.ShowRenderStatus);
    }

    public static class ImageViewerRoiMenuTags
    {
        public static ImageViewerRoiMenuCommandTag Undo { get; } = new(ImageViewerRoiMenuCommand.Undo);
        public static ImageViewerRoiMenuCommandTag Redo { get; } = new(ImageViewerRoiMenuCommand.Redo);
        public static ImageViewerRoiMenuCommandTag DeleteSelected { get; } = new(ImageViewerRoiMenuCommand.DeleteSelected);
        public static ImageViewerRoiMenuCommandTag ClearAll { get; } = new(ImageViewerRoiMenuCommand.ClearAll);
        public static ImageViewerRoiMenuCommandTag EditProperties { get; } = new(ImageViewerRoiMenuCommand.EditProperties);
        public static ImageViewerRoiMenuCommandTag SetLabel { get; } = new(ImageViewerRoiMenuCommand.SetLabel);
        public static ImageViewerRoiMenuCommandTag SetColorCyan { get; } = new(ImageViewerRoiMenuCommand.SetColorCyan);
        public static ImageViewerRoiMenuCommandTag SetColorRed { get; } = new(ImageViewerRoiMenuCommand.SetColorRed);
        public static ImageViewerRoiMenuCommandTag SetColorGreen { get; } = new(ImageViewerRoiMenuCommand.SetColorGreen);
        public static ImageViewerRoiMenuCommandTag SetColorYellow { get; } = new(ImageViewerRoiMenuCommand.SetColorYellow);
        public static ImageViewerRoiMenuCommandTag SetColorMagenta { get; } = new(ImageViewerRoiMenuCommand.SetColorMagenta);
        public static ImageViewerRoiMenuCommandTag CalibratePixels { get; } = new(ImageViewerRoiMenuCommand.CalibratePixels);
        public static ImageViewerRoiMenuCommandTag EditCaliperSettings { get; } = new(ImageViewerRoiMenuCommand.EditCaliperSettings);
    }

    public static class ImageViewerFileMenuTags
    {
        public static ImageViewerFileMenuCommandTag OpenImage { get; } = new(ImageViewerFileMenuCommand.OpenImage);
        public static ImageViewerFileMenuCommandTag SaveRois { get; } = new(ImageViewerFileMenuCommand.SaveRois);
        public static ImageViewerFileMenuCommandTag LoadRois { get; } = new(ImageViewerFileMenuCommand.LoadRois);
        public static ImageViewerFileMenuCommandTag SaveSession { get; } = new(ImageViewerFileMenuCommand.SaveSession);
        public static ImageViewerFileMenuCommandTag LoadSession { get; } = new(ImageViewerFileMenuCommand.LoadSession);
        public static ImageViewerFileMenuCommandTag ExportProjectPackage { get; } = new(ImageViewerFileMenuCommand.ExportProjectPackage);
        public static ImageViewerFileMenuCommandTag SaveRecipe { get; } = new(ImageViewerFileMenuCommand.SaveRecipe);
        public static ImageViewerFileMenuCommandTag LoadRecipe { get; } = new(ImageViewerFileMenuCommand.LoadRecipe);
        public static ImageViewerFileMenuCommandTag CaptureRecipeReferenceDatum1 { get; } = new(ImageViewerFileMenuCommand.CaptureRecipeReferenceDatum1);
        public static ImageViewerFileMenuCommandTag CaptureRecipeReferenceDatum2 { get; } = new(ImageViewerFileMenuCommand.CaptureRecipeReferenceDatum2);
        public static ImageViewerFileMenuCommandTag CaptureCurrentAlignmentDatum1 { get; } = new(ImageViewerFileMenuCommand.CaptureCurrentAlignmentDatum1);
        public static ImageViewerFileMenuCommandTag CaptureCurrentAlignmentDatum2 { get; } = new(ImageViewerFileMenuCommand.CaptureCurrentAlignmentDatum2);
        public static ImageViewerFileMenuCommandTag LoadAlignedRecipe { get; } = new(ImageViewerFileMenuCommand.LoadAlignedRecipe);
        public static ImageViewerFileMenuCommandTag ToggleAutoSave { get; } = new(ImageViewerFileMenuCommand.ToggleAutoSave);
    }

    public static class ImageViewerFeatureMenuTags
    {
        public static ImageViewerFeatureMenuCommandTag GradientDetect { get; } = new(ImageViewerFeatureMenuCommand.GradientDetect);
        public static ImageViewerFeatureMenuCommandTag ExportSnapshot { get; } = new(ImageViewerFeatureMenuCommand.ExportSnapshot);
        public static ImageViewerFeatureMenuCommandTag ExportAnalysisCsv { get; } = new(ImageViewerFeatureMenuCommand.ExportAnalysisCsv);
        public static ImageViewerFeatureMenuCommandTag ExportBatchAnalysisCsv { get; } = new(ImageViewerFeatureMenuCommand.ExportBatchAnalysisCsv);
        public static ImageViewerFeatureMenuCommandTag ShowAnalysisSummary { get; } = new(ImageViewerFeatureMenuCommand.ShowAnalysisSummary);
        public static ImageViewerFeatureMenuCommandTag ShowProcessCapability { get; } = new(ImageViewerFeatureMenuCommand.ShowProcessCapability);
        public static ImageViewerFeatureMenuCommandTag PrintReport { get; } = new(ImageViewerFeatureMenuCommand.PrintReport);
        public static ImageViewerFeatureMenuCommandTag CheckCalibrationReadiness { get; } = new(ImageViewerFeatureMenuCommand.CheckCalibrationReadiness);
    }
}
