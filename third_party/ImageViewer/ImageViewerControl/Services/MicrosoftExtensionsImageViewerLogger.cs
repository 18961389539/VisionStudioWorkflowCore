using System;
using ImageViewer.Abstractions;
using Microsoft.Extensions.Logging;

namespace ImageViewer.Services
{
    public sealed class MicrosoftExtensionsImageViewerLogger : IImageViewerLogger
    {
        private static readonly Action<ILogger, string, Exception?> LogInformationMessage = LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, "ImageViewerInformation"),
            "{ImageViewerMessage}");
        private static readonly Action<ILogger, string, Exception?> LogWarningMessage = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "ImageViewerWarning"),
            "{ImageViewerMessage}");
        private static readonly Action<ILogger, string, Exception?> LogErrorMessage = LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(3, "ImageViewerError"),
            "{ImageViewerMessage}");
        private readonly ILogger<MicrosoftExtensionsImageViewerLogger> _logger;

        public MicrosoftExtensionsImageViewerLogger(ILogger<MicrosoftExtensionsImageViewerLogger> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void LogInfo(string message)
        {
            LogInformationMessage(_logger, message, null);
        }

        public void LogWarning(string message)
        {
            LogWarningMessage(_logger, message, null);
        }

        public void LogError(string message, Exception? exception = null)
        {
            LogErrorMessage(_logger, message, exception);
        }
    }
}