using System;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerLogger
    {
        void LogInfo(string message);

        void LogWarning(string message);

        void LogError(string message, Exception? exception = null);
    }
}
