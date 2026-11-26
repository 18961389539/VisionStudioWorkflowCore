using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class TextAnnotationRoi : RoiBase
    {
        private PointD _position;

        public TextAnnotationRoi()
        {
            StrokeColor = RoiColors.White;
        }

        public PointD Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        public override RoiBase Clone()
        {
            return new TextAnnotationRoi
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
            if (source is not TextAnnotationRoi text)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(TextAnnotationRoi)}.", nameof(source));
            }

            Position = text.Position;
            ApplyCommonState(text);
        }
    }
}
