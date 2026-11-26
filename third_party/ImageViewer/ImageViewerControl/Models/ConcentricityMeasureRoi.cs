using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class ConcentricityMeasureRoi : RoiBase
    {
        private PointD _center1;
        private double _radius1;
        private PointD _center2;
        private double _radius2;

        public ConcentricityMeasureRoi()
        {
            StrokeColor = RoiColors.Coral;
        }

        public override string RoiTypeName => "Concentricity";

        public PointD Center1
        {
            get => _center1;
            set => SetProperty(ref _center1, value);
        }

        public double Radius1
        {
            get => _radius1;
            set => SetProperty(ref _radius1, value);
        }

        public PointD Center2
        {
            get => _center2;
            set => SetProperty(ref _center2, value);
        }

        public double Radius2
        {
            get => _radius2;
            set => SetProperty(ref _radius2, value);
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

        public double ConcentricityError => CenterDistance;

        public bool IsConcentric => CenterDistance < 1.0;

        public double RadiusDifference => Math.Abs(Radius1 - Radius2);

        public double AverageRadius => (Radius1 + Radius2) / 2;

        public PointD MidCenter => new PointD(
            (Center1.X + Center2.X) / 2,
            (Center1.Y + Center2.Y) / 2);

        public double Eccentricity
        {
            get
            {
                double avgRadius = AverageRadius;
                if (avgRadius < 1e-10) return 0;
                return CenterDistance / avgRadius;
            }
        }

        public override RoiBase Clone()
        {
            return new ConcentricityMeasureRoi
            {
                Center1 = Center1,
                Radius1 = Radius1,
                Center2 = Center2,
                Radius2 = Radius2,
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
            if (source is not ConcentricityMeasureRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ConcentricityMeasureRoi)}.", nameof(source));
            }

            Center1 = roi.Center1;
            Radius1 = roi.Radius1;
            Center2 = roi.Center2;
            Radius2 = roi.Radius2;
            ApplyCommonState(roi);
        }
    }
}
