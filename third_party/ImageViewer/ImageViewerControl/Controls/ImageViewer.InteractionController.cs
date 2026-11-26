using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal interface IImageViewerInteractionController
    {
        void HandleKeyDown(KeyEventArgs e);
        void HandleMouseRightButtonDown(MouseButtonEventArgs e);
        void HandleMouseWheel(MouseWheelEventArgs e);
        void HandleMouseDown(MouseButtonEventArgs e);
        void HandleMouseMove(MouseEventArgs e);
        void HandleMouseUp(MouseButtonEventArgs e);
        void HandleLostMouseCapture();
    }

    internal interface IImageViewerInteractionControllerHost
    {
        FrameworkElement RootElement { get; }
        IInputElement ImageElement { get; }
        bool IsToolInteractionActive { get; }
        bool HasImage { get; }

        void Focus();
        void ExecuteViewCommand(ImageViewerViewCommand command);
        void ShowFullImage();
        void DrawRois();
        void UpdateCrosshair(double x, double y);
        void ShowZoomLevelHint(System.Windows.Point rootPosition);
        void ShowZoomLevelHintAtCenter();
    }

    internal interface IImageViewerPointerInteractionHost
    {
        ImageViewerInteractionManipulationState ManipulationState { get; }
        bool IsToolInteractionActive { get; }
        bool IsRootMouseCaptured { get; }
        BitmapSource? AnalysisBitmapSource { get; }

        void CaptureRootMouse();
        void ReleaseRootMouse();
        void ZoomAt(Point mousePosition, double zoomFactor);
        void TranslateBy(Vector delta);
        Point SnapPoint(Point point);
        ResizeHandle GetHandleAt(Point point);
        int GetPolygonPointIndexAt(Point point);
        RoiBase? HitTest(Point point);
        void SetCoordinateText(string text);
        void SetCursor(Cursor cursor);
        void UpdateCrosshair(double x, double y);
    }

    internal interface IImageViewerSelectionInteractionHost
    {
        bool IsToolInteractionActive { get; }
        RoiBase? SelectedRoi { get; set; }

        RoiBase? HitTest(Point point);
        int GetPathVertexIndexAt(Point point) => -1;
        void CaptureContextualPathVertex(RoiBase? roi, int? pointIndex) { }
        void ExitCurrentMode();
        void DrawRois();
        void ShowRoiProperties(RoiBase roi);
    }

    internal interface IImageViewerEditInteractionHost
    {
        ImageViewerViewModel ViewModel { get; }
        ImageViewerInteractionManipulationState ManipulationState { get; }
        bool IsRootMouseCaptured { get; }
        bool IsToolInteractionActive { get; }

        void CaptureRootMouse();
        void ReleaseRootMouse();
        bool RemoveSelectedRoi();
        void Undo();
        void Redo();
        ResizeHandle GetHandleAt(Point point);
        int GetPolygonPointIndexAt(Point point);
        int GetPolygonSegmentAt(Point point);
        RoiBase? HitTest(Point point);
        void TryRefreshCaliperDetection(RoiBase? roi);
        void DrawRois();
        void DrawSelectedRoiLayer();
        void UpdateInfoPanel(bool force);
        void ExitCurrentMode();
        void ResizeRoi(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos);
        IUndoRedoCommand? CreateStateCommand(RoiBase roi, RoiBase oldState, RoiBase newState);
        void MoveRoi(RoiBase roi, double dx, double dy);
        void ShowStatusHint(string message, StatusHintKind kind);
    }

    internal interface IImageViewerPointerInteractionFlow
    {
        Point SnapPoint(Point rawImagePoint);
        void HandleMouseWheel(Point mousePosition, int wheelDelta);
        bool TryBeginPan(MouseButtonState middleButtonState, Point rootPosition);
        void BeginCanvasPan(Point rootPosition);
        bool TryHandleActivePointerMove(Point rootPosition, Point imagePosition);
        bool TryCompletePan();
        void ResetPanState();
        void UpdatePixelInfo(Point imagePosition);
        void UpdateCursorFeedback(Point imagePosition);
    }

    internal interface IImageViewerSelectionInteractionFlow
    {
        bool HandleRightClick(Point imagePosition);
        void ClearSelection();
    }

    internal interface IImageViewerEditInteractionFlow
    {
        bool HandleKeyInput(Key key, bool isCtrlPressed, bool isShiftPressed);
        bool TryBeginEdit(Point imagePosition, bool isRightButtonPressed);
        bool TryHandleActiveEditMove(Point imagePosition);
        void CompleteEdit();
        void ResetManipulationState();
    }

    internal sealed class InteractionController : IImageViewerInteractionController
    {
        private readonly IImageViewerInteractionControllerHost _host;
        private readonly IImageViewerPointerInteractionFlow _pointerInteractionFlow;
        private readonly IImageViewerSelectionInteractionFlow _selectionInteractionFlow;
        private readonly IImageViewerEditInteractionFlow _editInteractionFlow;

        public InteractionController(
            IImageViewerInteractionControllerHost host,
            IImageViewerPointerInteractionFlow pointerInteractionFlow,
            IImageViewerSelectionInteractionFlow selectionInteractionFlow,
            IImageViewerEditInteractionFlow editInteractionFlow)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pointerInteractionFlow = pointerInteractionFlow ?? throw new ArgumentNullException(nameof(pointerInteractionFlow));
            _selectionInteractionFlow = selectionInteractionFlow ?? throw new ArgumentNullException(nameof(selectionInteractionFlow));
            _editInteractionFlow = editInteractionFlow ?? throw new ArgumentNullException(nameof(editInteractionFlow));
        }

        public void HandleKeyDown(KeyEventArgs e)
        {
            bool isCtrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            bool isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            if (isCtrlPressed && ImageViewerViewShortcuts.TryGetCommand(e.Key, isShiftPressed, out ImageViewerViewCommand command) && _host.HasImage)
            {
                _host.ExecuteViewCommand(command);
                e.Handled = true;
                if (command is ImageViewerViewCommand.ZoomIn or ImageViewerViewCommand.ZoomOut or ImageViewerViewCommand.ResetView)
                {
                    _host.ShowZoomLevelHintAtCenter();
                }

                return;
            }

            if (_editInteractionFlow.HandleKeyInput(e.Key, isCtrlPressed, isShiftPressed))
            {
                e.Handled = true;
            }
        }

        public void HandleMouseRightButtonDown(MouseButtonEventArgs e)
        {
            // Context selection and vertex hit testing must use the actual pointer location;
            // grid snapping is an editing aid and can otherwise redirect a right-click.
            Point imagePosition = e.GetPosition(_host.ImageElement);
            if (_editInteractionFlow.TryBeginEdit(imagePosition, isRightButtonPressed: true))
            {
                e.Handled = true;
                return;
            }

            if (_selectionInteractionFlow.HandleRightClick(imagePosition))
            {
                e.Handled = true;
            }
        }

        public void HandleMouseWheel(MouseWheelEventArgs e)
        {
            Point mousePosition = e.GetPosition(_host.ImageElement);
            _pointerInteractionFlow.HandleMouseWheel(mousePosition, e.Delta);
            if (_host.HasImage)
            {
                _host.ShowZoomLevelHint(e.GetPosition(_host.RootElement));
            }
        }

        public void HandleMouseDown(MouseButtonEventArgs e)
        {
            _host.Focus();

            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && !_host.IsToolInteractionActive)
            {
                _editInteractionFlow.ResetManipulationState();
                _pointerInteractionFlow.ResetPanState();
                _host.ShowFullImage();
                e.Handled = true;
                return;
            }

            if (_pointerInteractionFlow.TryBeginPan(e.MiddleButton, e.GetPosition(_host.RootElement)))
            {
                e.Handled = true;
                return;
            }

            if (_host.IsToolInteractionActive || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point imagePosition = _pointerInteractionFlow.SnapPoint(e.GetPosition(_host.ImageElement));
            if (_editInteractionFlow.TryBeginEdit(imagePosition, e.RightButton == MouseButtonState.Pressed))
            {
                e.Handled = true;
                return;
            }

            _selectionInteractionFlow.ClearSelection();
            _host.DrawRois();
            // 空白画布左键只负责清除选择。画布平移统一使用中键拖拽，避免
            // “点击空白处选择”与“拖动画布”抢夺同一个手势。
        }

        public void HandleMouseMove(MouseEventArgs e)
        {
            Point currentPosition = e.GetPosition(_host.RootElement);
            Point imagePosition = _pointerInteractionFlow.SnapPoint(e.GetPosition(_host.ImageElement));

            if (_pointerInteractionFlow.TryHandleActivePointerMove(currentPosition, imagePosition))
            {
                return;
            }

            if (_host.HasImage)
            {
                _pointerInteractionFlow.UpdatePixelInfo(imagePosition);
            }

            if (_editInteractionFlow.TryHandleActiveEditMove(imagePosition))
            {
                return;
            }

            _pointerInteractionFlow.UpdateCursorFeedback(imagePosition);
            _host.UpdateCrosshair(currentPosition.X, currentPosition.Y);
        }

        public void HandleMouseUp(MouseButtonEventArgs e)
        {
            if (_pointerInteractionFlow.TryCompletePan())
            {
                e.Handled = true;
                return;
            }

            if (_host.IsToolInteractionActive)
            {
                return;
            }

            _editInteractionFlow.CompleteEdit();
        }

        public void HandleLostMouseCapture()
        {
            _pointerInteractionFlow.ResetPanState();
            _editInteractionFlow.ResetManipulationState();
        }
    }
}
