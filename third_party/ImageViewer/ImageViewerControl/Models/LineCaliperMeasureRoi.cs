using System;
using System.Runtime.CompilerServices;

namespace ImageViewer.Models
{
    public class LineCaliperMeasureRoi : LineMeasureRoi, ISingleEdgeCaliperRoi, ISingleEdgeCaliperDetectionDisplayStateOwner, ICaliperInfoSource
    {
        private int _caliperCount = 16;
        private int _caliperSearchRange = 18;
        private int _caliperSamplingHalfWidth = 2;
        private double _caliperEdgeSigma = 1.0;
        private double _caliperMinimumGradient = 8;
        private int _minimumValidCalipers = 8;
        private double _caliperOutlierThreshold = 2.5;
        private CaliperEdgePolarity _caliperEdgePolarity = CaliperEdgePolarity.Any;
        private LineFitMode _caliperLineFitMode = LineFitMode.Tukey;
        private int _caliperFitClippingEndPoints;
        private double _angleDegrees;

        private SingleEdgeCaliperDetectionDisplayState DetectionDisplayState => SingleEdgeCaliperDetectionDisplayStateStore.GetOrCreate(this);

        public override string RoiTypeName => nameof(LineCaliperMeasureRoi);

        public bool HasDetectedLine
        {
            get => DetectionDisplayState.HasDetection;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.HasDetection, value, static (state, v) => state.HasDetection = v);
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

        public LineSegmentOverlay[] EdgeMarkers
        {
            get => DetectionDisplayState.EdgeMarkers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.EdgeMarkers, value, static (state, v) => state.EdgeMarkers = v);
        }

        public LineSegmentOverlay[] RejectedEdgeMarkers
        {
            get => DetectionDisplayState.RejectedEdgeMarkers;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.RejectedEdgeMarkers, value, static (state, v) => state.RejectedEdgeMarkers = v);
        }

        public CaliperScoreOverlay[] ScoreOverlays
        {
            get => DetectionDisplayState.ScoreOverlays;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ScoreOverlays, value, static (state, v) => state.ScoreOverlays = v);
        }

        public PointD[] EdgePoints
        {
            get => DetectionDisplayState.EdgePoints;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.EdgePoints, value, static (state, v) => state.EdgePoints = v);
        }

        public int CaliperCount
        {
            get => _caliperCount;
            set => SetProperty(ref _caliperCount, Math.Max(6, value));
        }

        public int CaliperSearchRange
        {
            get => _caliperSearchRange;
            set => SetProperty(ref _caliperSearchRange, Math.Max(1, value));
        }

        private int _edgeSelection = 1;

        /// <summary>
        /// 单边缘选择序号：1=最强梯度边缘，2=次强，依此类推。
        /// Chinese: 用于标签边缘旁存在更强干扰边（刻痕/反光）时，按序号锁定要测量的目标边缘。
        /// English: Selects the N-th strongest edge along the search profile; 1 is the strongest.
        /// </summary>
        public int EdgeSelection
        {
            get => _edgeSelection;
            set => SetProperty(ref _edgeSelection, Math.Clamp(value, 1, 8));
        }

        public int CaliperSamplingHalfWidth
        {
            get => _caliperSamplingHalfWidth;
            set => SetProperty(ref _caliperSamplingHalfWidth, Math.Max(0, value));
        }

        /// <summary>
        /// 检测器投影出的最终测量线端点。P1/P2 始终保留操作员手动画出的参考线。
        /// </summary>
        public PointD DetectedP1 => DetectionDisplayState.DetectedP1;

        public PointD DetectedP2 => DetectionDisplayState.DetectedP2;

        public PointD MeasurementP1 => HasDetectedLine && (DetectedP1 != default || DetectedP2 != default) ? DetectedP1 : P1;

        public PointD MeasurementP2 => HasDetectedLine && (DetectedP1 != default || DetectedP2 != default) ? DetectedP2 : P2;

        public double CaliperEdgeSigma
        {
            get => _caliperEdgeSigma;
            set => SetProperty(ref _caliperEdgeSigma, Math.Clamp(value, 0.5, 5.0));
        }

        public double CaliperMinimumGradient
        {
            get => _caliperMinimumGradient;
            set => SetProperty(ref _caliperMinimumGradient, Math.Max(0, value));
        }

        public int MinimumValidCalipers
        {
            get => _minimumValidCalipers;
            set => SetProperty(ref _minimumValidCalipers, Math.Max(3, value));
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

        /// <summary>拟合时从边缘轮廓首尾各忽略的点数；端点输出仍使用原始首尾点。</summary>
        public int CaliperFitClippingEndPoints
        {
            get => _caliperFitClippingEndPoints;
            set => SetProperty(ref _caliperFitClippingEndPoints, Math.Max(0, value));
        }

        public double AverageScore
        {
            get => DetectionDisplayState.AverageScore;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.AverageScore, value, static (state, v) => state.AverageScore = v);
        }

        public double ResidualRms
        {
            get => DetectionDisplayState.ResidualRms;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ResidualRms, value, static (state, v) => state.ResidualRms = v);
        }

        public double ResidualMax
        {
            get => DetectionDisplayState.ResidualMax;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ResidualMax, value, static (state, v) => state.ResidualMax = v);
        }

        public int ValidCaliperCount
        {
            get => DetectionDisplayState.ValidCaliperCount;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.ValidCaliperCount, Math.Max(0, value), static (state, v) => state.ValidCaliperCount = v);
        }

        public double AngleDegrees
        {
            get => _angleDegrees;
            set => SetProperty(ref _angleDegrees, value);
        }

        public double Confidence
        {
            get => DetectionDisplayState.Confidence;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Confidence, Math.Clamp(value, 0, 1), static (state, v) => state.Confidence = v);
        }

        public void ClearDetectedLine()
        {
            this.ClearDetection();
            AngleDegrees = 0;
        }

        internal void SetDetectedLine(PointD p1, PointD p2)
        {
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedP1, p1, static (state, value) => state.DetectedP1 = value);
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedP2, p2, static (state, value) => state.DetectedP2 = value);
        }

        public void SetCaliperVisualization(
            LineSegmentOverlay[] regionSegments,
            LineSegmentOverlay[] caliperBars,
            LineSegmentOverlay[] invalidCaliperMarkers,
            LineSegmentOverlay[] edgeMarkers,
            LineSegmentOverlay[] rejectedEdgeMarkers,
            CaliperScoreOverlay[] scoreOverlays,
            PointD[] edgePoints)
        {
            this.SetDetectionVisualization(
                SingleEdgeCaliperDetectionDisplayState.CreateDetected(
                    regionSegments,
                    caliperBars,
                    invalidCaliperMarkers,
                    edgeMarkers,
                    rejectedEdgeMarkers,
                    scoreOverlays,
                    edgePoints));
        }

        public override RoiBase Clone()
        {
            var clone = new LineCaliperMeasureRoi();
            clone.ApplyFrom(this);
            clone.CopyMeasurementIdentityFrom(this);
            return clone;
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not LineCaliperMeasureRoi lineCaliper)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(LineCaliperMeasureRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            this.CopyStateFrom(lineCaliper);
            AngleDegrees = lineCaliper.AngleDegrees;
        }

        bool ISingleEdgeCaliperDetectionDisplayStateOwner.HasDetection
        {
            get => SingleEdgeCaliperDetectionStateHelper.GetHasDetection(this);
            set => SingleEdgeCaliperDetectionStateHelper.SetHasDetection(this, value);
        }

        bool ICaliperInfoSource.HasDetection => SingleEdgeCaliperDetectionStateHelper.GetHasDetection(this);

        private void SetDetectionDisplayStateValue<T>(T currentValue, T newValue, Action<SingleEdgeCaliperDetectionDisplayState, T> assign, [CallerMemberName] string? propertyName = null)
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
