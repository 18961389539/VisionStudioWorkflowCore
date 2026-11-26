using System;
using System.Threading.Tasks;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerBackgroundOperationObserver
    {
        private readonly Action<string, Exception> _logError;

        public ImageViewerBackgroundOperationObserver(Action<string, Exception> logError)
        {
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        public async Task ObserveAsync(Task operation, string operationName)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

            try
            {
                await operation;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logError(operationName, ex);
            }
        }
    }
}