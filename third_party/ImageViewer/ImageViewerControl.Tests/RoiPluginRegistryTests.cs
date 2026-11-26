using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Trait("Category", "Integration")]
    public class RoiPluginRegistryTests
    {
        private static readonly RoiRegistrationExpectation[] ExpectedBuiltInRegistrations =
        [
            new("angle-measure", typeof(AngleMeasureRoi)),
            new("arc-caliper-measure", typeof(ArcCaliperMeasureRoi)),
            new("arc-measure", typeof(ArcMeasureRoi)),
            new("arrow-annotation", typeof(ArrowAnnotationRoi)),
            new("blob-analysis", typeof(BlobAnalysisRoi)),
            new("caliper-measure", typeof(CaliperMeasureRoi)),
            new("circle", typeof(CircleRoi)),
            new("circular-caliper-measure", typeof(CircularCaliperMeasureRoi)),
            new("concentricity-measure", typeof(ConcentricityMeasureRoi)),
            new("center-distance-measure", typeof(CenterDistanceMeasureRoi)),
            new("ellipse", typeof(EllipseRoi)),
            new("fitted-ellipse", typeof(FittedEllipseRoi)),
            new("line-caliper-measure", typeof(LineCaliperMeasureRoi)),
            new("line-measure", typeof(LineMeasureRoi)),
            new("parallelism-measure", typeof(ParallelismMeasureRoi)),
            new("perpendicularity-measure", typeof(PerpendicularityMeasureRoi)),
            new("point-annotation", typeof(PointAnnotationRoi)),
            new("point-coordinate-measure", typeof(PointCoordinateMeasureRoi)),
            new("point-to-circle-distance", typeof(PointToCircleDistanceRoi)),
            new("point-to-line-distance", typeof(PointToLineDistanceRoi)),
            new("polygon", typeof(PolygonRoi)),
            new("polyline", typeof(PolylineRoi)),
            new("ring", typeof(RingRoi)),
            new("rotated-rect", typeof(RotatedRect)),
            new("text-annotation", typeof(TextAnnotationRoi)),
            new("three-point-circle", typeof(ThreePointCircleMeasureRoi))
        ];

        private static readonly RoiPersistenceRoundTripExpectation[] PersistenceRoundTripExpectations =
        [
            new(
                "blob-analysis",
                new BlobAnalysisRoi
                {
                    Center = new PointD(10, 20),
                    Width = 30,
                    Height = 40,
                    Angle = 15,
                    UseOtsu = true,
                    ManualThreshold = 123,
                    DetectDark = true,
                    MinArea = 9
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.Center);
                    Assert.Equal(10, data.Geometry.Center!.X);
                    Assert.Equal(20, data.Center!.Y);
                    Assert.Equal(30, data.Geometry.Width);
                    Assert.Equal(40, data.Height);
                    Assert.Equal(15, data.Geometry.Angle);
                    Assert.True(data.Options.UseOtsu);
                    Assert.True(data.UseOtsu);
                    Assert.Equal(123, data.Options.ManualThreshold);
                    Assert.True(data.Options.DetectDark);
                    Assert.Equal(9, data.Options.MinArea);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<BlobAnalysisRoi>(roi);
                    Assert.Equal(new PointD(10, 20), roundTripped.Center);
                    Assert.Equal(30, roundTripped.Width);
                    Assert.Equal(40, roundTripped.Height);
                    Assert.Equal(15, roundTripped.Angle);
                    Assert.True(roundTripped.UseOtsu);
                    Assert.Equal(123, roundTripped.ManualThreshold);
                    Assert.True(roundTripped.DetectDark);
                    Assert.Equal(9, roundTripped.MinArea);
                }),
            new(
                "ellipse",
                new EllipseRoi
                {
                    Center = new PointD(5, 6),
                    RadiusX = 12,
                    RadiusY = 8,
                    Angle = 22
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.Center);
                    Assert.Equal(5, data.Center!.X);
                    Assert.Equal(6, data.Geometry.Center!.Y);
                    Assert.Equal(12, data.Geometry.RadiusX);
                    Assert.Equal(8, data.RadiusY);
                    Assert.Equal(22, data.Geometry.Angle);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<EllipseRoi>(roi);
                    Assert.Equal(new PointD(5, 6), roundTripped.Center);
                    Assert.Equal(12, roundTripped.RadiusX);
                    Assert.Equal(8, roundTripped.RadiusY);
                    Assert.Equal(22, roundTripped.Angle);
                }),
            new(
                "point-annotation",
                new PointAnnotationRoi
                {
                    Position = new PointD(3, 4)
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.Position);
                    Assert.Equal(3, data.Position!.X);
                    Assert.Equal(4, data.Geometry.Position!.Y);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<PointAnnotationRoi>(roi);
                    Assert.Equal(new PointD(3, 4), roundTripped.Position);
                }),
            new(
                "polygon",
                new PolygonRoi
                {
                    Points =
                    [
                        new PointD(0, 0),
                        new PointD(5, 0),
                        new PointD(5, 5)
                    ],
                    IsClosed = true
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.Points);
                    Assert.Equal(3, data.Geometry.Points!.Count);
                    Assert.Equal(3, data.Points!.Count);
                    Assert.True(data.Options.IsClosed);
                    Assert.True(data.IsClosed);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<PolygonRoi>(roi);
                    Assert.Equal(3, roundTripped.Points.Count);
                    Assert.True(roundTripped.IsClosed);
                }),
            new(
                "caliper-measure",
                new CaliperMeasureRoi
                {
                    P1 = new PointD(1, 1),
                    P2 = new PointD(9, 1),
                    CaliperCenter = new PointD(5, 2),
                    CaliperRegionLength = 14,
                    CaliperSearchRange = 6,
                    CaliperAngleDegrees = 12,
                    HasExplicitCaliperRegion = true
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.P1);
                    Assert.NotNull(data.Geometry.P2);
                    Assert.NotNull(data.Geometry.Center);
                    Assert.Equal(1, data.P1!.X);
                    Assert.Equal(9, data.Geometry.P2!.X);
                    Assert.Equal(5, data.Center!.X);
                    Assert.Equal(14, data.Geometry.Width);
                    Assert.Equal(12, data.Geometry.Height);
                    Assert.Equal(12, data.Angle);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<CaliperMeasureRoi>(roi);
                    Assert.Equal(new PointD(1, 1), roundTripped.P1);
                    Assert.Equal(new PointD(9, 1), roundTripped.P2);
                    Assert.Equal(new PointD(5, 2), roundTripped.CaliperCenter);
                    Assert.Equal(14, roundTripped.GetResolvedCaliperRegionLength());
                    Assert.Equal(6, roundTripped.CaliperSearchRange);
                    Assert.Equal(12, roundTripped.CaliperAngleDegrees);
                }),
            new(
                "circular-caliper-measure",
                new CircularCaliperMeasureRoi
                {
                    Center = new PointD(20, 30),
                    Radius = 18,
                    CaliperCount = 24,
                    CaliperSearchRange = 12,
                    CaliperSamplingHalfWidth = 4,
                    MinimumValidCalipers = 10,
                    CaliperMinimumGradient = 7.5,
                    CaliperOutlierThreshold = 1.25,
                    CaliperEdgePolarity = CaliperEdgePolarity.DarkToLight
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.Center);
                    Assert.Equal(20, data.Geometry.Center!.X);
                    Assert.Equal(18, data.Geometry.Radius);
                    Assert.Equal(24, data.Measurement.CaliperCount);
                    Assert.Equal(24, data.CaliperCount);
                    Assert.Equal(12, data.Measurement.CaliperSearchRange);
                    Assert.Equal(4, data.Measurement.CaliperSamplingHalfWidth);
                    Assert.Equal(10, data.Measurement.MinimumValidCalipers);
                    Assert.Equal(7.5, data.Measurement.CaliperMinimumGradient);
                    Assert.Equal(1.25, data.Measurement.CaliperOutlierThreshold);
                    Assert.Equal(nameof(CaliperEdgePolarity.DarkToLight), data.Measurement.CaliperEdgePolarity);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<CircularCaliperMeasureRoi>(roi);
                    Assert.Equal(new PointD(20, 30), roundTripped.Center);
                    Assert.Equal(18, roundTripped.Radius);
                    Assert.Equal(24, roundTripped.CaliperCount);
                    Assert.Equal(12, roundTripped.CaliperSearchRange);
                    Assert.Equal(4, roundTripped.CaliperSamplingHalfWidth);
                    Assert.Equal(10, roundTripped.MinimumValidCalipers);
                    Assert.Equal(7.5, roundTripped.CaliperMinimumGradient);
                    Assert.Equal(1.25, roundTripped.CaliperOutlierThreshold);
                    Assert.Equal(CaliperEdgePolarity.DarkToLight, roundTripped.CaliperEdgePolarity);
                }),
            new(
                "parallelism-measure",
                new ParallelismMeasureRoi
                {
                    Line1P1 = new PointD(1, 2),
                    Line1P2 = new PointD(3, 4),
                    Line2P1 = new PointD(5, 6),
                    Line2P2 = new PointD(7, 8)
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.P1);
                    Assert.NotNull(data.Geometry.P2);
                    Assert.NotNull(data.Geometry.Vertex);
                    Assert.NotNull(data.Geometry.P3);
                    Assert.Equal(1, data.P1!.X);
                    Assert.Equal(4, data.P2!.Y);
                    Assert.Equal(5, data.Vertex!.X);
                    Assert.Equal(8, data.Geometry.P3!.Y);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<ParallelismMeasureRoi>(roi);
                    Assert.Equal(new PointD(1, 2), roundTripped.Line1P1);
                    Assert.Equal(new PointD(3, 4), roundTripped.Line1P2);
                    Assert.Equal(new PointD(5, 6), roundTripped.Line2P1);
                    Assert.Equal(new PointD(7, 8), roundTripped.Line2P2);
                }),
            new(
                "concentricity-measure",
                new ConcentricityMeasureRoi
                {
                    Center1 = new PointD(10, 10),
                    Radius1 = 4,
                    Center2 = new PointD(12, 13),
                    Radius2 = 7
                },
                data =>
                {
                    Assert.NotNull(data.Geometry.P1);
                    Assert.NotNull(data.Geometry.P2);
                    Assert.Equal(10, data.P1!.X);
                    Assert.Equal(13, data.Geometry.P2!.Y);
                    Assert.Equal(4, data.Geometry.Radius);
                    Assert.Equal(7, data.Radius2);
                },
                roi =>
                {
                    var roundTripped = Assert.IsType<ConcentricityMeasureRoi>(roi);
                    Assert.Equal(new PointD(10, 10), roundTripped.Center1);
                    Assert.Equal(4, roundTripped.Radius1);
                    Assert.Equal(new PointD(12, 13), roundTripped.Center2);
                    Assert.Equal(7, roundTripped.Radius2);
                })
        ];

        public static IEnumerable<object[]> BuiltInRegistrationCases =>
            ExpectedBuiltInRegistrations.Select(static expectation => new object[] { expectation });

        public static IEnumerable<object[]> PersistenceRoundTripCases =>
            PersistenceRoundTripExpectations.Select(static expectation => new object[] { expectation });

        [Fact]
        public void Default_MatchesFreshBuiltInRegistry_AndExposesExpectedTypeKeys()
        {
#pragma warning disable CS0618
            RoiPluginRegistry defaultRegistry = RoiPluginRegistry.Default;
#pragma warning restore CS0618
            RoiPluginRegistry freshRegistry = RoiPluginRegistry.CreateBuiltIn();

            string[] expectedKeys = ExpectedBuiltInRegistrations.Select(static registration => registration.TypeKey).OrderBy(key => key).ToArray();
            string[] defaultKeys = defaultRegistry.RegisteredTypeKeys.OrderBy(key => key).ToArray();
            string[] freshKeys = freshRegistry.RegisteredTypeKeys.OrderBy(key => key).ToArray();

            Assert.Equal(expectedKeys, defaultKeys);
            Assert.Equal(expectedKeys, freshKeys);
            Assert.Equal(expectedKeys.Length, defaultRegistry.Plugins.Count);
            Assert.Equal(expectedKeys.Length, freshRegistry.Plugins.Count);
            Assert.All(defaultRegistry.Plugins, plugin =>
            {
                Assert.NotNull(plugin.Behavior);
                Assert.NotNull(plugin.Renderer);
            });
        }

        [Theory]
        [MemberData(nameof(BuiltInRegistrationCases))]
        public void CreateBuiltIn_RegistersExpectedPlugin(RoiRegistrationExpectation expectation)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            IRoiPlugin? plugin = registry.FindByTypeKey(expectation.TypeKey);

            Assert.NotNull(plugin);
            Assert.Equal(expectation.RoiType, plugin!.RoiType);
        }

        [Theory]
        [MemberData(nameof(PersistenceRoundTripCases))]
        public void CreateBuiltIn_RoundTripsRepresentativePersistenceMappings(RoiPersistenceRoundTripExpectation expectation)
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();
            IRoiPlugin plugin = Assert.IsAssignableFrom<IRoiPlugin>(registry.FindByTypeKey(expectation.TypeKey));
            var data = new RoiPersistenceData();

            plugin.PopulatePersistenceData(expectation.SourceRoi, data);

            expectation.AssertPersistenceData(data);
            expectation.AssertRoundTripped(plugin.CreateRoi(data));
        }

        [Fact]
        public void GetDrawingTools_SortsByMenuOrderThenHeader()
        {
            var registry = new RoiPluginRegistry();
            registry.Register(new FakeRoiPlugin(
                typeKey: "plugin-z",
                roiType: typeof(int),
                drawingTools:
                [
                    new RoiToolDescriptor("Zulu", static _ => { }, menuOrder: 10),
                    new RoiToolDescriptor("Alpha", static _ => { }, menuOrder: 10),
                    new RoiToolDescriptor("Beta", static _ => { }, menuOrder: 5)
                ]));

            registry.Register(new FakeRoiPlugin(
                typeKey: "plugin-a",
                roiType: typeof(long),
                drawingTools:
                [
                    new RoiToolDescriptor("Gamma", static _ => { }, menuOrder: 10)
                ]));

            string[] headers = registry.GetDrawingTools().Select(static tool => tool.Header).ToArray();
            string[] expectedHeaders = ["Beta", "Alpha", "Gamma", "Zulu"];

            Assert.Equal(expectedHeaders, headers);
        }

        [Fact]
        public void BuiltInMeasurementToolsUsePracticalOrder()
        {
            RoiPluginRegistry registry = RoiPluginRegistry.CreateBuiltIn();

            string[] actual = registry.GetDrawingTools()
                .Where(static tool => tool.IsMeasurement)
                .Select(static tool => tool.Header)
                .ToArray();
            string[] expected =
            [
                UiText.Get("ToolPointCoordinate"),
                UiText.Get("ToolBlobAnalysis"),
                UiText.Get("ToolLineMeasure"),
                UiText.Get("ToolCaliperMeasure"),
                UiText.Get("ToolAngleMeasure"),
                UiText.Get("ToolArcMeasure"),
                UiText.Get("ToolAreaMeasure"),
                UiText.Get("ToolPolylineMeasure"),
                UiText.Get("ToolThreePointCircle"),
                UiText.Get("ToolFittedEllipse"),
                UiText.Get("ToolLineCaliper"),
                UiText.Get("ToolCircularCaliper"),
                UiText.Get("ToolArcCaliper"),
                UiText.Get("ToolAutomaticCircle"),
                UiText.Get("ToolAutomaticEdgePoint"),
                UiText.Get("ToolPointToLineDistance"),
                UiText.Get("ToolPointToCircleDistance"),
                UiText.Get("ToolParallelism"),
                UiText.Get("ToolPerpendicularity"),
                UiText.Get("ToolConcentricity"),
                UiText.Get("ToolCenterDistance")
            ];

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void GetDrawingTools_UsesVisibilityMetadataInsteadOfHeaderText()
        {
            var registry = new RoiPluginRegistry();
            registry.Register(new FakeRoiPlugin(
                typeKey: "plugin-visibility",
                roiType: typeof(decimal),
                drawingTools:
                [
                    new RoiToolDescriptor("第三方菱形", static _ => { }),
                    new RoiToolDescriptor("隐藏工具", static _ => { }, isVisible: false)
                ]));

            string[] headers = registry.GetDrawingTools().Select(static tool => tool.Header).ToArray();

            string[] expectedHeaders = ["第三方菱形"];
            Assert.Equal(expectedHeaders, headers);
        }

        [Fact]
        public void Register_AllToolsHidden_StillRegistersPlugin()
        {
            var registry = new RoiPluginRegistry();
            var plugin = new FakeRoiPlugin(
                typeKey: "hidden-tools-plugin",
                roiType: typeof(float),
                drawingTools:
                [
                    new RoiToolDescriptor("隐藏工具", static _ => { }, isVisible: false)
                ]);

            registry.Register(plugin);

            IRoiPlugin? registeredPlugin = registry.FindByTypeKey("hidden-tools-plugin");

            Assert.NotNull(registeredPlugin);
            Assert.Equal(typeof(float), registeredPlugin!.RoiType);
            Assert.Empty(registeredPlugin.DrawingTools);
            Assert.Empty(registry.GetDrawingTools());
        }

        private sealed class FakeRoiPlugin : IRoiPlugin
        {
            public FakeRoiPlugin(string typeKey, Type roiType, IReadOnlyList<RoiToolDescriptor> drawingTools)
            {
                TypeKey = typeKey;
                RoiType = roiType;
                DrawingTools = drawingTools;
            }

            public string TypeKey { get; }

            public Type RoiType { get; }

            public int HitTestOrder => 0;

            public IReadOnlyList<RoiToolDescriptor> DrawingTools { get; }

            public ImageViewer.Abstractions.IRoiBehavior Behavior => throw new NotSupportedException();

            public ImageViewer.Rendering.IRoiRenderer Renderer => throw new NotSupportedException();

            public IEnumerable<RoiBase> GetRois(ImageViewer.ViewModels.ImageViewerViewModel viewModel) => throw new NotSupportedException();

            public void ClearCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel) => throw new NotSupportedException();

            public bool AddToCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel, RoiBase roi) => throw new NotSupportedException();

            public bool RemoveFromCollection(ImageViewer.ViewModels.ImageViewerViewModel viewModel, RoiBase roi) => throw new NotSupportedException();

            public RoiBase CreateRoi(RoiPersistenceData data) => throw new NotSupportedException();

            public void PopulatePersistenceData(RoiBase roi, RoiPersistenceData data) => throw new NotSupportedException();

            public IReadOnlyList<string> BuildInfoLines(RoiBase roi, System.Windows.Media.Imaging.BitmapSource? bitmap, double pixelSize, string? physicalUnit)
                => throw new NotSupportedException();

            public FrameworkElement? CreatePropertyEditor(RoiBase roi) => throw new NotSupportedException();
        }

        public sealed record RoiRegistrationExpectation(string TypeKey, Type RoiType);

        public sealed record RoiPersistenceRoundTripExpectation(
            string TypeKey,
            RoiBase SourceRoi,
            Action<RoiPersistenceData> AssertPersistenceData,
            Action<RoiBase> AssertRoundTripped);
    }
}
