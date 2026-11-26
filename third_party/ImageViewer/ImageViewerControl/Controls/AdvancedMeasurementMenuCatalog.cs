using ImageViewer.Core.Measurements;
using ImageViewer.Models;

namespace ImageViewer.Controls;

internal sealed record AdvancedMeasurementMenuDescriptor(AdvancedMeasurementKind Kind, string HeaderKey, string GroupKey, string AliasesKey);
internal sealed record MeasurementMenuAvailability(bool IsEnabled, string? ReasonKey);

/// <summary>Shared menu grouping, discovery aliases and prerequisites for advanced operations.</summary>
internal static class AdvancedMeasurementMenuCatalog
{
    public static IReadOnlyList<AdvancedMeasurementMenuDescriptor> All { get; } = Enum.GetValues<AdvancedMeasurementKind>()
        .Select(kind => new AdvancedMeasurementMenuDescriptor(kind, "AdvancedMeasurementOperation" + kind, GroupKey(kind), "MenuAliases" + kind)).ToArray();

    public static AdvancedMeasurementMenuDescriptor Get(AdvancedMeasurementKind kind) => All.Single(item => item.Kind == kind);

    private static string GroupKey(AdvancedMeasurementKind kind) => kind switch
    {
        AdvancedMeasurementKind.Midpoint or AdvancedMeasurementKind.Intersection or AdvancedMeasurementKind.Projection or
        AdvancedMeasurementKind.Centerline or AdvancedMeasurementKind.AngleBisector or AdvancedMeasurementKind.ShortestDistance or
        AdvancedMeasurementKind.ProjectedDistance => "MenuMeasurementGeometry",
        AdvancedMeasurementKind.Position or AdvancedMeasurementKind.Pitch or AdvancedMeasurementKind.DistributionCircle or
        AdvancedMeasurementKind.PeriodicPitch => "MenuMeasurementArray",
        _ => "MenuMeasurementShape"
    };

    public static MeasurementMenuAvailability GetAvailability(AdvancedMeasurementKind kind, IReadOnlyList<RoiBase> rois, bool hasBitmap, RoiBase? selected = null)
    {
        ArgumentNullException.ThrowIfNull(rois);
        bool hasDerived = rois.Any(roi => roi.AdvancedMeasurements.Length > 0);
        int points = rois.Count(IsPointSource), lines = rois.Count(IsLineSource), paths = rois.Count(IsPathSource);
        bool formLine = rois.Any(roi => roi is LineCaliperMeasureRoi { HasDetectedLine: true, EdgePoints.Length: >= 3 }
            or PolylineRoi { Points.Count: >= 3 } or PolygonRoi { IsClosed: true, Points.Count: >= 3 });
        bool formCircle = rois.Any(roi => roi is CircularCaliperMeasureRoi { HasDetectedEdges: true, EdgePoints.Length: >= 3, QualityStatus: CircularCaliperQualityStatus.Passed or CircularCaliperQualityStatus.Review }
            or PolylineRoi { Points.Count: >= 3 } or PolygonRoi { IsClosed: true, Points.Count: >= 3 });
        bool shape = rois.Any(roi => roi is PolygonRoi { IsClosed: true, Points.Count: >= 3 } || hasBitmap && roi is RotatedRect);
        (bool enabled, string reason) = kind switch
        {
            AdvancedMeasurementKind.Midpoint => (points >= 2 || hasDerived, "MenuNeedTwoPoints"),
            AdvancedMeasurementKind.Intersection or AdvancedMeasurementKind.Centerline => (lines >= 2 || hasDerived, "MenuNeedTwoLines"),
            AdvancedMeasurementKind.Projection => (points >= 1 && lines >= 1 || hasDerived, "MenuNeedPointAndLine"),
            AdvancedMeasurementKind.AngleBisector => (points >= 3 || hasDerived, "MenuNeedThreePoints"),
            AdvancedMeasurementKind.ShortestDistance => (rois.Count(IsBoundarySource) >= 2 || hasDerived, "MenuNeedTwoBoundaries"),
            AdvancedMeasurementKind.ProjectedDistance => (points >= 2 || hasDerived, "MenuNeedTwoPoints"),
            AdvancedMeasurementKind.Straightness => (formLine || hasDerived, "MenuNeedLineProfile"),
            AdvancedMeasurementKind.Circularity => (formCircle || hasDerived, "MenuNeedCircleProfile"),
            AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.Slot or AdvancedMeasurementKind.ShapeMetrics => (shape || hasDerived, "MenuNeedClosedContour"),
            AdvancedMeasurementKind.GapProfile or AdvancedMeasurementKind.ContourDeviation => (paths >= 2 || hasDerived, "MenuNeedTwoPaths"),
            AdvancedMeasurementKind.Position => (points >= 1 || hasDerived, "MenuNeedPoint"),
            AdvancedMeasurementKind.Pitch => (points >= 2 || hasDerived, "MenuNeedTwoPoints"),
            AdvancedMeasurementKind.DistributionCircle => (points >= 3 || hasDerived, "MenuNeedThreePoints"),
            AdvancedMeasurementKind.PeriodicPitch => (hasBitmap && rois.Any(roi => roi.GetType() == typeof(LineMeasureRoi)), hasBitmap ? "MenuNeedScanLine" : "MenuNeedImage"),
            _ => (false, "MenuUnavailable")
        };
        return new(enabled, enabled ? null : reason);
    }

    public static bool IsPointSource(RoiBase roi) => roi switch
    {
        CircularCaliperMeasureRoi circular => circular.HasDetectedEdges && circular.EdgePoints.Length >= 3
            && circular.QualityStatus is CircularCaliperQualityStatus.Passed or CircularCaliperQualityStatus.Review,
        ThreePointCircleMeasureRoi threePoint => threePoint.IsValid,
        CircleRoi circle => double.IsFinite(circle.Radius) && circle.Radius > 0,
        PointCoordinateMeasureRoi or PointAnnotationRoi => true,
        _ => false
    };
    public static bool IsLineSource(RoiBase roi) => roi switch
    {
        LineCaliperMeasureRoi line => line.HasDetectedLine,
        CaliperMeasureRoi caliper => caliper.HasDetectedEdges && caliper.WidthMeasurement.IsMeasured,
        LineMeasureRoi => true,
        _ => false
    };
    public static bool IsPathSource(RoiBase roi) => IsLineSource(roi) || roi is PolylineRoi or PolygonRoi;
    public static bool IsBoundarySource(RoiBase roi) => IsPathSource(roi) || IsPointSource(roi) && roi is CircleRoi or ThreePointCircleMeasureRoi;

    public static bool CanSeed(AdvancedMeasurementKind kind, RoiBase source, bool hasBitmap) => kind switch
    {
        AdvancedMeasurementKind.Midpoint or AdvancedMeasurementKind.AngleBisector or AdvancedMeasurementKind.ProjectedDistance or
        AdvancedMeasurementKind.Position or AdvancedMeasurementKind.Pitch or AdvancedMeasurementKind.DistributionCircle or
        AdvancedMeasurementKind.Projection => IsPointSource(source),
        AdvancedMeasurementKind.Intersection or AdvancedMeasurementKind.Centerline => IsLineSource(source),
        AdvancedMeasurementKind.ShortestDistance => IsBoundarySource(source),
        AdvancedMeasurementKind.GapProfile or AdvancedMeasurementKind.ContourDeviation => IsPathSource(source),
        AdvancedMeasurementKind.Straightness => source is LineCaliperMeasureRoi { HasDetectedLine: true, EdgePoints.Length: >= 3 }
            or PolylineRoi { Points.Count: >= 3 } or PolygonRoi { IsClosed: true, Points.Count: >= 3 },
        AdvancedMeasurementKind.Circularity => source is CircularCaliperMeasureRoi { HasDetectedEdges: true, EdgePoints.Length: >= 3, QualityStatus: CircularCaliperQualityStatus.Passed or CircularCaliperQualityStatus.Review }
            or PolylineRoi { Points.Count: >= 3 } or PolygonRoi { IsClosed: true, Points.Count: >= 3 },
        AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.Slot or AdvancedMeasurementKind.ShapeMetrics =>
            source is PolygonRoi { IsClosed: true, Points.Count: >= 3 } || hasBitmap && source is RotatedRect,
        AdvancedMeasurementKind.PeriodicPitch => hasBitmap && source.GetType() == typeof(LineMeasureRoi),
        _ => false
    };
}

internal sealed class ImageViewerAdvancedMeasurementMenuTag(AdvancedMeasurementKind kind, RoiBase? initialSource = null, string? hintKey = null) : ImageViewerMenuCommandTag
{
    public AdvancedMeasurementKind Kind { get; } = kind;
    public RoiBase? InitialSource { get; } = initialSource;
    public string? HintKey { get; } = hintKey;
    public AdvancedMeasurementMenuDescriptor Descriptor => AdvancedMeasurementMenuCatalog.Get(Kind);
}
