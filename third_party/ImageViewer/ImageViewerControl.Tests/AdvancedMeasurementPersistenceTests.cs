using System;
using System.Linq;
using ImageViewer.Core.Measurements;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

public class AdvancedMeasurementPersistenceTests
{
    [Fact]
    public void RoiClonePreservesStableIdentityAndDeepCopiesMeasurementDefinitions()
    {
        Guid sourceId = Guid.NewGuid();
        var source = new CircleRoi
        {
            MeasurementId = sourceId,
            AdvancedMeasurements =
            [
                new AdvancedMeasurementDefinition
                {
                    Name = "diameter",
                    Kind = AdvancedMeasurementKind.ShortestDistance,
                    Sources = [Guid.NewGuid()]
                }
            ]
        };

        CircleRoi clone = Assert.IsType<CircleRoi>(source.Clone());
        Assert.Equal(sourceId, clone.MeasurementId);
        Assert.NotSame(source.AdvancedMeasurements, clone.AdvancedMeasurements);
        Assert.NotSame(source.AdvancedMeasurements[0], clone.AdvancedMeasurements[0]);
        Assert.NotSame(source.AdvancedMeasurements[0].Sources, clone.AdvancedMeasurements[0].Sources);

        Guid originalSourceId = source.AdvancedMeasurements[0].Sources[0];
        clone.AdvancedMeasurements[0].Sources[0] = Guid.NewGuid();
        Assert.Equal(originalSourceId, source.AdvancedMeasurements[0].Sources[0]);
    }

    [Fact]
    public void ApplyFromCopiesDefinitionsButKeepsDestinationIdentity()
    {
        var source = new CircleRoi
        {
            MeasurementId = Guid.NewGuid(),
            AdvancedMeasurements = [new AdvancedMeasurementDefinition { Name = "position", Kind = AdvancedMeasurementKind.Position }]
        };
        var destination = new CircleRoi { MeasurementId = Guid.NewGuid() };
        Guid destinationId = destination.MeasurementId;

        destination.ApplyFrom(source);

        Assert.Equal(destinationId, destination.MeasurementId);
        Assert.Equal("position", Assert.Single(destination.AdvancedMeasurements).Name);
        Assert.NotSame(source.AdvancedMeasurements[0], destination.AdvancedMeasurements[0]);
    }

    [Fact]
    public void RoiDocumentRoundTripPersistsIdentityAndDefinitions()
    {
        var source = new CircularCaliperMeasureRoi
        {
            Center = new PointD(5, 6),
            Radius = 3,
            MeasurementId = Guid.NewGuid(),
            AdvancedMeasurements =
            [
                new AdvancedMeasurementDefinition
                {
                    Name = "pitch",
                    Kind = AdvancedMeasurementKind.Pitch,
                    Sources = [Guid.NewGuid(), Guid.NewGuid()],
                    Parameter = 2
                }
            ]
        };
        RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

        string json = RoiPersistenceService.Serialize([source], 1, "px", registry);
        var (restored, _, _, _) = RoiPersistenceService.Deserialize(json, registry);
        var copy = Assert.IsType<CircularCaliperMeasureRoi>(Assert.Single(restored));

        Assert.Equal(source.MeasurementId, copy.MeasurementId);
        Assert.Equal(source.AdvancedMeasurements[0].Name, copy.AdvancedMeasurements[0].Name);
        Assert.Equal(source.AdvancedMeasurements[0].Sources, copy.AdvancedMeasurements[0].Sources);
        Assert.Contains("\"MeasurementId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"AdvancedMeasurements\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLegacyIdentityGeneratesANewId()
    {
        var roi = new CircleRoi();

        roi.ApplyCommonState(new RoiPersistenceData());

        Assert.NotEqual(Guid.Empty, roi.MeasurementId);
        Assert.Empty(roi.AdvancedMeasurements);

        roi.AdvancedMeasurements = null!;
        Assert.Empty(roi.AdvancedMeasurements);
        roi.AdvancedMeasurements = [null!];
        Assert.Empty(roi.AdvancedMeasurements);
        roi.AdvancedMeasurements = [new AdvancedMeasurementDefinition { Sources = null! }];
        Assert.Empty(roi.AdvancedMeasurements[0].Sources);
    }

    [Fact]
    public void CopyVisualStateDoesNotTransferMeasurementOwnership()
    {
        var source = new CircleRoi
        {
            MeasurementId = Guid.NewGuid(),
            AdvancedMeasurements = [new AdvancedMeasurementDefinition { Name = "source-only" }]
        };
        var destination = new CircleRoi
        {
            MeasurementId = Guid.NewGuid(),
            AdvancedMeasurements = [new AdvancedMeasurementDefinition { Name = "destination-only" }]
        };
        Guid destinationId = destination.MeasurementId;

        destination.CopyVisualStateFrom(source);

        Assert.Equal(destinationId, destination.MeasurementId);
        Assert.Equal("destination-only", Assert.Single(destination.AdvancedMeasurements).Name);
    }

    [Fact]
    public void CaliperAndArcCloneHelpersPreserveIdentity()
    {
        RoiBase[] rois =
        [
            new CaliperMeasureRoi(),
            new LineCaliperMeasureRoi(),
            new CircularCaliperMeasureRoi(),
            new ArcCaliperMeasureRoi()
        ];

        foreach (RoiBase roi in rois)
        {
            roi.MeasurementId = Guid.NewGuid();
            roi.AdvancedMeasurements = [new AdvancedMeasurementDefinition { Name = "test" }];
            RoiBase clone = roi.Clone();
            Assert.Equal(roi.MeasurementId, clone.MeasurementId);
            Assert.Equal("test", Assert.Single(clone.AdvancedMeasurements).Name);
        }
    }

    [Fact]
    public void EveryParameterlessRoiImplementationPreservesMeasurementStateWhenCloned()
    {
        Type[] roiTypes = typeof(CircleRoi).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(RoiBase).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();
        Assert.True(roiTypes.Length >= 20);
        Guid identity = Guid.NewGuid();
        AdvancedMeasurementDefinition definition = new() { Name = "all-rois", Sources = [Guid.NewGuid()] };

        foreach (Type type in roiTypes)
        {
            var roi = (RoiBase)Activator.CreateInstance(type)!;
            roi.MeasurementId = identity;
            roi.AdvancedMeasurements = [definition];

            RoiBase clone = roi.Clone();

            Assert.Equal(identity, clone.MeasurementId);
            Assert.Equal("all-rois", Assert.Single(clone.AdvancedMeasurements).Name);
            Assert.NotSame(roi.AdvancedMeasurements[0], clone.AdvancedMeasurements[0]);
        }
    }
}
