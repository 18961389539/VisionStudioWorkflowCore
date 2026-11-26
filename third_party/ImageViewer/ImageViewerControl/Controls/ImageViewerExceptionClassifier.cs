using System;
using System.IO;

namespace ImageViewer.Controls
{
    internal static class ImageViewerExceptionClassifier
    {
        public static string Classify(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return exception switch
            {
                UnauthorizedAccessException => "Permission",
                InvalidDataException => "Data",
                IOException => "IO",
                FormatException => "Format",
                NotSupportedException => "Unsupported",
                _ => "Unexpected"
            };
        }
    }
}