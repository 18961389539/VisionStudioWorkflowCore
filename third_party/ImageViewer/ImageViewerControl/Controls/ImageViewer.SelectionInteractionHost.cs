using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private sealed class SelectionInteractionHost : IImageViewerSelectionInteractionHost
        {
            private readonly ImageViewer _owner;

            public SelectionInteractionHost(ImageViewer owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public bool IsToolInteractionActive => _owner.IsToolInteractionActive;

            public RoiBase? SelectedRoi
            {
                get => _owner.ViewerState.SelectedRoi;
                set => _owner.ViewerState.SelectedRoi = value;
            }

            public RoiBase? HitTest(System.Windows.Point point) => _owner.HitTest(point);

            public int GetPathVertexIndexAt(System.Windows.Point point) => _owner.GetPolygonPointIndexAt(point);

            public void CaptureContextualPathVertex(RoiBase? roi, int? pointIndex) => _owner.CaptureContextualPathVertex(roi, pointIndex);

            public void ExitCurrentMode() => _owner.ExitCurrentMode();

            public void DrawRois() => _owner.DrawRois();

            public void ShowRoiProperties(RoiBase roi) => _owner.ShowRoiProperties(roi);
        }
    }
}
