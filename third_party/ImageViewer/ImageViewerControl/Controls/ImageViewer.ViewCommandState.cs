namespace ImageViewer.Controls
{
    internal readonly record struct ImageViewerViewCommandStateInput(
        bool HasImage,
        bool HasSelection,
        bool ShowPixelGrid,
        bool ShowCrosshair,
        bool ShowCaliperScores,
        bool ShowInfoPanel,
        bool ShowHistogram,
        bool ShowProfile,
        bool ShowScaleBar,
        bool ShowRoiList,
        bool ShowToolbar,
        bool ShowSnapGrid,
        bool EnableSnapToGrid);

    internal readonly record struct ImageViewerViewCommandMenuState(
        bool FitToViewEnabled,
        bool ActualSizeEnabled,
        bool ResetViewEnabled,
        bool ZoomToSelectionEnabled,
        bool ShowPixelGridChecked,
        bool ShowCrosshairChecked,
        bool ShowCaliperScoresChecked,
        bool ShowInfoPanelChecked,
        bool ShowHistogramChecked,
        bool ShowProfileChecked,
        bool ShowScaleBarChecked,
        bool ShowRoiListChecked,
        bool ShowToolbarChecked,
        bool ShowSnapGridChecked,
        bool EnableSnapToGridChecked);

    internal static class ImageViewerViewCommandStateEvaluator
    {
        public static ImageViewerViewCommandMenuState Evaluate(ImageViewerViewCommandStateInput input)
        {
            return new ImageViewerViewCommandMenuState(
                FitToViewEnabled: input.HasImage,
                ActualSizeEnabled: input.HasImage,
                ResetViewEnabled: input.HasImage,
                ZoomToSelectionEnabled: input.HasSelection,
                ShowPixelGridChecked: input.ShowPixelGrid,
                ShowCrosshairChecked: input.ShowCrosshair,
                ShowCaliperScoresChecked: input.ShowCaliperScores,
                ShowInfoPanelChecked: input.ShowInfoPanel,
                ShowHistogramChecked: input.ShowHistogram,
                ShowProfileChecked: input.ShowProfile,
                ShowScaleBarChecked: input.ShowScaleBar,
                ShowRoiListChecked: input.ShowRoiList,
                ShowToolbarChecked: input.ShowToolbar,
                ShowSnapGridChecked: input.ShowSnapGrid,
                EnableSnapToGridChecked: input.EnableSnapToGrid);
        }
    }
}