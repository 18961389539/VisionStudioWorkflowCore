using System;
using System.Collections.Generic;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerViewCommandController
    {
        private readonly IReadOnlyDictionary<ImageViewerViewCommand, Action> _actions;

        public ImageViewerViewCommandController(
            IImageViewerViewOptions options,
            IImageViewerViewportOperations viewport,
            IImageViewerImageTransformOperations imageTransform)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(viewport);
            ArgumentNullException.ThrowIfNull(imageTransform);

            _actions = new Dictionary<ImageViewerViewCommand, Action>
            {
                [ImageViewerViewCommand.TogglePixelGrid] = () => options.ShowPixelGrid = !options.ShowPixelGrid,
                [ImageViewerViewCommand.ToggleCrosshair] = () => options.ShowCrosshair = !options.ShowCrosshair,
                [ImageViewerViewCommand.ToggleCaliperScores] = () => options.ShowCaliperScores = !options.ShowCaliperScores,
                [ImageViewerViewCommand.ToggleInfoPanel] = () => options.ShowInfoPanel = !options.ShowInfoPanel,
                [ImageViewerViewCommand.ToggleHistogram] = () => options.ShowHistogram = !options.ShowHistogram,
                [ImageViewerViewCommand.ToggleProfile] = () => options.ShowProfile = !options.ShowProfile,
                [ImageViewerViewCommand.ToggleScaleBar] = () => options.ShowScaleBar = !options.ShowScaleBar,
                [ImageViewerViewCommand.ToggleRoiList] = () => options.ShowRoiList = !options.ShowRoiList,
                [ImageViewerViewCommand.ToggleToolbar] = () => options.ShowToolbar = !options.ShowToolbar,
                [ImageViewerViewCommand.ToggleSnapGrid] = () => options.ShowSnapGrid = !options.ShowSnapGrid,
                [ImageViewerViewCommand.ToggleSnapToGrid] = () => options.EnableSnapToGrid = !options.EnableSnapToGrid,
                [ImageViewerViewCommand.FitToView] = viewport.FitToView,
                [ImageViewerViewCommand.FillToView] = viewport.FillToView,
                [ImageViewerViewCommand.ActualSize] = viewport.SetActualSize,
                [ImageViewerViewCommand.ZoomIn] = viewport.ZoomIn,
                [ImageViewerViewCommand.ZoomOut] = viewport.ZoomOut,
                [ImageViewerViewCommand.ZoomToSelection] = viewport.ZoomToSelection,
                [ImageViewerViewCommand.ResetView] = viewport.ResetView,
                [ImageViewerViewCommand.ShowFullImage] = viewport.ShowFullImage,
                [ImageViewerViewCommand.RotateLeft] = imageTransform.RotateLeft,
                [ImageViewerViewCommand.RotateRight] = imageTransform.RotateRight,
                [ImageViewerViewCommand.FlipHorizontal] = imageTransform.FlipHorizontal,
                [ImageViewerViewCommand.FlipVertical] = imageTransform.FlipVertical
            };

            if (_actions.Count != Enum.GetValues<ImageViewerViewCommand>().Length)
            {
                throw new InvalidOperationException("The view command action map is incomplete.");
            }
        }

        public void Execute(ImageViewerViewCommand command)
        {
            if (!_actions.TryGetValue(command, out Action? action))
            {
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }

            action();
        }
    }
}
