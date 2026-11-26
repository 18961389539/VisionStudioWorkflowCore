using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;
using CoreCircularDetectionResult = ImageViewer.Core.Analysis.CircularCaliperDetectionResult;
using CoreLineCaliperDetectionResult = ImageViewer.Core.Analysis.LineCaliperDetectionResult;
using CoreLineMeasureGradientDetectionResult = ImageViewer.Core.Analysis.LineMeasureGradientDetectionResult;

namespace ImageViewerControl.Tests
{
    public class RoiDetectionResultMapperTests
    {
        [Fact]
        public void Apply_LineCaliperKeepsManualReferenceAndStoresDetectedSegmentSeparately()
        {
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(10, 10),
                P2 = new PointD(90, 10)
            };
            var result = new CoreLineCaliperDetectionResult(
                ReferenceP1: new PointD(10, 10),
                ReferenceP2: new PointD(90, 10),
                DetectedP1: new PointD(12, 22),
                DetectedP2: new PointD(88, 22),
                InvalidSamplePoints: [],
                EdgePoints: [],
                RejectedEdgePoints: [],
                EdgeScores: [],
                FittedLine: new DetectedLineSegment(new PointD(12, 22), new PointD(88, 22)),
                AverageScore: 1,
                ResidualRms: 0,
                ResidualMax: 0,
                ValidCaliperCount: 8,
                AngleDegrees: 0,
                Confidence: 1);

            RoiDetectionResultMapper.Apply(roi, result);

            Assert.Equal(new PointD(10, 10), roi.P1);
            Assert.Equal(new PointD(90, 10), roi.P2);
            Assert.Equal(new PointD(12, 22), roi.DetectedP1);
            Assert.Equal(new PointD(88, 22), roi.DetectedP2);
        }

        [Fact]
        public void Apply_DualEdgeCaliperKeepsManualReferenceAndStoresDetectedSegmentSeparately()
        {
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(10, 20),
                P2 = new PointD(90, 20)
            };
            var result = new CoreLineMeasureGradientDetectionResult(
                DetectedP1: new PointD(20, 20),
                DetectedP2: new PointD(80, 20),
                InvalidCaliperCenters: [],
                Edge1Points: [],
                Edge2Points: [],
                RejectedEdge1Points: [],
                RejectedEdge2Points: [],
                Edge1Scores: [],
                Edge2Scores: [],
                FittedEdge1: new DetectedLineSegment(new PointD(20, 10), new PointD(20, 30)),
                FittedEdge2: new DetectedLineSegment(new PointD(80, 10), new PointD(80, 30)),
                Edge1AverageScore: 1,
                Edge2AverageScore: 1,
                Edge1ResidualRms: 0,
                Edge2ResidualRms: 0,
                Edge1ResidualMax: 0,
                Edge2ResidualMax: 0,
                ValidCaliperCount: 8,
                Edge1AngleDegrees: 90,
                Edge2AngleDegrees: 90,
                ParallelismErrorDegrees: 0,
                Confidence: 1,
                WidthSamples: [60]);

            RoiDetectionResultMapper.Apply(roi, result);

            Assert.Equal(new PointD(10, 20), roi.P1);
            Assert.Equal(new PointD(90, 20), roi.P2);
            Assert.Equal(new PointD(20, 20), roi.DetectedP1);
            Assert.Equal(new PointD(80, 20), roi.DetectedP2);
        }

        [Fact]
        public void Apply_CircularCaliperKeepsManualReferenceAndStoresDetectedCircleSeparately()
        {
            var roi = new CircularCaliperMeasureRoi
            {
                Center = new PointD(50, 50),
                Radius = 20
            };
            var result = new CoreCircularDetectionResult(
                ReferenceCenter: new PointD(50, 50),
                ReferenceRadius: 20,
                DetectedCenter: new PointD(54, 48),
                DetectedRadius: 24,
                InvalidSamplePoints: [],
                EdgePoints: [],
                RejectedEdgePoints: [],
                EdgeScores: [],
                AverageScore: 1,
                ResidualRms: 0,
                ResidualMax: 0,
                ValidCaliperCount: 12,
                Confidence: 1);

            RoiDetectionResultMapper.Apply(roi, result);

            Assert.Equal(new PointD(50, 50), roi.Center);
            Assert.Equal(20, roi.Radius);
            Assert.Equal(new PointD(54, 48), roi.DetectedCenter);
            Assert.Equal(24, roi.DetectedRadius);
        }
    }
}
