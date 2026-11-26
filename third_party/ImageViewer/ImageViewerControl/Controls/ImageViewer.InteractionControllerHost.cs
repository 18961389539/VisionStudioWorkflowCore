using System;
using System.Windows;
using ImageViewer.Localization;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private sealed class InteractionControllerHost : IImageViewerInteractionControllerHost
        {
            private readonly ImageViewer _owner;
            private readonly ViewportController _viewportController;

            public InteractionControllerHost(ImageViewer owner, ViewportController viewportController)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _viewportController = viewportController ?? throw new ArgumentNullException(nameof(viewportController));
            }

            public System.Windows.FrameworkElement RootElement => _owner.rootGrid;

            public System.Windows.IInputElement ImageElement => _owner.imageContainer;

            public bool IsToolInteractionActive => _owner.IsToolInteractionActive;

            public bool HasImage => _owner.ImageSource != null;

            public void Focus() => _owner.Focus();

            public void ExecuteViewCommand(ImageViewerViewCommand command) => _owner._viewCommandController.Execute(command);

            public void ShowFullImage() => _viewportController.ShowFullImage();

            public void DrawRois() => _owner.DrawRois();

            public void UpdateCrosshair(double x, double y) => _owner.UpdateCrosshair(x, y);

            public void ShowZoomLevelHint(System.Windows.Point rootPosition)
            {
                _owner.ShowZoomBadge(UiText.Format("StatusZoomLevel", _owner.Scale), rootPosition);
            }

            public void ShowZoomLevelHintAtCenter()
            {
                Point center = new(
                    _owner.rootGrid.ActualWidth > 0 ? _owner.rootGrid.ActualWidth / 2 : 0,
                    _owner.rootGrid.ActualHeight > 0 ? _owner.rootGrid.ActualHeight / 2 : 0);
                _owner.ShowZoomBadge(UiText.Format("StatusZoomLevel", _owner.Scale), center);
            }
        }
    }
}