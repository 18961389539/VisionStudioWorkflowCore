using System.Collections.ObjectModel;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    public class PolylineRoi : RoiBase
    {
        private ObservableCollection<PointD> _points = new();
        private bool _isFreehand;

        public PolylineRoi()
        {
            StrokeColor = RoiColors.LightGreen;
        }

        public ObservableCollection<PointD> Points
        {
            get => _points;
            set => SetProperty(ref _points, value);
        }

        public bool IsFreehand
        {
            get => _isFreehand;
            set => SetProperty(ref _isFreehand, value);
        }

        public override RoiBase Clone()
        {
            return new PolylineRoi
            {
                Points = new ObservableCollection<PointD>(Points),
                IsFreehand = IsFreehand,
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
            if (source is not PolylineRoi polyline)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PolylineRoi)}.", nameof(source));
            }

            Points = new ObservableCollection<PointD>(polyline.Points);
            IsFreehand = polyline.IsFreehand;
            ApplyCommonState(polyline);
        }
    }
}
