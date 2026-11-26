using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class PointAnnotationRoi : RoiBase
    {
        private PointD _position;

        public PointAnnotationRoi()
        {
            StrokeColor = RoiColors.DeepSkyBlue;
        }

        public PointD Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        public override RoiBase Clone()
        {
            return new PointAnnotationRoi
            {
                Position = Position,
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
            if (source is not PointAnnotationRoi PointD)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PointAnnotationRoi)}.", nameof(source));
            }

            Position = PointD.Position;
            ApplyCommonState(PointD);
        }
    }
}
