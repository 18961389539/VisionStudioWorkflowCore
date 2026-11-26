using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;
using IntensityImage = ImageViewer.Core.Analysis.IntensityImage;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// Managed geometry and measurement accuracy tests.
    /// </summary>
    public class ImageAnalysisServiceAccuracyTests
    {
    }

    /// <summary>
    /// 测量管线集成测试：在合成图像上验证检测精度与低质量判失败。
    /// Chinese: 使用确定性合成位图验证圆/拟合直线检测的亚像素精度与均匀图像拒绝。
    /// English: Integration tests over synthetic bitmaps for the caliper detection pipeline.
    /// </summary>
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Accuracy")]
    public class ImageAnalysisServiceDetectionAccuracyTests
    {
        [Fact]
        public void TryDetectCircularCaliperEdges_SyntheticDisk_RadiusWithinSubpixelTolerance()
        {
            const int size = 40;
            const double centerX = 20.0;
            const double centerY = 20.0;
            const double trueRadius = 8.4;
            Color[] pixels = BuildDiskPixels(size, new Point(centerX, centerY), trueRadius);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            var caliper = new CircularCaliperMeasureRoi
            {
                Center = new PointD(centerX, centerY),
                Radius = 8.0,
                CaliperCount = 24,
                CaliperSearchRange = 5
            };

            bool success = ImageAnalysisService.TryDetectCircularCaliperEdges(bitmap, caliper, out CircularCaliperDetectionResult result);

            Assert.True(success, "合成圆盘应能成功检出。");
            Assert.InRange(result.DetectedRadius, trueRadius - 0.4, trueRadius + 0.4);
        }

        [Fact]
        public void TryDetectLineMeasureEdges_SyntheticVerticalBand_WidthWithinSubpixelTolerance()
        {
            const int size = 40;
            // 垂直亮带：x ∈ [13, 17)，其中右边界像素 16 为中间灰度，模拟半像素偏移边缘。
            Color[] pixels = BuildBandPixels(size, leftStart: 13, bandWidthPixels: 4, boundaryGreyPixel: 16, greyLevel: 127);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            // 测量线水平横跨亮带：搜索沿 x 方向穿过左右两个边缘。
            var line = new CaliperMeasureRoi
            {
                P1 = new PointD(5, 20),
                P2 = new PointD(25, 20),
                CaliperCount = 12,
                CaliperSamplingHalfWidth = 1,
                CaliperMinimumGradient = 10,
                MinimumValidCalipers = 4
            };
            line.EnsureCaliperRegion();

            bool success = ImageAnalysisService.TryDetectLineMeasureEdges(bitmap, line, out LineMeasureGradientDetectionResult result);
            if (!success)
            {
                throw new InvalidOperationException(
                    $"Line detection failed: SearchRange={line.CaliperSearchRange}, RegionLength={line.GetResolvedCaliperRegionLength()}, " +
                    $"MeasurementDirection={line.GetCaliperMeasurementDirection()}, CaliperCenter={line.CaliperCenter}");
            }
            // 理想宽度 = 3.5（左边缘 13，右边缘 16.5 的半像素偏移处），亚像素定位应接近该值，
            // 整像素结果（3 或 4）不满足该区间。
            double detectedWidth = Math.Abs(result.DetectedP1.X - result.DetectedP2.X);
            Assert.InRange(detectedWidth, 3.15, 3.85);
            Assert.True(result.WidthSamples.Length >= line.MinimumValidCalipers);
            Assert.InRange(result.WidthSamples.Average(), 3.15, 3.85);
        }

        [Fact]
        public void TryDetectLineMeasureEdges_SharedIntensity_MatchesBitmapConversion()
        {
            const int size = 40;
            Color[] pixels = BuildBandPixels(size, leftStart: 13, bandWidthPixels: 4, boundaryGreyPixel: 16, greyLevel: 127);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);
            var line = new CaliperMeasureRoi
            {
                P1 = new PointD(5, 20),
                P2 = new PointD(25, 20),
                CaliperCount = 12,
                CaliperSamplingHalfWidth = 1,
                CaliperMinimumGradient = 10,
                MinimumValidCalipers = 4
            };
            line.EnsureCaliperRegion();
            IntensityImage intensity = VisionAlgorithmEntryPoint.CreateIntensity(bitmap);

            Assert.True(ImageAnalysisService.TryDetectLineMeasureEdges(bitmap, line, out LineMeasureGradientDetectionResult direct));
            Assert.True(ImageAnalysisService.TryDetectLineMeasureEdges(
                bitmap, line, out LineMeasureGradientDetectionResult shared, intensity: intensity));

            Assert.Equal(direct.DetectedP1, shared.DetectedP1);
            Assert.Equal(direct.DetectedP2, shared.DetectedP2);
            Assert.Equal(direct.Edge1Points, shared.Edge1Points);
            Assert.Equal(direct.Edge2Points, shared.Edge2Points);
            Assert.Equal(direct.WidthSamples, shared.WidthSamples);
            Assert.Equal(direct.ValidCaliperCount, shared.ValidCaliperCount);
            Assert.Equal(direct.Confidence, shared.Confidence);
        }

        [Fact]
        public void TryDetectCircularCaliperEdges_SharedIntensity_MatchesBitmapConversion()
        {
            const int size = 40;
            Color[] pixels = BuildDiskPixels(size, new Point(20.0, 20.0), 8.4);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);
            var caliper = new CircularCaliperMeasureRoi
            {
                Center = new PointD(20.0, 20.0),
                Radius = 8.0,
                CaliperCount = 24,
                CaliperSearchRange = 5
            };
            IntensityImage intensity = VisionAlgorithmEntryPoint.CreateIntensity(bitmap);

            Assert.True(ImageAnalysisService.TryDetectCircularCaliperEdges(bitmap, caliper, out CircularCaliperDetectionResult direct));
            Assert.True(ImageAnalysisService.TryDetectCircularCaliperEdges(
                bitmap, caliper, out CircularCaliperDetectionResult shared, intensity: intensity));

            Assert.Equal(direct.DetectedCenter, shared.DetectedCenter);
            Assert.Equal(direct.DetectedRadius, shared.DetectedRadius);
            Assert.Equal(direct.EdgePoints, shared.EdgePoints);
            Assert.Equal(direct.ValidCaliperCount, shared.ValidCaliperCount);
            Assert.Equal(direct.Confidence, shared.Confidence);
        }

        [Fact]
        public void TryDetectLineMeasureEdgesCore_WhenDetectionFails_DoesNotConvertDefaultResult()
        {
            BitmapSource bitmap = CreateBgraBitmap(12, [.. Enumerable.Repeat(Colors.Gray, 12 * 12)]);
            var line = new CaliperMeasureRoi
            {
                P1 = new PointD(4, 4),
                P2 = new PointD(4, 4)
            };

            bool success = ImageAnalysisService.TryDetectLineMeasureEdgesCore(bitmap, line, out _);

            Assert.False(success);
        }

        [Fact]
        public void FitLine_ClippingChangesFitButProjectsOriginalContourEndpoints()
        {
            Point[] contour =
            [
                new Point(0, 0.5),
                new Point(10, 0),
                new Point(20, 0),
                new Point(30, 0)
            ];

            LineSegmentOverlay untrimmed = ImageAnalysisService.FitLine(
                contour,
                new Vector(1, 0),
                fallbackHalfLength: 15,
                fitMode: LineFitMode.Regression);
            LineSegmentOverlay clipped = ImageAnalysisService.FitLine(
                contour,
                new Vector(1, 0),
                fallbackHalfLength: 15,
                fitMode: LineFitMode.Regression,
                clippingEndPoints: 1);

            Assert.True(Math.Abs(untrimmed.Start.Y - clipped.Start.Y) > 0.05, "Clipping the first contour point should alter the fitted line.");
            Assert.InRange(clipped.Start.X, -1e-6, 1e-6);
            Assert.InRange(clipped.Start.Y, -1e-6, 1e-6);
            Assert.InRange(clipped.End.X, 30 - 1e-6, 30 + 1e-6);
            Assert.InRange(clipped.End.Y, -1e-6, 1e-6);
        }

        [Fact]
        public void FitLine_ModesProduceFiniteComparableSegments()
        {
            Point[] contour =
            [
                new Point(0, 0),
                new Point(10, 1),
                new Point(20, 8)
            ];
            var results = new Dictionary<LineFitMode, LineSegmentOverlay>();

            foreach (LineFitMode mode in Enum.GetValues<LineFitMode>())
            {
                LineSegmentOverlay fit = ImageAnalysisService.FitLine(
                    contour,
                    new Vector(1, 0),
                    fallbackHalfLength: 10,
                    fitMode: mode);
                Assert.True(IsFinite(fit.Start) && IsFinite(fit.End), $"{mode} returned a non-finite segment.");
                results.Add(mode, fit);
            }

            Assert.True(
                Math.Abs(GetSegmentAngleDegrees(results[LineFitMode.Gauss]) - GetSegmentAngleDegrees(results[LineFitMode.Regression])) > 0.01,
                "The robust Gauss fit should differ from unweighted regression on the asymmetric outlier contour.");
        }

        private static CaliperMeasureRoi CreateComparisonLineRoi()
        {
            var line = new CaliperMeasureRoi
            {
                P1 = new PointD(5, 20),
                P2 = new PointD(25, 20),
                CaliperCount = 12,
                CaliperSamplingHalfWidth = 1,
                CaliperMinimumGradient = 10,
                MinimumValidCalipers = 4
            };
            line.EnsureCaliperRegion();
            return line;
        }

        private static bool IsFinite(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

        private static double GetSegmentAngleDegrees(LineSegmentOverlay segment) =>
            Math.Atan2(segment.End.Y - segment.Start.Y, segment.End.X - segment.Start.X) * 180 / Math.PI;

        [Fact]
        public void TryDetectCircularCaliperEdges_UniformImage_ReturnsFalse()
        {
            const int size = 20;
            Color[] pixels = Enumerable.Repeat(new Color { R = 128, G = 128, B = 128, A = 255 }, size * size).ToArray();
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            var caliper = new CircularCaliperMeasureRoi
            {
                Center = new PointD(10, 10),
                Radius = 4.0,
                CaliperCount = 12,
                CaliperSearchRange = 3
            };

            bool success = ImageAnalysisService.TryDetectCircularCaliperEdges(bitmap, caliper, out _);

            Assert.False(success, "均匀图像没有任何边缘，检测应失败。");
        }

        [Fact]
        public void TryDetectAutomaticCircle_SyntheticDisk_FitsCenterAndRadius()
        {
            const int size = 80;
            const double centerX = 40.0;
            const double centerY = 40.0;
            const double trueRadius = 18.0;
            Color[] pixels = BuildDiskPixels(size, new Point(centerX, centerY), trueRadius);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(centerX, centerY), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "合成圆盘应能一次点击检出。");
            Assert.InRange(roi.Radius, trueRadius - 0.6, trueRadius + 0.6);
            Assert.InRange(roi.Center.X, centerX - 0.6, centerX + 0.6);
            Assert.InRange(roi.Center.Y, centerY - 0.6, centerY + 0.6);
            // 提交后的 ROI 应保持紧凑可编辑，而不是整幅范围的宽搜索域。
            Assert.InRange(roi.CaliperSearchRange, 8, 64);
        }

        [Fact]
        public void TryDetectAutomaticCircle_OffCenterClick_StillFitsSameCircle()
        {
            const int size = 80;
            Color[] pixels = BuildDiskPixels(size, new Point(40, 40), 22.0);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(34, 43), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "落点偏离圆心时也应检出同一个圆。");
            Assert.InRange(roi.Radius, 22 - 0.8, 22 + 0.8);
            Assert.InRange(roi.Center.X, 40 - 0.8, 40 + 0.8);
            Assert.InRange(roi.Center.Y, 40 - 0.8, 40 + 0.8);
        }

        [Fact]
        public void TryDetectAutomaticCircle_ConcentricRing_LocksOneContour()
        {
            // 亮环内外两条轮廓强度相当：无论锁定哪一条，都应给出干净的单圆结果。
            const int size = 90;
            Color[] pixels = BuildRingPixels(size, new Point(45, 45), innerRadius: 14, outerRadius: 20);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(45, 45), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "同心亮环应能检出其中一条轮廓。");
            bool onInnerContour = Math.Abs(roi.Radius - 14) < 0.8;
            bool onOuterContour = Math.Abs(roi.Radius - 20) < 0.8;
            Assert.True(onInnerContour || onOuterContour, $"半径应落在环的内外轮廓之一，实际 {roi.Radius:F2}。");
            Assert.InRange(roi.ResidualRms, 0, 1.0);
        }

        [Fact]
        public void TryDetectAutomaticCircle_PartiallyOccludedDisk_StillFits()
        {
            // 150° 缺口（遮挡）：其余 210° 周长仍应提供足够的有效卡尺。
            const int size = 90;
            const double trueRadius = 24.0;
            Color[] pixels = BuildGappedDiskPixels(size, new Point(45, 45), trueRadius, gapStartDegrees: 20, gapEndDegrees: 170);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(45, 45), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "部分遮挡的圆应仍能检出。");
            Assert.InRange(roi.Radius, trueRadius - 1.0, trueRadius + 1.0);
            Assert.InRange(roi.Center.X, 45 - 1.0, 45 + 1.0);
            Assert.InRange(roi.Center.Y, 45 - 1.0, 45 + 1.0);
        }

        [Fact]
        public void TryDetectAutomaticCircle_StrongScratchBesideTarget_KeepsCircularConsensus()
        {
            // 圆外叠加一条同样亮的竖直刻痕：单支卡尺会被刻痕抢走，按半径投票应仍锁定圆。
            const int size = 90;
            const double trueRadius = 18.0;
            Color[] pixels = BuildDiskPixels(size, new Point(45, 45), trueRadius);
            DrawVerticalLine(pixels, size, x: 76, halfWidth: 2, level: 255);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(45, 45), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "存在强直线干扰时仍应检出圆。");
            Assert.InRange(roi.Radius, trueRadius - 0.8, trueRadius + 0.8);
            Assert.InRange(roi.Center.X, 45 - 0.8, 45 + 0.8);
            Assert.InRange(roi.Center.Y, 45 - 0.8, 45 + 0.8);
        }

        [Fact]
        public void TryDetectAutomaticCircle_NoisyDisk_StillFitsWithinLooseTolerance()
        {
            const int size = 120;
            const double trueRadius = 30.0;
            Color[] pixels = BuildDiskPixels(size, new Point(60, 60), trueRadius);
            AddUniformNoise(pixels, amplitude: 20, seed: 2718);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(60, 60), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "含噪声的圆盘应仍能检出。");
            Assert.InRange(roi.Radius, trueRadius - 1.5, trueRadius + 1.5);
        }

        [Fact]
        public void TryDetectAutomaticCircle_LargeImageSmallTarget_StillFits()
        {
            // 大图小目标：分级搜索先粗扫整幅范围，再只在候选半径附近做高精度采样。
            const int size = 600;
            const double trueRadius = 26.0;
            Color[] pixels = BuildDiskPixels(size, new Point(300, 300), trueRadius);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(300, 300), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "大图中的小圆应能一次点击检出。");
            Assert.InRange(roi.Radius, trueRadius - 1.0, trueRadius + 1.0);
        }

        [Fact]
        public void TryDetectAutomaticCircle_ThinRingInLargeImage_StillFits()
        {
            // 大图 + 细圆环（2 px 宽）：粗扫必须按足够细的径向网格覆盖，不能整圈漏在采样步长之间。
            const int size = 1100;
            Color[] pixels = BuildRingPixels(size, new Point(550, 550), innerRadius: 58, outerRadius: 60);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(550, 550), out CircularCaliperMeasureRoi roi);

            Assert.True(success, "大图中的细圆环应能一次点击检出。");
            Assert.InRange(roi.Radius, 56.5, 61.5);
        }

        [Fact]
        public void TryDetectAutomaticCircle_UniformImage_ReturnsFalse()
        {
            const int size = 64;
            Color[] pixels = Enumerable.Repeat(new Color { R = 128, G = 128, B = 128, A = 255 }, size * size).ToArray();
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(32, 32), out CircularCaliperMeasureRoi uniformRoi);

            Assert.False(success, success ? $"均匀图像没有圆，自动检测应失败。结果半径={uniformRoi.Radius}, 置信度={uniformRoi.Confidence}, 残差={uniformRoi.ResidualRms}, 有效={uniformRoi.ValidCaliperCount}" : "均匀图像没有圆，自动检测应失败。");
        }

        [Fact]
        public void TryDetectAutomaticCircle_NoiseOnlyImage_ReturnsFalse()
        {
            const int size = 128;
            Color[] pixels = Enumerable.Repeat(new Color { R = 128, G = 128, B = 128, A = 255 }, size * size).ToArray();
            AddUniformNoise(pixels, amplitude: 25, seed: 31415);
            BitmapSource bitmap = CreateBgraBitmap(size, pixels);

            bool success = ImageAnalysisService.TryDetectAutomaticCircle(bitmap, new Point(64, 64), out _);

            Assert.False(success, "纯噪声图像不应拟合出可信的圆。");
        }

        private static Color[] BuildRingPixels(int size, Point center, double innerRadius, double outerRadius)
        {
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - center.X;
                    double dy = y + 0.5 - center.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    byte level = distance >= innerRadius && distance <= outerRadius ? (byte)255 : (byte)0;
                    pixels[y * size + x] = new Color { R = level, G = level, B = level, A = 255 };
                }
            }

            return pixels;
        }

        private static Color[] BuildGappedDiskPixels(int size, Point center, double radius, double gapStartDegrees, double gapEndDegrees)
        {
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - center.X;
                    double dy = y + 0.5 - center.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    double angleDegrees = Math.Atan2(dy, dx) * 180 / Math.PI;
                    if (angleDegrees < 0)
                    {
                        angleDegrees += 360;
                    }

                    bool insideGap = angleDegrees >= gapStartDegrees && angleDegrees <= gapEndDegrees;
                    byte level = distance <= radius && !insideGap ? (byte)255 : (byte)0;
                    pixels[y * size + x] = new Color { R = level, G = level, B = level, A = 255 };
                }
            }

            return pixels;
        }

        private static void DrawVerticalLine(Color[] pixels, int size, int x, int halfWidth, byte level)
        {
            for (int y = 0; y < size; y++)
            {
                for (int offset = -halfWidth; offset <= halfWidth; offset++)
                {
                    int column = x + offset;
                    if (column < 0 || column >= size)
                    {
                        continue;
                    }

                    pixels[y * size + column] = new Color { R = level, G = level, B = level, A = 255 };
                }
            }
        }

        private static void AddUniformNoise(Color[] pixels, int amplitude, int seed)
        {
            var random = new Random(seed);
            for (int i = 0; i < pixels.Length; i++)
            {
                int level = Math.Clamp(pixels[i].R + random.Next(-amplitude, amplitude + 1), 0, 255);
                pixels[i] = new Color { R = (byte)level, G = (byte)level, B = (byte)level, A = pixels[i].A };
            }
        }

        private static Color[] BuildDiskPixels(int size, Point center, double radius)
        {
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x + 0.5 - center.X;
                    double dy = y + 0.5 - center.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    pixels[y * size + x] = distance <= radius
                        ? new Color { R = 255, G = 255, B = 255, A = 255 }
                        : new Color { R = 0, G = 0, B = 0, A = 255 };
                }
            }

            return pixels;
        }

        private static Color[] BuildBandPixels(int size, int leftStart, int bandWidthPixels, int boundaryGreyPixel, byte greyLevel)
        {
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte level = 0;
                    if (x >= leftStart && x < leftStart + bandWidthPixels)
                    {
                        level = x == boundaryGreyPixel ? greyLevel : (byte)255;
                    }

                    pixels[y * size + x] = new Color { R = level, G = level, B = level, A = 255 };
                }
            }

            return pixels;
        }

        private static BitmapSource CreateBgraBitmap(int size, Color[] pixels)
        {
            byte[] bgra = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color color = pixels[i];
                bgra[i * 4 + 0] = color.B;
                bgra[i * 4 + 1] = color.G;
                bgra[i * 4 + 2] = color.R;
                bgra[i * 4 + 3] = color.A;
            }

            return BitmapSource.Create(
                size,
                size,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                size * 4);
        }
    }
}
