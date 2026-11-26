using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Unit")]
    [Trait("Category", "Wpf")]
    public class ImageViewerAnalysisStateTests
    {
        [Fact]
        public void BeginHistogramTicket_SupersedesThePreviousTicket()
        {
            var state = new ImageViewerAnalysisState();

            ImageViewerAnalysisTicket first = state.BeginHistogramTicket();
            ImageViewerAnalysisTicket second = state.BeginHistogramTicket();

            Assert.True(first.IsCancellationRequested);
            Assert.False(state.IsCurrentHistogramTicket(first));
            Assert.True(state.IsCurrentHistogramTicket(second));
            Assert.Same(second, state.HistogramTicket);
        }

        [Fact]
        public void CancelHistogramWork_ReleasesTheTicketAndIsIdempotent()
        {
            var state = new ImageViewerAnalysisState();
            ImageViewerAnalysisTicket ticket = state.BeginHistogramTicket();

            state.CancelHistogramWork();
            state.CancelHistogramWork();

            Assert.True(ticket.IsCancellationRequested);
            Assert.Null(state.HistogramTicket);
        }

        [Fact]
        public void ResetForSource_BumpsGenerationAndReplacesBitmapAndPyramidTogether()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource first = CreateBitmap(1);
                BitmapSource second = CreateBitmap(2);
                var state = new ImageViewerAnalysisState();

                state.ResetForSource(first);
                long firstGeneration = state.SourceGeneration;
                Assert.Same(first, state.AnalysisBitmapSource);
                Assert.Single(state.PyramidLevels);

                state.ResetForSource(second);

                Assert.NotEqual(firstGeneration, state.SourceGeneration);
                Assert.Same(second, state.Snapshot.Bitmap);
                Assert.Same(second, state.PyramidLevels[0].Bitmap);
            });
        }

        [Fact]
        public void SetPyramidLevels_WhenSourceGenerationChanged_DiscardsStalePyramid()
        {
            WpfTestRunner.Run(() =>
            {
                BitmapSource first = CreateBitmap(1);
                BitmapSource second = CreateBitmap(2);
                var state = new ImageViewerAnalysisState();

                state.ResetForSource(first);
                long staleGeneration = state.SourceGeneration;
                state.ResetForSource(second);

                bool applied = state.SetPyramidLevels(
                    staleGeneration,
                    [new ImagePyramidLevel(first, 1.0), new ImagePyramidLevel(first, 0.5)],
                    TimeSpan.FromMilliseconds(3));

                Assert.False(applied);
                Assert.Same(second, state.AnalysisBitmapSource);
                Assert.Single(state.PyramidLevels);
                Assert.Equal(TimeSpan.Zero, state.LastPyramidBuildDuration);

                Assert.True(state.SetPyramidLevels(
                    state.SourceGeneration,
                    [new ImagePyramidLevel(second, 1.0), new ImagePyramidLevel(second, 0.5)],
                    TimeSpan.FromMilliseconds(4)));

                Assert.Equal(2, state.PyramidLevels.Count);
                Assert.Equal(TimeSpan.FromMilliseconds(4), state.LastPyramidBuildDuration);
            });
        }

        [Fact]
        public void ResetForSource_CancelsEveryOutstandingTicket()
        {
            WpfTestRunner.Run(() =>
            {
                var state = new ImageViewerAnalysisState();
                state.ResetForSource(CreateBitmap(1));
                ImageViewerAnalysisTicket histogram = state.BeginHistogramTicket();
                ImageViewerAnalysisTicket profile = state.BeginProfileTicket();
                ImageViewerAnalysisTicket pyramid = state.BeginPyramidTicket();

                state.ResetForSource(CreateBitmap(2));

                Assert.True(histogram.IsCancellationRequested);
                Assert.True(profile.IsCancellationRequested);
                Assert.True(pyramid.IsCancellationRequested);
                Assert.Null(state.HistogramTicket);
                Assert.Null(state.ProfileTicket);
                Assert.Null(state.PyramidTicket);
            });
        }

        private static BitmapSource CreateBitmap(byte intensity)
        {
            return BitmapSource.Create(
                pixelWidth: 2,
                pixelHeight: 2,
                dpiX: 96,
                dpiY: 96,
                pixelFormat: PixelFormats.Gray8,
                palette: null,
                pixels: new byte[] { intensity, intensity, intensity, intensity },
                stride: 2);
        }
    }
}
