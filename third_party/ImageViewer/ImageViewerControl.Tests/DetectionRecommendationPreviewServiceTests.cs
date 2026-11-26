using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
[Trait("Category", "Accuracy")]
public sealed class DetectionRecommendationPreviewServiceTests
{
    [Fact]
    public void KnownEdgeRunsBothRealTrialsWithoutMutatingSourceRoi()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateVerticalStep(128, edgeX: 64);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(60, 20),
                P2 = new PointD(60, 108),
                CaliperCount = 16,
                CaliperSearchRange = 18,
                CaliperMinimumGradient = 8,
                MinimumValidCalipers = 8,
            };

            DetectionRecommendationPreview preview = DetectionRecommendationPreviewService.Create(
                bitmap,
                roi,
                ImageAnalysisQualityProfile.Default);

            Assert.True(preview.Proposal.Success, preview.Proposal.FailureKey);
            Assert.True(preview.Before.Success, preview.Before.StatusKey);
            Assert.True(preview.After.Success, preview.After.StatusKey);
            Assert.True(preview.CanApply, preview.ApplyBlockedKey);
            Assert.NotEmpty(preview.Before.Points);
            Assert.NotEmpty(preview.After.Points);
            Assert.NotSame(roi, preview.Before.DetectedRoi);
            Assert.NotSame(roi, preview.After.DetectedRoi);
            Assert.False(roi.HasDetectedLine);
            Assert.Equal(16, roi.CaliperCount);
            Assert.Equal(18, roi.CaliperSearchRange);
            Assert.Equal(new PointD(60, 20), roi.P1);
        });
    }

    [Fact]
    public void UniformNoiseFreeImageRejectsRecommendationAndSkipsTrials()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateConstant(96, 128);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(48, 12),
                P2 = new PointD(48, 84),
            };

            DetectionRecommendationPreview preview = DetectionRecommendationPreviewService.Create(
                bitmap,
                roi,
                ImageAnalysisQualityProfile.Default);

            Assert.False(preview.Proposal.Success);
            Assert.False(preview.CanApply);
            Assert.Equal(preview.Proposal.FailureKey, preview.Before.StatusKey);
            Assert.Equal(preview.Proposal.FailureKey, preview.After.StatusKey);
            Assert.Null(preview.Before.DetectedRoi);
            Assert.Null(preview.After.DetectedRoi);
            Assert.False(roi.HasDetectedLine);
        });
    }

    [Fact]
    public void StricterProjectQualityGateBlocksCandidateTrial()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateVerticalStep(128, edgeX: 64, edgeValue: 32);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(60, 20),
                P2 = new PointD(60, 108),
                CaliperCount = 16,
                CaliperSearchRange = 18,
            };
            ImageAnalysisQualityProfile strictQuality = ImageAnalysisQualityProfile.Default with
            {
                MinimumDetectionConfidence = 0.9,
            };

            DetectionRecommendationPreview preview = DetectionRecommendationPreviewService.Create(bitmap, roi, strictQuality);

            Assert.True(preview.Proposal.Success, preview.Proposal.FailureKey);
            Assert.False(preview.After.Success);
            Assert.False(preview.CanApply);
            Assert.Equal(16, roi.CaliperCount);
            Assert.Equal(18, roi.CaliperSearchRange);
        });
    }

    [Fact]
    public void DualEdgeTrialRetainsOriginalCaliperGeometryWhileKeepingDetectedState()
    {
        WpfTestRunner.Run(() =>
        {
            const int width = 128;
            const int height = 100;
            byte[] pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = x is >= 54 and <= 73 ? (byte)220 : (byte)20;
            }

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(42, 50),
                P2 = new PointD(86, 50),
                CaliperCenter = new PointD(17, 29),
                CaliperAngleDegrees = 23,
                CaliperRegionLength = 31,
                HasExplicitCaliperRegion = false,
                CaliperCount = 12,
                CaliperSearchRange = 24,
                CaliperSamplingHalfWidth = 1,
                CaliperMinimumGradient = 7,
                MinimumValidCalipers = 5,
            };

            DetectionRecommendationPreview preview = DetectionRecommendationPreviewService.Create(
                bitmap,
                roi,
                ImageAnalysisQualityProfile.Default);

            Assert.True(preview.Before.Success, preview.Before.StatusKey);
            Assert.True(preview.After.Success, preview.After.StatusKey);
            CaliperMeasureRoi detected = Assert.IsType<CaliperMeasureRoi>(preview.After.DetectedRoi);
            Assert.Equal(new PointD(17, 29), detected.CaliperCenter);
            Assert.Equal(23, detected.CaliperAngleDegrees);
            Assert.Equal(31, detected.CaliperRegionLength);
            Assert.False(detected.HasExplicitCaliperRegion);
            Assert.True(detected.HasDetectedEdges);
            Assert.Equal(new PointD(42, 50), detected.P1);
            Assert.Equal(new PointD(86, 50), detected.P2);
            Assert.False(roi.HasDetectedEdges);
        });
    }

    [Fact]
    public void PreCancelledRecommendationDoesNotRunAnyTrial()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateVerticalStep(128, edgeX: 64);
            using var cancellation = new System.Threading.CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => DetectionRecommendationPreviewService.Create(
                bitmap,
                new LineCaliperMeasureRoi { P1 = new PointD(60, 20), P2 = new PointD(60, 108) },
                ImageAnalysisQualityProfile.Default,
                cancellation.Token));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CircularAndArcRecommendationsRunRadialDetection(bool isArc)
    {
        WpfTestRunner.Run(() =>
        {
            const int size = 128;
            var pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = Math.Pow(x - 64, 2) + Math.Pow(y - 64, 2) < 24 * 24 ? (byte)25 : (byte)225;
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
            bitmap.Freeze();
            CircularCaliperMeasureRoi roi = isArc ? new ArcCaliperMeasureRoi { StartAngle = -80, SweepAngle = 160 } : new CircularCaliperMeasureRoi();
            roi.Center = new PointD(64, 64);
            roi.Radius = 24;
            roi.CaliperCount = 24;
            roi.MinimumValidCalipers = 12;
            roi.CaliperSearchRange = 10;
            roi.CaliperSamplingHalfWidth = 1;
            var preview = DetectionRecommendationPreviewService.Create(bitmap, roi, ImageAnalysisQualityProfile.Default);
            Assert.True(preview.Proposal.Success, preview.Proposal.FailureKey);
            Assert.True(preview.Before.Success, "Before: " + preview.Before.StatusKey);
            Assert.True(preview.After.Success, "After: " + preview.After.StatusKey);
            Assert.False(roi.HasDetectedEdges);
            Assert.All(preview.After.Points, point => Assert.InRange(point.DistanceTo(new PointD(64, 64)), 22, 26));
            if (isArc)
            {
                var arc = Assert.IsType<ArcCaliperMeasureRoi>(preview.After.DetectedRoi);
                Assert.Equal(-80, arc.StartAngle);
                Assert.Equal(160, arc.SweepAngle);
            }
        });
    }

    [Fact]
    public void BlobRecommendationUsesNativeSegmentationAndShowsActualBoundary()
    {
        WpfTestRunner.Run(() =>
        {
            const int size = 128;
            var pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = x is >= 48 and < 80 && y is >= 48 and < 80 ? (byte)210 : (byte)35;
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
            bitmap.Freeze();
            var roi = new BlobAnalysisRoi { Center = new PointD(64, 64), Width = 80, Height = 80, UseOtsu = false, ManualThreshold = 90 };
            var preview = DetectionRecommendationPreviewService.Create(bitmap, roi, ImageAnalysisQualityProfile.Default);
            Assert.True(preview.Proposal.Success, preview.Proposal.FailureKey);
            Assert.True(preview.Before.Success, preview.Before.StatusKey);
            Assert.True(preview.After.Success, preview.After.StatusKey);
            Assert.True(preview.CanApply, preview.ApplyBlockedKey);
            Assert.True(preview.After.Points.Length >= 4);
            Assert.Null(preview.After.Score);
            Assert.All(preview.After.Points, point => Assert.True(point.X is 48 or 80 || point.Y is 48 or 80));
            Assert.Empty(roi.DetectedBlobs);
            Assert.False(roi.UseOtsu);
        });
    }

    private static BitmapSource CreateVerticalStep(int size, int edgeX, byte edgeValue = byte.MaxValue)
    {
        byte[] pixels = new byte[size * size];
        for (int y = 0; y < size; y++)
        for (int x = edgeX; x < size; x++)
        {
            pixels[y * size + x] = edgeValue;
        }

        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateConstant(int size, byte value)
    {
        byte[] pixels = new byte[size * size];
        Array.Fill(pixels, value);
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
