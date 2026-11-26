using System;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private sealed class EditInteractionHost : IImageViewerEditInteractionHost
        {
            private readonly ImageViewer _owner;
            private readonly RoiEditController _roiEditController;

            public EditInteractionHost(ImageViewer owner, RoiEditController roiEditController)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _roiEditController = roiEditController ?? throw new ArgumentNullException(nameof(roiEditController));
            }

            public ImageViewerViewModel ViewModel => _owner.ViewerState;

            public ImageViewerInteractionManipulationState ManipulationState => _owner._interactionManipulationState;

            public bool IsRootMouseCaptured => _owner.rootGrid.IsMouseCaptured;

            public bool IsToolInteractionActive => _owner.IsToolInteractionActive;

            public void CaptureRootMouse() => _owner.rootGrid.CaptureMouse();

            public void ReleaseRootMouse() => _owner.rootGrid.ReleaseMouseCapture();

            public bool RemoveSelectedRoi() => _roiEditController.RemoveSelectedRoi();

            public ResizeHandle GetHandleAt(System.Windows.Point point) => _owner.GetHandleAt(point);

            public int GetPolygonPointIndexAt(System.Windows.Point point) => _owner.GetPolygonPointIndexAt(point);

            public int GetPolygonSegmentAt(System.Windows.Point point) => _owner.GetPolygonSegmentAt(point);

            public RoiBase? HitTest(System.Windows.Point point) => _owner.HitTest(point);

            public void TryRefreshCaliperDetection(RoiBase? roi) => _ = _owner.TryRefreshCaliperDetection(roi);

            public void DrawRois() => _owner.DrawRois();

            public void DrawSelectedRoiLayer() => _owner.DrawSelectedRoiLayer();

            public void UpdateInfoPanel(bool force) => _owner.UpdateInfoPanel(force);

            public void ExitCurrentMode() => _owner.ExitCurrentMode();

            public void ResizeRoi(RoiBase roi, ResizeHandle handle, double dx, double dy, System.Windows.Point currentPos) => _owner.ResizeRoi(roi, handle, dx, dy, currentPos);

            public IUndoRedoCommand? CreateStateCommand(RoiBase roi, RoiBase oldState, RoiBase newState) => ImageViewer.CreateStateCommand(roi, oldState, newState);

            public void MoveRoi(RoiBase roi, double dx, double dy)
            {
                _owner.RoiInteraction.MoveRoi(roi, dx, dy);
                if (roi is CaliperMeasureRoi line)
                {
                    line.EnsureCaliperRegion();
                    line.ClearDetectedEdges();
                }
                else if (roi is CircularCaliperMeasureRoi circular)
                {
                    circular.ClearDetectedEdges();
                }
            }

            public void ShowStatusHint(string message, StatusHintKind kind) => _owner.ShowStatusHint(message, kind);

            // 撤销/重做统一走 RoiEditController：它在命令抛异常时记录日志并给出状态提示，
            // 避免键盘路径直接把异常抛出事件处理器。
            public void Undo() => _roiEditController.Undo();

            public void Redo() => _roiEditController.Redo();
        }
    }
}