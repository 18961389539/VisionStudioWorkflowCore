using System;
using System.Runtime.CompilerServices;
using ImageViewer.Localization;

namespace ImageViewer.Models
{
    public class CircularCaliperMeasureRoi : CircleRoi, ISingleEdgeCaliperRoi, ISingleEdgeCaliperDetectionDisplayStateOwner, ICaliperInfoSource
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
        private CircularCaliperQualityStatus _qualityStatus = CircularCaliperQualityStatus.NotMeasured;
        private CircularCaliperQualityReason _qualityReason = CircularCaliperQualityReason.NotMeasured;
        private double _qualityValidRatio;
        private double _qualityAngularCoverageDegrees;

        private SingleEdgeCaliperDetectionDisplayState DetectionDisplayState => SingleEdgeCaliperDetectionDisplayStateStore.GetOrCreate(this);

        public override string RoiTypeName => nameof(CircularCaliperMeasureRoi);

        /// <summary>
        /// 在 ROI 列表中直接显示质量门控状态，避免操作员必须打开属性面板确认结果是否可信。
        /// </summary>
        public override string DisplayName
        {
            get
            {
                string displayName = base.DisplayName;
                if (this is ArcCaliperMeasureRoi || QualityStatus == CircularCaliperQualityStatus.NotMeasured)
                {
                    return displayName;
                }

                string status = QualityStatus switch
                {
                    CircularCaliperQualityStatus.Passed => UiText.Get("RoiQualityPassed"),
                    CircularCaliperQualityStatus.Review => UiText.Get("RoiQualityReview"),
                    CircularCaliperQualityStatus.Failed => UiText.Get("RoiQualityFailed"),
                    _ => string.Empty
                };
                return string.IsNullOrWhiteSpace(status) ? displayName : $"{displayName} [{status}]";
            }
        }

        public bool HasDetectedEdges
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
        /// Chinese: 用于轮廓旁存在更强干扰边（刻痕/反光）时，按序号锁定要测量的目标边缘。
        /// English: Selects the N-th strongest edge along the radial search profile; 1 is the strongest.
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
        /// 检测器拟合出的最终圆几何。Center/Radius 始终保留操作员手动画出的参考圆。
        /// </summary>
        public PointD DetectedCenter => DetectionDisplayState.DetectedCenter;

        public double DetectedRadius => DetectionDisplayState.DetectedRadius;

        public PointD MeasurementCenter => HasDetectedEdges && DetectedRadius > 0 ? DetectedCenter : Center;

        public double MeasurementRadius => HasDetectedEdges && DetectedRadius > 0 ? DetectedRadius : Radius;

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

        /// <summary>圆拟合的稳健加权模式，与直线卡尺共用同一配置槽。</summary>
        public LineFitMode CaliperLineFitMode
        {
            get => _caliperLineFitMode;
            set => SetProperty(ref _caliperLineFitMode, value);
        }

        /// <summary>共享卡尺配置槽；圆形轮廓没有有序端点，因此圆拟合不使用端点裁剪。</summary>
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

        public double Confidence
        {
            get => DetectionDisplayState.Confidence;
            set => SetDetectionDisplayStateValue(DetectionDisplayState.Confidence, Math.Clamp(value, 0, 1), static (state, v) => state.Confidence = v);
        }

        /// <summary>
        /// 圆测量质量门控状态。
        /// </summary>
        public CircularCaliperQualityStatus QualityStatus
        {
            get => _qualityStatus;
            private set => SetProperty(ref _qualityStatus, value);
        }

        public CircularCaliperQualityReason QualityReason
        {
            get => _qualityReason;
            private set => SetProperty(ref _qualityReason, value);
        }

        /// <summary>
        /// 通过质量门控时实际参与拟合的卡尺比例。
        /// </summary>
        public double QualityValidRatio
        {
            get => _qualityValidRatio;
            private set => SetProperty(ref _qualityValidRatio, value);
        }

        /// <summary>
        /// 边缘点覆盖的圆周角度。
        /// </summary>
        public double QualityAngularCoverageDegrees
        {
            get => _qualityAngularCoverageDegrees;
            private set => SetProperty(ref _qualityAngularCoverageDegrees, value);
        }

        internal void ApplyQualityAssessment(CircularCaliperQualityAssessment assessment)
        {
            QualityStatus = assessment.Status;
            QualityReason = assessment.Reason;
            QualityValidRatio = assessment.ValidRatio;
            QualityAngularCoverageDegrees = assessment.AngularCoverageDegrees;
            OnPropertyChanged(nameof(DisplayName));
        }

        internal void MarkQualityFailure()
        {
            ApplyQualityAssessment(new CircularCaliperQualityAssessment(
                CircularCaliperQualityStatus.Failed,
                0,
                0,
                CircularCaliperQualityReason.DetectionFailed));
        }

        public void ClearDetectedEdges()
        {
            this.ClearDetection();
            ApplyQualityAssessment(new CircularCaliperQualityAssessment(
                CircularCaliperQualityStatus.NotMeasured,
                0,
                0,
                CircularCaliperQualityReason.NotMeasured));
        }

        internal void SetDetectedCircle(PointD center, double radius)
        {
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedCenter, center, static (state, value) => state.DetectedCenter = value);
            SetDetectionDisplayStateValue(DetectionDisplayState.DetectedRadius, radius, static (state, value) => state.DetectedRadius = value);
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
            var clone = new CircularCaliperMeasureRoi();
            clone.ApplyFrom(this);
            clone.CopyMeasurementIdentityFrom(this);
            return clone;
        }

        public override void ApplyFrom(RoiBase source)
        {
            if (source is not CircularCaliperMeasureRoi circleCaliper)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(CircularCaliperMeasureRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            this.CopyStateFrom(circleCaliper);
            QualityStatus = circleCaliper.QualityStatus;
            QualityReason = circleCaliper.QualityReason;
            QualityValidRatio = circleCaliper.QualityValidRatio;
            QualityAngularCoverageDegrees = circleCaliper.QualityAngularCoverageDegrees;
            OnPropertyChanged(nameof(DisplayName));
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
