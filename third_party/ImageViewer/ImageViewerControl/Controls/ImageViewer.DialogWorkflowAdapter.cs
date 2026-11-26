using System;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal interface IImageViewerDialogWorkflowAdapter
    {
        string? ShowOpenImageDialog();

        string? ShowTextInput(string message, string defaultValue);

        string? ShowSaveRoiDialog();

        string? ShowOpenRoiDialog();

        string? ShowSaveSessionDialog();

        string? ShowOpenSessionDialog();

        string? ShowSaveProjectPackageDialog();

        string? ShowSaveRecipeDialog();

        string? ShowOpenRecipeDialog();

        string? ShowSaveSnapshotDialog();

        string? ShowSaveAnalysisCsvDialog();

        CalibrationDialogResult? ShowCalibrationDialog(string currentUnit, CameraCalibration? currentCalibration);

        CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null);

        LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null);

        CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null);

        void ShowPropertyEditor(string title, FrameworkElement editor);

        void ShowReadOnlyText(string title, string text);

        void ShowWarning(string title, string message);
    }

    internal sealed class WpfImageViewerDialogWorkflowAdapter : IImageViewerDialogWorkflowAdapter
    {
        private readonly Func<Window?> _ownerWindowProvider;
        private readonly IImageViewerDialogService _dialogService;
        private readonly IImageViewerFileDialogService _fileDialogService;

        public WpfImageViewerDialogWorkflowAdapter(
            Func<Window?> ownerWindowProvider,
            IImageViewerDialogService dialogService,
            IImageViewerFileDialogService fileDialogService)
        {
            _ownerWindowProvider = ownerWindowProvider ?? throw new ArgumentNullException(nameof(ownerWindowProvider));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
        }

        public string? ShowOpenImageDialog() => _fileDialogService.ShowOpenImageDialog(_ownerWindowProvider());

        public string? ShowTextInput(string message, string defaultValue) => _dialogService.ShowLabelDialog(_ownerWindowProvider(), message, defaultValue);

        public string? ShowSaveRoiDialog() => _fileDialogService.ShowSaveRoiDialog(_ownerWindowProvider());

        public string? ShowOpenRoiDialog() => _fileDialogService.ShowOpenRoiDialog(_ownerWindowProvider());

        public string? ShowSaveSessionDialog() => _fileDialogService.ShowSaveSessionDialog(_ownerWindowProvider());

        public string? ShowOpenSessionDialog() => _fileDialogService.ShowOpenSessionDialog(_ownerWindowProvider());

        public string? ShowSaveProjectPackageDialog() => _fileDialogService.ShowSaveProjectPackageDialog(_ownerWindowProvider());

        public string? ShowSaveRecipeDialog() => _fileDialogService.ShowSaveRecipeDialog(_ownerWindowProvider());

        public string? ShowOpenRecipeDialog() => _fileDialogService.ShowOpenRecipeDialog(_ownerWindowProvider());

        public string? ShowSaveSnapshotDialog() => _fileDialogService.ShowSaveSnapshotDialog(_ownerWindowProvider());

        public string? ShowSaveAnalysisCsvDialog() => _fileDialogService.ShowSaveAnalysisCsvDialog(_ownerWindowProvider());

        public CalibrationDialogResult? ShowCalibrationDialog(string currentUnit, CameraCalibration? currentCalibration) => _dialogService.ShowCalibrationDialog(_ownerWindowProvider(), currentUnit, currentCalibration);

        public CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null)
            => _dialogService.ShowLineMeasureCaliperSettingsDialog(_ownerWindowProvider(), roi, previewAction);

        public LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null)
            => _dialogService.ShowLineCaliperSettingsDialog(_ownerWindowProvider(), roi, previewAction);

        public CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null)
            => _dialogService.ShowCircularCaliperSettingsDialog(_ownerWindowProvider(), roi, previewAction);

        public void ShowPropertyEditor(string title, FrameworkElement editor)
            => _dialogService.ShowPropertyEditorDialog(_ownerWindowProvider(), title, editor);

        public void ShowReadOnlyText(string title, string text)
            => _dialogService.ShowReadOnlyTextDialog(_ownerWindowProvider(), title, text);

        public void ShowWarning(string title, string message)
            => _dialogService.ShowWarning(_ownerWindowProvider(), title, message);
    }
}