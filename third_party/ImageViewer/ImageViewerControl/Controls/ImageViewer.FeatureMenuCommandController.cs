using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal interface IImageViewerFeatureAnalysisCapability
    {
        RoiBase? SelectedRoi { get; }
        BitmapSource? AnalysisBitmapSource { get; }
        ImageAnalysisQualityProfile QualityProfile { get; }
    }

    internal interface IImageViewerFeatureMutationCapability
    {
        IUndoRedoCommand? CreateStateCommand(RoiBase roi, RoiBase oldState, RoiBase newState);
        void ExecuteUndoRedoCommand(IUndoRedoCommand command);
        void DrawRois();
    }

    internal interface IImageViewerFeatureExportDataCapability
    {
        FrameworkElement RenderRoot { get; }
        IReadOnlyList<RoiBase> AllRois { get; }
        double PixelSize { get; }
        string PhysicalUnit { get; }
        CameraCalibration? Calibration { get; }
        string? CurrentImagePath { get; }
        RoiPluginRegistry PluginRegistry { get; }
        IReadOnlyDictionary<string, string> RenderSettings { get; }
    }

    internal interface IImageViewerFeatureDialogCapability
    {
        string? ShowSaveSnapshotDialog();
        string? ShowSaveAnalysisCsvDialog();
        string[] ShowOpenBatchImageFilesDialog();
        void ShowReadOnlyText(string title, string text);
        void ShowProcessCapability();
        void ShowPrintableReport();
    }

    internal interface IImageViewerFeatureFeedbackCapability
    {
        void ShowNonCriticalError(string title, string message, Exception exception);
        void ShowStatusHint(string message, StatusHintKind kind);
        void UpdateContextMenuState();
    }

    internal interface IImageViewerFeatureMenuCommandHost
    {
        void RunGradientDetection();

        Task ExportSnapshotAsync();

        Task ExportAnalysisCsvAsync();

        Task ExportBatchAnalysisCsvAsync();

        bool CancelBatchAnalysisExport() => false;

        void ShowAnalysisSummary();

        /// <summary>过程能力与 Xbar-R 控制图窗口。</summary>
        void ShowProcessCapability();

        /// <summary>可打印测量报告（打印 / 导出 XPS）。</summary>
        void ShowPrintableReport();

        /// <summary>“标定检查”步骤：在测量/出报告前确认数值是否可信。</summary>
        void CheckCalibrationReadiness();

        void UpdateContextMenuState();
    }

    internal sealed class ImageViewerFeatureMenuCommandController : ImageViewerMenuCommandControllerBase<IImageViewerFeatureMenuCommandHost>
    {
        public ImageViewerFeatureMenuCommandController(IImageViewerFeatureMenuCommandHost host)
            : base(host, host.UpdateContextMenuState)
        {
        }

        public async Task ExecuteAsync(ImageViewerFeatureMenuCommand command)
        {
            switch (command)
            {
                case ImageViewerFeatureMenuCommand.GradientDetect:
                    Host.RunGradientDetection();
                    break;
                case ImageViewerFeatureMenuCommand.ExportSnapshot:
                    await Host.ExportSnapshotAsync();
                    break;
                case ImageViewerFeatureMenuCommand.ExportAnalysisCsv:
                    await Host.ExportAnalysisCsvAsync();
                    break;
                case ImageViewerFeatureMenuCommand.ExportBatchAnalysisCsv:
                    await Host.ExportBatchAnalysisCsvAsync();
                    break;
                case ImageViewerFeatureMenuCommand.ShowAnalysisSummary:
                    Host.ShowAnalysisSummary();
                    break;
                case ImageViewerFeatureMenuCommand.ShowProcessCapability:
                    Host.ShowProcessCapability();
                    break;
                case ImageViewerFeatureMenuCommand.PrintReport:
                    Host.ShowPrintableReport();
                    break;
                case ImageViewerFeatureMenuCommand.CheckCalibrationReadiness:
                    Host.CheckCalibrationReadiness();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }

            RefreshMenuState();
        }

        public bool CancelBatchAnalysisExport() => Host.CancelBatchAnalysisExport();
    }
}
