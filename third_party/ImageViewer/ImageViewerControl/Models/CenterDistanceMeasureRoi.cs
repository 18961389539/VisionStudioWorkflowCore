using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 两个圆心之间的距离测量。
    /// Chinese: 记录两个被选圆的圆心，并显示圆心连线与距离。
    /// English: Measures the distance between the centers of two selected circles.
    /// </summary>
    public class CenterDistanceMeasureRoi : RoiBase
    {
        private PointD _center1;
        private PointD _center2;

        public CenterDistanceMeasureRoi()
        {
            StrokeColor = RoiColors.Coral;
        }

        public override string RoiTypeName => nameof(CenterDistanceMeasureRoi);

        public PointD Center1
        {
            get => _center1;
            set => SetProperty(ref _center1, value);
        }

        public PointD Center2
        {
            get => _center2;
            set => SetProperty(ref _center2, value);
        }

        public double CenterDistance
        {
            get
            {
                double dx = Center2.X - Center1.X;
                double dy = Center2.Y - Center1.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public PointD MidCenter => new(
            (Center1.X + Center2.X) / 2,
            (Center1.Y + Center2.Y) / 2);

        public override RoiBase Clone()
        {
            return new CenterDistanceMeasureRoi
            {
                Center1 = Center1,
                Center2 = Center2,
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
            if (source is not CenterDistanceMeasureRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(CenterDistanceMeasureRoi)}.", nameof(source));
            }

            Center1 = roi.Center1;
            Center2 = roi.Center2;
            ApplyCommonState(roi);
        }
    }
}
