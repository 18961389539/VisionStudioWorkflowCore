using System;
using System.Collections.Generic;

namespace ImageViewer.Models
{
    public class BlobAnalysisRoi : RotatedRect
    {
        private bool _useOtsu = true;
        private int _manualThreshold = 128;
        private bool _detectDark;
        private int _minArea = 10;
        private List<BlobFeature> _detectedBlobs = new List<BlobFeature>();

        public override string RoiTypeName => nameof(BlobAnalysisRoi);

        public bool UseOtsu
        {
            get => _useOtsu;
            set => SetProperty(ref _useOtsu, value);
        }

        public int ManualThreshold
        {
            get => _manualThreshold;
            set => SetProperty(ref _manualThreshold, Math.Clamp(value, 0, 255));
        }

        public bool DetectDark
        {
            get => _detectDark;
            set => SetProperty(ref _detectDark, value);
        }

        public int MinArea
        {
            get => _minArea;
            set => SetProperty(ref _minArea, Math.Max(1, value));
        }

        public List<BlobFeature> DetectedBlobs
        {
            get => _detectedBlobs;
            set => SetProperty(ref _detectedBlobs, value);
        }

        public override RoiBase Clone()
        {
            return new BlobAnalysisRoi
            {
                Center = Center,
                Width = Width,
                Height = Height,
                Angle = Angle,
                Label = Label,
                StrokeColor = StrokeColor,
                StrokeThickness = StrokeThickness,
                IsVisible = IsVisible,
                IsLocked = IsLocked,
                Tolerance = Tolerance?.Clone(),
                MeasurementId = MeasurementId,
                AdvancedMeasurements = AdvancedMeasurements,
                UseOtsu = UseOtsu,
                ManualThreshold = ManualThreshold,
                DetectDark = DetectDark,
                MinArea = MinArea
            };
        }
    }
}
