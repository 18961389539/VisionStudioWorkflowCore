using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        internal ImageViewerDialogWorkflowService? SharedFileImageWorkflow { get; set; }
        internal FrameworkElement? DocumentDialogOwner { get; set; }
        internal ImageViewerDialogWorkflowService FileImageWorkflow => SharedFileImageWorkflow ?? _dialogWorkflowService;
        internal Func<ImageSource, CancellationToken, Task<Action?>>? PrepareHostedImageReplacementAsync { get; set; }
        internal Func<IDisposable>? SuspendImageReplacementWrites { get; set; }
        internal event Action<bool, string, double, bool>? ImageLoadStateChanged;

        internal Window? GetDocumentDialogWindow() => Window.GetWindow(DocumentDialogOwner ?? this);

        private Task<Action?> PrepareOpenedImageAsync(ImageSource source, CancellationToken token)
            => PrepareHostedImageReplacementAsync?.Invoke(source, token)
                ?? _sessionController.PrepareImageDocumentAsync(
                    () => SetCurrentValue(ImageSourceProperty, source), markDirty: true, token);

        internal Task<Action?> PrepareDocumentContentAsync(
            Func<CancellationToken, Task<Action?>> prepareContent, CancellationToken token)
            => _sessionController.PrepareImageDocumentAsync(prepareContent, markDirty: true, token);
    }
}
