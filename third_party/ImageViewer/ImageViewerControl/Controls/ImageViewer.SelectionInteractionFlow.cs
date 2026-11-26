using System;
using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class SelectionInteractionFlow : IImageViewerSelectionInteractionFlow
    {
        private readonly IImageViewerSelectionInteractionHost _host;

        public SelectionInteractionFlow(IImageViewerSelectionInteractionHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public bool HandleRightClick(Point imagePosition)
        {
            if (_host.IsToolInteractionActive)
            {
                _host.CaptureContextualPathVertex(null, null);
                _host.ExitCurrentMode();
                return true;
            }

            RoiBase? hitRoi = _host.HitTest(imagePosition);
            if (hitRoi == null)
            {
                // 右键的上下文来自指针所在位置。空白处右键时清掉旧的选择，
                // 避免菜单继续展示上一件 ROI 的编辑命令。
                _host.SelectedRoi = null;
                _host.CaptureContextualPathVertex(null, null);
                _host.DrawRois();
                return false;
            }

            _host.SelectedRoi = hitRoi;
            int pointIndex = _host.GetPathVertexIndexAt(imagePosition);
            _host.CaptureContextualPathVertex(hitRoi, pointIndex >= 0 ? pointIndex : null);
            _host.DrawRois();
            return false;
        }

        public void ClearSelection()
        {
            _host.SelectedRoi = null;
            _host.CaptureContextualPathVertex(null, null);
        }
    }
}
