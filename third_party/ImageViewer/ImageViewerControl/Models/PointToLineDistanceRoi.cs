using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class PointToLineDistanceRoi : RoiBase
    {
        private PointD _point;
        private PointD _lineP1;
        private PointD _lineP2;

        public PointToLineDistanceRoi()
        {
            StrokeColor = RoiColors.LimeGreen;
        }

        public override string RoiTypeName => "PointToLineDistance";

        public PointD Point
        {
            get => _point;
            set => SetProperty(ref _point, value);
        }

        public PointD LineP1
        {
            get => _lineP1;
            set => SetProperty(ref _lineP1, value);
        }

        public PointD LineP2
        {
            get => _lineP2;
            set => SetProperty(ref _lineP2, value);
        }

        public double Distance
        {
            get
            {
                double dx = LineP2.X - LineP1.X;
                double dy = LineP2.Y - LineP1.Y;
                double lineLengthSquared = dx * dx + dy * dy;

                if (lineLengthSquared < 1e-10)
                {
                    return Math.Sqrt((Point.X - LineP1.X) * (Point.X - LineP1.X) +
                                     (Point.Y - LineP1.Y) * (Point.Y - LineP1.Y));
                }

                double t = ((Point.X - LineP1.X) * dx + (Point.Y - LineP1.Y) * dy) / lineLengthSquared;
                t = Math.Max(0, Math.Min(1, t));

                double projX = LineP1.X + t * dx;
                double projY = LineP1.Y + t * dy;

                return Math.Sqrt((Point.X - projX) * (Point.X - projX) +
                                 (Point.Y - projY) * (Point.Y - projY));
            }
        }

        public PointD FootPoint
        {
            get
            {
                double dx = LineP2.X - LineP1.X;
                double dy = LineP2.Y - LineP1.Y;
                double lineLengthSquared = dx * dx + dy * dy;

                if (lineLengthSquared < 1e-10)
                {
                    return LineP1;
                }

                double t = ((Point.X - LineP1.X) * dx + (Point.Y - LineP1.Y) * dy) / lineLengthSquared;
                t = Math.Max(0, Math.Min(1, t));

                return new PointD(LineP1.X + t * dx, LineP1.Y + t * dy);
            }
        }

        public override RoiBase Clone()
        {
            return new PointToLineDistanceRoi
            {
                Point = Point,
                LineP1 = LineP1,
                LineP2 = LineP2,
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
            if (source is not PointToLineDistanceRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PointToLineDistanceRoi)}.", nameof(source));
            }

            Point = roi.Point;
            LineP1 = roi.LineP1;
            LineP2 = roi.LineP2;
            ApplyCommonState(roi);
        }
    }
}
