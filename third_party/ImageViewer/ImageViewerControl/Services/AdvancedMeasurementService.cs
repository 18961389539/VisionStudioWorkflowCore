using System.Windows.Media.Imaging;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Measurements;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>Evaluates associative measurements against current ROI geometry in calibrated coordinates.</summary>
public static class AdvancedMeasurementService
{
    private const int MaximumDefinitions = 256;
    private const int MaximumSourcesPerDefinition = 64;
    private const int MaximumRecursionDepth = 64;
    private const int MaximumPointsPerSource = 4096;
    private const string MethodPrefix = "AdvancedMeasurement.Method.";
    private const string MetricPrefix = "AdvancedMeasurement.Metric.";

    /// <summary>Returns all saved advanced measurement definitions in ROI order.</summary>
    public static IReadOnlyList<AdvancedMeasurementDefinition> EnumerateDefinitions(IReadOnlyList<RoiBase> rois)
    {
        ArgumentNullException.ThrowIfNull(rois);
        return Array.AsReadOnly(rois.Where(roi => roi is not null)
            .SelectMany(roi => roi.AdvancedMeasurements ?? [])
            .Where(definition => definition is not null)
            .Select(definition => definition.DeepClone()).ToArray());
    }

    /// <summary>Evaluates one definition, resolving ROI and constructed-measurement sources recursively.</summary>
    public static AdvancedMeasurementEvaluation Evaluate(
        AdvancedMeasurementDefinition definition,
        IReadOnlyList<RoiBase> rois,
        double pixelSize,
        string unit,
        CameraCalibration? calibration = null,
        BitmapSource? bitmap = null,
        WorkpieceCoordinateSystem? workpiece = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rois);
        if (GetInputError(pixelSize, unit, calibration) is { } error)
            return Failure(definition.Id, error);

        RoiBase[] roiList = rois.Where(roi => roi is not null).ToArray();
        var definitions = EnumerateDefinitions(roiList);
        if (definitions.Count > MaximumDefinitions)
            return Failure(definition.Id, TooManyDefinitionsError);

        return CreateContext(roiList, definitions, pixelSize, unit, calibration, bitmap, workpiece).Evaluate(definition);
    }

    /// <summary>Evaluates every saved definition using the current image and calibration.</summary>
    public static IReadOnlyList<AdvancedMeasurementEvaluation> EvaluateAll(
        IReadOnlyList<RoiBase> rois,
        double pixelSize,
        string unit,
        CameraCalibration? calibration = null,
        BitmapSource? bitmap = null,
        WorkpieceCoordinateSystem? workpiece = null)
    {
        ArgumentNullException.ThrowIfNull(rois);
        RoiBase[] roiList = rois.Where(roi => roi is not null).ToArray();
        var definitions = EnumerateDefinitions(roiList);
        if (definitions.Count == 0)
            return Array.Empty<AdvancedMeasurementEvaluation>();

        string? error = definitions.Count > MaximumDefinitions ? TooManyDefinitionsError : GetInputError(pixelSize, unit, calibration);
        if (error is not null)
            return Array.AsReadOnly(definitions.Select(definition => Failure(definition.Id, error)).ToArray());

        var context = CreateContext(roiList, definitions, pixelSize, unit, calibration, bitmap, workpiece);
        return Array.AsReadOnly(definitions.Select(context.Evaluate).ToArray());
    }

    private static string TooManyDefinitionsError => $"A workspace may contain at most {MaximumDefinitions} advanced measurement definitions.";

    private static string? GetInputError(double pixelSize, string unit, CameraCalibration? calibration)
    {
        if (!double.IsFinite(pixelSize) || pixelSize <= 0) return "Pixel size must be finite and greater than zero.";
        if (string.IsNullOrWhiteSpace(unit)) return "A physical unit is required.";
        if (calibration is { IsValid: false }) return "Camera calibration contains invalid parameters.";
        return null;
    }

    private static EvaluationContext CreateContext(RoiBase[] rois, IReadOnlyList<AdvancedMeasurementDefinition> definitions,
        double pixelSize, string unit, CameraCalibration? calibration, BitmapSource? bitmap, WorkpieceCoordinateSystem? workpiece)
    {
        var byRoiId = rois.Where(roi => roi.MeasurementId != Guid.Empty)
            .GroupBy(roi => roi.MeasurementId).ToDictionary(group => group.Key, group => group.ToArray());
        var byDefinitionId = definitions.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.ToArray());
        return new EvaluationContext(pixelSize, unit.Trim(), calibration, bitmap, workpiece, byRoiId, byDefinitionId);
    }

    private static AdvancedMeasurementEvaluation Failure(Guid id, string error) => new()
    {
        Id = id, Success = false, Error = error, Method = string.Empty,
        Metrics = [], Geometry = [], Samples = [], Stations = [], WithinLimits = null
    };

    private static AdvancedMeasurementEvaluation Success(
        AdvancedMeasurementDefinition definition, string method, IEnumerable<AdvancedMeasurementMetric> metrics,
        IEnumerable<PointD>? geometry = null, IEnumerable<double>? samples = null, IEnumerable<double>? stations = null)
    {
        AdvancedMeasurementMetric[] metricArray = metrics.ToArray();
        PointD[] geometryArray = geometry?.ToArray() ?? [];
        double[] sampleArray = samples?.ToArray() ?? [];
        double[] stationArray = stations?.ToArray() ?? [];
        if (metricArray.Any(metric => !double.IsFinite(metric.Value)) ||
            geometryArray.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) ||
            sampleArray.Any(value => !double.IsFinite(value)) || stationArray.Any(value => !double.IsFinite(value)))
            return Failure(definition.Id, "Evaluation produced non-finite measurements.");
        bool? within = null;
        if (definition.LowerLimit.HasValue || definition.UpperLimit.HasValue)
        {
            if (metricArray.Length == 0 ||
                definition.LowerLimit.HasValue && !double.IsFinite(definition.LowerLimit.Value) ||
                definition.UpperLimit.HasValue && !double.IsFinite(definition.UpperLimit.Value) ||
                definition.LowerLimit.HasValue && definition.UpperLimit.HasValue && definition.LowerLimit > definition.UpperLimit)
                return Failure(definition.Id, "Tolerance limits are invalid or no primary metric is available.");
            double value = metricArray[0].Value;
            within = (!definition.LowerLimit.HasValue || value >= definition.LowerLimit.Value) &&
                     (!definition.UpperLimit.HasValue || value <= definition.UpperLimit.Value);
        }
        return new AdvancedMeasurementEvaluation
        {
            Id = definition.Id, Success = true, Error = string.Empty, Method = method,
            Metrics = metricArray, Geometry = geometryArray,
            Samples = sampleArray, Stations = stationArray, WithinLimits = within
        };
    }

    private static AdvancedMeasurementMetric Length(string key, double value, string unit, int power = 1) =>
        new(MetricPrefix + key, value, power, power == 1 ? unit : power == 2 ? unit + "²" : unit);

    private static AdvancedMeasurementMetric Scalar(string key, double value, string? unit = null) =>
        new(MetricPrefix + key, value, 0, unit);

    private sealed class EvaluationContext(
        double pixelSize,
        string unit,
        CameraCalibration? calibration,
        BitmapSource? bitmap,
        WorkpieceCoordinateSystem? workpiece,
        Dictionary<Guid, RoiBase[]> byRoiId,
        Dictionary<Guid, AdvancedMeasurementDefinition[]> byDefinitionId)
    {
        private readonly List<double> _undistortResiduals = [];
        private readonly HashSet<Guid> _activeDefinitions = [];
        private readonly Dictionary<Guid, (AdvancedMeasurementEvaluation Result, int Depth)> _successfulEvaluations = [];

        public AdvancedMeasurementEvaluation Evaluate(AdvancedMeasurementDefinition definition) => Evaluate(definition, out _);

        private AdvancedMeasurementEvaluation Evaluate(AdvancedMeasurementDefinition definition, out int depth)
        {
            depth = 1;
            if (definition.Id == Guid.Empty) return Failure(definition.Id, "Measurement definition ID is empty.");
            int ancestorCount = _activeDefinitions.Count;
            if (ancestorCount >= MaximumRecursionDepth) return Failure(definition.Id, $"Constructed measurement nesting is limited to {MaximumRecursionDepth} levels.");
            if (!_activeDefinitions.Add(definition.Id)) return Failure(definition.Id, "A cycle was found in constructed measurement sources.");
            int residualCountAtStart = _undistortResiduals.Count;
            try
            {
                // A valid result can still be too deep when used as a child of another definition.
                if (_successfulEvaluations.TryGetValue(definition.Id, out var cached) && ancestorCount + cached.Depth <= MaximumRecursionDepth)
                {
                    depth = cached.Depth;
                    return cached.Result;
                }

                if (definition.Sources is null) return Failure(definition.Id, "Source ID list is missing.");
                if (definition.Sources.Length > MaximumSourcesPerDefinition)
                    return Failure(definition.Id, $"A definition may contain at most {MaximumSourcesPerDefinition} sources.");
                if (byDefinitionId.TryGetValue(definition.Id, out AdvancedMeasurementDefinition[]? sameId) && sameId.Length > 1)
                    return Failure(definition.Id, "A measurement definition ID is duplicated.");
                if (definition.Sources.Distinct().Count() != definition.Sources.Length)
                    return Failure(definition.Id, "A source ID is duplicated.");
                var sources = new List<ResolvedSource>(definition.Sources.Length);
                foreach (Guid sourceId in definition.Sources)
                {
                    if (sourceId == Guid.Empty) return Failure(definition.Id, "A source ID is empty.");
                    if (byRoiId.ContainsKey(sourceId) && byDefinitionId.ContainsKey(sourceId))
                        return Failure(definition.Id, "A source ID ambiguously matches both an ROI and a constructed measurement.");
                    if (byRoiId.TryGetValue(sourceId, out RoiBase[]? roiMatches))
                    {
                        if (roiMatches.Length != 1) return Failure(definition.Id, "A source ROI ID is duplicated.");
                        (ResolvedSource? source, string? error) = FromRoi(roiMatches[0], definition.Kind);
                        if (source is null) return Failure(definition.Id, error ?? "The source ROI has no usable geometry.");
                        sources.Add(source);
                    }
                    else if (byDefinitionId.TryGetValue(sourceId, out AdvancedMeasurementDefinition[]? definitionMatches))
                    {
                        if (definitionMatches.Length != 1) return Failure(definition.Id, "A constructed source ID is duplicated.");
                        AdvancedMeasurementEvaluation nested = Evaluate(definitionMatches[0], out int nestedDepth);
                        if (!nested.Success)
                            return Failure(definition.Id, $"Constructed source {sourceId} failed: {nested.Error}") with { Warnings = [.. nested.Warnings] };
                        depth = Math.Max(depth, nestedDepth + 1);
                        sources.Add(ResolvedSource.FromEvaluation(definitionMatches[0].Kind, nested));
                    }
                    else return Failure(definition.Id, $"Source {sourceId} was not found.");
                }

                if (_undistortResiduals.Count > residualCountAtStart)
                {
                    return DistortionFailure(definition.Id, residualCountAtStart);
                }

                AdvancedMeasurementEvaluation evaluation = EvaluateKind(definition, sources);
                if (_undistortResiduals.Count > residualCountAtStart)
                {
                    return DistortionFailure(definition.Id, residualCountAtStart);
                }
                PointD[][] contours = sources.Where(source => source.WasRasterExtracted && source.Contour is not null)
                    .SelectMany(source => new[] { Close(source.Contour!) }.Concat(source.Holes.Select(Close)))
                    .ToArray();
                if (contours.Length > 0) evaluation = evaluation with { SourceContours = contours };
                if (evaluation.Success) _successfulEvaluations[definition.Id] = (evaluation, depth);
                return evaluation;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ArithmeticException)
            {
                return Failure(definition.Id, exception.Message);
            }
            finally
            {
                _undistortResiduals.RemoveRange(residualCountAtStart, _undistortResiduals.Count - residualCountAtStart);
                _activeDefinitions.Remove(definition.Id);
            }
        }

        private (ResolvedSource? Source, string? Error) FromRoi(RoiBase roi, AdvancedMeasurementKind requestedKind)
        {
            switch (roi)
            {
                case LineCaliperMeasureRoi lineCaliper:
                    if (!lineCaliper.HasDetectedLine) return (null, "Line caliper has no successful detected measurement.");
                    return (new ResolvedSource
                    {
                        Points = Convert(lineCaliper.EdgePoints),
                        Line = [Convert(lineCaliper.MeasurementP1), Convert(lineCaliper.MeasurementP2)],
                    Polyline = [Convert(lineCaliper.MeasurementP1), Convert(lineCaliper.MeasurementP2)],
                        ProfilePoints = Convert(lineCaliper.EdgePoints)
                    }, null);
                case CaliperMeasureRoi edgeCaliper:
                    if (!edgeCaliper.HasDetectedEdges || !edgeCaliper.WidthMeasurement.IsMeasured)
                        return (null, "Dual-edge caliper has no successful measured detection.");
                    return (new ResolvedSource
                    {
                        Points = [Convert(edgeCaliper.MeasurementP1), Convert(edgeCaliper.MeasurementP2)],
                        Line = [Convert(edgeCaliper.MeasurementP1), Convert(edgeCaliper.MeasurementP2)],
                        Polyline = [Convert(edgeCaliper.MeasurementP1), Convert(edgeCaliper.MeasurementP2)]
                    }, null);
                case CircularCaliperMeasureRoi circularCaliper:
                    if (!circularCaliper.HasDetectedEdges ||
                        circularCaliper.QualityStatus is not (CircularCaliperQualityStatus.Passed or CircularCaliperQualityStatus.Review) ||
                        circularCaliper.EdgePoints.Length < 3)
                        return (null, "Circular caliper has no successful detected edge profile.");
                    PointD[] circularSamples = Convert(circularCaliper.EdgePoints);
                    CircularityResult circleFit = ProfileMeasurementTools.EvaluateCircularity(circularSamples);
                    if (!circleFit.Success) return (null, circleFit.Error ?? "Detected circular edge samples do not define a fitted circle.");
                    return (new ResolvedSource
                    {
                        Points = circularSamples,
                        Circle = new CircleData(circleFit.Center, circleFit.Radius),
                        ProfilePoints = circularSamples
                    }, null);
                case PointCoordinateMeasureRoi coordinate:
                    return (new ResolvedSource { Points = [Convert(coordinate.Position)], Point = Convert(coordinate.Position) }, null);
                case PointAnnotationRoi annotation:
                    return (new ResolvedSource { Points = [Convert(annotation.Position)], Point = Convert(annotation.Position) }, null);
                case LineMeasureRoi line:
                    PointD[] linePoints = [Convert(line.P1), Convert(line.P2)];
                    return (new ResolvedSource { Points = linePoints, Line = linePoints, Polyline = linePoints }, null);
                case CircleRoi circle:
                    if (requestedKind == AdvancedMeasurementKind.ShortestDistance && calibration?.IsEnabled == true)
                        return (null, "Shortest distance to an ideal circle under distortion requires an explicit measured boundary contour.");
                    return (new ResolvedSource { Points = [Convert(circle.Center)], Circle = CircleFrom(circle.Center, circle.Radius) }, null);
                case ThreePointCircleMeasureRoi threePoint:
                    if (!threePoint.IsValid) return (null, "Three-point circle source is degenerate.");
                    if (requestedKind == AdvancedMeasurementKind.ShortestDistance && calibration?.IsEnabled == true)
                        return (null, "Shortest distance to an ideal circle under distortion requires an explicit measured boundary contour.");
                    return (new ResolvedSource { Points = [Convert(threePoint.Center)], Circle = CircleFrom(threePoint.Center, threePoint.Radius) }, null);
                case PolygonRoi polygon:
                    if (!polygon.IsClosed) return (null, "Polygon source must be closed.");
                    return (new ResolvedSource { Points = Convert(polygon.Points), Polyline = Close(Convert(polygon.Points)), Contour = Convert(polygon.Points), ProfilePoints = Convert(polygon.Points), IsClosed = true }, null);
                case PolylineRoi polyline:
                    return (new ResolvedSource { Points = Convert(polyline.Points), Polyline = Convert(polyline.Points), Contour = Convert(polyline.Points), ProfilePoints = Convert(polyline.Points) }, null);
                case RotatedRect rectangle:
                    if (requestedKind is not (AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.Slot or AdvancedMeasurementKind.ShapeMetrics))
                        return (null, "A rectangle ROI is a search region; measured shape operations require its detected contour.");
                    if (bitmap is null) return (null, "A bitmap is required to extract a measured contour from this search region.");
                    ImageContourExtractionResult extraction = ImageContourExtractionService.Extract(bitmap, rectangle);
                    if (!extraction.Success) return (null, extraction.Error);
                    return (new ResolvedSource
                    {
                        Points = Convert(extraction.OuterContour), Contour = Convert(extraction.OuterContour),
                        Polyline = Close(Convert(extraction.OuterContour)), IsClosed = true,
                        Holes = extraction.Holes.Select(Convert).ToArray(), WasRasterExtracted = true
                    }, null);
                default:
                    return (null, $"ROI type {roi.GetType().Name} is not a supported source for {requestedKind}.");
            }
        }

        private AdvancedMeasurementEvaluation EvaluateKind(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            switch (d.Kind)
            {
                case AdvancedMeasurementKind.Midpoint:
                {
                    if (!RequireCount(s, 2, out string error) || !TryGetPoint(s[0], out PointD a) || !TryGetPoint(s[1], out PointD b))
                        return Failure(d.Id, error.Length > 0 ? error : "Midpoint requires two point sources.");
                    GeometryMeasurementResult result = GeometryMeasurementTools.Midpoint(a, b);
                    return FromGeometry(d, result, "Midpoint", []);
                }
                case AdvancedMeasurementKind.Intersection:
                {
                    if (!RequireCount(s, 2, out string error) || !TryGetLine(s[0], out PointD a, out PointD b) || !TryGetLine(s[1], out PointD c, out PointD e))
                        return Failure(d.Id, error.Length > 0 ? error : "Intersection requires two line sources.");
                    return FromGeometry(d, GeometryMeasurementTools.IntersectLines(a, b, c, e), "Intersection", []);
                }
                case AdvancedMeasurementKind.Projection:
                {
                    if (!RequireCount(s, 2, out string error) || !TryGetPoint(s[0], out PointD point) || !TryGetLine(s[1], out PointD a, out PointD b))
                        return Failure(d.Id, error.Length > 0 ? error : "Projection requires a point and a line source.");
                    return FromGeometry(d, GeometryMeasurementTools.ProjectPointToLine(point, a, b), "Projection", [Length("Distance", 1, unit)]);
                }
                case AdvancedMeasurementKind.Centerline:
                {
                    if (!RequireCount(s, 2, out string error) || !TryGetLine(s[0], out PointD a, out PointD b) || !TryGetLine(s[1], out PointD c, out PointD e))
                        return Failure(d.Id, error.Length > 0 ? error : "Centerline requires two line segments.");
                    return FromGeometry(d, GeometryMeasurementTools.ParallelSegmentCenterline(a, b, c, e), "Centerline", [Length("Length", 1, unit)]);
                }
                case AdvancedMeasurementKind.AngleBisector:
                {
                    if (!RequireCount(s, 3, out string error) || !TryGetPoint(s[0], out PointD vertex) || !TryGetPoint(s[1], out PointD first) || !TryGetPoint(s[2], out PointD second))
                        return Failure(d.Id, error.Length > 0 ? error : "Angle bisector requires vertex and two ray points.");
                    GeometryMeasurementResult result = GeometryMeasurementTools.AngleBisector(vertex, first, second);
                    if (!result.IsValid) return Failure(d.Id, result.Error ?? "Angle bisector could not be constructed.");
                    return Success(d, MethodPrefix + "AngleBisector", [Scalar("AngleDegrees", result.Value * 180 / Math.PI, "°")], [result.FirstWitness!.Value, result.SecondWitness!.Value]);
                }
                case AdvancedMeasurementKind.ShortestDistance:
                    return EvaluateShortest(d, s);
                case AdvancedMeasurementKind.ProjectedDistance:
                {
                    if (s.Count is not (2 or 3) || !TryGetPoint(s[0], out PointD a) || !TryGetPoint(s[1], out PointD b))
                        return Failure(d.Id, "Projected distance requires two point sources and an optional direction line.");
                    VectorD direction;
                    if (s.Count == 3)
                    {
                        if (!TryGetLine(s[2], out PointD axisStart, out PointD axisEnd)) return Failure(d.Id, "The projected-distance axis source is not a usable line.");
                        direction = axisEnd - axisStart;
                    }
                    else
                    {
                        if (!double.IsFinite(d.Parameter)) return Failure(d.Id, "Projection angle must be finite.");
                        double radians = d.Parameter * Math.PI / 180;
                        direction = new VectorD(Math.Cos(radians), Math.Sin(radians));
                    }
                    return FromGeometry(d, GeometryMeasurementTools.ProjectedCenterDistance(a, b, direction), "ProjectedDistance", [Length("Distance", 1, unit)]);
                }
                case AdvancedMeasurementKind.Straightness:
                {
                    if (!RequireCount(s, 1, out string error)) return Failure(d.Id, error);
                    PointD[]? points = s[0].ProfilePoints;
                    if (points is null || points.Length < 3) return Failure(d.Id, "Straightness requires at least three detected edge samples or ordered profile points; an ideal line ROI has no form-error data.");
                    StraightnessResult result = ProfileMeasurementTools.EvaluateStraightness(points);
                    if (!result.Success) return Failure(d.Id, result.Error ?? "Straightness could not be evaluated.");
                    VectorD tangent = new(-result.Normal.Y, result.Normal.X);
                    double[] projections = points.Select(point => (point.X - result.Centroid.X) * tangent.X + (point.Y - result.Centroid.Y) * tangent.Y).ToArray();
                    PointD[] geometry = [result.Centroid + new VectorD(tangent.X * projections.Min(), tangent.Y * projections.Min()), result.Centroid + new VectorD(tangent.X * projections.Max(), tangent.Y * projections.Max())];
                    return Success(d, MethodPrefix + "OrthogonalLeastSquares", [Length("PeakToValley", result.PeakToValley, unit)], geometry, result.SignedResiduals);
                }
                case AdvancedMeasurementKind.Circularity:
                {
                    if (!RequireCount(s, 1, out string error)) return Failure(d.Id, error);
                    PointD[]? points = s[0].ProfilePoints;
                    if (points is null) return Failure(d.Id, "Circularity requires detected edge samples; an ideal circle ROI has no form-error data.");
                    CircularityResult result = ProfileMeasurementTools.EvaluateCircularity(points);
                    if (!result.Success) return Failure(d.Id, result.Error ?? "Circularity could not be evaluated.");
                    return Success(d, MethodPrefix + "AlgebraicLeastSquaresCircle", [Length("PeakToValleyRadialDeviation", result.PeakToValley, unit)], [result.Center, new PointD(result.Center.X + result.Radius, result.Center.Y)], result.RadialResiduals);
                }
                case AdvancedMeasurementKind.Rectangle:
                    return EvaluateRectangle(d, s);
                case AdvancedMeasurementKind.Slot:
                    return EvaluateSlot(d, s);
                case AdvancedMeasurementKind.GapProfile:
                    return EvaluateGap(d, s);
                case AdvancedMeasurementKind.Position:
                {
                    if (!RequireCount(s, 1, out string error) || !TryGetPoint(s[0], out PointD actual))
                        return Failure(d.Id, error.Length > 0 ? error : "Position requires one point source.");
                    PointD nominal = d.NominalInWorkpieceCoordinates ? new PointD(d.NominalX, d.NominalY) : Convert(new PointD(d.NominalX, d.NominalY));
                    if (d.NominalInWorkpieceCoordinates)
                    {
                        if (workpiece is not { IsDefined: true }) return Failure(d.Id, "A defined workpiece coordinate system is required for a workpiece nominal position.");
                        PointD datumOrigin = Convert(workpiece.ToImage(new PointD(0, 0)));
                        PointD datumAxis = Convert(workpiece.ToImage(new PointD(1, 0)));
                        try { actual = RigidTransform2D.FromDatum(datumOrigin, datumAxis - datumOrigin).Transform(actual); }
                        catch (ArgumentOutOfRangeException exception) { return Failure(d.Id, exception.Message); }
                    }
                    GeometryMeasurementResult deviation = GeometryMeasurementTools.PositionDeviation(actual, nominal);
                    if (!deviation.IsValid) return Failure(d.Id, deviation.Error ?? "Position deviation could not be evaluated.");
                    string positionMethod = d.NominalInWorkpieceCoordinates ? "WorkpiecePositionDeviation" : "ImagePositionDeviation";
                    return Success(d, MethodPrefix + positionMethod, [Length("Deviation", deviation.Value, unit), Length("DiametricalPosition", 2 * deviation.Value, unit)], [actual, nominal]);
                }
                case AdvancedMeasurementKind.Pitch:
                    return EvaluatePitch(d, s);
                case AdvancedMeasurementKind.PeriodicPitch:
                    return EvaluatePeriodicPitch(d, s);
                case AdvancedMeasurementKind.DistributionCircle:
                    return EvaluateDistributionCircle(d, s);
                case AdvancedMeasurementKind.ContourDeviation:
                    return EvaluateContour(d, s);
                case AdvancedMeasurementKind.ShapeMetrics:
                    return EvaluateShapeMetrics(d, s);
                default:
                    return Failure(d.Id, $"Measurement kind {d.Kind} is not supported.");
            }
        }

        private AdvancedMeasurementEvaluation EvaluateShortest(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 2, out string error)) return Failure(d.Id, error);
            GeometryMeasurementResult result;
            if (TryGetLine(s[0], out PointD a, out PointD b) && TryGetLine(s[1], out PointD c, out PointD e))
                result = GeometryMeasurementTools.SegmentSegmentDistance(a, b, c, e);
            else if (s[0].Circle is { } firstCircle && s[1].Circle is { } secondCircle)
                result = GeometryMeasurementTools.CircleCircleBoundaryDistance(firstCircle.Center, firstCircle.Radius, secondCircle.Center, secondCircle.Radius);
            else if (s[0].Circle is { } circle && TryGetLine(s[1], out a, out b))
                result = GeometryMeasurementTools.SegmentCircleBoundaryDistance(a, b, circle.Center, circle.Radius);
            else if (s[1].Circle is { } otherCircle && TryGetLine(s[0], out a, out b))
                result = GeometryMeasurementTools.SegmentCircleBoundaryDistance(a, b, otherCircle.Center, otherCircle.Radius);
            else if (s[0].Circle is { } circleBoundary && s[1].Polyline is { } secondPolyline)
                result = PolylineCircleDistance(secondPolyline, circleBoundary);
            else if (s[1].Circle is { } otherBoundary && s[0].Polyline is { } firstPolyline)
                result = PolylineCircleDistance(firstPolyline, otherBoundary);
            else if (s[0].Polyline is { } firstLine && s[1].Polyline is { } secondLine)
                result = GeometryMeasurementTools.PolylinePolylineDistance(firstLine, secondLine);
            else return Failure(d.Id, "Shortest distance requires two line, circle, or polyline sources.");
            return FromGeometry(d, result, "ShortestDistance", [Length("Distance", 1, unit)]);
        }

        private AdvancedMeasurementEvaluation EvaluateRectangle(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 1, out string error)) return Failure(d.Id, error);
            PointD[]? contour = s[0].Contour;
            if (contour is null || !s[0].IsClosed) return Failure(d.Id, "Rectangle measurement requires a closed polygon or extracted image contour.");
            OrientedRectangleFit fit = ShapeMeasurementTools.FitMinimumAreaRectangle(contour);
            if (!double.IsFinite(fit.Area) || fit.Width <= 0 || fit.Height <= 0) return Failure(d.Id, "The contour does not enclose a measurable rectangle.");
            return Success(d, MethodPrefix + (s[0].WasRasterExtracted ? "RasterContourMinimumAreaRectangle" : "PolygonMinimumAreaRectangle"),
                [Length("LongSide", fit.Width, unit), Length("ShortSide", fit.Height, unit), Length("Area", fit.Area, unit, 2), Scalar("AngleDegrees", fit.AngleRadians * 180 / Math.PI, "°")], fit.Corners);
        }

        private AdvancedMeasurementEvaluation EvaluateSlot(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 1, out string error)) return Failure(d.Id, error);
            if (s[0].Contour is not { } contour || !s[0].IsClosed) return Failure(d.Id, "Slot measurement requires a closed polygon or extracted image contour.");
            if (!ShapeMeasurementTools.TryFitSlot(contour, out SlotShapeFit? fit, out string? reason) || fit is null)
                return Failure(d.Id, reason ?? "The supplied contour does not fit a slot.");
            return Success(d, MethodPrefix + (s[0].WasRasterExtracted ? "RasterContourCapsuleFit" : "PolygonCapsuleFit"),
                [Length("Length", fit.Length, unit), Length("Width", fit.Width, unit), Length("MaximumResidual", fit.MaximumResidual, unit), Scalar("AngleDegrees", fit.AngleRadians * 180 / Math.PI, "°")], [fit.AxisStart, fit.AxisEnd]);
        }

        private AdvancedMeasurementEvaluation EvaluateGap(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 2, out string error)) return Failure(d.Id, error);
            if (s[0].Polyline is not { } source || s[1].Polyline is not { } opposing)
                return Failure(d.Id, "Gap profile requires two ordered polyline sources.");
            if (!double.IsFinite(d.Parameter) || d.Parameter <= 0)
                return Failure(d.Id, "Gap-profile spacing must be finite and greater than zero.");
            double intervals = Math.Ceiling(PolylineLength(source) / d.Parameter);
            if (!double.IsFinite(intervals) || intervals + 1 > MaximumPointsPerSource)
                return Failure(d.Id, $"Gap-profile sampling is limited to {MaximumPointsPerSource} stations.");
            GapProfileResult result = ProfileMeasurementTools.MeasureNormalGaps(source, opposing, d.Parameter);
            if (!result.Success) return Failure(d.Id, result.Error ?? "Gap profile could not be evaluated.");
            return Success(d, MethodPrefix + "UniformNormalIntersections",
                [Length("Minimum", result.Minimum, unit), Length("Maximum", result.Maximum, unit), Length("Mean", result.Mean, unit), Length("StandardDeviation", result.StandardDeviation, unit)],
                result.Samples.SelectMany(sample => new[] { sample.SourcePoint, sample.OpposingPoint }),
                result.Samples.Select(sample => sample.Gap), result.Samples.Select(sample => sample.Station));
        }

        private AdvancedMeasurementEvaluation EvaluatePitch(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!TryGetPointList(s, out PointD[] points, out string error)) return Failure(d.Id, error);
            PitchResult result = ProfileMeasurementTools.EvaluatePitch(points);
            if (!result.Success) return Failure(d.Id, result.Error ?? "Pitch could not be evaluated.");
            return Success(d, MethodPrefix + "ConsecutivePointSpacing",
                [Length("Mean", result.Mean, unit), Length("Minimum", result.Minimum, unit), Length("Maximum", result.Maximum, unit), Length("StandardDeviation", result.StandardDeviation, unit)],
                points, result.Pitches, PitchMidpointStations(points));
        }

        private AdvancedMeasurementEvaluation EvaluatePeriodicPitch(AdvancedMeasurementDefinition d, List<ResolvedSource> sources)
        {
            if (d.Sources.Length != 1 || sources.Count != 1)
                return Failure(d.Id, "Periodic pitch requires exactly one direct line ROI source.");
            if (bitmap is null) return Failure(d.Id, "A bitmap is required to extract periodic features along the line.");
            if (!double.IsFinite(d.Parameter)) return Failure(d.Id, "The periodic-pitch polarity parameter must be finite.");
            if (!byRoiId.TryGetValue(d.Sources[0], out RoiBase[]? matches) || matches.Length != 1 || matches[0].GetType() != typeof(LineMeasureRoi))
                return Failure(d.Id, "Periodic pitch requires a direct LineMeasureRoi, not a constructed line or caliper ROI.");
            LineMeasureRoi line = (LineMeasureRoi)matches[0];
            if (line.P1.DistanceTo(line.P2) > MaximumPointsPerSource - 1)
                return Failure(d.Id, $"Periodic-pitch line sampling is limited to {MaximumPointsPerSource} points.");
            ImagePeriodicMeasurementResult extraction = ImagePeriodicMeasurementService.Extract(bitmap, line, detectDark: d.Parameter < 0);
            if (!extraction.Success) return Failure(d.Id, extraction.Error);
            if (extraction.Centers.Length > MaximumPointsPerSource)
                return Failure(d.Id, $"Periodic pitch is limited to {MaximumPointsPerSource} detected centers.");
            PointD[] centers = Convert(extraction.Centers);
            PitchResult pitch = ProfileMeasurementTools.EvaluatePitch(centers);
            if (!pitch.Success) return Failure(d.Id, pitch.Error ?? "Periodic pitch could not be evaluated.");
            return Success(d, MethodPrefix + "ThresholdRunCenterSpacing",
                [Length("Mean", pitch.Mean, unit), Length("Minimum", pitch.Minimum, unit), Length("Maximum", pitch.Maximum, unit), Length("StandardDeviation", pitch.StandardDeviation, unit)],
                centers, pitch.Pitches, PitchMidpointStations(centers));
        }

        private AdvancedMeasurementEvaluation EvaluateDistributionCircle(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!TryGetPointList(s, out PointD[] points, out string error)) return Failure(d.Id, error);
            DistributionCircleFit fit = GeometryMeasurementTools.FitDistributionCircle(points);
            if (!fit.IsValid) return Failure(d.Id, fit.Error ?? "Distribution circle could not be fitted.");
            PointD radiusWitness = new(fit.Center.X + fit.Radius, fit.Center.Y);
            return Success(d, MethodPrefix + "AlgebraicLeastSquaresCircle",
                [Length("Radius", fit.Radius, unit), Length("Diameter", fit.Radius * 2, unit), Length("RootMeanSquareResidual", fit.RootMeanSquareResidual, unit)], [fit.Center, radiusWitness]);
        }

        private AdvancedMeasurementEvaluation EvaluateContour(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 2, out string error)) return Failure(d.Id, error);
            if (s[0].Polyline is not { } measured || s[1].Polyline is not { } reference)
                return Failure(d.Id, "Contour deviation requires measured and reference polylines.");
            if (!double.IsFinite(d.Parameter)) return Failure(d.Id, "Contour-deviation mode parameter must be finite.");
            bool signedOffsets = d.Parameter >= 0;
            ContourDeviationResult result = ProfileMeasurementTools.EvaluateContourDeviation(measured, reference, signedOffsets);
            if (!result.Success) return Failure(d.Id, result.Error ?? "Contour deviation could not be evaluated.");
            double[] stations = VertexStations(measured);
            return Success(d, MethodPrefix + (signedOffsets ? "NearestReferenceSegmentSigned" : "NearestReferenceSegmentAbsolute"),
                [Length("MaximumAbsoluteDeviation", result.MaximumAbsoluteDeviation, unit), Length("PeakToValley", result.PeakToValley, unit)], measured,
                result.Deviations, stations);
        }

        private AdvancedMeasurementEvaluation EvaluateShapeMetrics(AdvancedMeasurementDefinition d, List<ResolvedSource> s)
        {
            if (!RequireCount(s, 1, out string error)) return Failure(d.Id, error);
            if (s[0].Contour is not { } contour || !s[0].IsClosed) return Failure(d.Id, "Shape metrics require a closed polygon or extracted image contour.");
            ContourShapeMetrics shape = ShapeMeasurementTools.MeasureContour(contour, s[0].Holes);
            return Success(d, MethodPrefix + (s[0].WasRasterExtracted ? "RasterContourShapeMetrics" : "PolygonShapeMetrics"),
                [Length("Perimeter", shape.Perimeter, unit), Length("Area", shape.Area, unit, 2),
                 Length("MinimumFeret", shape.MinimumFeretDiameter, unit), Length("MaximumFeret", shape.MaximumFeretDiameter, unit),
                 Scalar("AspectRatio", shape.AspectRatio), Scalar("Circularity", shape.Circularity), Scalar("HoleAreaFraction", shape.HoleAreaFraction)],
                shape.ConvexHull);
        }

        private static AdvancedMeasurementEvaluation FromGeometry(AdvancedMeasurementDefinition d, GeometryMeasurementResult result, string method, AdvancedMeasurementMetric[] metrics)
        {
            if (!result.IsValid) return Failure(d.Id, result.Error ?? "The geometric operation failed.");
            PointD[] geometry = result.FirstWitness.HasValue
                ? result.SecondWitness.HasValue && result.SecondWitness.Value != result.FirstWitness.Value
                    ? [result.FirstWitness.Value, result.SecondWitness.Value]
                    : [result.FirstWitness.Value]
                : [];
            AdvancedMeasurementMetric[] actualMetrics = metrics.Length == 0 ? [] : [metrics[0] with { Value = result.Value }];
            return Success(d, MethodPrefix + method, actualMetrics, geometry);
        }

        private static bool RequireCount(List<ResolvedSource> sources, int count, out string error)
        {
            error = sources.Count == count ? string.Empty : $"This operation requires {count} sources; {sources.Count} were supplied.";
            return error.Length == 0;
        }

        private static bool TryGetPoint(ResolvedSource source, out PointD point)
        {
            if (source.Point is { } value) { point = value; return true; }
            if (source.Circle is { } circle) { point = circle.Center; return true; }
            point = default; return false;
        }

        private static bool TryGetPointList(List<ResolvedSource> sources, out PointD[] points, out string error)
        {
            var gathered = new List<PointD>();
            for (int index = 0; index < sources.Count; index++)
            {
                ResolvedSource source = sources[index];
                if (source.Point is { } point) gathered.Add(point);
                else if (source.Circle is { } circle) gathered.Add(circle.Center);
                else if (source.IsPointList && source.Points.Length > 0) gathered.AddRange(source.Points);
                else
                {
                    points = [];
                    error = $"Source {index + 1} is not an ordered point or circle-center source.";
                    return false;
                }
            }
            points = gathered.ToArray();
            error = string.Empty;
            return true;
        }

        private static GeometryMeasurementResult PolylineCircleDistance(PointD[] polyline, CircleData circle)
        {
            GeometryMeasurementResult? best = null;
            for (int i = 1; i < polyline.Length; i++)
            {
                GeometryMeasurementResult candidate = GeometryMeasurementTools.SegmentCircleBoundaryDistance(
                    polyline[i - 1], polyline[i], circle.Center, circle.Radius);
                if (candidate.IsValid && (best is null || candidate.Value < best.Value)) best = candidate;
            }
            return best ?? GeometryMeasurementResult.Invalid("A polyline-circle distance requires at least one non-degenerate segment.");
        }

        private static bool TryGetLine(ResolvedSource source, out PointD start, out PointD end)
        {
            PointD[]? line = source.Line;
            if (line is { Length: >= 2 } && line[0] != line[1]) { start = line[0]; end = line[1]; return true; }
            start = end = default; return false;
        }

        private PointD[] Convert(IEnumerable<PointD> points)
        {
            PointD[] source = points.Take(MaximumPointsPerSource + 1).ToArray();
            if (source.Length > MaximumPointsPerSource)
                throw new ArgumentException($"A geometry source may contain at most {MaximumPointsPerSource} points.");
            return source.Select(Convert).ToArray();
        }
        private PointD Convert(PointD point)
        {
            PointD corrected = point;
            if (calibration is { } activeCalibration)
            {
                UndistortResult result = activeCalibration.Undistort(point);
                corrected = result.Point;
                if (!result.Converged)
                {
                    _undistortResiduals.Add(double.IsFinite(result.ResidualPixels) ? result.ResidualPixels : double.MaxValue);
                }
            }

            return new PointD(corrected.X * pixelSize, corrected.Y * pixelSize);
        }

        private AdvancedMeasurementEvaluation DistortionFailure(Guid id, int residualCountAtStart)
        {
            int count = _undistortResiduals.Count - residualCountAtStart;
            double maximumResidual = 0;
            for (int index = residualCountAtStart; index < _undistortResiduals.Count; index++)
            {
                maximumResidual = Math.Max(maximumResidual, _undistortResiduals[index]);
            }

            string warning = FormattableString.Invariant(
                $"Distortion inversion did not converge for {count} source point(s); max residual {maximumResidual:G4} px. The affected measurement cannot be evaluated.");
            return Failure(id, warning) with { Warnings = [warning] };
        }

        private CircleData CircleFrom(PointD center, double radius)
        {
            PointD worldCenter = Convert(center);
            PointD x = Convert(new PointD(center.X + radius, center.Y));
            PointD y = Convert(new PointD(center.X, center.Y + radius));
            return new CircleData(worldCenter, (worldCenter.DistanceTo(x) + worldCenter.DistanceTo(y)) / 2);
        }

        private static PointD[] Close(PointD[] points) => points.Length > 0 && points[0] != points[^1] ? [.. points, points[0]] : points;
        private static double[] PitchMidpointStations(PointD[] points)
        {
            double[] stations = new double[Math.Max(0, points.Length - 1)];
            double distance = 0;
            for (int i = 0; i < stations.Length; i++)
            {
                double pitch = points[i].DistanceTo(points[i + 1]);
                stations[i] = distance + pitch / 2;
                distance += pitch;
            }
            return stations;
        }
        private static double[] VertexStations(PointD[] points)
        {
            double[] stations = new double[points.Length];
            for (int i = 1; i < points.Length; i++) stations[i] = stations[i - 1] + points[i - 1].DistanceTo(points[i]);
            return stations;
        }
        private static double PolylineLength(PointD[] points)
        {
            double length = 0;
            for (int i = 1; i < points.Length; i++) length += points[i - 1].DistanceTo(points[i]);
            return length;
        }
    }

    private sealed record CircleData(PointD Center, double Radius);

    private sealed class ResolvedSource
    {
        public PointD[] Points { get; init; } = [];
        public PointD? Point { get; init; }
        public PointD[]? Line { get; init; }
        public CircleData? Circle { get; init; }
        public PointD[]? Polyline { get; init; }
        public PointD[]? Contour { get; init; }
        public PointD[]? ProfilePoints { get; init; }
        public PointD[][] Holes { get; init; } = [];
        public bool IsClosed { get; init; }
        public bool WasRasterExtracted { get; init; }
        public bool IsPointList { get; init; }

        public static ResolvedSource FromEvaluation(AdvancedMeasurementKind kind, AdvancedMeasurementEvaluation evaluation)
        {
            if ((kind is AdvancedMeasurementKind.DistributionCircle or AdvancedMeasurementKind.Circularity) && evaluation.Geometry.Length >= 2)
                return new ResolvedSource { Points = [evaluation.Geometry[0]], Circle = new CircleData(evaluation.Geometry[0], evaluation.Geometry[0].DistanceTo(evaluation.Geometry[1])) };
            return kind switch
            {
                AdvancedMeasurementKind.Midpoint or AdvancedMeasurementKind.Intersection or AdvancedMeasurementKind.Projection or AdvancedMeasurementKind.Position
                    when evaluation.Geometry.Length > 0 => new ResolvedSource { Points = [evaluation.Geometry[0]], Point = evaluation.Geometry[0] },
                AdvancedMeasurementKind.Centerline or AdvancedMeasurementKind.AngleBisector or AdvancedMeasurementKind.Straightness or AdvancedMeasurementKind.Slot
                    when evaluation.Geometry.Length >= 2 => new ResolvedSource { Points = evaluation.Geometry, Line = evaluation.Geometry, Polyline = evaluation.Geometry },
                AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.ContourDeviation
                    when evaluation.Geometry.Length >= 2 => new ResolvedSource { Points = evaluation.Geometry, Polyline = evaluation.Geometry, Contour = evaluation.Geometry },
                AdvancedMeasurementKind.ShapeMetrics
                    when evaluation.Geometry.Length >= 3 => new ResolvedSource { Points = evaluation.Geometry, Polyline = evaluation.Geometry, Contour = evaluation.Geometry, IsClosed = true },
                AdvancedMeasurementKind.Pitch
                    when evaluation.Geometry.Length >= 2 => new ResolvedSource { Points = evaluation.Geometry, IsPointList = true },
                _ => new ResolvedSource()
            };
        }
    }
}
