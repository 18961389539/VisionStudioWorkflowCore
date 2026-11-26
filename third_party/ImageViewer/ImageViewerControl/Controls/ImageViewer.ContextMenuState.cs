namespace ImageViewer.Controls
{
    internal readonly record struct ImageViewerContextMenuStateInput(
        bool CanUndo,
        bool CanRedo,
        bool HasSelection,
        bool HasRois,
        bool HasDrawingTools,
        bool HasEditableProperties,
        bool CanCalibratePixels,
        bool CanEditCaliperSettings,
        bool CanRunGradientDetection,
        bool HasImage);

    internal readonly record struct ImageViewerContextMenuState(
        bool UndoEnabled,
        bool RedoEnabled,
        bool DeleteSelectedEnabled,
        bool ClearAllEnabled,
        bool DrawRoiEnabled,
        bool EditPropertiesEnabled,
        bool SetLabelEnabled,
        bool SetColorEnabled,
        bool CalibratePixelsEnabled,
        bool EditCaliperSettingsEnabled,
        bool GradientDetectEnabled,
        bool ExportSnapshotEnabled,
        bool ExportAnalysisCsvEnabled,
        bool ShowAnalysisSummaryEnabled);

    internal static class ImageViewerContextMenuStateEvaluator
    {
        public static ImageViewerContextMenuState Evaluate(ImageViewerContextMenuStateInput input)
        {
            return new ImageViewerContextMenuState(
                UndoEnabled: input.CanUndo,
                RedoEnabled: input.CanRedo,
                DeleteSelectedEnabled: input.HasSelection,
                ClearAllEnabled: input.HasRois,
                DrawRoiEnabled: input.HasDrawingTools && input.HasImage,
                EditPropertiesEnabled: input.HasEditableProperties,
                SetLabelEnabled: input.HasSelection,
                SetColorEnabled: input.HasSelection,
                CalibratePixelsEnabled: input.CanCalibratePixels,
                EditCaliperSettingsEnabled: input.CanEditCaliperSettings,
                GradientDetectEnabled: input.CanRunGradientDetection,
                ExportSnapshotEnabled: input.HasImage,
                ExportAnalysisCsvEnabled: input.HasRois,
                ShowAnalysisSummaryEnabled: input.HasRois);
        }
    }
}
