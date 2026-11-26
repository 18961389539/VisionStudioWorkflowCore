using System;
using ImageViewer.Core.Measurements;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

public class AdvancedMeasurementRecipeAlignmentTests
{
    [Theory]
    [InlineData(0, 10, 90)]
    [InlineData(0, -10, -90)]
    [InlineData(-10, 0, 180)]
    public void AlignmentPreservesSignedProjectionAndLimitsForAngleAndLineDirections(int axisX, int axisY, double angle)
    {
        RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
        var first = new PointCoordinateMeasureRoi { Position = new PointD(12, 24) };
        var second = new PointCoordinateMeasureRoi { Position = new PointD(4, 29) };
        var axis = new LineMeasureRoi { P1 = new PointD(5, 0), P2 = new PointD(5, 10) };
        var byAngle = new AdvancedMeasurementDefinition
        {
            Kind = AdvancedMeasurementKind.ProjectedDistance, Parameter = 45,
            Sources = [first.MeasurementId, second.MeasurementId], LowerLimit = -1.1, UpperLimit = -1.0
        };
        var byLine = new AdvancedMeasurementDefinition
        {
            Kind = AdvancedMeasurementKind.ProjectedDistance, Parameter = 37,
            Sources = [first.MeasurementId, second.MeasurementId, axis.MeasurementId], LowerLimit = 2.4, UpperLimit = 2.6
        };
        first.AdvancedMeasurements = [byAngle, byLine];
        RoiBase[] referenceRois = [first, second, axis];
        MeasurementRecipeDocument recipe = MeasurementRecipeService.Create("projection", null, referenceRois, 0.5, "mm", registry,
            datumPoints: [new() { X = 10, Y = 20 }, new() { X = 20, Y = 20 }]);
        string referenceJson = MeasurementRecipeService.Serialize(recipe);

        RoiDocument aligned = MeasurementRecipeService.BuildAlignedRoiDocument(
            recipe, new PointD(30, 40), new PointD(30 + axisX, 40 + axisY));
        var restored = RoiPersistenceService.CreateRois(aligned, registry);
        var definitions = AdvancedMeasurementService.EnumerateDefinitions(restored.Rois);
        var results = AdvancedMeasurementService.EvaluateAll(restored.Rois, restored.PixelSize, restored.PhysicalUnit);

        Assert.Equal(referenceJson, MeasurementRecipeService.Serialize(recipe));
        Assert.Equal(byAngle.Id, definitions[0].Id);
        Assert.Equal(byAngle.Sources, definitions[0].Sources);
        Assert.Equal(byAngle.Parameter + angle, definitions[0].Parameter, 9);
        Assert.Equal(byLine.Parameter, definitions[1].Parameter);
        Assert.True(results[0].Success, results[0].Error);
        Assert.True(results[1].Success, results[1].Error);
        Assert.Equal(-1.5 / Math.Sqrt(2), Assert.Single(results[0].Metrics).Value, 9);
        Assert.Equal(2.5, Assert.Single(results[1].Metrics).Value, 9);
        Assert.True(results[0].WithinLimits);
        Assert.True(results[1].WithinLimits);
    }

    [Fact]
    public void AlignmentTransformsImageCoordinatePositionNominalsAndKeepsWorkpieceNominalsFixed()
    {
        AdvancedMeasurementDefinition imageNominal = new()
        {
            Name = "image-position",
            Kind = AdvancedMeasurementKind.Position,
            NominalX = 120,
            NominalY = 130
        };
        AdvancedMeasurementDefinition workpieceNominal = new()
        {
            Name = "workpiece-position",
            Kind = AdvancedMeasurementKind.Position,
            NominalX = 7,
            NominalY = 8,
            NominalInWorkpieceCoordinates = true
        };
        MeasurementRecipeDocument recipe = MeasurementRecipeService.Create(
            "position nominals",
            null,
            [new CircleRoi { Center = new PointD(120, 130), Radius = 4, AdvancedMeasurements = [imageNominal, workpieceNominal] }],
            1,
            "px",
            RoiPluginRegistry.CreateBuiltIn(),
            datumPoints:
            [
                new RecipeDatumPoint { Name = "reference-1", X = 100, Y = 100 },
                new RecipeDatumPoint { Name = "reference-2", X = 200, Y = 100 }
            ]);

        RoiDocument aligned = MeasurementRecipeService.BuildAlignedRoiDocument(
            recipe, new PointD(300, 400), new PointD(300, 500));
        AdvancedMeasurementDefinition[] transformed = Assert.Single(aligned.Items).AdvancedMeasurements;

        Assert.Equal(new PointD(270, 420), new PointD(transformed[0].NominalX, transformed[0].NominalY));
        Assert.False(transformed[0].NominalInWorkpieceCoordinates);
        Assert.Equal(new PointD(7, 8), new PointD(transformed[1].NominalX, transformed[1].NominalY));
        Assert.True(transformed[1].NominalInWorkpieceCoordinates);
        RoiPersistencePoint originalCenter = recipe.Items[0].Template!.Center!;
        Assert.Equal(new PointD(120, 130), new PointD(originalCenter.X, originalCenter.Y));
    }
}
