using System;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Abstractions;
using ImageViewer.Models;

namespace ImageViewer.Dialogs
{
    public sealed class ImageViewerDialogService : IImageViewerDialogService
    {
        public string? ShowLabelDialog(Window? owner, string message, string defaultValue)
        {
            var dialog = new TextInputDialog(message, defaultValue)
            {
                Owner = owner
            };

            return dialog.ShowDialog() == true ? dialog.Input : null;
        }

        public CalibrationDialogResult? ShowCalibrationDialog(Window? owner, string currentUnit, CameraCalibration? currentCalibration)
        {
            var dialog = new CalibrationDialog(currentUnit, currentCalibration)
            {
                Owner = owner
            };

            return dialog.ShowDialog() == true
                ? new CalibrationDialogResult(dialog.Length, dialog.Unit, dialog.Distortion)
                : null;
        }

        public CaliperMeasureRoi? ShowLineMeasureCaliperSettingsDialog(Window? owner, CaliperMeasureRoi roi, Action<CaliperMeasureRoi>? previewAction = null)
        {
            ArgumentNullException.ThrowIfNull(roi);

            var dialog = new LineMeasureCaliperSettingsDialog((CaliperMeasureRoi)roi.Clone(), previewAction)
            {
                Owner = owner
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }

        public LineCaliperMeasureRoi? ShowLineCaliperSettingsDialog(Window? owner, LineCaliperMeasureRoi roi, Action<LineCaliperMeasureRoi>? previewAction = null)
        {
            ArgumentNullException.ThrowIfNull(roi);

            var dialog = new LineCaliperSettingsDialog((LineCaliperMeasureRoi)roi.Clone(), previewAction)
            {
                Owner = owner
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }

        public CircularCaliperMeasureRoi? ShowCircularCaliperSettingsDialog(Window? owner, CircularCaliperMeasureRoi roi, Action<CircularCaliperMeasureRoi>? previewAction = null)
        {
            ArgumentNullException.ThrowIfNull(roi);

            var dialog = new CircularCaliperSettingsDialog((CircularCaliperMeasureRoi)roi.Clone(), previewAction)
            {
                Owner = owner
            };

            return dialog.ShowDialog() == true ? dialog.Result : null;
        }

        public void ShowPropertyEditorDialog(Window? owner, string title, FrameworkElement editor)
        {
            ArgumentNullException.ThrowIfNull(editor);

            var dialog = new PropertyEditorDialog(title, editor)
            {
                Owner = owner
            };

            dialog.ShowDialog();
        }

        public void ShowReadOnlyTextDialog(Window? owner, string title, string text)
        {
            var dialog = new ReadOnlyTextDialog(title, text)
            {
                Owner = owner
            };

            dialog.ShowDialog();
        }

        /// <summary>Shows scrollable, selectable text and returns true only after explicit confirmation.</summary>
        public static bool ConfirmReadOnlyTextDialog(Window? owner, string title, string text, string confirmLabel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
            var dialog = new ReadOnlyTextDialog(title, text, confirmLabel) { Owner = owner };
            return dialog.ShowDialog() == true;
        }

        public void ShowWarning(Window? owner, string title, string message)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
