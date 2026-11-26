using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class CalibrationController
    {
        private readonly ImageViewerDialogWorkflowService _dialogWorkflowService;
        private readonly Func<RoiBase?> _selectedRoiProvider;

        public CalibrationController(ImageViewerDialogWorkflowService dialogWorkflowService, Func<RoiBase?> selectedRoiProvider)
        {
            _dialogWorkflowService = dialogWorkflowService ?? throw new ArgumentNullException(nameof(dialogWorkflowService));
            _selectedRoiProvider = selectedRoiProvider ?? throw new ArgumentNullException(nameof(selectedRoiProvider));
        }

        public void CalibrateSelectedRoi()
        {
            _dialogWorkflowService.CalibrateSelectedRoi(_selectedRoiProvider());
        }
    }
}