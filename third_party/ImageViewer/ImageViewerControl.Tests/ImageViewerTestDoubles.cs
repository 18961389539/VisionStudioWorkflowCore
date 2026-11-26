using System;
using System.Collections.Generic;
using ImageViewer.Abstractions;
using ImageViewer.Controls;

namespace ImageViewerControl.Tests
{
    internal sealed class RecordingAnalysisDiagnostics : IImageViewerAnalysisDiagnostics
    {
        private readonly List<DiagnosticsEntry> _entries = [];

        public IReadOnlyList<DiagnosticsEntry> Entries => _entries;

        public void LogNonCriticalError(IImageViewerLogger logger, string message, Exception exception)
        {
            _entries.Add(new DiagnosticsEntry(logger, message, exception));
        }
    }

    internal sealed record DiagnosticsEntry(IImageViewerLogger Logger, string Message, Exception Exception);

    internal sealed class RecordingLogger : IImageViewerLogger
    {
        public void LogInfo(string message)
        {
        }

        public void LogWarning(string message)
        {
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }
}
