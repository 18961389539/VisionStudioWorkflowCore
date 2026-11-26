using System;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using ImageViewer.Abstractions;

namespace ImageViewer.Controls
{
    internal sealed class WpfImageViewerRenderedImageApplier
    {
        private readonly Canvas _imageContainer;
        private readonly Image _imageElement;
        private readonly IImageViewerRenderService _renderService;

        public WpfImageViewerRenderedImageApplier(Canvas imageContainer, Image imageElement, IImageViewerRenderService renderService)
        {
            _imageContainer = imageContainer ?? throw new ArgumentNullException(nameof(imageContainer));
            _imageElement = imageElement ?? throw new ArgumentNullException(nameof(imageElement));
            _renderService = renderService ?? throw new ArgumentNullException(nameof(renderService));
        }

        public void Apply(ImageViewerRenderedImagePlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            _renderService.ApplyGpuCaching(_imageContainer, plan.EnableGpuRendering);
            _imageElement.Source = plan.RenderFrame.Source;
            _imageElement.Effect = plan.Effect;
            Canvas.SetLeft(_imageElement, plan.RenderFrame.Left);
            Canvas.SetTop(_imageElement, plan.RenderFrame.Top);
            _imageElement.Width = plan.RenderFrame.Width;
            _imageElement.Height = plan.RenderFrame.Height;
        }
    }
}