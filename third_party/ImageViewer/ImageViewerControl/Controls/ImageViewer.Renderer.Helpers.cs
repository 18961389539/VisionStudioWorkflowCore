using System.Windows;
using System.Windows.Controls;
using ImageViewer.Models;
using ImageViewer.Rendering;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private RoiRenderContext CreateRoiRenderContext(Canvas targetCanvas)
        {
            return new RoiRenderContext(
                targetCanvas,
                ScreenOverlayCanvas,
                imageContainer.RenderTransform.Value.Transform,
                Scale,
                PixelSize,
                PhysicalUnit,
                ShowCaliperScores,
                HandleSize,
                InfoTextOffset,
                AngleArcRadius,
                PointAnnotationSize,
                PolygonResizeHandlePadding,
                PolygonCloseHighlightPadding) { Calibration = Calibration };
        }

        /// <summary>
        /// 把像素长度格式化为"数值 + 物理单位"文本。
        /// Chinese: 比例尺与信息面板共用，故保留在控件侧。
        /// English: Formats a pixel length as "value + physical unit". Shared by the scale bar and the
        /// info panel, hence kept on the control.
        /// </summary>
        private string FormatLength(double pixelLength)
        {
            return $"{pixelLength * PixelSize:F2} {GetDisplayUnit()}";
        }

        private string GetDisplayUnit()
        {
            return string.IsNullOrWhiteSpace(PhysicalUnit) ? "px" : PhysicalUnit;
        }

        public ResizeHandle GetHandleAt(Point point)
        {
            return RoiInteraction.GetHandleAt(ViewModel.SelectedRoi, point, Scale, HandleSize, HandleHitPadding, InfoTextOffset, PolygonVertexHitPadding);
        }

        private void ResizeRoi(RoiBase roi, ResizeHandle handle, double dx, double dy, Point currentPos)
        {
            RoiInteraction.ResizeRoi(roi, handle, dx, dy, currentPos, MinimumRoiDimension);
            if (roi is CaliperMeasureRoi line)
            {
                line.ClearDetectedEdges();
            }
            else if (roi is CircularCaliperMeasureRoi circular)
            {
                circular.ClearDetectedEdges();
            }
        }

        public RoiBase? HitTest(Point point)
        {
            return RoiInteraction.HitTest(ViewModel, point, Scale, HitTestTolerance);
        }
    }
}
