namespace ImageViewer.Controls
{
    internal enum ImageViewerViewCommand
    {
        TogglePixelGrid,
        ToggleCrosshair,
        ToggleCaliperScores,
        ToggleInfoPanel,
        ToggleHistogram,
        ToggleProfile,
        ToggleScaleBar,
        ToggleRoiList,
        ToggleToolbar,
        ToggleSnapGrid,
        ToggleSnapToGrid,
        FitToView,
        FillToView,
        ActualSize,
        ZoomIn,
        ZoomOut,
        ZoomToSelection,
        ResetView,
        ShowFullImage,
        RotateLeft,
        RotateRight,
        FlipHorizontal,
        FlipVertical
    }

    internal enum ImageViewerModeCommand
    {
        Rectangle,
        Ellipse,
        Circle,
        Polygon,
        Polyline,
        Freehand,
        PointAnnotation,
        TextAnnotation,
        LineMeasure,
        AngleMeasure
    }

    internal enum ImageViewerAnalysisCommand
    {
        ToggleAsyncAnalysis,
        TogglePauseRealtimeHistogram,
        TogglePauseRealtimeProfile,
        RefreshAnalysis,
        ToggleImagePyramid,
        ToggleAutoSelectPyramidLevel,
        ToggleTiledRendering,
        TogglePrefetchAdjacentTiles,
        ToggleGpuRendering,
        ClearPyramidCache,
        ClearAnalysisCache,
        TogglePreferShaderPseudoColor,
        ToggleAllowCpuPseudoColorFallback,
        SetPseudoColorPaletteNone,
        SetPseudoColorPaletteHot,
        SetPseudoColorPaletteJet,
        SetPseudoColorPaletteViridis,
        ShowSmartDisplaySuggestion,
        ShowDisplayAdjustment,
        ResetDisplayAdjustment,
        ShowRenderStatus
    }

    internal enum ImageViewerRoiMenuCommand
    {
        Undo,
        Redo,
        DeleteSelected,
        ClearAll,
        EditProperties,
        SetLabel,
        SetColorCyan,
        SetColorRed,
        SetColorGreen,
        SetColorYellow,
        SetColorMagenta,
        CalibratePixels,
        EditCaliperSettings
    }

    internal enum ImageViewerFileMenuCommand
    {
        OpenImage,
        SaveRois,
        LoadRois,
        SaveSession,
        LoadSession,
        ExportProjectPackage,
        SaveRecipe,
        LoadRecipe,
        CaptureRecipeReferenceDatum1,
        CaptureRecipeReferenceDatum2,
        CaptureCurrentAlignmentDatum1,
        CaptureCurrentAlignmentDatum2,
        LoadAlignedRecipe,
        ToggleAutoSave
    }

    internal enum ImageViewerFeatureMenuCommand
    {
        GradientDetect,
        ExportSnapshot,
        ExportAnalysisCsv,
        ExportBatchAnalysisCsv,
        ShowAnalysisSummary,
        ShowProcessCapability,
        PrintReport,
        CheckCalibrationReadiness
    }
}
