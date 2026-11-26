using System;
using ImageViewer.Common;

namespace ImageViewer.Models
{
    /// <summary>
    /// 由三个点确定的圆测量。
    /// Chinese: 三点不共线时自动计算圆心与半径；拖动任一点即可重新计算。
    /// English: Computes a circle from three non-collinear points and recomputes it when a point moves.
    /// </summary>
    public class ThreePointCircleMeasureRoi : RoiBase
    {
        private PointD _p1;
        private PointD _p2;
        private PointD _p3;
        private PointD _center;
        private double _radius;
        private bool _isComputed;

        public ThreePointCircleMeasureRoi()
        {
            StrokeColor = RoiColors.Coral;
        }

        public override string RoiTypeName => nameof(ThreePointCircleMeasureRoi);

        public PointD P1
        {
            get => _p1;
            set
            {
                if (SetProperty(ref _p1, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD P2
        {
            get => _p2;
            set
            {
                if (SetProperty(ref _p2, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD P3
        {
            get => _p3;
            set
            {
                if (SetProperty(ref _p3, value))
                {
                    _isComputed = false;
                }
            }
        }

        public PointD Center
        {
            get
            {
                EnsureComputed();
                return _center;
            }
        }

        public double Radius
        {
            get
            {
                EnsureComputed();
                return _radius;
            }
        }

        public bool IsValid
        {
            get
            {
                EnsureComputed();
                return _radius > 0;
            }
        }

        public void EnsureComputed()
        {
            if (_isComputed)
            {
                return;
            }

            _isComputed = true;
            if (!ArcMeasureRoi.TryComputeArcParameters(P1, P2, P3, out _center, out _radius, out _, out _))
            {
                _center = default;
                _radius = 0;
            }
        }

        public override RoiBase Clone()
        {
            return new ThreePointCircleMeasureRoi
            {
                P1 = P1,
                P2 = P2,
                P3 = P3,
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
            if (source is not ThreePointCircleMeasureRoi roi)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(ThreePointCircleMeasureRoi)}.", nameof(source));
            }

            P1 = roi.P1;
            P2 = roi.P2;
            P3 = roi.P3;
            ApplyCommonState(roi);
        }
    }
}
