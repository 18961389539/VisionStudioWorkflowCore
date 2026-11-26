using System;
using System.Collections.Generic;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Plugins
{
    public sealed partial class RoiPluginRegistry
    {
        private static class MeasurementCatalog
        {
            public static IReadOnlyList<IBuiltInPluginRegistration> GetRegistrations()
            {
                return
                [
                    CreateRegistration<PointCoordinateMeasureRoi>(
                    typeKey: "point-coordinate-measure",
                    hitTestOrder: 41,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolPointCoordinate"), BuiltInDrawControllers.PointCoordinate, 10, CreatePointCoordinateIcon, isMeasurement: true),
                        new RoiToolDescriptor(UiText.Get("ToolAutomaticEdgePoint"), BuiltInDrawControllers.AutomaticEdgePoint, 95, CreateAutomaticEdgePointIcon, isMeasurement: true)
                    ],
                    persistence: CreatePositionPersistence(
                        data => new PointCoordinateMeasureRoi
                        {
                            Position = data.Geometry.Position.ToPoint(),
                            EdgeScore = data.Measurement.EdgeScore,
                            EdgeConfidence = data.Measurement.EdgeConfidence,
                            IsEdgeSnapped = data.Measurement.IsEdgeSnapped
                        },
                        static roi => roi.Position,
                        static (roi, data) =>
                        {
                            data.Measurement.EdgeScore = roi.EdgeScore;
                            data.Measurement.EdgeConfidence = roi.EdgeConfidence;
                            data.Measurement.IsEdgeSnapped = roi.IsEdgeSnapped;
                        })),

                    CreateRegistration<CircularCaliperMeasureRoi>(
                    typeKey: "circular-caliper-measure",
                    hitTestOrder: 78,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolAutomaticCircle"), BuiltInDrawControllers.AutomaticCircle, 90, CreateCircularCaliperMeasureIcon, isMeasurement: true),
                        new RoiToolDescriptor(UiText.Get("ToolCircularCaliper"), BuiltInDrawControllers.CircularCaliper, 81, CreateCircularCaliperMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreateCenterRadiusPersistence(
                        data => new CircularCaliperMeasureRoi
                        {
                            Center = data.Geometry.Center.ToPoint(),
                            Radius = data.Geometry.Radius,
                            CaliperCount = data.Measurement.CaliperCount > 0 ? data.Measurement.CaliperCount : 16,
                            CaliperSearchRange = data.Measurement.CaliperSearchRange > 0 ? data.Measurement.CaliperSearchRange : 18,
                            CaliperSamplingHalfWidth = data.Measurement.CaliperSamplingHalfWidth,
                            CaliperEdgeSigma = data.Measurement.CaliperEdgeSigma > 0 ? data.Measurement.CaliperEdgeSigma : 1.0,
                            MinimumValidCalipers = data.Measurement.MinimumValidCalipers > 0 ? data.Measurement.MinimumValidCalipers : 8,
                            CaliperMinimumGradient = data.Measurement.CaliperMinimumGradient > 0 ? data.Measurement.CaliperMinimumGradient : 8,
                            CaliperOutlierThreshold = data.Measurement.CaliperOutlierThreshold > 0 ? data.Measurement.CaliperOutlierThreshold : 2.5,
                            CaliperEdgePolarity = ParseCaliperEdgePolarity(data.Measurement.CaliperEdgePolarity),
                            CaliperLineFitMode = ParseCaliperLineFitMode(data.Measurement.CaliperLineFitMode),
                            CaliperFitClippingEndPoints = Math.Max(0, data.Measurement.CaliperFitClippingEndPoints),
                            EdgeSelection = Math.Max(1, data.Measurement.EdgeSelection)
                        },
                        static roi => roi.Center,
                        static roi => roi.Radius,
                        static (roi, data) =>
                        {
                            PopulateCaliperPersistenceData(
                                data,
                                roi.CaliperCount,
                                roi.CaliperSearchRange,
                                roi.CaliperSamplingHalfWidth,
                                roi.CaliperEdgeSigma,
                                roi.MinimumValidCalipers,
                                roi.CaliperMinimumGradient,
                                roi.CaliperOutlierThreshold,
                                roi.CaliperEdgePolarity,
                                roi.CaliperLineFitMode,
                                roi.CaliperFitClippingEndPoints);
                            data.Measurement.EdgeSelection = roi.EdgeSelection;
                        })),

                            CreateRegistration<ArcCaliperMeasureRoi>(
                    typeKey: "arc-caliper-measure",
                    hitTestOrder: 77,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolArcCaliper"), BuiltInDrawControllers.ArcCaliper, 82, CreateArcCaliperMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreateCenterRadiusPersistence(
                        data => new ArcCaliperMeasureRoi
                        {
                            Center = data.Geometry.Center.ToPoint(),
                            Radius = data.Geometry.Radius,
                            StartAngle = data.Geometry.Angle,
                            SweepAngle = data.Geometry.Height == 0 ? 180 : data.Geometry.Height,
                            CaliperCount = data.Measurement.CaliperCount > 0 ? data.Measurement.CaliperCount : 16,
                            CaliperSearchRange = data.Measurement.CaliperSearchRange > 0 ? data.Measurement.CaliperSearchRange : 18,
                            CaliperSamplingHalfWidth = data.Measurement.CaliperSamplingHalfWidth,
                            CaliperEdgeSigma = data.Measurement.CaliperEdgeSigma > 0 ? data.Measurement.CaliperEdgeSigma : 1.0,
                            MinimumValidCalipers = data.Measurement.MinimumValidCalipers > 0 ? data.Measurement.MinimumValidCalipers : 8,
                            CaliperMinimumGradient = data.Measurement.CaliperMinimumGradient > 0 ? data.Measurement.CaliperMinimumGradient : 8,
                            CaliperOutlierThreshold = data.Measurement.CaliperOutlierThreshold > 0 ? data.Measurement.CaliperOutlierThreshold : 2.5,
                            CaliperEdgePolarity = ParseCaliperEdgePolarity(data.Measurement.CaliperEdgePolarity),
                            CaliperLineFitMode = ParseCaliperLineFitMode(data.Measurement.CaliperLineFitMode),
                            CaliperFitClippingEndPoints = Math.Max(0, data.Measurement.CaliperFitClippingEndPoints),
                            EdgeSelection = Math.Max(1, data.Measurement.EdgeSelection)
                        },
                        static roi => roi.Center,
                        static roi => roi.Radius,
                        static (roi, data) =>
                        {
                            data.Geometry.Angle = roi.StartAngle;
                            data.Geometry.Height = roi.SweepAngle;
                            PopulateCaliperPersistenceData(
                                data,
                                roi.CaliperCount,
                                roi.CaliperSearchRange,
                                 roi.CaliperSamplingHalfWidth,
                                 roi.CaliperEdgeSigma,
                                roi.MinimumValidCalipers,
                                 roi.CaliperMinimumGradient,
                                 roi.CaliperOutlierThreshold,
                                roi.CaliperEdgePolarity,
                                roi.CaliperLineFitMode,
                                roi.CaliperFitClippingEndPoints);
                            data.Measurement.EdgeSelection = roi.EdgeSelection;
                        })),

                    CreateRegistration<LineMeasureRoi>(
                    typeKey: "line-measure",
                    hitTestOrder: 20,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolLineMeasure"), BuiltInDrawControllers.LineMeasure, 20, CreateLineMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new LineMeasureRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint()
                        },
                        static roi => roi.P1,
                        static roi => roi.P2)),

                    CreateRegistration<CaliperMeasureRoi>(
                    typeKey: "caliper-measure",
                    hitTestOrder: 25,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolCaliperMeasure"), BuiltInDrawControllers.CaliperMeasure, 30, CreateCaliperMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new CaliperMeasureRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint(),
                            CaliperCenter = data.Geometry.Center.ToPoint(),
                            CaliperRegionLength = data.Geometry.Width,
                            CaliperSearchRange = data.Geometry.Height > 0 ? (int)Math.Round(data.Geometry.Height / 2) : 24,
                            CaliperAngleDegrees = data.Geometry.Angle,
                            CaliperEdgeSigma = data.Measurement.CaliperEdgeSigma > 0 ? data.Measurement.CaliperEdgeSigma : 1.0,
                            CaliperLineFitMode = ParseCaliperLineFitMode(data.Measurement.CaliperLineFitMode),
                            CaliperFitClippingEndPoints = Math.Max(0, data.Measurement.CaliperFitClippingEndPoints),
                            HasExplicitCaliperRegion = data.Geometry.Center != null,
                            MinimumEdgeGap = data.Measurement.MinimumEdgeGap,
                            NominalEdgeGap = data.Measurement.NominalEdgeGap,
                            NominalEdgeGapTolerance = data.Measurement.NominalEdgeGapTolerance
                        },
                        static roi => roi.P1,
                        static roi => roi.P2,
                        static (roi, data) =>
                        {
                            data.Geometry.Center = RoiPersistencePointExtensions.FromPoint(roi.CaliperCenter);
                            data.Geometry.Width = roi.GetResolvedCaliperRegionLength();
                            data.Geometry.Height = roi.CaliperSearchRange * 2;
                            data.Geometry.Angle = roi.CaliperAngleDegrees;
                            data.Measurement.CaliperEdgeSigma = roi.CaliperEdgeSigma;
                            data.Measurement.CaliperLineFitMode = roi.CaliperLineFitMode.ToString();
                            data.Measurement.CaliperFitClippingEndPoints = roi.CaliperFitClippingEndPoints;
                            data.Measurement.MinimumEdgeGap = roi.MinimumEdgeGap;
                            data.Measurement.NominalEdgeGap = roi.NominalEdgeGap;
                            data.Measurement.NominalEdgeGapTolerance = roi.NominalEdgeGapTolerance;
                        })),

                    CreateRegistration<LineCaliperMeasureRoi>(
                    typeKey: "line-caliper-measure",
                    hitTestOrder: 24,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolLineCaliper"), BuiltInDrawControllers.LineCaliper, 80, CreateLineCaliperMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new LineCaliperMeasureRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint(),
                            CaliperCount = data.Measurement.CaliperCount > 0 ? data.Measurement.CaliperCount : 16,
                            CaliperSearchRange = data.Measurement.CaliperSearchRange > 0 ? data.Measurement.CaliperSearchRange : 18,
                            CaliperSamplingHalfWidth = data.Measurement.CaliperSamplingHalfWidth,
                            CaliperEdgeSigma = data.Measurement.CaliperEdgeSigma > 0 ? data.Measurement.CaliperEdgeSigma : 1.0,
                            MinimumValidCalipers = data.Measurement.MinimumValidCalipers > 0 ? data.Measurement.MinimumValidCalipers : 8,
                            CaliperMinimumGradient = data.Measurement.CaliperMinimumGradient > 0 ? data.Measurement.CaliperMinimumGradient : 8,
                            CaliperOutlierThreshold = data.Measurement.CaliperOutlierThreshold > 0 ? data.Measurement.CaliperOutlierThreshold : 2.5,
                            CaliperEdgePolarity = ParseCaliperEdgePolarity(data.Measurement.CaliperEdgePolarity),
                            CaliperLineFitMode = ParseCaliperLineFitMode(data.Measurement.CaliperLineFitMode),
                            CaliperFitClippingEndPoints = Math.Max(0, data.Measurement.CaliperFitClippingEndPoints),
                            EdgeSelection = Math.Max(1, data.Measurement.EdgeSelection)
                        },
                        static roi => roi.P1,
                        static roi => roi.P2,
                        static (roi, data) =>
                        {
                            PopulateCaliperPersistenceData(
                                data,
                                roi.CaliperCount,
                                roi.CaliperSearchRange,
                                roi.CaliperSamplingHalfWidth,
                                roi.CaliperEdgeSigma,
                                roi.MinimumValidCalipers,
                                roi.CaliperMinimumGradient,
                                roi.CaliperOutlierThreshold,
                                roi.CaliperEdgePolarity,
                                roi.CaliperLineFitMode,
                                roi.CaliperFitClippingEndPoints);
                            data.Measurement.EdgeSelection = roi.EdgeSelection;
                        })),

                    CreateRegistration<AngleMeasureRoi>(
                    typeKey: "angle-measure",
                    hitTestOrder: 10,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolAngleMeasure"), BuiltInDrawControllers.AngleMeasure, 40, CreateAngleMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointTriplePersistence(
                        data => new AngleMeasureRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            Vertex = data.Geometry.Vertex.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint()
                        },
                        static roi => roi.P1,
                        static roi => roi.P2,
                        static roi => roi.Vertex)),

                    CreateRegistration<ArcMeasureRoi>(
                    typeKey: "arc-measure",
                    hitTestOrder: 8,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolArcMeasure"), BuiltInDrawControllers.ArcMeasure, 50, CreateArcMeasureIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointTriplePersistence(
                        data => new ArcMeasureRoi
                        {
                            StartPoint = data.Geometry.P1.ToPoint(),
                            EndPoint = data.Geometry.P2.ToPoint(),
                            ArcPoint = data.Geometry.Vertex.ToPoint()
                        },
                        static roi => roi.StartPoint,
                        static roi => roi.EndPoint,
                        static roi => roi.ArcPoint)),

                    CreateRegistration<ThreePointCircleMeasureRoi>(
                    typeKey: "three-point-circle",
                    hitTestOrder: 9,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolThreePointCircle"), BuiltInDrawControllers.ThreePointCircle, 70, CreateThreePointCircleIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointTriplePersistence(
                        data => new ThreePointCircleMeasureRoi
                        {
                            P1 = data.Geometry.P1.ToPoint(),
                            P2 = data.Geometry.P2.ToPoint(),
                            P3 = data.Geometry.Vertex.ToPoint()
                        },
                        static roi => roi.P1,
                        static roi => roi.P2,
                        static roi => roi.P3)),

                    CreateRegistration<PointToLineDistanceRoi>(
                    typeKey: "point-to-line-distance",
                    hitTestOrder: 7,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolPointToLineDistance"), BuiltInDrawControllers.PointToLineDistance, 100, CreatePointToLineIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointTriplePersistence(
                        data => new PointToLineDistanceRoi
                        {
                            Point = data.Geometry.P1.ToPoint(),
                            LineP1 = data.Geometry.P2.ToPoint(),
                            LineP2 = data.Geometry.Vertex.ToPoint()
                        },
                        static roi => roi.Point,
                        static roi => roi.LineP1,
                        static roi => roi.LineP2)),

                    CreateRegistration<PointToCircleDistanceRoi>(
                    typeKey: "point-to-circle-distance",
                    hitTestOrder: 6,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolPointToCircleDistance"), BuiltInDrawControllers.PointToCircleDistance, 101, CreatePointToCircleIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new PointToCircleDistanceRoi
                        {
                            Point = data.Geometry.P1.ToPoint(),
                            Center = data.Geometry.P2.ToPoint(),
                            Radius = data.Geometry.Radius
                        },
                        static roi => roi.Point,
                        static roi => roi.Center,
                        static (roi, data) => data.Geometry.Radius = roi.Radius)),

                    CreateRegistration<ParallelismMeasureRoi>(
                    typeKey: "parallelism-measure",
                    hitTestOrder: 5,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolParallelism"), BuiltInDrawControllers.Parallelism, 110, CreateParallelismIcon, isMeasurement: true)
                    ],
                    persistence: CreateLinePairPersistence(
                        data => new ParallelismMeasureRoi
                        {
                            Line1P1 = data.Geometry.P1.ToPoint(),
                            Line1P2 = data.Geometry.P2.ToPoint(),
                            Line2P1 = data.Geometry.Vertex.ToPoint(),
                            Line2P2 = data.Geometry.P3.ToPoint()
                        },
                        static roi => roi.Line1P1,
                        static roi => roi.Line1P2,
                        static roi => roi.Line2P1,
                        static roi => roi.Line2P2)),

                    CreateRegistration<PerpendicularityMeasureRoi>(
                    typeKey: "perpendicularity-measure",
                    hitTestOrder: 4,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolPerpendicularity"), BuiltInDrawControllers.Perpendicularity, 111, CreatePerpendicularityIcon, isMeasurement: true)
                    ],
                    persistence: CreateLinePairPersistence(
                        data => new PerpendicularityMeasureRoi
                        {
                            Line1P1 = data.Geometry.P1.ToPoint(),
                            Line1P2 = data.Geometry.P2.ToPoint(),
                            Line2P1 = data.Geometry.Vertex.ToPoint(),
                            Line2P2 = data.Geometry.P3.ToPoint()
                        },
                        static roi => roi.Line1P1,
                        static roi => roi.Line1P2,
                        static roi => roi.Line2P1,
                        static roi => roi.Line2P2)),

                    CreateRegistration<ConcentricityMeasureRoi>(
                    typeKey: "concentricity-measure",
                    hitTestOrder: 3,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolConcentricity"), BuiltInDrawControllers.Concentricity, 112, CreateConcentricityIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairRadiusPairPersistence(
                        data => new ConcentricityMeasureRoi
                        {
                            Center1 = data.Geometry.P1.ToPoint(),
                            Radius1 = data.Geometry.Radius,
                            Center2 = data.Geometry.P2.ToPoint(),
                            Radius2 = data.Geometry.Radius2
                        },
                        static roi => roi.Center1,
                        static roi => roi.Center2,
                        static roi => roi.Radius1,
                        static roi => roi.Radius2)),

                    CreateRegistration<CenterDistanceMeasureRoi>(
                    typeKey: "center-distance-measure",
                    hitTestOrder: 2,
                    drawingTools:
                    [
                        new RoiToolDescriptor(UiText.Get("ToolCenterDistance"), BuiltInDrawControllers.CenterDistance, 113, CreateCenterDistanceIcon, isMeasurement: true)
                    ],
                    persistence: CreatePointPairPersistence(
                        data => new CenterDistanceMeasureRoi
                        {
                            Center1 = data.Geometry.P1.ToPoint(),
                            Center2 = data.Geometry.P2.ToPoint()
                        },
                        static roi => roi.Center1,
                        static roi => roi.Center2))
                ];
            }
        }
    }
}
