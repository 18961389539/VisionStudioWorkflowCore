using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
[Trait("Category", "Wpf")]
public sealed class DetectionParameterRecommendationServiceTests
{
    [Fact]
    public void SingleEdgeRecommendationUsesMeasuredOffsetAndSignedEvidenceWithoutMutatingSource()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8(128, 100, (x, _) => x < 51 ? (byte)20 : (byte)220);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(48, 25),
                P2 = new PointD(48, 75),
                CaliperCount = 6,
                MinimumValidCalipers = 3,
                CaliperSearchRange = 18,
                CaliperSamplingHalfWidth = 1,
                CaliperEdgePolarity = CaliperEdgePolarity.Any
            };
            Guid measurementId = roi.MeasurementId;

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.True(proposal.Success, proposal.FailureKey);
            var candidate = Assert.IsType<LineCaliperMeasureRoi>(proposal.SuggestedRoi);
            Assert.NotSame(roi, candidate);
            Assert.Equal(measurementId, candidate.MeasurementId);
            Assert.Equal(48, roi.P1.X);
            Assert.Equal(18, roi.CaliperSearchRange);
            Assert.Equal(6, roi.CaliperCount);
            Assert.True(candidate.CaliperSearchRange < roi.CaliperSearchRange);
            Assert.True(candidate.CaliperCount > roi.CaliperCount);
            Assert.True((double)candidate.MinimumValidCalipers / candidate.CaliperCount >= (double)roi.MinimumValidCalipers / roi.CaliperCount);
            // The detector scans perpendicular to P1->P2, which here points toward decreasing X.
            Assert.Equal(CaliperEdgePolarity.LightToDark, candidate.CaliperEdgePolarity);
            Assert.Contains(proposal.Changes, change => change.PropertyName == nameof(ISingleEdgeCaliperRoi.CaliperSearchRange));
        });
    }

    [Fact]
    public void DualEdgeRecommendationKeepsAnyPolarityForOpposingBoundaries()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8(128, 100, (x, _) => x is >= 54 and <= 73 ? (byte)220 : (byte)20);
            var roi = new CaliperMeasureRoi
            {
                P1 = new PointD(42, 50),
                P2 = new PointD(86, 50),
                CaliperSearchRange = 24,
                CaliperSamplingHalfWidth = 1,
                CaliperEdgePolarity = CaliperEdgePolarity.Any,
                CaliperCount = 12,
                MinimumValidCalipers = 5,
                CaliperMinimumGradient = 7.25,
                CaliperOutlierThreshold = 3.75,
                MinimumEdgeGap = 4.5,
                NominalEdgeGap = 20,
                NominalEdgeGapTolerance = 2,
                CaliperLineFitMode = LineFitMode.Regression,
                CaliperFitClippingEndPoints = 2,
                AdvancedMeasurements = [new AdvancedMeasurementDefinition { Name = "width", Kind = AdvancedMeasurementKind.Position }]
            };
            Guid measurementId = roi.MeasurementId;
            AdvancedMeasurementDefinition[] definitions = roi.AdvancedMeasurements;

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.True(proposal.Success, proposal.FailureKey);
            var candidate = Assert.IsType<CaliperMeasureRoi>(proposal.SuggestedRoi);
            Assert.Equal(CaliperEdgePolarity.Any, candidate.CaliperEdgePolarity);
            Assert.DoesNotContain(proposal.Changes, change => change.PropertyName == nameof(CaliperMeasureRoi.CaliperEdgePolarity));
            Assert.Contains("RecommendationNoteDualAnyPolarity", proposal.Evidence.Notes);
            Assert.Equal(measurementId, candidate.MeasurementId);
            Assert.NotSame(definitions, candidate.AdvancedMeasurements);
            Assert.Equal(definitions[0].Id, candidate.AdvancedMeasurements[0].Id);
            Assert.NotSame(definitions[0], candidate.AdvancedMeasurements[0]);
            Assert.True(candidate.CaliperMinimumGradient >= roi.CaliperMinimumGradient);
            DetectionParameterChange? gradientChange = proposal.Changes.SingleOrDefault(
                change => change.PropertyName == "CaliperMinimumGradient");
            if (gradientChange is not null)
            {
                Assert.Equal("7.25", gradientChange.Before);
            }
            Assert.Equal(roi.CaliperOutlierThreshold, candidate.CaliperOutlierThreshold);
            Assert.Equal(roi.MinimumEdgeGap, candidate.MinimumEdgeGap);
            Assert.Equal(roi.NominalEdgeGap, candidate.NominalEdgeGap);
            Assert.Equal(roi.NominalEdgeGapTolerance, candidate.NominalEdgeGapTolerance);
            Assert.Equal(roi.CaliperLineFitMode, candidate.CaliperLineFitMode);
            Assert.Equal(roi.CaliperFitClippingEndPoints, candidate.CaliperFitClippingEndPoints);
        });
    }

    [Fact]
    public void CircularCaliperUsesRadialProfilesAndCanRecommendMoreSamples()
    {
        WpfTestRunner.Run(() =>
        {
            const int center = 64;
            const double radius = 24;
            BitmapSource bitmap = CreateGray8(128, 128, (x, y) =>
            {
                double distance = Math.Sqrt(Math.Pow(x - center, 2) + Math.Pow(y - center, 2));
                return distance < radius ? (byte)25 : (byte)225;
            });
            var roi = new CircularCaliperMeasureRoi
            {
                Center = new PointD(center, center),
                Radius = radius,
                CaliperCount = 6,
                MinimumValidCalipers = 3,
                CaliperSearchRange = 10,
                CaliperSamplingHalfWidth = 1
            };

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.True(proposal.Success, proposal.FailureKey);
            var candidate = Assert.IsType<CircularCaliperMeasureRoi>(proposal.SuggestedRoi);
            Assert.True(candidate.CaliperCount > roi.CaliperCount);
            Assert.True(candidate.CaliperSearchRange < roi.CaliperSearchRange);
            Assert.Equal(CaliperEdgePolarity.DarkToLight, candidate.CaliperEdgePolarity);
        });
    }

    [Fact]
    public void BlobRecommendationUsesOtsuWhenClassesSeparateAndRetainsDetectDark()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8(128, 128, (x, _) => x < 64 ? (byte)35 : (byte)210);
            var roi = new BlobAnalysisRoi
            {
                Center = new PointD(64, 64),
                Width = 80,
                Height = 64,
                UseOtsu = false,
                ManualThreshold = 90,
                DetectDark = true,
                MinArea = 22
            };

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.True(proposal.Success, proposal.FailureKey);
            var candidate = Assert.IsType<BlobAnalysisRoi>(proposal.SuggestedRoi);
            Assert.True(candidate.UseOtsu);
            Assert.True(candidate.DetectDark);
            Assert.Equal(roi.ManualThreshold, candidate.ManualThreshold);
            Assert.Equal(roi.MinArea, candidate.MinArea);
            Assert.False(roi.UseOtsu);
            Assert.True(roi.DetectDark);
        });
    }

    [Fact]
    public void Gray16LowContrastIsRejectedUsingNativeIntensityEvidence()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray16(128, 100, (x, _) => x < 50 ? (ushort)3000 : (ushort)3040);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(48, 25),
                P2 = new PointD(48, 75),
                CaliperSearchRange = 18,
                CaliperSamplingHalfWidth = 1
            };

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.False(proposal.Success);
            Assert.Equal("RecommendationLowContrast", proposal.FailureKey);
            Assert.Null(proposal.SuggestedRoi);
        });
    }

    [Fact]
    public void NoisySingleEdgeRetainsMinimumValidFractionAndDoesNotLowerGradientGate()
    {
        WpfTestRunner.Run(() =>
        {
            var random = new Random(12);
            BitmapSource bitmap = CreateGray8(128, 100, (x, _) =>
            {
                int baseline = x < 53 ? 22 : 202;
                return (byte)Math.Clamp(baseline + random.Next(-5, 6), 0, 255);
            });
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(48, 25),
                P2 = new PointD(48, 75),
                CaliperCount = 8,
                MinimumValidCalipers = 4,
                CaliperSearchRange = 18,
                CaliperSamplingHalfWidth = 2,
                CaliperMinimumGradient = 9,
                CaliperEdgePolarity = CaliperEdgePolarity.Any
            };

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.True(proposal.Success, proposal.FailureKey);
            var candidate = Assert.IsType<LineCaliperMeasureRoi>(proposal.SuggestedRoi);
            Assert.True((double)candidate.MinimumValidCalipers / candidate.CaliperCount >= 0.5);
            Assert.True(candidate.CaliperMinimumGradient >= roi.CaliperMinimumGradient);
        });
    }

    [Fact]
    public void ClippedCaliperSearchRegionFailsExplicitly()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8(96, 96, (x, _) => x < 4 ? (byte)20 : (byte)200);
            var roi = new LineCaliperMeasureRoi
            {
                P1 = new PointD(3, 20),
                P2 = new PointD(3, 75),
                CaliperSearchRange = 12
            };

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, roi);

            Assert.False(proposal.Success);
            Assert.Equal("RecommendationClippedRoi", proposal.FailureKey);
            Assert.Null(proposal.SuggestedRoi);
        });
    }

    [Fact]
    public void SupportedTypesAreExplicitAndUnsupportedRoisFailWithoutAClone()
    {
        WpfTestRunner.Run(() =>
        {
            BitmapSource bitmap = CreateGray8(32, 32, static (_, _) => (byte)50);
            Assert.True(DetectionParameterRecommendationService.IsSupported(new LineCaliperMeasureRoi()));
            Assert.True(DetectionParameterRecommendationService.IsSupported(new CaliperMeasureRoi()));
            Assert.True(DetectionParameterRecommendationService.IsSupported(new CircularCaliperMeasureRoi()));
            Assert.True(DetectionParameterRecommendationService.IsSupported(new ArcCaliperMeasureRoi()));
            Assert.True(DetectionParameterRecommendationService.IsSupported(new BlobAnalysisRoi()));
            Assert.False(DetectionParameterRecommendationService.IsSupported(new LineMeasureRoi()));

            DetectionParameterProposal proposal = DetectionParameterRecommendationService.Recommend(bitmap, new LineMeasureRoi());
            Assert.False(proposal.Success);
            Assert.Equal("RecommendationUnsupportedRoi", proposal.FailureKey);
            Assert.Null(proposal.SuggestedRoi);
        });
    }

    private static BitmapSource CreateGray8(int width, int height, Func<int, int, byte> pixel)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) pixels[y * width + x] = pixel(x, y);
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateGray16(int width, int height, Func<int, int, ushort> pixel)
    {
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) pixels[y * width + x] = pixel(x, y);
        var bytes = new byte[pixels.Length * sizeof(ushort)];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, bytes, width * sizeof(ushort));
        bitmap.Freeze();
        return bitmap;
    }
}
