using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

public class DetectionParameterRecommendationDialogTests
{
    [Fact]
    public void ApplyIsBlockedForUnusableOrUnchangedPreview()
    {
        WpfTestRunner.Run(() =>
        {
            CircleRoi source = MakeCircle(30, 30, 10);
            DetectionRecommendationPreview preview = MakePreview(source, canApply: false, changed: false);
            var dialog = new DetectionParameterRecommendationDialog(preview, MakeGray16Image());

            Assert.False(dialog.ApplyButton.IsEnabled);
            WpfTestRunner.InvokePrivate(dialog, "ApplyRecommendation");

            Assert.Null(dialog.AcceptedRoi);
            dialog.Close();
        });
    }

    [Fact]
    public void ApplyReturnsCloneOfSuggestedDetectedRoiAndLeavesSourceUntouched()
    {
        WpfTestRunner.Run(() =>
        {
            CircleRoi source = MakeCircle(30, 30, 10);
            CircleRoi suggested = MakeCircle(31, 30, 11);
            double originalRadius = source.Radius;
            var dialog = new DetectionParameterRecommendationDialog(MakePreview(source, true, true, suggested), MakeGray16Image());
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));

            bool? accepted = dialog.ShowDialog();

            Assert.True(accepted);
            Assert.NotNull(dialog.AcceptedRoi);
            Assert.IsType<CircleRoi>(dialog.AcceptedRoi);
            Assert.NotSame(suggested, dialog.AcceptedRoi);
            Assert.Equal(suggested.Center, ((CircleRoi)dialog.AcceptedRoi!).Center);
            Assert.Equal(suggested.Radius, ((CircleRoi)dialog.AcceptedRoi!).Radius);
            Assert.Equal(originalRadius, source.Radius);
        });
    }

    [Fact]
    public void ClosingWithoutApplyingDoesNotAcceptOrMutateTheSource()
    {
        WpfTestRunner.Run(() =>
        {
            CircleRoi source = MakeCircle(30, 30, 10);
            CircleRoi suggested = MakeCircle(31, 30, 11);
            var dialog = new DetectionParameterRecommendationDialog(MakePreview(source, true, true, suggested), MakeGray16Image());

            dialog.Close();

            Assert.Null(dialog.AcceptedRoi);
            Assert.Equal(new PointD(30, 30), source.Center);
            Assert.Equal(10, source.Radius);
        });
    }

    [Fact]
    public void ComparisonGridIsAccessibleAndPreviewCropsGray16WithAlignedImageSpaceMarkers()
    {
        WpfTestRunner.Run(() =>
        {
            CircleRoi source = MakeCircle(32, 32, 8);
            DetectionRecommendationPreview preview = MakePreview(source, true, true, MakeCircle(50, 32, 8));
            var dialog = new DetectionParameterRecommendationDialog(preview, MakeGray16Image());

            Assert.Equal(UiText.Get("DetectionRecommendationTitle"), AutomationProperties.GetName(dialog));
            Assert.Equal(UiText.Get("DetectionRecommendationParameters"), AutomationProperties.GetName(dialog.ParameterGrid));
            Assert.Equal(2, dialog.ParameterGrid.Items.Count);
            object row = dialog.ParameterGrid.Items[0];
            Assert.Equal(UiText.Get("DetectionRecommendationParameter"), row.GetType().GetProperty("Name")!.GetValue(row));
            Assert.Equal("default", row.GetType().GetProperty("Before")!.GetValue(row));
            Assert.Equal("recommended", row.GetType().GetProperty("After")!.GetValue(row));
            object polarityRow = dialog.ParameterGrid.Items[1];
            Assert.Equal(UiText.Get("RecommendationValueAny"), polarityRow.GetType().GetProperty("Before")!.GetValue(polarityRow));
            Assert.Equal(UiText.Get("RecommendationValueDarkToLight"), polarityRow.GetType().GetProperty("After")!.GetValue(polarityRow));
            Assert.Equal(dialog.BeforeImage.Source!.Width, dialog.AfterImage.Source!.Width);
            Assert.Equal(dialog.BeforeImage.Source.Height, dialog.AfterImage.Source.Height);
            Assert.True(dialog.PreviewCropRect.X <= 32 - 8 - 12);
            Assert.True(dialog.PreviewCropRect.X + dialog.PreviewCropRect.Width > 50 + 8);
            Assert.Equal(PixelFormats.Bgra32, ((BitmapSource)dialog.AfterImage.Source!).Format);
            Assert.True(((BitmapSource)dialog.AfterImage.Source!).PixelWidth < 64);
            Assert.Equal(2, dialog.AfterOverlay.Children.Count);
            Ellipse marker = Assert.IsType<Ellipse>(dialog.AfterOverlay.Children[0]);
            Assert.Equal(17.5, Canvas.GetLeft(marker), 4);
            Assert.Equal(9.5, Canvas.GetTop(marker), 4);

            dialog.Close();
        });
    }

    [Fact]
    public void FailedNoSignalPreviewUsesBoundedCenteredCropAndExtremeOffImageGeometryIsClippedSafely()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource largeImage = MakeGray8Image(1300, 1100);
            var emptyPreview = MakeEmptyPreview();
            var fallbackDialog = new DetectionParameterRecommendationDialog(emptyPreview, largeImage);
            Assert.Equal(1024, fallbackDialog.PreviewCropRect.Width);
            Assert.Equal(1024, fallbackDialog.PreviewCropRect.Height);
            Assert.Equal(138, fallbackDialog.PreviewCropRect.X);
            Assert.Equal(38, fallbackDialog.PreviewCropRect.Y);
            Assert.True((long)fallbackDialog.PreviewCropRect.Width * fallbackDialog.PreviewCropRect.Height <= 4L * 1024 * 1024);
            fallbackDialog.Close();

            CircleRoi offImage = MakeCircle(1e300, 1e300, 8);
            var offImageTrial = new DetectionTrial(false, "RecommendationTrialFailed", null, null, 0, 0, [], offImage);
            var offImagePreview = new DetectionRecommendationPreview(
                new DetectionParameterProposal(false, "RecommendationInvalidGeometry", null, [], new DetectionParameterEvidence(0, 0, 0, 0, [])),
                offImageTrial,
                offImageTrial,
                false,
                "RecommendationTrialFailed");
            var clippedDialog = new DetectionParameterRecommendationDialog(offImagePreview, MakeGray8Image(64, 64));
            Assert.Equal(new Int32Rect(0, 0, 64, 64), clippedDialog.PreviewCropRect);
            clippedDialog.Close();
        });
    }

    [Fact]
    public void OversizedSharedFieldOfViewIsDownsampledWithoutTruncatingTheCrop()
    {
        WpfTestRunner.Run(() =>
        {
            const int width = 2200;
            const int height = 2000;
            var roi = new RotatedRect { Center = new PointD(width / 2d, height / 2d), Width = width, Height = height };
            var trial = new DetectionTrial(true, "DetectionRecommendationSuccessfulBaseline", 1, 0.1, 50, 50, [], roi);
            var preview = new DetectionRecommendationPreview(
                new DetectionParameterProposal(true, string.Empty, roi, [], new DetectionParameterEvidence(16, 1, 50, 0.8, [])),
                trial,
                trial,
                false,
                "RecommendationNoParameterChange");
            var dialog = new DetectionParameterRecommendationDialog(preview, MakeGray8Image(width, height));

            Assert.Equal(new Int32Rect(0, 0, width, height), dialog.PreviewCropRect);
            BitmapSource image = Assert.IsAssignableFrom<BitmapSource>(dialog.AfterImage.Source);
            Assert.True((long)image.PixelWidth * image.PixelHeight <= 4L * 1024 * 1024);
            Assert.True(image.PixelWidth < width);
            Assert.True(image.PixelHeight < height);
            Assert.Equal(image.PixelWidth, dialog.AfterOverlay.Width);
            Assert.Equal(image.PixelHeight, dialog.AfterOverlay.Height);
            dialog.Close();
        });
    }

    private static DetectionRecommendationPreview MakePreview(CircleRoi source, bool canApply, bool changed, CircleRoi? suggested = null)
    {
        suggested ??= (CircleRoi)source.Clone();
        var change = new DetectionParameterChange("Threshold", "DetectionRecommendationParameter", "default", "recommended", "DetectionRecommendationMethod");
        var polarityChange = new DetectionParameterChange("CaliperEdgePolarity", "DetectionRecommendationPolarity", "Any", "DarkToLight", "DetectionRecommendationMethod");
        var evidence = new DetectionParameterEvidence(32, 2, 24, 0.9, []);
        var proposal = new DetectionParameterProposal(true, string.Empty, changed ? suggested : source, [change, polarityChange], evidence);
        var before = new DetectionTrial(true, "DetectionRecommendationSuccessfulBaseline", 8.2, 0.4, 20, 24,
            [new PointD(30, 24), new PointD(34, 26)], source);
        var after = new DetectionTrial(true, "DetectionRecommendationSuccessfulBaseline", 7.8, 0.3, 22, 24,
            [new PointD(32, 24), new PointD(36, 26)], suggested);
        return new DetectionRecommendationPreview(proposal, before, after, canApply, canApply ? string.Empty : "RecommendationNoParameterChange");
    }

    private static CircleRoi MakeCircle(double x, double y, double radius) => new() { Center = new PointD(x, y), Radius = radius };

    private static BitmapSource MakeGray16Image()
    {
        const int width = 64;
        const int height = 64;
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = (ushort)(x * 1000 + y * 100);
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * sizeof(ushort));
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource MakeGray8Image(int width, int height)
    {
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % byte.MaxValue);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }

    private static DetectionRecommendationPreview MakeEmptyPreview()
    {
        var evidence = new DetectionParameterEvidence(0, 0, 0, 0, []);
        var proposal = new DetectionParameterProposal(false, "RecommendationLowContrast", null, [], evidence);
        var trial = new DetectionTrial(false, "RecommendationTrialFailed", null, null, 0, 0, [], null);
        return new DetectionRecommendationPreview(proposal, trial, trial, false, "RecommendationTrialFailed");
    }
}
