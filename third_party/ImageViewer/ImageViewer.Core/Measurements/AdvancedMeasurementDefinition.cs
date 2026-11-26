using ImageViewer.Models;

namespace ImageViewer.Core.Measurements;

#pragma warning disable CS1591
/// <summary>Portable definition of an associative two-dimensional measurement.</summary>
public sealed record AdvancedMeasurementDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public AdvancedMeasurementKind Kind { get; init; }
    // IDs can refer to source ROIs or to other definitions (constructed geometry).
    public Guid[] Sources { get; init; } = [];
    public double Parameter { get; init; } = 1;
    public double NominalX { get; init; }
    public double NominalY { get; init; }
    public bool NominalInWorkpieceCoordinates { get; init; }
    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
    public AdvancedMeasurementDefinition DeepClone() => this with { Sources = Sources is null ? [] : [.. Sources] };
}

/// <summary>Measurement operations available to the analysis workbench.</summary>
public enum AdvancedMeasurementKind
{
    Midpoint, Intersection, Projection, Centerline, AngleBisector,
    ShortestDistance, ProjectedDistance, Straightness, Circularity,
    Rectangle, Slot, GapProfile, Position, Pitch, DistributionCircle,
    ContourDeviation, ShapeMetrics, PeriodicPitch
}

/// <summary>Named metric; power is zero for ratios/angles, one for lengths, two for areas.</summary>
public sealed record AdvancedMeasurementMetric(string Name, double Value, int LengthPower = 1, string? Unit = null);

/// <summary>Live evaluation with witness geometry and diagnostic details.</summary>
public sealed record AdvancedMeasurementEvaluation
{
    public Guid Id { get; init; }
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public AdvancedMeasurementMetric[] Metrics { get; init; } = [];
    public PointD[] Geometry { get; init; } = [];
    public PointD[][] SourceContours { get; init; } = [];
    public double[] Samples { get; init; } = [];
    public double[] Stations { get; init; } = [];
    public bool? WithinLimits { get; init; }
    /// <summary>Non-fatal defects that affect how much the reported values can be trusted.</summary>
    public string[] Warnings { get; init; } = [];
}
#pragma warning restore CS1591
