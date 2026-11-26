using System;

namespace ImageViewer.Models
{
    public class ArrowAnnotationRoi : LineMeasureRoi
    {
        private double _arrowHeadLength = 12;

        public override string RoiTypeName => nameof(ArrowAnnotationRoi);

        public double ArrowHeadLength
        {
            get => _arrowHeadLength;
            set => SetProperty(ref _arrowHeadLength, Math.Max(4, value));
        }

        public override RoiBase Clone()
        {
            return new ArrowAnnotationRoi
            {
                P1 = P1,
                P2 = P2,
                ArrowHeadLength = ArrowHeadLength,
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
            if (source is not ArrowAnnotationRoi arrow)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ArrowAnnotationRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            ArrowHeadLength = arrow.ArrowHeadLength;
        }
    }
}
