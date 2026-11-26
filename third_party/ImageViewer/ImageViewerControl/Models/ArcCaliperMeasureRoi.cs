using System;

namespace ImageViewer.Models
{
    public class ArcCaliperMeasureRoi : CircularCaliperMeasureRoi
    {
        private double _startAngle = -90;
        private double _sweepAngle = 180;

        public override string RoiTypeName => nameof(ArcCaliperMeasureRoi);

        public double StartAngle
        {
            get => _startAngle;
            set => SetProperty(ref _startAngle, value);
        }

        public double SweepAngle
        {
            get => _sweepAngle;
            set => SetProperty(ref _sweepAngle, Math.Clamp(value, -360, 360));
        }

        public override RoiBase Clone()
        {
            var clone = new ArcCaliperMeasureRoi();
            clone.ApplyFrom(this);
            clone.CopyMeasurementIdentityFrom(this);
            return clone;
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not ArcCaliperMeasureRoi arc)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ArcCaliperMeasureRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            StartAngle = arc.StartAngle;
            SweepAngle = arc.SweepAngle;
        }
    }
}
