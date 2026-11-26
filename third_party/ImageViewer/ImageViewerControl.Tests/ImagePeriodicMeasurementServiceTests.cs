using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class ImagePeriodicMeasurementServiceTests
{
    private static readonly double[] ForwardCenters = [6, 12, 18, 24, 30];
    private static readonly double[] ReverseCenters = [30, 24, 18, 12, 6];
    private static readonly double[] ClippedCenters = [12, 18, 24, 30];

    [Fact]
    public void ExtractFindsBarCentersInDirectedOrderAndSkipsClippedRuns()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8Bars(40, background: 10, bar: 240);
            ImagePeriodicMeasurementResult forward = ImagePeriodicMeasurementService.Extract(
                bitmap, new LineMeasureRoi { P1 = new PointD(0, 4), P2 = new PointD(39, 4) });
            ImagePeriodicMeasurementResult reverse = ImagePeriodicMeasurementService.Extract(
                bitmap, new LineMeasureRoi { P1 = new PointD(39, 4), P2 = new PointD(0, 4) });

            Assert.True(forward.Success, forward.Error);
            Assert.Equal(ForwardCenters, forward.Centers.Select(point => point.X));
            Assert.All(forward.Intensities, intensity => Assert.Equal(240, intensity, 8));
            Assert.True(reverse.Success, reverse.Error);
            Assert.Equal(ReverseCenters, reverse.Centers.Select(point => point.X));

            ImagePeriodicMeasurementResult clipped = ImagePeriodicMeasurementService.Extract(
                bitmap, new LineMeasureRoi { P1 = new PointD(6, 4), P2 = new PointD(39, 4) });
            Assert.True(clipped.Success, clipped.Error);
            Assert.Equal(ClippedCenters, clipped.Centers.Select(point => point.X));
        });
    }

    [Fact]
    public void ExtractSupportsDarkRunsAndPreservesGray16ThresholdPrecision()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource darkBars = CreateGray8Bars(40, background: 240, bar: 10);
            ImagePeriodicMeasurementResult dark = ImagePeriodicMeasurementService.Extract(
                darkBars, new LineMeasureRoi { P1 = new PointD(0, 4), P2 = new PointD(39, 4) }, detectDark: true);
            Assert.True(dark.Success, dark.Error);
            Assert.Equal(ForwardCenters, dark.Centers.Select(point => point.X));
            Assert.All(dark.Intensities, intensity => Assert.Equal(10, intensity, 8));

            const int width = 40, height = 9;
            ushort[] pixels = Enumerable.Repeat((ushort)1000, width * height).ToArray();
            SetBars(pixels, width, height, 1010);
            BitmapSource gray16 = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * sizeof(ushort));
            ImagePeriodicMeasurementResult precise = ImagePeriodicMeasurementService.Extract(
                gray16, new LineMeasureRoi { P1 = new PointD(0, 4), P2 = new PointD(39, 4) });
            Assert.True(precise.Success, precise.Error);
            Assert.InRange(precise.Threshold, 1000, 1009);
            Assert.All(precise.Intensities, intensity => Assert.Equal(1010, intensity, 8));
        });
    }

    [Fact]
    public void ExtractRejectsInvalidProfilesAndRequiresTwoCompleteRuns()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource uniform = BitmapSource.Create(24, 9, 96, 96, PixelFormats.Gray8, null, new byte[24 * 9], 24);
            Assert.False(ImagePeriodicMeasurementService.Extract(
                uniform, new LineMeasureRoi { P1 = new PointD(0, 4), P2 = new PointD(23, 4) }).Success);
            Assert.False(ImagePeriodicMeasurementService.Extract(
                uniform, new LineMeasureRoi { P1 = new PointD(2, 4), P2 = new PointD(2, 4) }).Success);
            Assert.False(ImagePeriodicMeasurementService.Extract(
                uniform, new LineMeasureRoi { P1 = new PointD(-1, 4), P2 = new PointD(23, 4) }).Success);

            BitmapSource oneBar = CreateGray8Bars(24, background: 10, bar: 240, barCount: 1);
            ImagePeriodicMeasurementResult insufficient = ImagePeriodicMeasurementService.Extract(
                oneBar, new LineMeasureRoi { P1 = new PointD(0, 4), P2 = new PointD(23, 4) });
            Assert.False(insufficient.Success);
            Assert.Empty(insufficient.Centers);
        });
    }

    [Fact]
    public void ExtractEnforcesSampleAndSearchPixelBudgetsBeforeAllocatingProfileBuffers()
    {
        WpfTestRunner.Run(() =>
        {
            const int width = 65_537;
            BitmapSource longLine = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Gray8, null, new byte[width], width);
            ImagePeriodicMeasurementResult tooManySamples = ImagePeriodicMeasurementService.Extract(
                longLine, new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(width - 1, 0) });
            Assert.False(tooManySamples.Success);
            Assert.Contains("sample limit", tooManySamples.Error, StringComparison.OrdinalIgnoreCase);

            const int side = 2001;
            BitmapSource largeSearch = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, new byte[side * side], side);
            ImagePeriodicMeasurementResult tooManyPixels = ImagePeriodicMeasurementService.Extract(
                largeSearch, new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(side - 1, side - 1) });
            Assert.False(tooManyPixels.Success);
            Assert.Contains("memory limit", tooManyPixels.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static BitmapSource CreateGray8Bars(int width, byte background, byte bar, int barCount = 5)
    {
        const int height = 9;
        byte[] pixels = Enumerable.Repeat(background, width * height).ToArray();
        for (int i = 0; i < barCount; i++)
        {
            int center = 6 + i * 6;
            for (int x = center - 1; x <= center + 1 && x < width; x++)
            for (int y = 0; y < height; y++) pixels[y * width + x] = bar;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
    }

    private static void SetBars(ushort[] pixels, int width, int height, ushort bar)
    {
        for (int center = 6; center <= 30; center += 6)
        for (int x = center - 1; x <= center + 1; x++)
        for (int y = 0; y < height; y++) pixels[y * width + x] = bar;
    }
}
