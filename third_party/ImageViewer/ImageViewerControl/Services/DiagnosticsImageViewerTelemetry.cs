using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImageViewer.Controls;

namespace ImageViewer.Services
{
    public sealed class DiagnosticsImageViewerTelemetry : IImageViewerTelemetry
    {
        internal const string InstrumentationName = "ImageViewer";

        private static readonly ActivitySource ActivitySource = new(InstrumentationName);
        private static readonly Meter Meter = new(InstrumentationName);
        private static readonly Counter<long> NonCriticalErrorCounter = Meter.CreateCounter<long>(
            "imageviewer.analysis.noncritical_errors",
            unit: "errors",
            description: "Number of non-critical image analysis errors.");

        public void RecordNonCriticalError(string operation, Exception exception)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(exception);

            var tags = new TagList
            {
                { "imageviewer.operation", operation },
                { "error.type", exception.GetType().FullName ?? exception.GetType().Name }
            };
            NonCriticalErrorCounter.Add(1, tags);

            using Activity? activity = ActivitySource.StartActivity("imageviewer.analysis.error");
            activity?.SetTag("imageviewer.operation", operation);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.SetTag("error.type", exception.GetType().FullName);
        }
    }
}