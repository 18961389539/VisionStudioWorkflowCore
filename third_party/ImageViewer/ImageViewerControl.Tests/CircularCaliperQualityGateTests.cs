using System.Linq;
using System.Windows;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Utils;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class CircularCaliperQualityGateTests
    {
        [Fact]
        public void Evaluate_StrongFullCircle_Passes()
        {
            CircularCaliperMeasureRoi roi = CreateRoi(32);
            CircularCaliperQualityAssessment assessment = CircularCaliperQualityGate.Evaluate(
                roi,
                CreateResult(roi, confidence: 0.82, residualRms: 0.35, validCount: 28, CreateCirclePoints(32, 360)));

            Assert.Equal(CircularCaliperQualityStatus.Passed, assessment.Status);
            Assert.True(assessment.ValidRatio >= 0.8);
            Assert.True(assessment.AngularCoverageDegrees > 340);
            roi.ApplyQualityAssessment(assessment);
            Assert.Contains("通过", roi.DisplayName);
        }

        [Fact]
        public void Evaluate_LowConfidenceWithEnoughGeometry_RequiresReview()
        {
            CircularCaliperMeasureRoi roi = CreateRoi(32);
            CircularCaliperQualityAssessment assessment = CircularCaliperQualityGate.Evaluate(
                roi,
                CreateResult(roi, confidence: 0.31, residualRms: 0.8, validCount: 26, CreateCirclePoints(32, 360)));

            Assert.Equal(CircularCaliperQualityStatus.Review, assessment.Status);
        }

        [Fact]
        public void Evaluate_InsufficientCoverage_Fails()
        {
            CircularCaliperMeasureRoi roi = CreateRoi(32);
            CircularCaliperQualityAssessment assessment = CircularCaliperQualityGate.Evaluate(
                roi,
                CreateResult(roi, confidence: 0.8, residualRms: 0.4, validCount: 24, CreateCirclePoints(24, 90)));

            Assert.Equal(CircularCaliperQualityStatus.Failed, assessment.Status);
            Assert.Equal(CircularCaliperQualityReason.InsufficientAngularCoverage, assessment.Reason);
        }

        [Fact]
        public void Evaluate_UsesInjectedQualityProfileInsteadOfFixedThresholds()
        {
            CircularCaliperMeasureRoi roi = CreateRoi(32);
            ImageAnalysisQualityProfile profile = ImageAnalysisQualityProfile.Default with
            {
                MinimumReviewConfidence = 0.40,
                PassConfidence = 0.80
            };

            CircularCaliperQualityAssessment assessment = CircularCaliperQualityGate.Evaluate(
                roi,
                CreateResult(roi, confidence: 0.31, residualRms: 0.8, validCount: 26, CreateCirclePoints(32, 360)),
                profile);

            Assert.Equal(CircularCaliperQualityStatus.Failed, assessment.Status);
            Assert.Equal(CircularCaliperQualityReason.LowConfidence, assessment.Reason);
        }

        private static CircularCaliperMeasureRoi CreateRoi(int count)
        {
            return new CircularCaliperMeasureRoi
            {
                Center = new PointD(50, 50),
                Radius = 20,
                CaliperCount = count
            };
        }

        private static CircularCaliperDetectionResult CreateResult(
            CircularCaliperMeasureRoi roi,
            double confidence,
            double residualRms,
            int validCount,
            Point[] edgePoints)
        {
            return new CircularCaliperDetectionResult(
                roi.Center.ToWpfPoint(),
                roi.Radius,
                roi.Center.ToWpfPoint(),
                roi.Radius,
                [],
                edgePoints,
                [],
                Enumerable.Repeat(40d, edgePoints.Length).ToArray(),
                40,
                residualRms,
                residualRms * 2,
                validCount,
                confidence);
        }

        private static Point[] CreateCirclePoints(int count, double spanDegrees)
        {
            return Enumerable.Range(0, count)
                .Select(index =>
                {
                    double angle = (spanDegrees * index / Math.Max(1, count - 1)) * Math.PI / 180;
                    return new Point(50 + 20 * System.Math.Cos(angle), 50 + 20 * System.Math.Sin(angle));
                })
                .ToArray();
        }
    }
}
