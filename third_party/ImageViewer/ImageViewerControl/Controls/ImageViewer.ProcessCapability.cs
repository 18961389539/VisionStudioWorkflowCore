using System;
using System.Collections.Generic;
using System.Windows;
using ImageViewer.Dialogs;
using ImageViewer.Services;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private ProcessCapabilityDialog? _processCapabilityDialog;
    private bool _processCapabilityDisposed;

    /// <summary>
    /// Opens the process capability and Xbar-R control chart window for the current ROIs.
    /// Chinese: 窗口持有的是打开时的测量快照；继续增删 ROI 不会改写已经展示的过程能力结果。
    /// </summary>
    public void ShowProcessCapability()
    {
        ObjectDisposedException.ThrowIf(_processCapabilityDisposed, this);
        if (_processCapabilityDialog != null)
        {
            _processCapabilityDialog.Activate();
            return;
        }

        IReadOnlyList<SpcMeasurementSeries> series = SpcMeasurementSeriesBuilder.BuildFromRois(
            ViewerState.AllRois,
            PixelSize,
            PhysicalUnit,
            Calibration);
        var dialog = new ProcessCapabilityDialog(series);
        Window? owner = Window.GetWindow(this);
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        dialog.Resources.MergedDictionaries.Add(ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
        _processCapabilityDialog = dialog;
        Unloaded += OnProcessCapabilityViewerUnloaded;
        dialog.Closed += (_, _) =>
        {
            Unloaded -= OnProcessCapabilityViewerUnloaded;
            _processCapabilityDialog = null;
        };
        dialog.Show();
    }

    private void OnProcessCapabilityViewerUnloaded(object sender, RoutedEventArgs e) => CloseProcessCapabilityDialog();

    private void CloseProcessCapabilityDialog() => _processCapabilityDialog?.Close();

    private void DisposeProcessCapabilityDialog()
    {
        _processCapabilityDisposed = true;
        CloseProcessCapabilityDialog();
    }
}
