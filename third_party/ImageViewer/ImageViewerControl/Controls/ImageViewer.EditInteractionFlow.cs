using System;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal sealed class EditInteractionFlow : IImageViewerEditInteractionFlow
    {
        private readonly IImageViewerEditInteractionHost _host;
        private readonly ImageViewerInteractionManipulationState _state;

        public EditInteractionFlow(IImageViewerEditInteractionHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _state = host.ManipulationState;
        }

        public bool HandleKeyInput(Key key, bool isCtrlPressed, bool isShiftPressed)
        {
            var viewModel = _host.ViewModel;

            if (key == Key.Delete)
            {
                if (_host.RemoveSelectedRoi())
                {
                    _host.DrawRois();
                    _host.ShowStatusHint(UiText.Get("StatusRoiDeleted"), StatusHintKind.Info);
                    return true;
                }

                return false;
            }

            if (isCtrlPressed && key == Key.Z)
            {
                if (viewModel.UndoRedo.CanUndo)
                {
                    // 走宿主（内部是 RoiEditController），由它负责刷新、状态提示与失败兜底。
                    _host.Undo();
                }

                return true;
            }

            if (isCtrlPressed && key == Key.Y)
            {
                if (viewModel.UndoRedo.CanRedo)
                {
                    _host.Redo();
                }

                return true;
            }

            if (viewModel.SelectedRoi != null && (key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right))
            {
                double step = isShiftPressed ? 10 : 1;
                double dx = 0;
                double dy = 0;

                if (key == Key.Up)
                {
                    dy = -step;
                }
                else if (key == Key.Down)
                {
                    dy = step;
                }
                else if (key == Key.Left)
                {
                    dx = -step;
                }
                else if (key == Key.Right)
                {
                    dx = step;
                }

                MoveRoi(viewModel.SelectedRoi, dx, dy);
                _host.TryRefreshCaliperDetection(viewModel.SelectedRoi);
                _host.DrawRois();
                return true;
            }

            if (key == Key.Escape)
            {
                if (_host.IsToolInteractionActive)
                {
                    _host.ShowStatusHint(UiText.Get("StatusDrawCancelled"), StatusHintKind.Info);
                }

                _host.ExitCurrentMode();
                return true;
            }

            return false;
        }

        public bool TryBeginEdit(Point imagePosition, bool isRightButtonPressed)
        {
            var viewModel = _host.ViewModel;
            // 右键保留给对象上下文菜单。顶点删除由菜单中的显式命令完成，
            // 不能在打开菜单之前改写几何或历史栈。
            if (isRightButtonPressed)
            {
                return false;
            }

            ResizeHandle handle = _host.GetHandleAt(imagePosition);
            if (handle != ResizeHandle.None)
            {
                _state.OriginalRoiState = viewModel.SelectedRoi?.Clone();
                _state.ActiveHandle = handle;
                _state.LastMousePosition = imagePosition;
                _host.CaptureRootMouse();
                return true;
            }

            int pointIndex = _host.GetPolygonPointIndexAt(imagePosition);
            if (pointIndex != -1)
            {
                _state.OriginalRoiState = viewModel.SelectedRoi?.Clone();
                _state.ActivePolygonPointIndex = pointIndex;
                _state.LastMousePosition = imagePosition;
                _host.CaptureRootMouse();
                return true;
            }

            int segmentIndex = _host.GetPolygonSegmentAt(imagePosition);
            if (!isRightButtonPressed && segmentIndex != -1 && viewModel.SelectedRoi is PolygonRoi or PolylineRoi)
            {
                RoiBase selectedPath = viewModel.SelectedRoi;
                _state.OriginalRoiState = selectedPath.Clone();
                if (selectedPath is PolygonRoi segmentPolygon)
                {
                    segmentPolygon.Points.Insert(segmentIndex + 1, imagePosition.ToPointD());
                }
                else if (selectedPath is PolylineRoi segmentPolyline)
                {
                    segmentPolyline.Points.Insert(segmentIndex + 1, imagePosition.ToPointD());
                }
                else
                {
                    _state.OriginalRoiState = null;
                    return false;
                }

                _state.ActivePolygonPointIndex = segmentIndex + 1;
                _state.LastMousePosition = imagePosition;
                _host.DrawRois();
                _host.CaptureRootMouse();
                return true;
            }

            RoiBase? hitRoi = _host.HitTest(imagePosition);
            if (hitRoi == null)
            {
                return false;
            }

            viewModel.SelectedRoi = hitRoi;
            if (hitRoi.IsLocked)
            {
                _host.DrawRois();
                return true;
            }

            _state.OriginalRoiState = hitRoi.Clone();
            _state.IsRoiDragging = true;
            _state.LastMousePosition = imagePosition;
            _host.CaptureRootMouse();
            _host.DrawRois();
            return true;
        }

        public bool TryHandleActiveEditMove(Point imagePosition)
        {
            var viewModel = _host.ViewModel;
            if (_state.ActiveHandle != ResizeHandle.None && viewModel.SelectedRoi != null)
            {
                double dx = imagePosition.X - _state.LastMousePosition.X;
                double dy = imagePosition.Y - _state.LastMousePosition.Y;
                _host.ResizeRoi(viewModel.SelectedRoi, _state.ActiveHandle, dx, dy, imagePosition);
                _state.LastMousePosition = imagePosition;
                _host.DrawSelectedRoiLayer();
                return true;
            }

            if (_state.ActivePolygonPointIndex != -1 && viewModel.SelectedRoi is PolygonRoi or PolylineRoi)
            {
                if (viewModel.SelectedRoi is PolygonRoi polygon)
                {
                    polygon.Points[_state.ActivePolygonPointIndex] = imagePosition.ToPointD();
                }
                else if (viewModel.SelectedRoi is PolylineRoi polyline)
                {
                    polyline.Points[_state.ActivePolygonPointIndex] = imagePosition.ToPointD();
                }

                _state.LastMousePosition = imagePosition;
                _host.DrawSelectedRoiLayer();
                return true;
            }

            if (_state.IsRoiDragging && viewModel.SelectedRoi != null)
            {
                double dx = imagePosition.X - _state.LastMousePosition.X;
                double dy = imagePosition.Y - _state.LastMousePosition.Y;
                MoveRoi(viewModel.SelectedRoi, dx, dy);
                _state.LastMousePosition = imagePosition;
                _host.DrawSelectedRoiLayer();
                return true;
            }

            return false;
        }

        public void CompleteEdit()
        {
            var viewModel = _host.ViewModel;
            bool hadInteractiveManipulation = _state.HasActiveRoiManipulation;

            if (_state.OriginalRoiState != null && viewModel.SelectedRoi is RoiBase selectedRoi)
            {
                if (hadInteractiveManipulation)
                {
                    _host.TryRefreshCaliperDetection(selectedRoi);
                }

                RoiBase newRoiState = selectedRoi.Clone();
                IUndoRedoCommand? command = _host.CreateStateCommand(selectedRoi, _state.OriginalRoiState, newRoiState);
                if (command != null)
                {
                    viewModel.UndoRedo.Execute(command);
                }
            }

            ResetManipulationState();

            if (hadInteractiveManipulation)
            {
                _host.DrawRois();
                _host.UpdateInfoPanel(force: true);
            }
        }

        public void ResetManipulationState()
        {
            _state.ResetRoiManipulation();
            if (_host.IsRootMouseCaptured && !_state.IsCanvasDragging)
            {
                _host.ReleaseRootMouse();
            }
        }

        private void MoveRoi(RoiBase roi, double dx, double dy)
        {
            _host.MoveRoi(roi, dx, dy);
        }
    }
}
