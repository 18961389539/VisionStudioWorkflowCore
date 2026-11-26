using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class ParallelismMeasureRoi : RoiBase
    {
        private PointD _line1P1;
        private PointD _line1P2;
        private PointD _line2P1;
        private PointD _line2P2;

        public ParallelismMeasureRoi()
        {
            StrokeColor = RoiColors.Gold;
        }

        public override string RoiTypeName => "Parallelism";

        public PointD Line1P1
        {
            get => _line1P1;
            set => SetProperty(ref _line1P1, value);
        }

        public PointD Line1P2
        {
            get => _line1P2;
            set => SetProperty(ref _line1P2, value);
        }

        public PointD Line2P1
        {
            get => _line2P1;
            set => SetProperty(ref _line2P1, value);
        }

        public PointD Line2P2
        {
            get => _line2P2;
            set => SetProperty(ref _line2P2, value);
        }

        public double Line1Angle
        {
            get
            {
                double dx = Line1P2.X - Line1P1.X;
                double dy = Line1P2.Y - Line1P1.Y;
                return Math.Atan2(dy, dx) * 180.0 / Math.PI;
            }
        }

        public double Line2Angle
        {
            get
            {
                double dx = Line2P2.X - Line2P1.X;
                double dy = Line2P2.Y - Line2P1.Y;
                return Math.Atan2(dy, dx) * 180.0 / Math.PI;
            }
        }

        public double AngleDifference
        {
            get
            {
                double diff = Math.Abs(Line1Angle - Line2Angle);
                if (diff > 90) diff = 180 - diff;
                if (diff > 90) diff = 180 - diff;
                return diff;
            }
        }

        public double ParallelismError => AngleDifference;

        public bool IsParallel => AngleDifference < 1.0;

        public double Line1Length
        {
            get
            {
                double dx = Line1P2.X - Line1P1.X;
                double dy = Line1P2.Y - Line1P1.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public double Line2Length
        {
            get
            {
                double dx = Line2P2.X - Line2P1.X;
                double dy = Line2P2.Y - Line2P1.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        public double AverageDistance
        {
            get
            {
                double dist1 = PointToLineDistance(Line2P1, Line1P1, Line1P2);
                double dist2 = PointToLineDistance(Line2P2, Line1P1, Line1P2);
                return (dist1 + dist2) / 2;
            }
        }

        private static double PointToLineDistance(PointD point, PointD lineP1, PointD lineP2)
        {
            double dx = lineP2.X - lineP1.X;
            double dy = lineP2.Y - lineP1.Y;
            double lineLengthSquared = dx * dx + dy * dy;

            if (lineLengthSquared < 1e-10)
            {
                return Math.Sqrt((point.X - lineP1.X) * (point.X - lineP1.X) +
                                 (point.Y - lineP1.Y) * (point.Y - lineP1.Y));
            }

            double t = ((point.X - lineP1.X) * dx + (point.Y - lineP1.Y) * dy) / lineLengthSquared;
            double projX = lineP1.X + t * dx;
            double projY = lineP1.Y + t * dy;

            return Math.Sqrt((point.X - projX) * (point.X - projX) +
                             (point.Y - projY) * (point.Y - projY));
        }

        public override RoiBase Clone()
        {
            return new ParallelismMeasureRoi
            {
                Line1P1 = Line1P1,
                Line1P2 = Line1P2,
                Line2P1 = Line2P1,
                Line2P2 = Line2P2,
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
            if (source is not ParallelismMeasureRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ParallelismMeasureRoi)}.", nameof(source));
            }

            Line1P1 = roi.Line1P1;
            Line1P2 = roi.Line1P2;
            Line2P1 = roi.Line2P1;
            Line2P2 = roi.Line2P2;
            ApplyCommonState(roi);
        }
    }
}
