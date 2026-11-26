using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class ArcMeasureRoi : RoiBase
    {
        private PointD _startPoint;
        private PointD _endPoint;
        private PointD _arcPoint;
        private PointD _center;
        private double _radius;
        private double _startAngle;
        private double _sweepAngle;
        private bool _isComputed;

        public ArcMeasureRoi()
        {
            StrokeColor = RoiColors.Coral;
        }

        public override string RoiTypeName => nameof(ArcMeasureRoi);

        public PointD StartPoint
        {
            get => _startPoint;
            set
            {
                if (SetProperty(ref _startPoint, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD EndPoint
        {
            get => _endPoint;
            set
            {
                if (SetProperty(ref _endPoint, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD ArcPoint
        {
            get => _arcPoint;
            set
            {
                if (SetProperty(ref _arcPoint, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD Center
        {
            get
            {
                EnsureComputed();
                return _center;
            }
        }

        public double Radius
        {
            get
            {
                EnsureComputed();
                return _radius;
            }
        }

        public double StartAngle
        {
            get
            {
                EnsureComputed();
                return _startAngle;
            }
        }

        public double SweepAngle
        {
            get
            {
                EnsureComputed();
                return _sweepAngle;
            }
        }

        public double ArcLength
        {
            get
            {
                EnsureComputed();
                return Math.Abs(_sweepAngle) * Math.PI / 180.0 * _radius;
            }
        }

        public double CentralAngle => Math.Abs(SweepAngle);

        public bool IsValid
        {
            get
            {
                EnsureComputed();
                return _radius > 0 && !double.IsNaN(_sweepAngle);
            }
        }

        public void EnsureComputed()
        {
            if (_isComputed) return;

            _isComputed = true;
            if (!TryComputeArcParameters(_startPoint, _endPoint, _arcPoint, out _center, out _radius, out _startAngle, out _sweepAngle))
            {
                _radius = 0;
                _sweepAngle = double.NaN;
            }
        }

        public static bool TryComputeArcParameters(PointD start, PointD end, PointD arcPoint, out PointD center, out double radius, out double startAngle, out double sweepAngle)
        {
            center = default;
            radius = 0;
            startAngle = 0;
            sweepAngle = double.NaN;

            double d1 = DistanceSquared(start, end);
            double d2 = DistanceSquared(start, arcPoint);
            double d3 = DistanceSquared(end, arcPoint);

            if (d1 < 1e-10 || d2 < 1e-10 || d3 < 1e-10)
            {
                return false;
            }

            double ax = start.X, ay = start.Y;
            double bx = end.X, by = end.Y;
            double cx = arcPoint.X, cy = arcPoint.Y;

            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-10)
            {
                return false;
            }

            double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

            center = new PointD(ux, uy);
            radius = Math.Sqrt(DistanceSquared(center, start));

            startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X) * 180.0 / Math.PI;
            double endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X) * 180.0 / Math.PI;
            double midAngle = Math.Atan2(arcPoint.Y - center.Y, arcPoint.X - center.X) * 180.0 / Math.PI;

            sweepAngle = ComputeSweepAngle(startAngle, midAngle, endAngle);

            return true;
        }

        private static double ComputeSweepAngle(double startAngle, double midAngle, double endAngle)
        {
            startAngle = NormalizeAngle(startAngle);
            midAngle = NormalizeAngle(midAngle);
            endAngle = NormalizeAngle(endAngle);

            double sweepCW = NormalizeAngle(endAngle - startAngle);
            double sweepCCW = NormalizeAngle(startAngle - endAngle);

            double midFromStart = NormalizeAngle(midAngle - startAngle);

            if (midFromStart < sweepCW)
            {
                return sweepCW;
            }
            else
            {
                return -sweepCCW;
            }
        }

        private static double NormalizeAngle(double angle)
        {
            angle = angle % 360;
            if (angle < 0) angle += 360;
            return angle;
        }

        private static double DistanceSquared(PointD p1, PointD p2)
        {
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            return dx * dx + dy * dy;
        }

        public override RoiBase Clone()
        {
            return new ArcMeasureRoi
            {
                StartPoint = StartPoint,
                EndPoint = EndPoint,
                ArcPoint = ArcPoint,
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
            if (source is not ArcMeasureRoi arc)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ArcMeasureRoi)}.", nameof(source));
            }

            StartPoint = arc.StartPoint;
            EndPoint = arc.EndPoint;
            ArcPoint = arc.ArcPoint;
            ApplyCommonState(arc);
        }
    }
}
