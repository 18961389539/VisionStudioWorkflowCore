using System;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ImageViewer.Controls
{
    internal sealed class ImageSourceController
    {
        private readonly Func<bool> _isLoadedProvider;
        private readonly Func<ImageSource?> _imageSourceProvider;
        private readonly Func<ImageSource?, Task> _prepareAnalysisResourcesAsync;
        private readonly Func<Task, string, Task> _observeBackgroundOperation;
        private readonly IImageViewStateController _imageViewStateController;
        private readonly Action _refreshAllCaliperDetections;
        private readonly Action _drawRois;
        private bool _pendingInitialization;

        public ImageSourceController(
            Func<bool> isLoadedProvider,
            Func<ImageSource?> imageSourceProvider,
            Func<ImageSource?, Task> prepareAnalysisResourcesAsync,
            Func<Task, string, Task> observeBackgroundOperation,
            IImageViewStateController imageViewStateController,
            Action refreshAllCaliperDetections,
            Action drawRois)
        {
            _isLoadedProvider = isLoadedProvider ?? throw new ArgumentNullException(nameof(isLoadedProvider));
            _imageSourceProvider = imageSourceProvider ?? throw new ArgumentNullException(nameof(imageSourceProvider));
            _prepareAnalysisResourcesAsync = prepareAnalysisResourcesAsync ?? throw new ArgumentNullException(nameof(prepareAnalysisResourcesAsync));
            _observeBackgroundOperation = observeBackgroundOperation ?? throw new ArgumentNullException(nameof(observeBackgroundOperation));
            _imageViewStateController = imageViewStateController ?? throw new ArgumentNullException(nameof(imageViewStateController));
            _refreshAllCaliperDetections = refreshAllCaliperDetections ?? throw new ArgumentNullException(nameof(refreshAllCaliperDetections));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
        }

        public void HandleLoaded()
        {
            if (_pendingInitialization)
            {
                _pendingInitialization = false;
                ApplyImageSource(_imageSourceProvider());
            }

            _imageViewStateController.HandleLoaded();
        }

        public void HandleImageSourceChanged(ImageSource? source)
        {
            if (!_isLoadedProvider())
            {
                _pendingInitialization = true;
                return;
            }

            ApplyImageSource(source);
        }

        private void ApplyImageSource(ImageSource? source)
        {
            _ = _observeBackgroundOperation(_prepareAnalysisResourcesAsync(source), "Prepare analysis resources");
            _imageViewStateController.ApplyImageSurfaceLayout(source);
            _imageViewStateController.RefreshImageOverlays();
            _refreshAllCaliperDetections();
            _drawRois();
        }
    }
}
