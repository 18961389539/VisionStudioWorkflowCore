using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using ImageViewer.Services;

namespace ImageViewer.Models
{
    public enum ImageInputKind
    {
        SingleImage,
        Volume
    }

    public sealed record ImageInputProbeResult(ImageInputKind Kind, BitmapSource? Image, VolumeData? Volume, IReadOnlyList<string> Warnings);

    public sealed record DisplaySettingsSuggestion(double Contrast, double Brightness, bool UsePseudoColor, string Reason);

    public enum VolumeAnomalyKind
    {
        BlankSlice,
        OverexposedSlice,
        UnderexposedSlice,
        LowContrastSlice,
        MissingOrInconsistentSlice
    }

    public sealed record VolumeAnomaly(VolumeAnomalyKind Kind, int SliceIndex, string Message);

    public sealed record VolumeQualityReport(IReadOnlyList<VolumeAnomaly> Anomalies)
    {
        public bool HasAnomalies => Anomalies.Count > 0;
    }

    public sealed record RenderCapability(bool SupportsGpu, bool UseGpuRendering, bool UseCpuFallback, string Reason);

    public sealed record SegmentationResult(int SliceIndex, IReadOnlyList<BlobFeature> Blobs, int Threshold);
}
