using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private sealed class PointerInteractionHost : IImageViewerPointerInteractionHost
        {
            private readonly ImageViewer _owner;
            private readonly ViewportController _viewportController;

            public PointerInteractionHost(ImageViewer owner, ViewportController viewportController)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _viewportController = viewportController ?? throw new ArgumentNullException(nameof(viewportController));
            }

            public ImageViewerInteractionManipulationState ManipulationState => _owner._interactionManipulationState;

            public bool IsToolInteractionActive => _owner.IsToolInteractionActive;

            public bool IsRootMouseCaptured => _owner.rootGrid.IsMouseCaptured;

            public System.Windows.Media.Imaging.BitmapSource? AnalysisBitmapSource => _owner.GetAnalysisBitmapSource();

            public void CaptureRootMouse() => _owner.rootGrid.CaptureMouse();

            public void ReleaseRootMouse() => _owner.rootGrid.ReleaseMouseCapture();

            public void ZoomAt(System.Windows.Point mousePosition, double zoomFactor) => _viewportController.ZoomAt(mousePosition, zoomFactor);

            public void TranslateBy(System.Windows.Vector delta) => _viewportController.TranslateBy(delta);

            public System.Windows.Point SnapPoint(System.Windows.Point point) => _owner.SnapPoint(point);

            public ResizeHandle GetHandleAt(System.Windows.Point point) => _owner.GetHandleAt(point);

            public int GetPolygonPointIndexAt(System.Windows.Point point) => _owner.GetPolygonPointIndexAt(point);

            public RoiBase? HitTest(System.Windows.Point point) => _owner.HitTest(point);

            public void SetCoordinateText(string text) => _owner.coordTextBlock.Text = text;

            public void SetCursor(System.Windows.Input.Cursor cursor) => _owner.rootGrid.Cursor = cursor;

            public void UpdateCrosshair(double x, double y) => _owner.UpdateCrosshair(x, y);
        }
    }
}