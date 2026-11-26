using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class CircleRoi : RoiBase
    {
        private PointD _center;
        private double _radius;

        public CircleRoi()
        {
            StrokeColor = RoiColors.Gold;
        }

        public PointD Center
        {
            get => _center;
            set => SetProperty(ref _center, value);
        }

        public double Radius
        {
            get => _radius;
            set => SetProperty(ref _radius, value);
        }

        public override RoiBase Clone()
        {
            return new CircleRoi
            {
                Center = Center,
                Radius = Radius,
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
            if (source is not CircleRoi circle)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(CircleRoi)}.", nameof(source));
            }

            Center = circle.Center;
            Radius = circle.Radius;
            ApplyCommonState(circle);
        }
    }
}
