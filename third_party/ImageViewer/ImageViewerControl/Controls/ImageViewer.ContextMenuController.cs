using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    internal interface IImageViewerContextMenuController
    {
        void HandleOpened();
        void UpdateState();
    }

    internal interface IImageViewerContextMenuHost
    {
        bool CanUndo { get; }
        bool CanRedo { get; }
        RoiBase? SelectedRoi { get; }
        bool HasRois { get; }
        bool HasImage { get; }
        bool HasDrawingTools { get; }
        bool IsAutoSaveEnabled { get; }
        bool ShowPixelGrid { get; }
        bool ShowCrosshair { get; }
        bool ShowCaliperScores { get; }
        bool ShowInfoPanel { get; }
        bool ShowHistogram { get; }
        bool ShowProfile { get; }
        bool ShowScaleBar { get; }
        bool ShowRoiList { get; }
        bool ShowToolbar { get; }
        bool ShowSnapGrid { get; }
        bool EnableSnapToGrid { get; }
        bool HasAnalysisBitmap { get; }

        bool CanCreatePropertyEditor(RoiBase roi);
        IReadOnlyList<ImageViewerDynamicMenuItem> GetRecentProjectMenuItems();
        ImageViewerAnalysisMenuState BuildAnalysisMenuState();
        void RefreshRoiDrawingMenuItems();
        void SetMenuState(ImageViewerMenuStateSnapshot state);
    }

    internal sealed class ContextMenuController : IImageViewerContextMenuController
    {
        private readonly IImageViewerContextMenuHost _host;

        public ContextMenuController(IImageViewerContextMenuHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public void HandleOpened()
        {
            _host.RefreshRoiDrawingMenuItems();
            UpdateState();
        }

        public void UpdateState()
        {
            RoiBase? selectedRoi = _host.SelectedRoi;
            bool hasImage = _host.HasImage;
            bool hasRois = _host.HasRois;
            bool hasContent = hasImage || hasRois;
            bool hasCaliperSelection = selectedRoi is CaliperMeasureRoi or LineCaliperMeasureRoi or CircularCaliperMeasureRoi;
            bool hasEditableProperties = hasCaliperSelection || (selectedRoi != null && _host.CanCreatePropertyEditor(selectedRoi));
            IReadOnlyList<ImageViewerDynamicMenuItem> recentProjects = _host.GetRecentProjectMenuItems();
            ImageViewerContextMenuState contextMenuState = ImageViewerContextMenuStateEvaluator.Evaluate(
                new ImageViewerContextMenuStateInput(
                    CanUndo: _host.CanUndo,
                    CanRedo: _host.CanRedo,
                    HasSelection: selectedRoi != null,
                    HasRois: hasRois,
                    HasDrawingTools: _host.HasDrawingTools,
                    HasEditableProperties: hasEditableProperties,
                    CanCalibratePixels: selectedRoi is LineMeasureRoi or CaliperMeasureRoi,
                    CanEditCaliperSettings: hasCaliperSelection,
                    CanRunGradientDetection: CanRunGradientDetection(selectedRoi, _host.HasAnalysisBitmap),
                    HasImage: hasImage));
            ImageViewerFileMenuState fileMenuState = ImageViewerFileMenuStateEvaluator.Evaluate(
                new ImageViewerFileMenuStateInput(
                    HasRois: hasRois,
                    HasContent: hasContent,
                    AutoSaveEnabled: _host.IsAutoSaveEnabled,
                    RecentProjects: recentProjects));
            ImageViewerViewCommandMenuState viewCommandMenuState = ImageViewerViewCommandStateEvaluator.Evaluate(
                new ImageViewerViewCommandStateInput(
                    HasImage: hasImage,
                    HasSelection: selectedRoi != null,
                    ShowPixelGrid: _host.ShowPixelGrid,
                    ShowCrosshair: _host.ShowCrosshair,
                    ShowCaliperScores: _host.ShowCaliperScores,
                    ShowInfoPanel: _host.ShowInfoPanel,
                    ShowHistogram: _host.ShowHistogram,
                    ShowProfile: _host.ShowProfile,
                    ShowScaleBar: _host.ShowScaleBar,
                    ShowRoiList: _host.ShowRoiList,
                    ShowToolbar: _host.ShowToolbar,
                    ShowSnapGrid: _host.ShowSnapGrid,
                    EnableSnapToGrid: _host.EnableSnapToGrid));
            ImageViewerAnalysisMenuState analysisMenuState = _host.BuildAnalysisMenuState();

            _host.SetMenuState(new ImageViewerMenuStateSnapshot(contextMenuState, fileMenuState, viewCommandMenuState, analysisMenuState));
        }

        internal static bool CanRunGradientDetection(RoiBase? selectedRoi, bool hasAnalysisBitmap)
        {
            return hasAnalysisBitmap && selectedRoi is CaliperMeasureRoi or CircularCaliperMeasureRoi;
        }
    }
}