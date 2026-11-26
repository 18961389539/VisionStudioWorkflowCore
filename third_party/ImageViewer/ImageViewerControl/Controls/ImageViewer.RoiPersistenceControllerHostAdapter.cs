using System;
using System.Collections.Generic;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerRoiPersistenceControllerHostAdapter : IImageViewerRoiPersistenceControllerHost
    {
        private readonly ImageViewer _owner;
        private readonly ImageViewerDialogWorkflowService _dialogWorkflowService;
        private readonly Action _refreshAllCaliperDetections;
        private readonly Action _drawRois;
        private readonly Action _refreshSelectedRoiPropertyPanel;
        private readonly Action<string, string, Exception> _showNonCriticalError;

        public ImageViewerRoiPersistenceControllerHostAdapter(
            ImageViewer owner,
            ImageViewerDialogWorkflowService dialogWorkflowService,
            Action refreshAllCaliperDetections,
            Action drawRois,
            Action refreshSelectedRoiPropertyPanel,
            Action<string, string, Exception> showNonCriticalError)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _dialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
            _refreshAllCaliperDetections = refreshAllCaliperDetections ?? throw new ArgumentNullException(nameof(refreshAllCaliperDetections));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
            _refreshSelectedRoiPropertyPanel = refreshSelectedRoiPropertyPanel ?? throw new ArgumentNullException(nameof(refreshSelectedRoiPropertyPanel));
            _showNonCriticalError = showNonCriticalError ?? throw new ArgumentNullException(nameof(showNonCriticalError));
        }

        public string? ShowSaveRoiDialog() => _dialogWorkflowService.ShowSaveRoiDialog();

        public string? ShowOpenRoiDialog() => _dialogWorkflowService.ShowOpenRoiDialog();

        public RoiPluginRegistry PluginRegistry => _owner.PluginRegistry;

        public IReadOnlyList<RoiBase> AllRois => _owner.ViewerState.AllRois;

        public double PixelSize
        {
            get => _owner.PixelSize;
            set => _owner.PixelSize = value;
        }

        public string PhysicalUnit
        {
            get => _owner.PhysicalUnit;
            set => _owner.PhysicalUnit = value;
        }

        public ImageAnalysisQualityProfile QualityProfile
        {
            get => _owner.QualityProfile;
            set => _owner.QualityProfile = value;
        }

        public void ReplaceAllRois(IReadOnlyList<RoiBase> rois) => _owner.ViewerState.ReplaceAllRois(rois);

        public void RefreshAllCaliperDetections() => _refreshAllCaliperDetections();

        public void DrawRois() => _drawRois();

        public void RefreshSelectedRoiPropertyPanel() => _refreshSelectedRoiPropertyPanel();

        public void ShowNonCriticalError(string title, string message, Exception ex) => _showNonCriticalError(title, message, ex);

        public void ShowStatusHint(string message, StatusHintKind kind) => _owner.ShowStatusHint(message, kind);

        public void ClearUndoHistory() => _owner.ViewerState.UndoRedo.Clear();

        public void AppendUnresolvedRois(IReadOnlyList<RoiPersistenceData> payloads) => _owner.AppendUnresolvedRois(payloads);
    }
}
