using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Abstractions;

namespace ImageViewer.Controls
{
    internal interface IImageViewerAnalysisUiFacade
    {
        void SetHistogramPanelVisibility(bool isVisible);
        void SetProfilePanelVisibility(bool isVisible);
        void ApplyRenderedImagePlan(ImageViewerRenderedImagePlan plan);
        void ApplyPseudoColorMenuState(ImageViewerPseudoColorMenuState state);
        void PresentHistogram(ImageViewerHistogramOutput? output);
        void PresentProfile(ImageViewerProfileOutput? output);
        void PresentHistogramError(string message);
        void PresentProfileError(string message);
    }

    internal sealed class ImageViewerAnalysisUiFacade : IImageViewerAnalysisUiFacade
    {
        private readonly FrameworkElement _histogramPanel;
        private readonly FrameworkElement _profilePanel;
        private readonly Canvas _histogramCanvas;
        private readonly Canvas _profileCanvas;
        private readonly MenuItem[] _pseudoColorMenuItems;
        private readonly WpfImageViewerRenderedImageApplier _renderedImageApplier;

        public ImageViewerAnalysisUiFacade(
            FrameworkElement histogramPanel,
            FrameworkElement profilePanel,
            Canvas histogramCanvas,
            Canvas profileCanvas,
            IEnumerable<MenuItem> pseudoColorMenuItems,
            WpfImageViewerRenderedImageApplier renderedImageApplier)
        {
            _histogramPanel = histogramPanel ?? throw new ArgumentNullException(nameof(histogramPanel));
            _profilePanel = profilePanel ?? throw new ArgumentNullException(nameof(profilePanel));
            _histogramCanvas = histogramCanvas ?? throw new ArgumentNullException(nameof(histogramCanvas));
            _profileCanvas = profileCanvas ?? throw new ArgumentNullException(nameof(profileCanvas));
            _pseudoColorMenuItems = pseudoColorMenuItems?.ToArray() ?? throw new ArgumentNullException(nameof(pseudoColorMenuItems));
            _renderedImageApplier = renderedImageApplier ?? throw new ArgumentNullException(nameof(renderedImageApplier));
        }

        public void SetHistogramPanelVisibility(bool isVisible)
        {
            _histogramPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetProfilePanelVisibility(bool isVisible)
        {
            _profilePanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ApplyRenderedImagePlan(ImageViewerRenderedImagePlan plan)
        {
            _renderedImageApplier.Apply(plan);
        }

        public void ApplyPseudoColorMenuState(ImageViewerPseudoColorMenuState state)
        {
            foreach (MenuItem item in _pseudoColorMenuItems)
            {
                string? tag = item.Tag as string;
                item.IsChecked = string.Equals(tag, state.SelectedPalette.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        public void PresentHistogram(ImageViewerHistogramOutput? output)
        {
            _histogramCanvas.Children.Clear();
            if (output != null)
            {
                ImageViewerAnalysisGraphRenderer.DrawHistogram(_histogramCanvas, output.Histogram, output.HistogramBinCount);
            }
        }

        public void PresentProfile(ImageViewerProfileOutput? output)
        {
            _profileCanvas.Children.Clear();
            if (output != null)
            {
                ImageViewerAnalysisGraphRenderer.DrawProfile(_profileCanvas, output);
            }
        }

        public void PresentHistogramError(string message)
        {
            _histogramCanvas.Children.Clear();
            _histogramCanvas.Children.Add(CreateErrorTextBlock(message, _histogramCanvas));
        }

        public void PresentProfileError(string message)
        {
            _profileCanvas.Children.Clear();
            _profileCanvas.Children.Add(CreateErrorTextBlock(message, _profileCanvas));
        }

        private static TextBlock CreateErrorTextBlock(string message, Canvas canvas)
        {
            return new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 200,
                TextAlignment = TextAlignment.Center
            };
        }
    }
}
