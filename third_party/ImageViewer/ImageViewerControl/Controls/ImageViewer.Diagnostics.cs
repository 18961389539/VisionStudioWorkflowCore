using System;
using System.Threading.Tasks;
using System.Windows;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private ImageViewerBackgroundOperationObserver? _backgroundOperationObserver;

        private void LogNonCriticalError(string context, Exception ex)
        {
            Logger.LogError(context, ex);
        }

        private ImageViewerBackgroundOperationObserver BackgroundOperationObserver =>
            _backgroundOperationObserver ??= new ImageViewerBackgroundOperationObserver(LogNonCriticalError);

        internal void ShowNonCriticalError(string title, string message, Exception ex)
        {
            LogNonCriticalError(title, ex);
            DiagnosticErrorText = $"{title}: {message}";
            HasDiagnosticError = true;
            if (Window.GetWindow(this) != null)
            {
                _dialogWorkflowService.ShowWarning(title, message);
            }
        }

        private async Task RunUiOperationAsync(string operationName, Func<Task> operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
            ArgumentNullException.ThrowIfNull(operation);

            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
                Logger.LogInfo($"UI operation canceled: {operationName}; category=Cancellation");
            }
            catch (Exception ex)
            {
                ReportUiOperationFailure(operationName, ex);
                string title = UiText.Get("ErrorUiOperationFailedTitle");
                string message = UiText.Format("ErrorUiOperationFailedMessage", operationName);
                _dialogWorkflowService.ShowWarning(title, message);
            }
        }

        /// <summary>
        /// 非阻塞地记录一次 UI 操作失败：写日志并显示诊断横幅，不弹出模态对话框。
        /// Chinese: 用于高频输入路径（如键盘快捷键）的失败兜底，避免异常击穿 async void 事件处理器。
        /// English: Records a UI operation failure without a modal dialog, for high-frequency input paths.
        /// </summary>
        private void ReportUiOperationFailure(string operationName, Exception ex)
        {
            string category = ImageViewerExceptionClassifier.Classify(ex);
            Logger.LogError($"UI operation failed: {operationName}; category={category}", ex);
            string title = UiText.Get("ErrorUiOperationFailedTitle");
            DiagnosticErrorText = $"{title} [{category}]：{operationName}。";
            HasDiagnosticError = true;
        }

        private async Task RunShutdownOperationAsync(string operationName, Func<Task> operation)
        {
            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
                Logger.LogInfo($"Shutdown operation canceled: {operationName}; category=Cancellation");
            }
            catch (Exception ex)
            {
                string category = ImageViewerExceptionClassifier.Classify(ex);
                Logger.LogError($"Shutdown operation failed: {operationName}; category={category}", ex);
            }
        }

        private void DismissDiagnosticError()
        {
            HasDiagnosticError = false;
            DiagnosticErrorText = string.Empty;
        }

        internal static RoiStateCommand CreateStateCommand(RoiBase roi, RoiBase oldState, RoiBase newState)
        {
            return new RoiStateCommand(roi, oldState, newState);
        }
    }
}
