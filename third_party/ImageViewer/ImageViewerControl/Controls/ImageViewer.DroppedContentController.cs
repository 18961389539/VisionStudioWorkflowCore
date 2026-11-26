using System;
using System.Threading.Tasks;
using System.Windows;
using ImageViewer.Localization;
using ImageViewer.Utils;

namespace ImageViewer.Controls
{
    internal sealed class DroppedContentController
    {
        private readonly Func<string, Task> _openImageFromPathAsync;
        private readonly ImageViewerSessionController _sessionController;
        private readonly Action _refreshSelectedRoiPropertyPanel;
        private readonly Action<string, StatusHintKind> _showStatusHint;

        public DroppedContentController(
            Func<string, Task> openImageFromPathAsync,
            ImageViewerSessionController sessionController,
            Action refreshSelectedRoiPropertyPanel,
            Action<string, StatusHintKind> showStatusHint)
        {
            _openImageFromPathAsync = openImageFromPathAsync ?? throw new ArgumentNullException(nameof(openImageFromPathAsync));
            _sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
            _refreshSelectedRoiPropertyPanel = refreshSelectedRoiPropertyPanel ?? throw new ArgumentNullException(nameof(refreshSelectedRoiPropertyPanel));
            _showStatusHint = showStatusHint ?? throw new ArgumentNullException(nameof(showStatusHint));
        }

        public static void HandleDragOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths && DroppedFileHelper.TryGetOpenablePath(paths, out _, out _))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        public async Task HandleDropAsync(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return;
            }

            if (DroppedFileHelper.TryGetOpenablePath(paths, out string path, out DroppedFileKind kind))
            {
                await OpenDroppedPathAsync(path, kind);
                return;
            }

            // 拖入的文件没有可打开类型时给出明确提示，避免用户面对"无反应"的困惑
            _showStatusHint(UiText.Get("StatusDropUnsupported"), StatusHintKind.Error);
        }

        private async Task OpenDroppedPathAsync(string path, DroppedFileKind kind)
        {
            switch (kind)
            {
                case DroppedFileKind.Image:
                    await _openImageFromPathAsync(path);
                    break;
                case DroppedFileKind.Session:
                case DroppedFileKind.ProjectPackage:
                    await _sessionController.OpenProjectAsync(path);
                    break;
            }

            _refreshSelectedRoiPropertyPanel();
        }
    }
}
