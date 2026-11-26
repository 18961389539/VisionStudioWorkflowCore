using System;
using System.Diagnostics;
using ImageViewer.Abstractions;

namespace ImageViewer.Services
{
    public sealed class TraceImageViewerLogger : IImageViewerLogger
    {
        public void LogInfo(string message)
        {
            Trace.TraceInformation(message);
        }

        public void LogWarning(string message)
        {
            Trace.TraceWarning(message);
        }

        public void LogError(string message, Exception? exception = null)
        {
            if (exception == null)
            {
                Trace.TraceError(message);
                return;
            }

            Trace.TraceError($"{message}{Environment.NewLine}{exception}");
        }
    }
}
