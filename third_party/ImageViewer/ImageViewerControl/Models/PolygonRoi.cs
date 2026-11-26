using System.Collections.ObjectModel;
using ImageViewer.Common;

using System.Linq;

namespace ImageViewer.Models
{
    /// <summary>
    /// 多边形 ROI
    /// Chinese: 表示一个多边形 ROI，使用 PointD 集合来描述顶点，并支持闭合标记。
    /// English: Represents a polygon ROI defined by a collection of Points and an IsClosed flag.
    /// </summary>
    public class PolygonRoi : RoiBase
    {
        private ObservableCollection<PointD> _points = new ObservableCollection<PointD>();
        private bool _isClosed;

        public ObservableCollection<PointD> Points
        {
            get => _points;
            set => SetProperty(ref _points, value);
        }

        public bool IsClosed
        {
            get => _isClosed;
            set => SetProperty(ref _isClosed, value);
        }

        public override RoiBase Clone()
        {
            return new PolygonRoi
            {
                Points = new ObservableCollection<PointD>(Points),
                IsClosed = IsClosed,
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
            if (source is not PolygonRoi polygon)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PolygonRoi)}.", nameof(source));
            }

            Points = new ObservableCollection<PointD>(polygon.Points);
            IsClosed = polygon.IsClosed;
            ApplyCommonState(polygon);
        }
        /// <summary>
        /// 克隆当前 PolygonRoi 实例。
        /// Chinese: 返回表示此多边形当前状态（顶点集合与闭合标记）的副本，供撤销/重做或状态备份使用。
        /// English: Creates a copy of this PolygonRoi instance (points collection and IsClosed) for undo/redo.
        /// </summary>
        /// <returns>PolygonRoi 的副本 / A copy of the PolygonRoi.</returns>
    }
}
