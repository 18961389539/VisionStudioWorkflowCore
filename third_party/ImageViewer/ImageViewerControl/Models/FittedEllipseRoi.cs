using System;

namespace ImageViewer.Models
{
    public class FittedEllipseRoi : EllipseRoi
    {
        private int _sourcePointCount;
        private double _fitResidualRms;
        private double _fitResidualMedian;
        private double _fitResidualMax;
        private double _fitNoiseScale;
        private int _fitInlierCount;
        private int _fitOutlierCount;
        private double _fitAspectRatio;
        private string _fitAlgorithm = string.Empty;

        public override string RoiTypeName => nameof(FittedEllipseRoi);

        internal bool HasFitEvidence => SourcePointCount > 0 || FitInlierCount > 0;

        public int SourcePointCount
        {
            get => _sourcePointCount;
            set => SetProperty(ref _sourcePointCount, Math.Max(0, value));
        }

        public double FitResidualRms
        {
            get => _fitResidualRms;
            set => SetProperty(ref _fitResidualRms, Math.Max(0, value));
        }

        public double FitResidualMedian
        {
            get => _fitResidualMedian;
            set => SetProperty(ref _fitResidualMedian, Math.Max(0, value));
        }

        public double FitResidualMax
        {
            get => _fitResidualMax;
            set => SetProperty(ref _fitResidualMax, Math.Max(0, value));
        }

        public double FitNoiseScale
        {
            get => _fitNoiseScale;
            set => SetProperty(ref _fitNoiseScale, Math.Max(0, value));
        }

        public int FitInlierCount
        {
            get => _fitInlierCount;
            set => SetProperty(ref _fitInlierCount, Math.Max(0, value));
        }

        public int FitOutlierCount
        {
            get => _fitOutlierCount;
            set => SetProperty(ref _fitOutlierCount, Math.Max(0, value));
        }

        public double FitAspectRatio
        {
            get => _fitAspectRatio;
            set => SetProperty(ref _fitAspectRatio, Math.Max(0, value));
        }

        public string FitAlgorithm
        {
            get => _fitAlgorithm;
            set => SetProperty(ref _fitAlgorithm, value ?? string.Empty);
        }

        public override RoiBase Clone()
        {
            return new FittedEllipseRoi
            {
                Center = Center,
                RadiusX = RadiusX,
                RadiusY = RadiusY,
                Angle = Angle,
                SourcePointCount = SourcePointCount,
                FitResidualRms = FitResidualRms,
                FitResidualMedian = FitResidualMedian,
                FitResidualMax = FitResidualMax,
                FitNoiseScale = FitNoiseScale,
                FitInlierCount = FitInlierCount,
                FitOutlierCount = FitOutlierCount,
                FitAspectRatio = FitAspectRatio,
                FitAlgorithm = FitAlgorithm,
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
            if (source is not FittedEllipseRoi ellipse)
            {
                throw new ArgumentException($"Cannot apply state from {source.GetType().Name} to {nameof(FittedEllipseRoi)}.", nameof(source));
            }

            base.ApplyFrom(source);
            SourcePointCount = ellipse.SourcePointCount;
            FitResidualRms = ellipse.FitResidualRms;
            FitResidualMedian = ellipse.FitResidualMedian;
            FitResidualMax = ellipse.FitResidualMax;
            FitNoiseScale = ellipse.FitNoiseScale;
            FitInlierCount = ellipse.FitInlierCount;
            FitOutlierCount = ellipse.FitOutlierCount;
            FitAspectRatio = ellipse.FitAspectRatio;
            FitAlgorithm = ellipse.FitAlgorithm;
        }
    }
}
