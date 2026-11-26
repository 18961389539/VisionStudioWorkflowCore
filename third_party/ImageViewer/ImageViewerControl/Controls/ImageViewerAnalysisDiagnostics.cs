using System;
using ImageViewer.Abstractions;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerAnalysisDiagnostics : IImageViewerAnalysisErrorSink
    {
        private readonly IImageViewerAnalysisDiagnostics _diagnostics;
        private readonly IImageViewerLogger _logger;

        public ImageViewerAnalysisDiagnostics(IImageViewerAnalysisDiagnostics diagnostics, IImageViewerLogger logger)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void LogNonCriticalError(string message, Exception ex)
        {
            _diagnostics.LogNonCriticalError(_logger, message, ex);
        }
    }
}
