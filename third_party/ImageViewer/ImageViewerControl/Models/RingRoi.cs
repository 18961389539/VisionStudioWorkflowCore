using System;

namespace ImageViewer.Models
{
    public class RingRoi : RoiBase
    {
        private PointD _center;
        private double _innerRadius;
        private double _outerRadius;

        public override string RoiTypeName => nameof(RingRoi);

        public PointD Center
        {
            get => _center;
            set => SetProperty(ref _center, value);
        }

        public double InnerRadius
        {
            get => _innerRadius;
            set => SetProperty(ref _innerRadius, Math.Max(0, value));
        }

        public double OuterRadius
        {
            get => _outerRadius;
            set => SetProperty(ref _outerRadius, Math.Max(0, value));
        }

        public double Area => Math.PI * Math.Max(0, OuterRadius * OuterRadius - InnerRadius * InnerRadius);

        public override RoiBase Clone()
        {
            return new RingRoi
            {
                Center = Center,
                InnerRadius = InnerRadius,
                OuterRadius = OuterRadius,
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
            if (source is not RingRoi ring)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(RingRoi)}.", nameof(source));
            }

            Center = ring.Center;
            InnerRadius = ring.InnerRadius;
            OuterRadius = ring.OuterRadius;
            ApplyCommonState(ring);
        }
    }
}
