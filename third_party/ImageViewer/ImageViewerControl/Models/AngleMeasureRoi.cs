using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 角度测量 ROI
    /// Chinese: 表示角度测量工具的 ROI，包含三个点：P1（起点）、Vertex（顶点）和 P2（终点）。
    /// English: Represents an angle measurement ROI with three points: P1 (start), Vertex (vertex) and P2 (end).
    /// </summary>
    public class AngleMeasureRoi : RoiBase
    {
        private PointD _p1;
        private PointD _vertex;
        private PointD _p2;

        public AngleMeasureRoi()
        {
            StrokeColor = RoiColors.Orange;
        }

        public PointD P1
        {
            get => _p1;
            set => SetProperty(ref _p1, value);
        }

        public PointD Vertex
        {
            get => _vertex;
            set => SetProperty(ref _vertex, value);
        }

        public PointD P2
        {
            get => _p2;
            set => SetProperty(ref _p2, value);
        }

        public override RoiBase Clone()
        {
            return new AngleMeasureRoi
            {
                P1 = P1,
                Vertex = Vertex,
                P2 = P2,
                Label = Label,
                StrokeColor = StrokeColor,
                StrokeThickness = StrokeThickness,
                IsVisible = IsVisible,
                IsLocked = IsLocked,
                Tolerance = Tolerance?.Clone(),
                MeasurementId = MeasurementId,
                AdvancedMeasurements = AdvancedMeasurements
            };
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not AngleMeasureRoi angle)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(AngleMeasureRoi)}.", nameof(source));
            }

            P1 = angle.P1;
            Vertex = angle.Vertex;
            P2 = angle.P2;
            ApplyCommonState(angle);
        }
        /// <summary>
        /// 克隆当前 AngleMeasureRoi 实例。
        /// Chinese: 返回表示此角度测量当前状态的副本，供撤销/重做或状态备份使用。
        /// English: Creates a copy of this AngleMeasureRoi instance for undo/redo or snapshot purposes.
        /// </summary>
        /// <returns>AngleMeasureRoi 的副本 / A copy of the AngleMeasureRoi.</returns>
    }
}
