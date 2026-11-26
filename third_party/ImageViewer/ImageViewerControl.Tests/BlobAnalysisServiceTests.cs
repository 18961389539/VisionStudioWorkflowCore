using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

/// <summary>
/// Chinese: 斑点分析的搜索区必须是旋转矩形的内点，而不是它的水平外接矩形。
/// English: The blob search region follows the rotated rectangle's interior rather than its
/// axis-aligned bounding box, so corner targets outside a slanted ROI stay undetected.
/// </summary>
[Collection(WpfTestCollection.Name)]
public class BlobAnalysisServiceTests
{
    [Fact]
    public void RotatedRoi_ExcludesBrightCornersOutsideTheSlantedBox()
    {
        WpfTestRunner.Run(() =>
        {
            // 60x60 dark frame: one target inside the slanted box plus one bright block in each
            // free corner of its bounding box.
            var pixels = new byte[60 * 60];
            Fill(pixels, 60, 28, 28, 5, 5);
            Fill(pixels, 60, 9, 13, 4, 4);
            Fill(pixels, 60, 47, 44, 4, 4);
            BitmapSource bitmap = BitmapSource.Create(60, 60, 96, 96, PixelFormats.Gray8, null, pixels, 60);

            var roi = new BlobAnalysisRoi
            {
                Center = new PointD(30, 30),
                Width = 40,
                Height = 20,
                Angle = 30,
                UseOtsu = false,
                ManualThreshold = 128,
                MinArea = 1
            };

            // The bounding box of this ROI: 22.32 x 18.66 half extents around (30, 30).
            var boundingBox = new Rect(8, 12, 45, 37);

            var slanted = BlobAnalysisService.DetectBlobs(bitmap, roi, useOtsu: false, threshold: 128, detectDark: false, minArea: 1);
            var boxed = BlobAnalysisService.DetectBlobs(bitmap, boundingBox, useOtsu: false, threshold: 128, detectDark: false, minArea: 1);

            // Only the target inside the slanted box survives; the corner blocks are masked out.
            var inside = Assert.Single(slanted);
            Assert.Equal(25, inside.Area);
            Assert.Equal(30, inside.Centroid.X, 6);
            Assert.Equal(30, inside.Centroid.Y, 6);

            // Control: searching the same bounding box without the mask still reports all three,
            // which is exactly the pre-fix behaviour this test guards against.
            Assert.Equal(3, boxed.Count);
        });
    }

    [Fact]
    public void RotatedRoi_MasksAllPixelsWhenTheBoxIsSmallerThanOnePixel()
    {
        WpfTestRunner.Run(() =>
        {
            var pixels = new byte[20 * 20];
            Fill(pixels, 20, 0, 0, 20, 20);
            BitmapSource bitmap = BitmapSource.Create(20, 20, 96, 96, PixelFormats.Gray8, null, pixels, 20);

            var roi = new BlobAnalysisRoi
            {
                Center = new PointD(10, 10),
                Width = 0.4,
                Height = 0.4,
                Angle = 45,
                UseOtsu = false,
                ManualThreshold = 128,
                MinArea = 1
            };

            Assert.Empty(BlobAnalysisService.DetectBlobs(bitmap, roi, useOtsu: false, threshold: 128, detectDark: false, minArea: 1));
        });
    }

    [Fact]
    public void AxisAlignedRectOverload_KeepsTheWholeRectAsSearchRegion()
    {
        WpfTestRunner.Run(() =>
        {
            var pixels = new byte[40 * 30];
            Fill(pixels, 40, 10, 8, 6, 4);
            BitmapSource bitmap = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Gray8, null, pixels, 40);

            var detections = BlobAnalysisService.DetectBlobs(
                bitmap, new Rect(4, 4, 30, 20), useOtsu: false, threshold: 128, detectDark: false, minArea: 1);

            var blob = Assert.Single(detections);
            Assert.Equal(24, blob.Area);
            Assert.Equal(12.5, blob.Centroid.X, 6);
            Assert.Equal(9.5, blob.Centroid.Y, 6);
            Assert.Equal(1, blob.Label);
        });
    }

    [Fact]
    public void DetectBlobs_ReturnsContoursInImageCoordinates()
    {
        WpfTestRunner.Run(() =>
        {
            var pixels = new byte[40 * 30];
            Fill(pixels, 40, 10, 8, 6, 4);
            BitmapSource bitmap = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Gray8, null, pixels, 40);

            var roi = new BlobAnalysisRoi
            {
                Center = new PointD(20, 15),
                Width = 30,
                Height = 20,
                UseOtsu = false,
                ManualThreshold = 128,
                MinArea = 1
            };

            var blob = Assert.Single(
                BlobAnalysisService.DetectBlobs(bitmap, roi, useOtsu: false, threshold: 128, detectDark: false, minArea: 1));

            Assert.NotNull(blob.Contour);
            Assert.Equal(4, blob.Contour!.Length);
            Assert.Equal(10, blob.Contour.Min(point => point.X));
            Assert.Equal(16, blob.Contour.Max(point => point.X));
            Assert.Equal(8, blob.Contour.Min(point => point.Y));
            Assert.Equal(12, blob.Contour.Max(point => point.Y));
        });
    }

    private static void Fill(byte[] pixels, int stride, int left, int top, int width, int height)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                pixels[(y * stride) + x] = 255;
            }
        }
    }
}
