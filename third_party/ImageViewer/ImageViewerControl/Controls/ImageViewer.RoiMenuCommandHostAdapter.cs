using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal interface IImageViewerRoiSelectionCapability
    {
        RoiBase? SelectedRoi { get; }
    }

    internal interface IImageViewerRoiEditingCapability
    {
        void Undo();
        void Redo();
        void DeleteSelected();
        void ClearAll();
        void SetSelectedLabel();
        void SetSelectedColor(RoiColor color);
        void CalibrateSelectedRoi();
        void ShowProperties(RoiBase roi);
        void ShowCaliperSettings(RoiBase roi);
    }

    internal interface IImageViewerContextMenuCapability
    {
        void UpdateContextMenuState();
    }

    internal sealed class ImageViewerRoiMenuCommandHostAdapter : IImageViewerRoiMenuCommandHost
    {
        private readonly IImageViewerRoiSelectionCapability _selection;
        private readonly IImageViewerRoiEditingCapability _editing;
        private readonly IImageViewerContextMenuCapability _contextMenu;

        public ImageViewerRoiMenuCommandHostAdapter(
            IImageViewerRoiSelectionCapability selection,
            IImageViewerRoiEditingCapability editing,
            IImageViewerContextMenuCapability contextMenu)
        {
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _editing = editing ?? throw new ArgumentNullException(nameof(editing));
            _contextMenu = contextMenu ?? throw new ArgumentNullException(nameof(contextMenu));
        }

        public void Undo() => _editing.Undo();

        public void Redo() => _editing.Redo();

        public void DeleteSelected() => _editing.DeleteSelected();

        public void ClearAll() => _editing.ClearAll();

        public void EditSelectedProperties()
        {
            if (_selection.SelectedRoi is RoiBase roi)
            {
                _editing.ShowProperties(roi);
            }
        }

        public void SetSelectedLabel() => _editing.SetSelectedLabel();

        public void SetSelectedColor(RoiColor color) => _editing.SetSelectedColor(color);

        public void CalibrateSelectedRoi() => _editing.CalibrateSelectedRoi();

        public void EditSelectedCaliperSettings()
        {
            if (_selection.SelectedRoi is RoiBase roi)
            {
                _editing.ShowCaliperSettings(roi);
            }
        }

        public void UpdateContextMenuState() => _contextMenu.UpdateContextMenuState();
    }

    internal sealed class ImageViewerRoiSelectionCapability : IImageViewerRoiSelectionCapability
    {
        private readonly ImageViewer _owner;

        public ImageViewerRoiSelectionCapability(ImageViewer owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public RoiBase? SelectedRoi => _owner.ViewerState.SelectedRoi;
    }

    internal sealed class ImageViewerRoiEditingCapability : IImageViewerRoiEditingCapability
    {
        private readonly RoiEditController _roiEditController;
        private readonly CalibrationController _calibrationController;
        private readonly ImageViewerDialogWorkflowService _dialogWorkflowService;

        public ImageViewerRoiEditingCapability(
            RoiEditController roiEditController,
            CalibrationController calibrationController,
            ImageViewerDialogWorkflowService dialogWorkflowService)
        {
            _roiEditController = roiEditController ?? throw new ArgumentNullException(nameof(roiEditController));
            _calibrationController = calibrationController ?? throw new ArgumentNullException(nameof(calibrationController));
            _dialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
        }

        public void Undo() => _roiEditController.Undo();
        public void Redo() => _roiEditController.Redo();
        public void DeleteSelected() => _roiEditController.DeleteSelected();
        public void ClearAll() => _roiEditController.ClearAll();
        public void SetSelectedLabel() => _roiEditController.SetSelectedLabel();
        public void SetSelectedColor(RoiColor color) => _roiEditController.SetSelectedColor(color);
        public void CalibrateSelectedRoi() => _calibrationController.CalibrateSelectedRoi();
        public void ShowProperties(RoiBase roi) => _dialogWorkflowService.ShowRoiProperties(roi);
        public void ShowCaliperSettings(RoiBase roi) => _dialogWorkflowService.ShowCaliperSettings(roi);
    }

    internal sealed class ImageViewerContextMenuCapability : IImageViewerContextMenuCapability
    {
        private readonly ImageViewer _owner;

        public ImageViewerContextMenuCapability(ImageViewer owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        public void UpdateContextMenuState() => _owner.UpdateContextMenuState();
    }
}
