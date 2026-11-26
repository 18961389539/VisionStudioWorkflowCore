using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class PointToCircleDistanceRoi : RoiBase
    {
        private PointD _point;
        private PointD _center;
        private double _radius;

        public PointToCircleDistanceRoi()
        {
            StrokeColor = RoiColors.Turquoise;
        }

        public override string RoiTypeName => "PointToCircleDistance";

        public PointD Point
        {
            get => _point;
            set => SetProperty(ref _point, value);
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

        public double DistanceToCenter
        {
            get
            {
                double dx = Point.X - Center.X;
                double dy = Point.Y - Center.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public double DistanceToCircle
        {
            get
            {
                return Math.Abs(DistanceToCenter - Radius);
            }
        }

        public bool IsInside => DistanceToCenter < Radius;

        public bool IsOnCircle => Math.Abs(DistanceToCenter - Radius) < 0.5;

        public PointD NearestPointOnCircle
        {
            get
            {
                double dist = DistanceToCenter;
                if (dist < 1e-10)
                {
                    return new PointD(Center.X + Radius, Center.Y);
                }

                double ratio = Radius / dist;
                return new PointD(
                    Center.X + (Point.X - Center.X) * ratio,
                    Center.Y + (Point.Y - Center.Y) * ratio);
            }
        }

        public override RoiBase Clone()
        {
            return new PointToCircleDistanceRoi
            {
                Point = Point,
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
            if (source is not PointToCircleDistanceRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PointToCircleDistanceRoi)}.", nameof(source));
            }

            Point = roi.Point;
            Center = roi.Center;
            Radius = roi.Radius;
            ApplyCommonState(roi);
        }
    }
}
