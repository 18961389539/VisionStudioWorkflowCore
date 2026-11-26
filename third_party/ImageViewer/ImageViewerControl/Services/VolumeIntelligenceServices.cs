using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    public static class ImageInputProbeService
    {
        public static ImageInputProbeResult Probe(IReadOnlyList<BitmapSource> images, double spacingX = 1, double spacingY = 1, double spacingZ = 1)
        {
            ArgumentNullException.ThrowIfNull(images);
            if (images.Count == 0)
            {
                throw new ArgumentException("At least one image is required.", nameof(images));
            }

            if (images.Count == 1)
            {
                return new ImageInputProbeResult(ImageInputKind.SingleImage, images[0], null, Array.Empty<string>());
            }

            List<string> warnings = new();
            try
            {
                VolumeData volume = new(images, spacingX, spacingY, spacingZ);
                return new ImageInputProbeResult(ImageInputKind.Volume, null, volume, warnings);
            }
            catch (ArgumentException exception)
            {
                warnings.Add(exception.Message);
                return new ImageInputProbeResult(ImageInputKind.SingleImage, images[0], null, warnings);
            }
        }
    }

    public static class DisplaySettingsSuggestionService
    {
        public static DisplaySettingsSuggestion Suggest(BitmapSource bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            int[] histogram = ImageAnalysisService.CreateHistogram(bitmap, 256);
            int total = histogram.Sum();
            if (total == 0)
            {
                return new DisplaySettingsSuggestion(1, 0, false, "The image contains no pixels.");
            }

            int low = FindPercentile(histogram, total, 0.02);
            int high = FindPercentile(histogram, total, 0.98);
            double contrast = Math.Max(1, (high - low) / 255.0);
            double brightness = ((low + high) / 2.0 - 127.5) / 127.5;
            bool usePseudoColor = high - low < 32;
            return new DisplaySettingsSuggestion(contrast, -brightness, usePseudoColor, usePseudoColor ? "Low contrast detected." : "Histogram-based display settings.");
        }

        private static int FindPercentile(int[] histogram, int total, double percentile)
        {
            int target = Math.Max(1, (int)(total * percentile));
            int count = 0;
            for (int index = 0; index < histogram.Length; index++)
            {
                count += histogram[index];
                if (count >= target)
                {
                    return index;
                }
            }

            return histogram.Length - 1;
        }
    }

    public sealed class VolumeQualityAnalyzer
    {
        public static VolumeQualityReport Analyze(VolumeData volume, ImageAnalysisQualityProfile? qualityProfile = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ImageAnalysisQualityProfile quality = qualityProfile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();
            List<VolumeAnomaly> anomalies = new();
            for (int index = 0; index < volume.Depth; index++)
            {
                BitmapSource slice = volume.GetAxialSlice(index);
                int[] histogram = ImageAnalysisService.CreateHistogram(slice, 256);
                int total = histogram.Sum();
                int minimum = FindFirst(histogram);
                int maximum = FindLast(histogram);
                if (total == 0 || maximum == 0)
                {
                    anomalies.Add(new VolumeAnomaly(VolumeAnomalyKind.BlankSlice, index, "Slice contains no signal."));
                }
                else if (minimum >= quality.VolumeOverexposedMinimumBin)
                {
                    anomalies.Add(new VolumeAnomaly(VolumeAnomalyKind.OverexposedSlice, index, "Slice is saturated."));
                }
                else if (maximum <= quality.VolumeUnderexposedMaximumBin)
                {
                    anomalies.Add(new VolumeAnomaly(VolumeAnomalyKind.UnderexposedSlice, index, "Slice is nearly black."));
                }
                else if (maximum - minimum < quality.VolumeLowContrastMinimumRange)
                {
                    anomalies.Add(new VolumeAnomaly(VolumeAnomalyKind.LowContrastSlice, index, "Slice has low contrast."));
                }
            }

            return new VolumeQualityReport(anomalies);
        }

        private static int FindFirst(int[] histogram)
        {
            for (int index = 0; index < histogram.Length; index++)
            {
                if (histogram[index] > 0) return index;
            }
            return 0;
        }

        private static int FindLast(int[] histogram)
        {
            for (int index = histogram.Length - 1; index >= 0; index--)
            {
                if (histogram[index] > 0) return index;
            }
            return 0;
        }
    }

    public interface IRenderCapabilityProbe
    {
        bool SupportsGpu { get; }
    }

    public sealed class RenderCapabilityService
    {
        private readonly IRenderCapabilityProbe _probe;

        public RenderCapabilityService(IRenderCapabilityProbe probe)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public RenderCapability Resolve(bool preferGpu, bool allowCpuFallback)
        {
            if (preferGpu && _probe.SupportsGpu)
            {
                return new RenderCapability(true, true, false, "GPU rendering is available.");
            }

            return new RenderCapability(_probe.SupportsGpu, false, allowCpuFallback, _probe.SupportsGpu ? "GPU rendering was not requested." : "GPU rendering is unavailable; using CPU fallback.");
        }
    }

    public sealed class SegmentationPipelineService
    {
        public static SegmentationResult Segment(BitmapSource bitmap, Rect roi, bool useOtsu = true, int threshold = 128, int minArea = 10, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            List<BlobFeature> blobs = BlobAnalysisService.DetectBlobs(bitmap, roi, useOtsu, threshold, minArea: minArea, cancellationToken: cancellationToken);
            return new SegmentationResult(0, blobs, threshold);
        }
    }
}
