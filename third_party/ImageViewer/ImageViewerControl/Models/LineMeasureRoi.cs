using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 测量线 ROI
    /// Chinese: 表示一条用于测量距离的线段 ROI，包含两个端点 P1 与 P2。
    /// English: Represents a line measurement ROI with endpoints P1 and P2.
    /// </summary>
    public class LineMeasureRoi : RoiBase
    {
        private PointD _p1;
        private PointD _p2;

        public LineMeasureRoi()
        {
            StrokeColor = RoiColors.Pink;
        }

        public PointD P1
        {
            get => _p1;
            set => SetProperty(ref _p1, value);
        }

        public PointD P2
        {
            get => _p2;
            set => SetProperty(ref _p2, value);
        }

        public override RoiBase Clone()
        {
            return new LineMeasureRoi
            {
                P1 = P1,
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
            if (source is not LineMeasureRoi line)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(LineMeasureRoi)}.", nameof(source));
            }

            P1 = line.P1;
            P2 = line.P2;
            ApplyCommonState(line);
        }
        /// <summary>
        /// 克隆当前 LineMeasureRoi 实例。
        /// Chinese: 返回表示此测量线当前状态的副本，供撤销/重做或状态备份使用。
        /// English: Creates a copy of this LineMeasureRoi instance for undo/redo or snapshot purposes.
        /// </summary>
        /// <returns>LineMeasureRoi 的副本 / A copy of the LineMeasureRoi.</returns>
    }
}
