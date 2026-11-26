using System;
using System.Collections.Generic;
using System.Windows.Controls;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerContextMenuHost : IImageViewerContextMenuHost
    {
        private readonly ImageViewer _owner;
        private readonly ItemsControl _drawRoiMenu;
        private readonly ImageViewerSessionController _sessionController;
        private readonly ImageViewerAnalysisCoordinator _analysisController;
        private readonly Action _refreshRoiDrawingMenuItems;
        private readonly Func<bool> _hasAnalysisBitmap;

        public ImageViewerContextMenuHost(
            ImageViewer owner,
            ItemsControl drawRoiMenu,
            ImageViewerSessionController sessionController,
            ImageViewerAnalysisCoordinator analysisController,
            Action refreshRoiDrawingMenuItems,
            Func<bool> hasAnalysisBitmap)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _drawRoiMenu = drawRoiMenu ?? throw new ArgumentNullException(nameof(drawRoiMenu));
            _sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
            _analysisController = analysisController ?? throw new ArgumentNullException(nameof(analysisController));
            _refreshRoiDrawingMenuItems = refreshRoiDrawingMenuItems ?? throw new ArgumentNullException(nameof(refreshRoiDrawingMenuItems));
            _hasAnalysisBitmap = hasAnalysisBitmap ?? throw new ArgumentNullException(nameof(hasAnalysisBitmap));
        }

        public bool CanUndo => _owner.ViewerState.UndoRedo.CanUndo;
        public bool CanRedo => _owner.ViewerState.UndoRedo.CanRedo;
        public RoiBase? SelectedRoi => _owner.ViewerState.SelectedRoi;
        public bool HasRois => _owner.ViewerState.AllRois.Count > 0;
        public bool HasImage => _owner.ImageSource != null;
        public bool HasDrawingTools => _drawRoiMenu.Items.Count > 0;
        public bool IsAutoSaveEnabled => _sessionController.IsAutoSaveEnabled;
        public bool ShowPixelGrid => _owner.ShowPixelGrid;
        public bool ShowCrosshair => _owner.ShowCrosshair;
        public bool ShowCaliperScores => _owner.ShowCaliperScores;
        public bool ShowInfoPanel => _owner.ShowInfoPanel;
        public bool ShowHistogram => _owner.ShowHistogram;
        public bool ShowProfile => _owner.ShowProfile;
        public bool ShowScaleBar => _owner.ShowScaleBar;
        public bool ShowRoiList => _owner.ShowRoiList;
        public bool ShowToolbar => _owner.ShowToolbar;
        public bool ShowSnapGrid => _owner.ShowSnapGrid;
        public bool EnableSnapToGrid => _owner.EnableSnapToGrid;
        public bool HasAnalysisBitmap => _hasAnalysisBitmap();

        public bool CanCreatePropertyEditor(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);
            return _owner.PluginRegistry.FindByRoi(roi)?.CreatePropertyEditor(roi) != null;
        }

        public IReadOnlyList<ImageViewerDynamicMenuItem> GetRecentProjectMenuItems()
        {
            return _sessionController.GetRecentProjectMenuItems();
        }

        public ImageViewerAnalysisMenuState BuildAnalysisMenuState()
        {
            return _analysisController.BuildMenuState();
        }

        public void RefreshRoiDrawingMenuItems()
        {
            _refreshRoiDrawingMenuItems();
        }

        public void SetMenuState(ImageViewerMenuStateSnapshot state)
        {
            _owner.SetCurrentValue(ImageViewer.MenuStateProperty, state);
        }
    }
}