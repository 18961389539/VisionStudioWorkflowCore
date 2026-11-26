using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.Dialogs;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.ViewModels;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
public class DetectionRecommendationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicWorkflowRunsInBackgroundAndAppliesOnlyWhenAccepted(bool accept)
    {
        WpfTestRunner.RunAsync(async () =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            const int size = 128;
            var pixels = new byte[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 64; x < size; x++) pixels[y * size + x] = 255;
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
            bitmap.Freeze();
            viewer.SetImage(bitmap);
            var roi = new LineCaliperMeasureRoi { P1 = new PointD(60, 20), P2 = new PointD(60, 108), CaliperCount = 16,
                CaliperSearchRange = 18, MinimumValidCalipers = 8 };
            viewer.AddRoi(roi);
            viewer.ViewerState.SelectedRoi = roi;
            var host = new Window { Content = viewer, Width = 900, Height = 600, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            viewer.ViewerState.UndoRedo.Clear();
            bool sawDialog = false;
            bool wasApplicable = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                if (host.OwnedWindows.OfType<DetectionParameterRecommendationDialog>().FirstOrDefault() is { } dialog)
                {
                    sawDialog = true;
                    wasApplicable = dialog.ApplyButton.IsEnabled;
                    if (accept && wasApplicable) dialog.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                    timer.Stop();
                }
                else if (watch.Elapsed > TimeSpan.FromSeconds(10)) { timer.Stop(); host.Close(); }
            };
            timer.Start();
            try
            {
                await viewer.RecommendDetectionParametersAsync();
                Assert.True(sawDialog);
                Assert.True(wasApplicable);
                Assert.Equal(accept, viewer.ViewerState.UndoRedo.CanUndo);
                if (accept)
                {
                    Assert.True(roi.CaliperSearchRange < 18);
                    Assert.True(roi.HasDetectedLine);
                    viewer.ViewerState.UndoRedo.Undo();
                }
                Assert.Equal(18, roi.CaliperSearchRange);
                Assert.Equal(16, roi.CaliperCount);
            }
            finally { timer.Stop(); host.Close(); }
        });
    }

    [Fact]
    public void AcceptedParametersAreUndoableAndPreserveIdentity()
    {
        WpfTestRunner.Run(() =>
        {
            var model = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
            var source = new LineCaliperMeasureRoi { P1 = new PointD(20, 30), P2 = new PointD(80, 30), CaliperSearchRange = 20 };
            Assert.True(model.AddRoi(source));
            var edit = DetectionRecommendationEdit.Capture(model, source, 1, "px");
            var candidate = (LineCaliperMeasureRoi)source.Clone();
            candidate.CaliperSearchRange = 8;
            Assert.True(edit.TryApply(model, candidate, 1, "px"));
            Assert.Equal(8, source.CaliperSearchRange);
            Assert.Equal(candidate.MeasurementId, source.MeasurementId);
            model.UndoRedo.Undo();
            Assert.Equal(20, source.CaliperSearchRange);
            model.UndoRedo.Redo();
            Assert.Equal(8, source.CaliperSearchRange);
        });
    }

    [Fact]
    public void ConcurrentChangesLocksRemovedSourcesAndCalibrationRejectStaleSuggestions()
    {
        WpfTestRunner.Run(() =>
        {
            var model = new ImageViewerViewModel(RoiPluginRegistry.CreateBuiltIn());
            var source = new LineCaliperMeasureRoi { P1 = new PointD(20, 30), P2 = new PointD(80, 30) };
            Assert.True(model.AddRoi(source));
            var candidate = source.Clone();
            var edit = DetectionRecommendationEdit.Capture(model, source, 1, "px");
            source.CaliperMinimumGradient += 1;
            Assert.False(edit.TryApply(model, candidate, 1, "px"));
            edit = DetectionRecommendationEdit.Capture(model, source, 1, "px");
            Assert.False(edit.TryApply(model, candidate, 0.5, "mm"));
            source.IsLocked = true;
            Assert.False(edit.TryApply(model, candidate, 1, "px"));
            source.IsLocked = false;
            model.RemoveRoi(source);
            Assert.False(edit.TryApply(model, candidate, 1, "px"));
            Assert.False(model.UndoRedo.CanUndo);
        });
    }

    [Fact]
    public void RecommendationIsSearchableAndDisabledWithExplanationWithoutImage()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            WpfTestRunner.InvokePrivate(viewer, "UpdateContextMenuState");
            var menu = viewer.mainContextMenu;
            MenuItem item = Leaves(menu).Single(leaf => leaf.Tag is ImageViewerDetectionRecommendationMenuTag);
            Assert.False(item.IsEnabled);
            Assert.NotNull(item.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(item));
            Assert.Contains(MenuSearchCommands.Find(menu.Items.Cast<object>(), ["自动调参"]), command => ReferenceEquals(command.Original, item));
        });
    }

    private static IEnumerable<MenuItem> Leaves(ItemsControl parent)
    {
        foreach (MenuItem item in parent.Items.OfType<MenuItem>())
        {
            if (item.Items.Count == 0) yield return item;
            foreach (MenuItem child in Leaves(item)) yield return child;
        }
    }
}
