using System;
using System.Runtime.CompilerServices;

namespace ImageViewer.Models
{
    public class CaliperMeasureRoi : LineMeasureRoi, ICaliperInfoSource
    {
        private PointD _caliperCenter;
        private double _caliperAngleDegrees;
        private bool _hasExplicitCaliperRegion;
        private int _caliperCount = 9;
        private int _caliperSearchRange = 24;
        private int _caliperSamplingHalfWidth = 3;
        private double _caliperEdgeSigma = 1.0;
        private double _caliperRegionLength;
        private double _caliperMinimumGradient = 8;
        private int _minimumValidCalipers = 4;
        private double _caliperOutlierThreshold = 2.5;
        private CaliperEdgePolarity _caliperEdgePolarity = CaliperEdgePolarity.Any;
        private LineFitMode _caliperLineFitMode = LineFitMode.Tukey;
        private int _caliperFitClippingEndPoints;
        private CaliperWidthMeasurementResult _widthMeasurement = CaliperWidthMeasurementResult.Empty;

        private DualEdgeCaliperDetectionDisplayState DetectionDisplayState => DualEdgeCaliperDetectionDisplayStateStore.GetOrCreate(this);

        public override string RoiTypeName => nameof(CaliperMeasureRoi);

        public PointD CaliperCenter
        {
            get => _caliperCenter;
            set => SetProperty(ref _caliperCenter, value);
        }

        public double CaliperAngleDegrees
        {
            get => _caliperAngleDegrees;
            set => SetProperty(ref _caliperAngleDegrees, value);
        }

        public bool HasExplicitCaliperRegion
        {
            get => _hasExplicitCaliperRegion;
            set => SetProperty(ref _hasExplicitCaliperRegion, value);
        }

        public bool HasDetectedEdges
        {
            get => DetectionDisplayState.HasDetection;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.HasDetection, value, static (state, v) => state.HasDetection = v);
        }

        public PointD Edge1Start
        {
            get => DetectionDisplayState.Edge1Start;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1Start, value, static (state, v) => state.Edge1Start = v);
        }

        public PointD Edge1End
        {
            get => DetectionDisplayState.Edge1End;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1End, value, static (state, v) => state.Edge1End = v);
        }

        public PointD Edge2Start
        {
            get => DetectionDisplayState.Edge2Start;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2Start, value, static (state, v) => state.Edge2Start = v);
        }

        public PointD Edge2End
        {
            get => DetectionDisplayState.Edge2End;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2End, value, static (state, v) => state.Edge2End = v);
        }

        public LineSegmentOverlay[] RegionSegments
        {
            get => DetectionDisplayState.RegionSegments;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.RegionSegments, value, static (state, v) => state.RegionSegments = v);
        }

        public LineSegmentOverlay[] CaliperBars
        {
            get => DetectionDisplayState.CaliperBars;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.CaliperBars, value, static (state, v) => state.CaliperBars = v);
        }

        public LineSegmentOverlay[] InvalidCaliperMarkers
        {
            get => DetectionDisplayState.InvalidCaliperMarkers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.InvalidCaliperMarkers, value, static (state, v) => state.InvalidCaliperMarkers = v);
        }

        public LineSegmentOverlay[] Edge1Markers
        {
            get => DetectionDisplayState.Edge1Markers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1Markers, value, static (state, v) => state.Edge1Markers = v);
        }

        public LineSegmentOverlay[] Edge2Markers
        {
            get => DetectionDisplayState.Edge2Markers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2Markers, value, static (state, v) => state.Edge2Markers = v);
        }

        public LineSegmentOverlay[] RejectedEdge1Markers
        {
            get => DetectionDisplayState.RejectedEdge1Markers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.RejectedEdge1Markers, value, static (state, v) => state.RejectedEdge1Markers = v);
        }

        public LineSegmentOverlay[] RejectedEdge2Markers
        {
            get => DetectionDisplayState.RejectedEdge2Markers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.RejectedEdge2Markers, value, static (state, v) => state.RejectedEdge2Markers = v);
        }

        public CaliperScoreOverlay[] ScoreOverlays
        {
            get => DetectionDisplayState.ScoreOverlays;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ScoreOverlays, value, static (state, v) => state.ScoreOverlays = v);
        }

        public PointD[] Edge1Points
        {
            get => DetectionDisplayState.Edge1Points;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1Points, value, static (state, v) => state.Edge1Points = v);
        }

        public PointD[] Edge2Points
        {
            get => DetectionDisplayState.Edge2Points;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2Points, value, static (state, v) => state.Edge2Points = v);
        }

        public int CaliperCount
        {
            get => _caliperCount;
            set => SetProperty(ref _caliperCount, Math.Max(3, value));
        }

        public int CaliperSearchRange
        {
            get => _caliperSearchRange;
            set => SetProperty(ref _caliperSearchRange, Math.Max(1, value));
        }

        private double _minimumEdgeGap;

        /// <summary>
        /// 双边缘最小间距（px）；0 表示不约束。
        /// Chinese: 用于排除相邻纹理伪边导致的错误配对；两边缘间距低于该值时自动改选更远的候选对。
        /// English: Minimum required distance between the two detected edges, in pixels; 0 disables the constraint.
        /// </summary>
        public double MinimumEdgeGap
        {
            get => _minimumEdgeGap;
            set => SetProperty(ref _minimumEdgeGap, Math.Max(0, value));
        }

        private double _nominalEdgeGap;

        /// <summary>
        /// 标称边缘间距先验（px，像素单位）；0 表示不启用宽度先验。
        /// Chinese: 双边缘配对时，偏离标称间距的组合会被扣分，优先选择宽度接近标称的边缘对。
        /// English: Nominal edge gap prior used when selecting the best edge pair; 0 disables the prior.
        /// </summary>
        public double NominalEdgeGap
        {
            get => _nominalEdgeGap;
            set => SetProperty(ref _nominalEdgeGap, Math.Max(0, value));
        }

        private double _nominalEdgeGapTolerance;

        /// <summary>
        /// 标称间距容差（px）：间距落在标称 ± 容差内不扣分，超出部分按像素线性扣分。
        /// Chinese: 与 NominalEdgeGap 配合使用；0 表示不允许偏差。
        /// English: Tolerance band around the nominal edge gap before scoring penalty applies.
        /// </summary>
        public double NominalEdgeGapTolerance
        {
            get => _nominalEdgeGapTolerance;
            set => SetProperty(ref _nominalEdgeGapTolerance, Math.Max(0, value));
        }

        public int CaliperSamplingHalfWidth
        {
            get => _caliperSamplingHalfWidth;
            set => SetProperty(ref _caliperSamplingHalfWidth, Math.Max(0, value));
        }

        /// <summary>
        /// 检测器得到的最终测量线端点。P1/P2 保留操作员手动画出的参考线。
        /// </summary>
        public PointD DetectedP1 => DetectionDisplayState.DetectedP1;

        public PointD DetectedP2 => DetectionDisplayState.DetectedP2;

        public PointD MeasurementP1 => HasDetectedEdges && (DetectedP1 != default || DetectedP2 != default) ? DetectedP1 : P1;

        public PointD MeasurementP2 => HasDetectedEdges && (DetectedP1 != default || DetectedP2 != default) ? DetectedP2 : P2;

        public double CaliperEdgeSigma
        {
            get => _caliperEdgeSigma;
            set => SetProperty(ref _caliperEdgeSigma, Math.Clamp(value, 0.5, 5.0));
        }

        public double CaliperRegionLength
        {
            get => _caliperRegionLength;
            set => SetProperty(ref _caliperRegionLength, Math.Max(0, value));
        }

        public double CaliperMinimumGradient
        {
            get => _caliperMinimumGradient;
            set => SetProperty(ref _caliperMinimumGradient, Math.Max(0, value));
        }

        public int MinimumValidCalipers
        {
            get => _minimumValidCalipers;
            set => SetProperty(ref _minimumValidCalipers, Math.Max(2, value));
        }

        public double CaliperOutlierThreshold
        {
            get => _caliperOutlierThreshold;
            set => SetProperty(ref _caliperOutlierThreshold, Math.Max(0, value));
        }

        public CaliperEdgePolarity CaliperEdgePolarity
        {
            get => _caliperEdgePolarity;
            set => SetProperty(ref _caliperEdgePolarity, value);
        }

        /// <summary>边缘点直线拟合模式。</summary>
        public LineFitMode CaliperLineFitMode
        {
            get => _caliperLineFitMode;
            set => SetProperty(ref _caliperLineFitMode, value);
        }

        /// <summary>拟合时从每条边缘轮廓首尾各忽略的点数；端点输出仍使用原始首尾点。</summary>
        public int CaliperFitClippingEndPoints
        {
            get => _caliperFitClippingEndPoints;
            set => SetProperty(ref _caliperFitClippingEndPoints, Math.Max(0, value));
        }

        public double Edge1AverageScore
        {
            get => DetectionDisplayState.Edge1AverageScore;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1AverageScore, value, static (state, v) => state.Edge1AverageScore = v);
        }

        public double Edge2AverageScore
        {
            get => DetectionDisplayState.Edge2AverageScore;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2AverageScore, value, static (state, v) => state.Edge2AverageScore = v);
        }

        public double Edge1ResidualRms
        {
            get => DetectionDisplayState.Edge1ResidualRms;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1ResidualRms, value, static (state, v) => state.Edge1ResidualRms = v);
        }

        public double Edge2ResidualRms
        {
            get => DetectionDisplayState.Edge2ResidualRms;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2ResidualRms, value, static (state, v) => state.Edge2ResidualRms = v);
        }

        public double Edge1ResidualMax
        {
            get => DetectionDisplayState.Edge1ResidualMax;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1ResidualMax, value, static (state, v) => state.Edge1ResidualMax = v);
        }

        public double Edge2ResidualMax
        {
            get => DetectionDisplayState.Edge2ResidualMax;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2ResidualMax, value, static (state, v) => state.Edge2ResidualMax = v);
        }

        public int ValidCaliperCount
        {
            get => DetectionDisplayState.ValidCaliperCount;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ValidCaliperCount, Math.Max(0, value), static (state, v) => state.ValidCaliperCount = v);
        }

        public double Edge1AngleDegrees
        {
            get => DetectionDisplayState.Edge1AngleDegrees;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge1AngleDegrees, value, static (state, v) => state.Edge1AngleDegrees = v);
        }

        public double Edge2AngleDegrees
        {
            get => DetectionDisplayState.Edge2AngleDegrees;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Edge2AngleDegrees, value, static (state, v) => state.Edge2AngleDegrees = v);
        }

        public double ParallelismErrorDegrees
        {
            get => DetectionDisplayState.ParallelismErrorDegrees;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ParallelismErrorDegrees, value, static (state, v) => state.ParallelismErrorDegrees = v);
        }

        public double Confidence
        {
            get => DetectionDisplayState.Confidence;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Confidence, Math.Clamp(value, 0, 1), static (state, v) => state.Confidence = v);
        }

        /// <summary>
        /// 双边缘卡尺的逐点宽度结果和统计值。
        /// </summary>
        public CaliperWidthMeasurementResult WidthMeasurement
        {
            get => _widthMeasurement;
            private set => SetProperty(ref _widthMeasurement, value);
        }

        internal void ApplyWidthMeasurement(CaliperWidthMeasurementResult measurement)
        {
            ArgumentNullException.ThrowIfNull(measurement);
            WidthMeasurement = measurement;
        }

        public void SetDetectedEdges(PointD edge1Start, PointD edge1End, PointD edge2Start, PointD edge2End)
        {
            Edge1Start = edge1Start;
            Edge1End = edge1End;
            Edge2Start = edge2Start;
            Edge2End = edge2End;
            HasDetectedEdges = true;
        }

        public void ClearDetectedEdges()
        {
            this.ClearDetection();
            ApplyWidthMeasurement(CaliperWidthMeasurementResult.Empty);
        }

        internal void SetDetectedMeasurementLine(PointD p1, PointD p2)
        {
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedP1, p1, static (state, value) => state.DetectedP1 = value);
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedP2, p2, static (state, value) => state.DetectedP2 = value);
        }

        public void SyncCaliperRegionFromMeasurementLine(bool updateSearchRange = true)
        {
            VectorD measurementVector = P2 - P1;
            double distance = measurementVector.Length;
            if (distance <= 0)
            {
                return;
            }

            CaliperCenter = new PointD((P1.X + P2.X) / 2, (P1.Y + P2.Y) / 2);
            CaliperAngleDegrees = Math.Atan2(measurementVector.Y, measurementVector.X) * 180 / Math.PI;
            if (updateSearchRange)
            {
                CaliperSearchRange = Math.Max(1, (int)Math.Ceiling(distance / 2 + 4));
            }

            HasExplicitCaliperRegion = true;
        }

        public void EnsureCaliperRegion()
        {
            if (!HasExplicitCaliperRegion)
            {
                SyncCaliperRegionFromMeasurementLine();
            }
        }

        public void MoveCaliperRegion(double dx, double dy)
        {
            if (!HasExplicitCaliperRegion)
            {
                return;
            }

            CaliperCenter = new PointD(CaliperCenter.X + dx, CaliperCenter.Y + dy);
        }

        public VectorD GetCaliperMeasurementDirection()
        {
            double radians = CaliperAngleDegrees * Math.PI / 180.0;
            VectorD direction = new(Math.Cos(radians), Math.Sin(radians));
            if (direction.LengthSquared < 1e-6)
            {
                direction = P2 - P1;
            }

            if (direction.LengthSquared < 1e-6)
            {
                direction = new VectorD(0, 1);
            }

            direction = direction.Normalized();
            return direction;
        }

        public double GetResolvedCaliperRegionLength()
        {
            return CaliperRegionLength > 0
                ? Math.Max(6, CaliperRegionLength)
                : Math.Max(10, Math.Min(48, P1.DistanceTo(P2) * 0.1));
        }

        public void SetCaliperVisualization(
            LineSegmentOverlay[] regionSegments,
            LineSegmentOverlay[] caliperBars,
            LineSegmentOverlay[] invalidCaliperMarkers,
            LineSegmentOverlay[] edge1Markers,
            LineSegmentOverlay[] edge2Markers,
            LineSegmentOverlay[] rejectedEdge1Markers,
            LineSegmentOverlay[] rejectedEdge2Markers,
            CaliperScoreOverlay[] scoreOverlays,
            PointD[] edge1Points,
            PointD[] edge2Points)
        {
            RegionSegments = [..regionSegments];
            CaliperBars = [..caliperBars];
            InvalidCaliperMarkers = [..invalidCaliperMarkers];
            Edge1Markers = [..edge1Markers];
            Edge2Markers = [..edge2Markers];
            RejectedEdge1Markers = [..rejectedEdge1Markers];
            RejectedEdge2Markers = [..rejectedEdge2Markers];
            ScoreOverlays = [..scoreOverlays];
            Edge1Points = [..edge1Points];
            Edge2Points = [..edge2Points];
        }

        public override RoiBase Clone()
        {
            var clone = new CaliperMeasureRoi();
            clone.ApplyFrom(this);
            clone.CopyMeasurementIdentityFrom(this);
            return clone;
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not CaliperMeasureRoi caliper)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(CaliperMeasureRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            this.CopyStateFrom(caliper);
        }

        bool ICaliperInfoSource.HasDetection => HasDetectedEdges;

        double ICaliperInfoSource.AverageScore => (Edge1AverageScore + Edge2AverageScore) / 2;

        double ICaliperInfoSource.ResidualRms => Math.Max(Edge1ResidualRms, Edge2ResidualRms);

        double ICaliperInfoSource.ResidualMax => Math.Max(Edge1ResidualMax, Edge2ResidualMax);

        private void SetDetectionDisplayStateValue<T>(T currentValue, T newValue, Action<DualEdgeCaliperDetectionDisplayState, T> assign, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(currentValue, newValue))
            {
                return;
            }

            assign(DetectionDisplayState, newValue);
            OnPropertyChanged(propertyName);
        }
    }
}
