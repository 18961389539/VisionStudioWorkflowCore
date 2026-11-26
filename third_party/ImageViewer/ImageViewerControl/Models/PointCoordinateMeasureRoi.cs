using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 单点坐标测量。位置可以来自普通点击，也可以来自自动边缘吸附。
    /// </summary>
    public sealed class PointCoordinateMeasureRoi : RoiBase
    {
        private PointD _position;
        private double _edgeScore;
        private double _edgeConfidence;
        private bool _isEdgeSnapped;

        public PointCoordinateMeasureRoi()
        {
            StrokeColor = RoiColors.LimeGreen;
        }

        public override string RoiTypeName => "PointCoordinateMeasure";

        public PointD Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        /// <summary>自动边缘搜索得到的梯度分数，原始灰度单位。</summary>
        public double EdgeScore
        {
            get => _edgeScore;
            set => SetProperty(ref _edgeScore, value);
        }

        /// <summary>自动边缘搜索的综合置信度，范围 0 到 1。</summary>
        public double EdgeConfidence
        {
            get => _edgeConfidence;
            set => SetProperty(ref _edgeConfidence, value);
        }

        public bool IsEdgeSnapped
        {
            get => _isEdgeSnapped;
            set => SetProperty(ref _isEdgeSnapped, value);
        }

        public override RoiBase Clone()
        {
            return new PointCoordinateMeasureRoi
            {
                Position = Position,
                EdgeScore = EdgeScore,
                EdgeConfidence = EdgeConfidence,
                IsEdgeSnapped = IsEdgeSnapped,
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
            if (source is not PointCoordinateMeasureRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(PointCoordinateMeasureRoi)}.", nameof(source));
            }

            Position = roi.Position;
            EdgeScore = roi.EdgeScore;
            EdgeConfidence = roi.EdgeConfidence;
            IsEdgeSnapped = roi.IsEdgeSnapped;
            ApplyCommonState(roi);
        }
    }
}
