using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;
using IntelligenceRenderCapability = ImageViewer.Models.RenderCapability;

namespace ImageViewerControl.Tests
{
    public class VolumeIntelligenceServicesTests
    {
        [Fact]
        public void InputProbe_RecognizesSingleImageAndVolume()
        {
            BitmapSource first = CreateBitmap(2, 2, 10);
            BitmapSource second = CreateBitmap(2, 2, 20);

            ImageInputProbeResult single = ImageInputProbeService.Probe([first]);
            ImageInputProbeResult volume = ImageInputProbeService.Probe([first, second]);

            Assert.Equal(ImageInputKind.SingleImage, single.Kind);
            Assert.Same(first, single.Image);
            Assert.Equal(ImageInputKind.Volume, volume.Kind);
            Assert.Equal(2, volume.Volume!.Depth);
        }

        [Fact]
        public void SliceService_BuildsCoronalAndSagittalSlices()
        {
            var volume = new VolumeData([
                CreateBitmap(2, 3, 10, 20, 30, 40, 50, 60),
                CreateBitmap(2, 3, 70, 80, 90, 100, 110, 120)]);

            BitmapSource coronal = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Coronal, 1);
            BitmapSource sagittal = VolumeSliceService.GetSlice(volume, VolumeSliceOrientation.Sagittal, 1);

            Assert.Equal(2, coronal.PixelWidth);
            Assert.Equal(2, coronal.PixelHeight);
            Assert.Equal(2, sagittal.PixelWidth);
            Assert.Equal(3, sagittal.PixelHeight);
            Assert.Equal((byte)90, ReadPixel(coronal, 0, 1));
            Assert.Equal((byte)100, ReadPixel(coronal, 1, 1));
            Assert.Equal((byte)20, ReadPixel(sagittal, 0, 0));
            Assert.Equal((byte)80, ReadPixel(sagittal, 1, 0));
        }

        [Fact]
        public void QualityAnalyzer_FindsOverexposedAndLowContrastSlices()
        {
            var volume = new VolumeData([CreateBitmap(4, 4, 255), CreateBitmap(4, 4, 100)]);

            VolumeQualityReport report = VolumeQualityAnalyzer.Analyze(volume);

            Assert.Contains(report.Anomalies, anomaly => anomaly.Kind == VolumeAnomalyKind.OverexposedSlice && anomaly.SliceIndex == 0);
            Assert.Contains(report.Anomalies, anomaly => anomaly.Kind == VolumeAnomalyKind.LowContrastSlice && anomaly.SliceIndex == 1);
        }

        [Fact]
        public void QualityAnalyzer_UsesInjectedHistogramThresholds()
        {
            var volume = new VolumeData([CreateBitmap(2, 2, 210), CreateBitmap(2, 2, 100)]);
            ImageAnalysisQualityProfile profile = ImageAnalysisQualityProfile.Default with
            {
                VolumeOverexposedMinimumBin = 200,
                VolumeLowContrastMinimumRange = 0
            };

            VolumeQualityReport report = VolumeQualityAnalyzer.Analyze(volume, profile);

            Assert.Contains(report.Anomalies, anomaly => anomaly.Kind == VolumeAnomalyKind.OverexposedSlice && anomaly.SliceIndex == 0);
            Assert.DoesNotContain(report.Anomalies, anomaly => anomaly.Kind == VolumeAnomalyKind.LowContrastSlice);
        }

        [Fact]
        public void DisplaySettingsSuggestion_LowContrastImage_RecommendsPseudoColor()
        {
            BitmapSource bitmap = CreateBitmap(4, 4, 100);

            DisplaySettingsSuggestion suggestion = DisplaySettingsSuggestionService.Suggest(bitmap);

            Assert.True(suggestion.UsePseudoColor);
            Assert.Equal(1, suggestion.Contrast);
            Assert.Equal("Low contrast detected.", suggestion.Reason);
        }

        [Fact]
        public void RenderCapabilityService_UsesCpuFallbackWhenGpuUnavailable()
        {
            var service = new RenderCapabilityService(new FixedRenderCapabilityProbe(false));

            IntelligenceRenderCapability capability = service.Resolve(preferGpu: true, allowCpuFallback: true);

            Assert.False(capability.SupportsGpu);
            Assert.False(capability.UseGpuRendering);
            Assert.True(capability.UseCpuFallback);
        }

        [Fact]
        public void SegmentationPipeline_ReturnsDetectedBlob()
        {
            BitmapSource bitmap = CreateBitmap(4, 4,
                0, 0, 0, 0,
                0, 255, 255, 0,
                0, 255, 255, 0,
                0, 0, 0, 0);

            SegmentationResult result = SegmentationPipelineService.Segment(bitmap, new Rect(0, 0, 4, 4), useOtsu: false, threshold: 128, minArea: 1);

            Assert.Single(result.Blobs);
            Assert.Equal(4, result.Blobs[0].Area);
        }

        private static BitmapSource CreateBitmap(int width, int height, params byte[] values)
        {
            if (values.Length == 1)
            {
                byte value = values[0];
                values = new byte[width * height];
                Array.Fill(values, value);
            }

            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, values, width);
        }

        private static byte ReadPixel(BitmapSource bitmap, int x, int y)
        {
            byte[] value = new byte[1];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), value, 1, 0);
            return value[0];
        }

        private sealed class FixedRenderCapabilityProbe : IRenderCapabilityProbe
        {
            public FixedRenderCapabilityProbe(bool supportsGpu)
            {
                SupportsGpu = supportsGpu;
            }

            public bool SupportsGpu { get; }
        }
    }
}
