using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 椭圆 ROI
    /// Chinese: 表示一个椭圆形的 ROI，包含中心、半径和旋转角度。
    /// English: Represents an elliptical ROI with Center, RadiusX, RadiusY and Angle properties.
    /// </summary>
    public class EllipseRoi : RoiBase
    {
        private PointD _center;
        private double _radiusX;
        private double _radiusY;
        private double _angle;

        public EllipseRoi()
        {
            StrokeColor = RoiColors.Yellow;
        }

        public PointD Center
        {
            get => _center;
            set => SetProperty(ref _center, value);
        }

        public double RadiusX
        {
            get => _radiusX;
            set => SetProperty(ref _radiusX, value);
        }

        public double RadiusY
        {
            get => _radiusY;
            set => SetProperty(ref _radiusY, value);
        }

        public double Angle
        {
            get => _angle;
            set => SetProperty(ref _angle, value);
        }

        public override RoiBase Clone()
        {
            return new EllipseRoi
            {
                Center = Center,
                RadiusX = RadiusX,
                RadiusY = RadiusY,
                Angle = Angle,
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
            if (source is not EllipseRoi ellipse)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(EllipseRoi)}.", nameof(source));
            }

            Center = ellipse.Center;
            RadiusX = ellipse.RadiusX;
            RadiusY = ellipse.RadiusY;
            Angle = ellipse.Angle;
            ApplyCommonState(ellipse);
        }
        /// <summary>
        /// 克隆当前 EllipseRoi 实例。
        /// Chinese: 返回表示此椭圆当前状态的副本，供撤销/重做或状态备份使用。
        /// English: Creates a copy of this EllipseRoi instance for undo/redo or snapshot purposes.
        /// </summary>
        /// <returns>EllipseRoi 的副本 / A copy of the EllipseRoi.</returns>
    }
}
