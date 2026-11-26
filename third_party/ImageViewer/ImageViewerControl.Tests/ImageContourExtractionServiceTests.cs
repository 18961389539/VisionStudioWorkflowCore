using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class ImageContourExtractionServiceTests
{
    [Fact]
    public void ExtractMeasuresActualObjectAndRetainsHoleContours()
    {
        WpfTestRunner.Run(() =>
        {
            byte[] pixels = new byte[30 * 24];
            for (int y = 5; y < 19; y++)
            for (int x = 6; x < 24; x++) pixels[y * 30 + x] = 255;
            for (int y = 9; y < 14; y++)
            for (int x = 11; x < 17; x++) pixels[y * 30 + x] = 0;
            BitmapSource bitmap = BitmapSource.Create(30, 24, 96, 96, PixelFormats.Gray8, null, pixels, 30);
            var roi = new BlobAnalysisRoi { Center = new(15, 12), Width = 29, Height = 23, UseOtsu = false, ManualThreshold = 128 };
            ImageContourExtractionResult contour = ImageContourExtractionService.Extract(bitmap, roi);
            Assert.True(contour.Success, contour.Error);
            Assert.Equal(4, contour.OuterContour.Length);
            Assert.Single(contour.Holes);
            Assert.Equal(6, contour.OuterContour.Min(point => point.X));
            Assert.Equal(24, contour.OuterContour.Max(point => point.X));
            var definition = new ImageViewer.Core.Measurements.AdvancedMeasurementDefinition
            {
                Kind = ImageViewer.Core.Measurements.AdvancedMeasurementKind.Rectangle, Sources = [roi.MeasurementId]
            };
            var result = AdvancedMeasurementService.Evaluate(definition, [roi], 0.1, "mm", bitmap: bitmap);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1.8, result.Metrics[0].Value, 9);
            Assert.Equal(1.4, result.Metrics[1].Value, 9);
        });
    }

    [Fact]
    public void Gray16OtsuKeepsIntensityPrecisionAndClippedObjectsFail()
    {
        WpfTestRunner.Run(() =>
        {
            ushort[] pixels = Enumerable.Repeat((ushort)1000, 20 * 20).ToArray();
            for (int y = 6; y < 14; y++)
            for (int x = 5; x < 15; x++) pixels[y * 20 + x] = 1100;
            BitmapSource bitmap = BitmapSource.Create(20, 20, 96, 96, PixelFormats.Gray16, null, pixels, 40);
            var roi = new RotatedRect { Center = new(10, 10), Width = 19, Height = 19 };
            var contour = ImageContourExtractionService.Extract(bitmap, roi);
            Assert.True(contour.Success, contour.Error);
            Assert.Equal(10, contour.OuterContour.Max(point => point.X) - contour.OuterContour.Min(point => point.X));
            roi.Width = 10;
            Assert.False(ImageContourExtractionService.Extract(bitmap, roi).Success);
        });
    }
}
