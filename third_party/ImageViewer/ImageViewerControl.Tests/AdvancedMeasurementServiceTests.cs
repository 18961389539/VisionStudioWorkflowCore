using ImageViewer.Core.Measurements;
using ImageViewer.Core.Geometry;
using ImageViewer.Models;
using ImageViewer.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace ImageViewerControl.Tests;

[Trait("Category", "Unit")]
public sealed class AdvancedMeasurementServiceTests
{
    private static readonly int[] PeriodicBrightRunStarts = [4, 11, 20];
    [Fact]
    public void MissingAndCyclicSourcesReturnFailuresInsteadOfValues()
    {
        var host = new PointCoordinateMeasureRoi();
        AdvancedMeasurementDefinition missing = Definition(AdvancedMeasurementKind.Position, Guid.NewGuid());
        Assert.Contains("not found", AdvancedMeasurementService.Evaluate(missing, [host], 1, "mm").Error);

        var first = Definition(AdvancedMeasurementKind.Midpoint, Guid.NewGuid(), host.MeasurementId);
        var second = Definition(AdvancedMeasurementKind.Midpoint, first.Id, host.MeasurementId);
        first = first with { Sources = [second.Id, host.MeasurementId] };
        second = second with { Sources = [first.Id, host.MeasurementId] };
        host.AdvancedMeasurements = [first, second];
        AdvancedMeasurementEvaluation cycle = AdvancedMeasurementService.Evaluate(first, [host], 1, "mm");
        Assert.False(cycle.Success);
        Assert.Contains("cycle", cycle.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cycle.Metrics);
    }

    [Fact]
    public void FailedCaliperIsNotTreatedAsItsManualSearchLine()
    {
        var holder = new PointCoordinateMeasureRoi();
        var caliper = new LineCaliperMeasureRoi
        {
            P1 = new PointD(0, 0), P2 = new PointD(10, 0)
        };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.Straightness, caliper.MeasurementId);

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [holder, caliper], 1, "px");

        Assert.False(result.Success);
        Assert.Contains("successful", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Metrics);

        var manual = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(10, 0) };
        AdvancedMeasurementEvaluation manualResult = AdvancedMeasurementService.Evaluate(
            Definition(AdvancedMeasurementKind.Straightness, manual.MeasurementId), [manual], 1, "px");
        Assert.False(manualResult.Success);
        Assert.Contains("form-error", manualResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PointMeasurementsUndistortThenScaleAndApplyLimits()
    {
        var point = new PointCoordinateMeasureRoi { Position = new PointD(6, 8) };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.Position, point.MeasurementId) with
        {
            NominalX = 0, NominalY = 0, UpperLimit = 4.9
        };

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [point], 0.5, "mm");

        Assert.True(result.Success);
        Assert.Equal(5, result.Metrics[0].Value, 10);
        Assert.Equal("mm", result.Metrics[0].Unit);
        Assert.False(result.WithinLimits);
    }

    [Fact]
    public void PositionCanCompareAgainstNonzeroWorkpieceNominalDatumCoordinates()
    {
        WorkpieceCoordinateSystem workpiece = WorkpieceCoordinateSystem.Localize(
            new PointD(10, 20), new PointD(10, 30), new PointD(100, 200), new PointD(100, 210));
        PointD actualImage = workpiece.ToImage(new PointD(103, 202));
        var point = new PointCoordinateMeasureRoi { Position = actualImage };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.Position, point.MeasurementId) with
        {
            NominalX = 100, NominalY = 200, NominalInWorkpieceCoordinates = true
        };

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [point], 1, "mm", workpiece: workpiece);

        Assert.True(result.Success, result.Error);
        Assert.Equal(Math.Sqrt(13), result.Metrics[0].Value, 8);
        Assert.Equal(2 * Math.Sqrt(13), result.Metrics[1].Value, 8);
    }

    [Fact]
    public void ProjectedDistanceUsesConfiguredAngleWhenNoAxisSourceIsProvided()
    {
        var first = new PointCoordinateMeasureRoi { Position = new PointD(0, 0) };
        var second = new PointCoordinateMeasureRoi { Position = new PointD(3, 4) };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.ProjectedDistance, first.MeasurementId, second.MeasurementId) with { Parameter = 0 };

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [first, second], 0.5, "mm");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1.5, result.Metrics[0].Value, 10);
    }

    [Fact]
    public void CircleRelationsUsePhysicalGeometryAndExposeWitnesses()
    {
        var first = new CircleRoi { Center = new PointD(0, 0), Radius = 4 };
        var second = new CircleRoi { Center = new PointD(12, 0), Radius = 2 };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.ShortestDistance, first.MeasurementId, second.MeasurementId);

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [first, second], 0.5, "mm");

        Assert.True(result.Success);
        Assert.Equal(3, result.Metrics[0].Value, 10);
        Assert.Equal(2, result.Geometry.Length);
        Assert.All(result.Geometry, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
    }

    [Fact]
    public void RectangleAndShapeMetricsUseClosedPolygonContourInCalibratedCoordinates()
    {
        var polygon = new PolygonRoi
        {
            IsClosed = true,
            Points = [new(0, 0), new(4, 0), new(4, 2), new(0, 2)]
        };
        var host = new PointCoordinateMeasureRoi();
        AdvancedMeasurementDefinition rectangle = Definition(AdvancedMeasurementKind.Rectangle, polygon.MeasurementId);
        AdvancedMeasurementDefinition shape = Definition(AdvancedMeasurementKind.ShapeMetrics, polygon.MeasurementId);

        AdvancedMeasurementEvaluation rectangleResult = AdvancedMeasurementService.Evaluate(rectangle, [host, polygon], 0.5, "mm");
        AdvancedMeasurementEvaluation shapeResult = AdvancedMeasurementService.Evaluate(shape, [host, polygon], 0.5, "mm");

        Assert.True(rectangleResult.Success, rectangleResult.Error);
        Assert.Equal(2, rectangleResult.Metrics[0].Value, 10);
        Assert.Equal(2, rectangleResult.Metrics[2].Value, 10);
        Assert.True(shapeResult.Success, shapeResult.Error);
        Assert.Equal(6, shapeResult.Metrics[0].Value, 10);
        Assert.Equal(2, shapeResult.Metrics[1].Value, 10);
        Assert.Equal("AdvancedMeasurement.Method.PolygonShapeMetrics", shapeResult.Method);
    }

    [Fact]
    public void SlotFitUsesOrderedClosedContourAndReportsResidual()
    {
        PointD[] capsule = MakeCapsule();
        var polygon = new PolygonRoi { IsClosed = true, Points = [.. capsule] };
        AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.Slot, polygon.MeasurementId);

        AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [polygon], 1, "px");

        Assert.True(result.Success, result.Error);
        Assert.Equal(8, result.Metrics[0].Value, 1);
        Assert.Equal(2, result.Metrics[1].Value, 1);
        Assert.True(result.Metrics[2].Value >= 0);
        Assert.Equal(2, result.Geometry.Length);
    }

    [Fact]
    public void GapPitchAndContourProfilesReturnSamplesAndStations()
    {
        var first = new PolylineRoi { Points = [new(0, 0), new(4, 0)] };
        var second = new PolylineRoi { Points = [new(0, 4), new(4, 4)] };
        var p0 = new PointCoordinateMeasureRoi { Position = new PointD(0, 0) };
        var p1 = new PointCoordinateMeasureRoi { Position = new PointD(3, 0) };
        var p2 = new PointCoordinateMeasureRoi { Position = new PointD(3, 4) };
        AdvancedMeasurementDefinition gap = Definition(AdvancedMeasurementKind.GapProfile, first.MeasurementId, second.MeasurementId) with { Parameter = 2 };
        AdvancedMeasurementDefinition pitch = Definition(AdvancedMeasurementKind.Pitch, p0.MeasurementId, p1.MeasurementId, p2.MeasurementId);
        AdvancedMeasurementDefinition deviation = Definition(AdvancedMeasurementKind.ContourDeviation, first.MeasurementId, second.MeasurementId);

        AdvancedMeasurementEvaluation gapResult = AdvancedMeasurementService.Evaluate(gap, [first, second], 0.5, "mm");
        AdvancedMeasurementEvaluation pitchResult = AdvancedMeasurementService.Evaluate(pitch, [p0, p1, p2], 0.5, "mm");
        AdvancedMeasurementEvaluation deviationResult = AdvancedMeasurementService.Evaluate(deviation, [first, second], 0.5, "mm");

        Assert.True(gapResult.Success, gapResult.Error);
        Assert.Equal(2, gapResult.Samples.Length);
        Assert.All(gapResult.Samples, sample => Assert.Equal(2, sample, 10));
        Assert.Equal(2, gapResult.Metrics[2].Value, 10);
        Assert.True(pitchResult.Success, pitchResult.Error);
        Assert.Equal(2, pitchResult.Samples.Length);
        Assert.Equal(1.75, pitchResult.Metrics[0].Value, 10);
        Assert.True(deviationResult.Success, deviationResult.Error);
        Assert.Equal(2, deviationResult.Samples.Length);
        Assert.Equal(2, deviationResult.Metrics[0].Value, 10);
    }

    [Fact]
    public void DistributionCircleFitsCentersAndProfileToolsRequireMeasuredSamples()
    {
        PointCoordinateMeasureRoi[] points =
        [
            new() { Position = new PointD(3, 0) }, new() { Position = new PointD(0, 3) },
            new() { Position = new PointD(-3, 0) }, new() { Position = new PointD(0, -3) }
        ];
        AdvancedMeasurementDefinition distribution = Definition(AdvancedMeasurementKind.DistributionCircle, points.Select(point => point.MeasurementId).ToArray());
        AdvancedMeasurementEvaluation circle = AdvancedMeasurementService.Evaluate(distribution, points, 1, "mm");
        Assert.True(circle.Success, circle.Error);
        Assert.Equal(3, circle.Metrics[0].Value, 10);

        var circular = new CircularCaliperMeasureRoi();
        AdvancedMeasurementDefinition circularity = Definition(AdvancedMeasurementKind.Circularity, circular.MeasurementId);
        Assert.False(AdvancedMeasurementService.Evaluate(circularity, [circular], 1, "mm").Success);
        circular.HasDetectedEdges = true;
        circular.EdgePoints = [new(2, 0), new(0, 2), new(-2, 0), new(0, -2)];
        circular.ApplyQualityAssessment(new CircularCaliperQualityAssessment(
            CircularCaliperQualityStatus.Passed, 1, 360, CircularCaliperQualityReason.None));
        AdvancedMeasurementEvaluation form = AdvancedMeasurementService.Evaluate(circularity, [circular], 1, "mm");
        Assert.True(form.Success, form.Error);
        Assert.Equal(0, form.Metrics[0].Value, 10);
    }

    [Fact]
    public void EnumerateAndEvaluateAllReturnDefinitionsInRoiOrder()
    {
        var first = new PointCoordinateMeasureRoi { Position = new PointD(1, 0) };
        var second = new PointCoordinateMeasureRoi { Position = new PointD(2, 0) };
        var owner = new PointCoordinateMeasureRoi();
        AdvancedMeasurementDefinition a = Definition(AdvancedMeasurementKind.Pitch, first.MeasurementId, second.MeasurementId);
        AdvancedMeasurementDefinition b = Definition(AdvancedMeasurementKind.Position, first.MeasurementId);
        owner.AdvancedMeasurements = [a, b];

        Assert.Equal([a.Id, b.Id], AdvancedMeasurementService.EnumerateDefinitions([owner]).Select(item => item.Id));
        Assert.Equal([a.Id, b.Id], AdvancedMeasurementService.EvaluateAll([owner, first, second], 1, "mm").Select(item => item.Id));
    }

    [Fact]
    public void PeriodicPitchExtractsThresholdRunsAndScalesCenterSpacing()
    {
        WpfTestRunner.Run(() =>
        {
            const int width = 30, height = 20;
            byte[] pixels = new byte[width * height];
            foreach (int start in PeriodicBrightRunStarts)
                for (int x = start; x < start + 3; x++) pixels[10 * width + x] = 255;
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            var line = new LineMeasureRoi { P1 = new PointD(1, 10), P2 = new PointD(28, 10) };
            AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.PeriodicPitch, line.MeasurementId) with { Parameter = 1 };

            AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [line], 0.5, "mm", bitmap: bitmap);

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.Geometry.Length);
            Assert.Equal(2, result.Samples.Length);
            Assert.Equal(2, result.Stations.Length);
            Assert.Equal(4, result.Metrics[0].Value, 8);
            Assert.Equal("AdvancedMeasurement.Method.ThresholdRunCenterSpacing", result.Method);
        });
    }

    [Fact]
    public void PeriodicPitchRejectsNoContrastAndMissingBitmap()
    {
        WpfTestRunner.Run(() =>
        {
            const int width = 20, height = 12;
            byte[] pixels = new byte[width * height];
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            var line = new LineMeasureRoi { P1 = new PointD(1, 6), P2 = new PointD(18, 6) };
            AdvancedMeasurementDefinition definition = Definition(AdvancedMeasurementKind.PeriodicPitch, line.MeasurementId);

            Assert.False(AdvancedMeasurementService.Evaluate(definition, [line], 1, "px").Success);
            AdvancedMeasurementEvaluation result = AdvancedMeasurementService.Evaluate(definition, [line], 1, "px", bitmap: bitmap);

            Assert.False(result.Success);
            Assert.Contains("contrast", result.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static AdvancedMeasurementDefinition Definition(AdvancedMeasurementKind kind, params Guid[] sources) =>
        new() { Kind = kind, Sources = sources };

    private static PointD[] MakeCapsule()
    {
        var points = new List<PointD>();
        for (int i = 0; i <= 16; i++)
        {
            double angle = -Math.PI / 2 + Math.PI * i / 16;
            points.Add(new PointD(3 + Math.Cos(angle), Math.Sin(angle)));
        }
        for (int i = 0; i <= 16; i++)
        {
            double angle = Math.PI / 2 + Math.PI * i / 16;
            points.Add(new PointD(-3 + Math.Cos(angle), Math.Sin(angle)));
        }
        return points.ToArray();
    }
}
