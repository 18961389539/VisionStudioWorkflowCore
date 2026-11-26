using System;
using System.IO;
using System.Windows;
using ImageViewer.Core.Analysis;
using ImageViewer.Dialogs;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private PrintableReportDialog? _printableReportDialog;
    private bool _printableReportDisposed;

    /// <summary>
    /// Opens the printable report window for the current measurement snapshot.
    /// Chinese: 报告模型与“导出分析 CSV”的 Markdown 报告同源；窗口打开后继续增删 ROI 不会改写已生成的内容。
    /// English: Same report model as the Markdown export; the window keeps the snapshot taken at open time.
    /// </summary>
    public void ShowPrintableReport()
    {
        ObjectDisposedException.ThrowIf(_printableReportDisposed, this);
        if (RoiToleranceDrafts.TryGetError(ViewerState.AllRois, out string? message))
        {
            ShowStatusHint(message, StatusHintKind.Error);
            return;
        }
        if (_printableReportDialog != null)
        {
            _printableReportDialog.Activate();
            return;
        }

        string? sourcePath = _controlComposition.ViewportController.TryGetCurrentImagePath();
        MeasurementReport report = RoiAnalysisExportService.BuildReportModel(
            ViewerState.AllRois,
            PixelSize,
            PhysicalUnit,
            Calibration,
            sourceName: string.IsNullOrWhiteSpace(sourcePath) ? null : Path.GetFileName(sourcePath));
        var spcRows = SpcReportFormatter.CreateRows(
            SpcMeasurementSeriesBuilder.BuildFromRois(ViewerState.AllRois, PixelSize, PhysicalUnit, Calibration));

        var dialog = new PrintableReportDialog(
            report,
            spcRows,
            () => FileDialogService.ShowSaveXpsDialog(Window.GetWindow(this)));
        Window? owner = Window.GetWindow(this);
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        dialog.Resources.MergedDictionaries.Add(ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
        _printableReportDialog = dialog;
        Unloaded += OnPrintableReportViewerUnloaded;
        dialog.Closed += (_, _) =>
        {
            Unloaded -= OnPrintableReportViewerUnloaded;
            _printableReportDialog = null;
        };
        dialog.Show();
    }

    private void OnPrintableReportViewerUnloaded(object sender, RoutedEventArgs e) => ClosePrintableReportDialog();

    private void ClosePrintableReportDialog() => _printableReportDialog?.Close();

    private void DisposePrintableReportDialog()
    {
        _printableReportDisposed = true;
        ClosePrintableReportDialog();
    }
}
