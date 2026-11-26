using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerInteractionManipulationState
    {
        public Point LastMousePosition { get; set; }

        public bool IsCanvasDragging { get; set; }

        public bool IsRoiDragging { get; set; }

        public ResizeHandle ActiveHandle { get; set; } = ResizeHandle.None;

        public int ActivePolygonPointIndex { get; set; } = -1;

        public RoiBase? OriginalRoiState { get; set; }

        public bool HasActiveRoiManipulation =>
            ActiveHandle != ResizeHandle.None ||
            ActivePolygonPointIndex != -1 ||
            IsRoiDragging;

        public void ResetRoiManipulation()
        {
            OriginalRoiState = null;
            ActiveHandle = ResizeHandle.None;
            ActivePolygonPointIndex = -1;
            IsRoiDragging = false;
        }
    }
}