using System;

namespace ImageViewer.Controls
{
    internal interface IImageViewerAnalysisErrorSink
    {
        void LogNonCriticalError(string message, Exception ex);
    }
}
