using System;
using System.Linq;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Measurements;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;

namespace ImageViewer.Services
{
    /// <summary>
    /// Applies an operator-selected two-point rigid alignment to saved ROI templates.
    /// The image pixel scale remains owned by the recipe calibration and is never changed here.
    /// </summary>
    internal static class MeasurementRecipeAlignmentService
    {
        public static RoiDocument BuildAlignedDocument(
            MeasurementRecipeDocument recipe,
            PointD currentDatum1,
            PointD currentDatum2)
        {
            ArgumentNullException.ThrowIfNull(recipe);
            if (!recipe.CanLocalize || recipe.DatumPoints.Count < 2)
            {
                throw new InvalidOperationException("The recipe does not contain two distinct reference datum points.");
            }

            PointD referenceDatum1 = new(recipe.DatumPoints[0].X, recipe.DatumPoints[0].Y);
            PointD referenceDatum2 = new(recipe.DatumPoints[1].X, recipe.DatumPoints[1].Y);
            RigidTransform2D referenceToCurrent = RigidTransform2D.FromDatumPair(
                referenceDatum1, referenceDatum2, currentDatum1, currentDatum2);

            foreach (MeasurementRecipeItem item in recipe.Items)
            {
                if (item.Template is null)
                {
                    if (item.IsEnabled)
                    {
                        throw new InvalidOperationException(
                            $"Enabled recipe item '{item.DisplayName ?? item.Key}' has no ROI geometry.");
                    }

                    continue;
                }

                ValidateTemplateForAlignment(item.Template);
            }

            return MeasurementRecipeService.BuildRoiDocument(recipe, template => TransformTemplate(template, referenceToCurrent));
        }

        private static void ValidateTemplateForAlignment(RoiPersistenceData data)
        {
            if (!IsSupportedType(data.Type))
            {
                throw new NotSupportedException(
                    $"Recipe ROI geometry '{data.Type}' has no alignment adapter. No recipe items were applied.");
            }

            foreach (RoiPersistencePoint? point in new[]
            {
                data.Center, data.Position, data.P1, data.P2, data.P3, data.Vertex
            }.Concat(data.Points ?? []))
            {
                if (point is not null && (!double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                {
                    throw new InvalidOperationException($"Recipe ROI '{data.Type}' contains a non-finite point.");
                }
            }

            double[] scalarGeometry =
            [
                data.Width, data.Height, data.RadiusX, data.RadiusY, data.Radius,
                data.Radius2, data.Angle, data.FitResidualRms, data.FitResidualMedian,
                data.FitResidualMax, data.FitNoiseScale, data.FitAspectRatio
            ];
            if (scalarGeometry.Any(value => !double.IsFinite(value)))
            {
                throw new InvalidOperationException($"Recipe ROI '{data.Type}' contains a non-finite geometry value.");
            }

            foreach (AdvancedMeasurementDefinition? definition in data.AdvancedMeasurements ?? [])
            {
                if (definition is { Kind: AdvancedMeasurementKind.Position, NominalInWorkpieceCoordinates: false } &&
                    (!double.IsFinite(definition.NominalX) || !double.IsFinite(definition.NominalY)))
                {
                    throw new InvalidOperationException($"Recipe ROI '{data.Type}' contains a non-finite position nominal.");
                }

                if (definition is { Kind: AdvancedMeasurementKind.ProjectedDistance, Sources.Length: 2 } &&
                    !double.IsFinite(definition.Parameter))
                {
                    throw new InvalidOperationException($"Recipe ROI '{data.Type}' contains a non-finite projection angle.");
                }
            }
        }

        private static bool IsSupportedType(string type) => type is
            "point-coordinate-measure" or "point-annotation" or "text-annotation" or
            "circle" or "circular-caliper-measure" or "ring" or "ellipse" or "fitted-ellipse" or
            "rotated-rect" or "blob-analysis" or "arc-caliper-measure" or "line-measure" or
            "line-caliper-measure" or "caliper-measure" or "arrow-annotation" or "angle-measure" or
            "arc-measure" or "three-point-circle" or "point-to-line-distance" or
            "point-to-circle-distance" or "parallelism-measure" or "perpendicularity-measure" or
            "concentricity-measure" or "center-distance-measure" or "polygon" or "polyline";

        private static void TransformTemplate(RoiPersistenceData data, RigidTransform2D transform)
        {
            string key = data.Type;
            switch (key)
            {
                case "point-coordinate-measure":
                case "point-annotation":
                case "text-annotation":
                    data.Position = Transform(data.Position, transform, key, "position");
                    break;

                case "circle":
                case "circular-caliper-measure":
                case "ring":
                    data.Center = Transform(data.Center, transform, key, "center");
                    break;

                case "ellipse":
                case "fitted-ellipse":
                case "rotated-rect":
                case "blob-analysis":
                    data.Center = Transform(data.Center, transform, key, "center");
                    data.Angle += transform.RotationDegrees;
                    break;

                case "arc-caliper-measure":
                    data.Center = Transform(data.Center, transform, key, "center");
                    data.Angle += transform.RotationDegrees;
                    break;

                case "line-measure":
                case "line-caliper-measure":
                case "caliper-measure":
                case "arrow-annotation":
                    data.P1 = Transform(data.P1, transform, key, "p1");
                    data.P2 = Transform(data.P2, transform, key, "p2");
                    if (data.Center is not null)
                    {
                        data.Center = Transform(data.Center, transform, key, "center");
                    }
                    data.Angle += transform.RotationDegrees;
                    break;

                case "angle-measure":
                case "arc-measure":
                case "three-point-circle":
                    data.P1 = Transform(data.P1, transform, key, "p1");
                    data.P2 = Transform(data.P2, transform, key, "p2");
                    data.Vertex = Transform(data.Vertex, transform, key, "vertex");
                    break;

                case "point-to-line-distance":
                    data.P1 = Transform(data.P1, transform, key, "point");
                    data.P2 = Transform(data.P2, transform, key, "line p1");
                    data.Vertex = Transform(data.Vertex, transform, key, "line p2");
                    break;

                case "point-to-circle-distance":
                    data.P1 = Transform(data.P1, transform, key, "point");
                    data.P2 = Transform(data.P2, transform, key, "circle center");
                    break;

                case "parallelism-measure":
                case "perpendicularity-measure":
                    data.P1 = Transform(data.P1, transform, key, "line 1 p1");
                    data.P2 = Transform(data.P2, transform, key, "line 1 p2");
                    data.Vertex = Transform(data.Vertex, transform, key, "line 2 p1");
                    data.P3 = Transform(data.P3, transform, key, "line 2 p2");
                    break;

                case "concentricity-measure":
                case "center-distance-measure":
                    data.P1 = Transform(data.P1, transform, key, "center 1");
                    data.P2 = Transform(data.P2, transform, key, "center 2");
                    break;

                case "polygon":
                case "polyline":
                    if (data.Points is null || data.Points.Count == 0)
                    {
                        throw new InvalidOperationException($"Recipe ROI '{key}' has no points to align.");
                    }

                    foreach (RoiPersistencePoint point in data.Points)
                    {
                        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                        {
                            throw new InvalidOperationException($"Recipe ROI '{key}' contains a non-finite point.");
                        }

                        PointD mapped = transform.Transform(new PointD(point.X, point.Y));
                        if (!double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
                        {
                            throw new InvalidOperationException($"Recipe ROI '{key}' transforms to a non-finite point.");
                        }

                        point.X = mapped.X;
                        point.Y = mapped.Y;
                    }
                    break;

                default:
                    throw new NotSupportedException(
                        $"Recipe ROI geometry '{key}' has no alignment adapter. No recipe items were applied.");
            }

            if (!double.IsFinite(data.Angle))
            {
                throw new InvalidOperationException($"Recipe ROI '{key}' has a non-finite angle.");
            }

            TransformAdvancedMeasurementParameters(data, transform, key);
        }

        private static void TransformAdvancedMeasurementParameters(RoiPersistenceData data, RigidTransform2D transform, string type)
        {
            if (data.AdvancedMeasurements is null)
            {
                return;
            }

            for (int index = 0; index < data.AdvancedMeasurements.Length; index++)
            {
                AdvancedMeasurementDefinition? definition = data.AdvancedMeasurements[index];
                if (definition is { Kind: AdvancedMeasurementKind.ProjectedDistance, Sources.Length: 2 })
                {
                    // The optional third source already rotates with its ROI. Only rotate the explicit angle.
                    double rotatedAngle = transform.TransformAngleDegrees(definition.Parameter % 360) % 360;
                    data.AdvancedMeasurements[index] = definition with { Parameter = rotatedAngle };
                    continue;
                }

                if (definition is not { Kind: AdvancedMeasurementKind.Position, NominalInWorkpieceCoordinates: false })
                {
                    continue;
                }

                PointD mapped = transform.Transform(new PointD(definition.NominalX, definition.NominalY));
                if (!double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
                {
                    throw new InvalidOperationException($"Recipe ROI '{type}' position nominal transforms to a non-finite point.");
                }

                data.AdvancedMeasurements[index] = definition with { NominalX = mapped.X, NominalY = mapped.Y };
            }
        }

        private static RoiPersistencePoint Transform(
            RoiPersistencePoint? point,
            RigidTransform2D transform,
            string type,
            string geometryName)
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new InvalidOperationException(
                    $"Recipe ROI '{type}' is missing valid {geometryName} geometry.");
            }

            PointD mapped = transform.Transform(new PointD(point.X, point.Y));
            if (!double.IsFinite(mapped.X) || !double.IsFinite(mapped.Y))
            {
                throw new InvalidOperationException($"Recipe ROI '{type}' transforms {geometryName} to a non-finite point.");
            }

            return new RoiPersistencePoint { X = mapped.X, Y = mapped.Y };
        }
    }
}
