using System.Windows;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerDialogService
    {
        string? ShowLabelDialog(Window? owner, string message, string defaultValue);

        CalibrationDialogResult? ShowCalibrationDialog(Window? owner, string currentUnit, CameraCalibration? currentCalibration);

        CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(Window? owner, CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null);

        LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(Window? owner, LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null);

        CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(Window? owner, CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null);

        void ShowPropertyEditorDialog(Window? owner, string title, FrameworkElement editor);

        void ShowReadOnlyTextDialog(Window? owner, string title, string text);

        void ShowWarning(Window? owner, string title, string message);

        /// <summary>Asks whether to save, discard, or cancel before replacing a dirty document.</summary>
        MessageBoxResult ConfirmUnsavedDocumentChanges(Window? owner) => owner == null
            ? MessageBoxResult.Cancel
            : MessageBox.Show(owner, UiText.Get("UnsavedDocumentReplaceMessage"), UiText.Get("UnsavedDocumentTitle"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
    }
}
