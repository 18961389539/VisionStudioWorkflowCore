using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Controls
{
    internal sealed class PointerInteractionFlow : IImageViewerPointerInteractionFlow
    {
        private const double ZoomInFactor = 1.1;
        private const double ZoomOutFactor = 0.9;

        private readonly IImageViewerPointerInteractionHost _host;
        private readonly ImageViewerInteractionManipulationState _state;

        public PointerInteractionFlow(IImageViewerPointerInteractionHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _state = host.ManipulationState;
        }

        public Point SnapPoint(Point rawImagePoint)
        {
            return _host.SnapPoint(rawImagePoint);
        }

        public void HandleMouseWheel(Point mousePosition, int wheelDelta)
        {
            double zoomFactor = wheelDelta > 0 ? ZoomInFactor : ZoomOutFactor;
            _host.ZoomAt(mousePosition, zoomFactor);
        }

        public bool TryBeginPan(MouseButtonState middleButtonState, Point rootPosition)
        {
            if (middleButtonState != MouseButtonState.Pressed)
            {
                return false;
            }

            BeginCanvasPan(rootPosition);
            return true;
        }

        public void BeginCanvasPan(Point rootPosition)
        {
            _state.LastMousePosition = rootPosition;
            _state.IsCanvasDragging = true;
            // 进入平移时切换为抓取光标，明确反馈"正在拖动画布"
            _host.SetCursor(Cursors.Hand);
            _host.CaptureRootMouse();
        }

        public bool TryHandleActivePointerMove(Point rootPosition, Point imagePosition)
        {
            if (_state.IsCanvasDragging)
            {
                Vector delta = rootPosition - _state.LastMousePosition;
                _host.TranslateBy(delta);
                _state.LastMousePosition = rootPosition;

                UpdatePixelInfo(imagePosition);
                _host.UpdateCrosshair(rootPosition.X, rootPosition.Y);
                return true;
            }

            if (_host.IsToolInteractionActive)
            {
                UpdatePixelInfo(imagePosition);
                _host.UpdateCrosshair(rootPosition.X, rootPosition.Y);
                return true;
            }

            return false;
        }

        public bool TryCompletePan()
        {
            if (!_state.IsCanvasDragging)
            {
                return false;
            }

            ResetPanState();
            return true;
        }

        public void ResetPanState()
        {
            _state.IsCanvasDragging = false;
            // 平移结束后恢复默认光标，悬停在 ROI 上时会在下次移动时重新评估
            _host.SetCursor(Cursors.Arrow);
            if (_host.IsRootMouseCaptured && !_state.HasActiveRoiManipulation)
            {
                _host.ReleaseRootMouse();
            }
        }

        public void UpdatePixelInfo(Point imagePosition)
        {
            if (_host.AnalysisBitmapSource is BitmapSource bitmap && PixelInfoFormatter.TryBuildPixelInfoText(bitmap, imagePosition, out string text))
            {
                _host.SetCoordinateText(text);
            }
            else
            {
                _host.SetCoordinateText($"X: {imagePosition.X:F0}, Y: {imagePosition.Y:F0}");
            }
        }

        public void UpdateCursorFeedback(Point imagePosition)
        {
            if (_state.IsCanvasDragging || _state.HasActiveRoiManipulation)
            {
                return;
            }

            ResizeHandle handle = _host.GetHandleAt(imagePosition);
            if (handle != ResizeHandle.None)
            {
                _host.SetCursor(GetCursorForHandle(handle));
                return;
            }

            int polyPointIndex = _host.GetPolygonPointIndexAt(imagePosition);
            if (polyPointIndex != -1)
            {
                _host.SetCursor(Cursors.Hand);
                return;
            }

            RoiBase? hitRoi = _host.HitTest(imagePosition);
            if (hitRoi != null)
            {
                _host.SetCursor(Cursors.SizeAll);
                return;
            }

            _host.SetCursor(Cursors.Arrow);
        }

        private static Cursor GetCursorForHandle(ResizeHandle handle)
        {
            switch (handle)
            {
                case ResizeHandle.TopLeft:
                case ResizeHandle.BottomRight:
                    return Cursors.SizeNWSE;
                case ResizeHandle.TopRight:
                case ResizeHandle.BottomLeft:
                    return Cursors.SizeNESW;
                case ResizeHandle.TopCenter:
                case ResizeHandle.BottomCenter:
                    return Cursors.SizeNS;
                case ResizeHandle.MiddleLeft:
                case ResizeHandle.MiddleRight:
                    return Cursors.SizeWE;
                case ResizeHandle.Rotation:
                case ResizeHandle.P1:
                case ResizeHandle.P2:
                case ResizeHandle.Vertex:
                    return Cursors.Hand;
                default:
                    return Cursors.Arrow;
            }
        }
    }
}