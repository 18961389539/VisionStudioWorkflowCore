using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Utils
{
    public static class GeometryUtils
    {
        /// <summary>
        /// 几何工具方法集合
        /// Chinese: 提供常用的几何计算辅助方法，例如两点距离、角度计算、点旋转与包围盒计算。
        /// English: Collection of common geometry helper methods such as distance, angle, rotate point and bounding box.
        /// </summary>

        public static double Distance(Point p1, Point p2)
        {
            return ImageViewer.Core.Geometry.Geometry2D.Distance(p1.ToPointD(), p2.ToPointD());
        }

        public static double DistanceToSegment(Point point, Point segmentStart, Point segmentEnd)
        {
            return ImageViewer.Core.Geometry.Geometry2D.DistanceToSegment(
                point.ToPointD(), segmentStart.ToPointD(), segmentEnd.ToPointD());
        }

        public static double PolylineLength(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return ImageViewer.Core.Geometry.Geometry2D.PolylineLength(points.ToPointDs().ToArray());
        }

        public static IReadOnlyList<double> GetPolylineSegmentLengths(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return ImageViewer.Core.Geometry.Geometry2D.GetPolylineSegmentLengths(points.ToPointDs().ToArray());
        }

        public static bool IsPointNearSegment(Point point, Point segmentStart, Point segmentEnd, double threshold)
        {
            return ImageViewer.Core.Geometry.Geometry2D.IsPointNearSegment(
                point.ToPointD(), segmentStart.ToPointD(), segmentEnd.ToPointD(), threshold);
        }

        public static double Angle(Point p1, Point center, Point p2)
        {
            return ImageViewer.Core.Geometry.Geometry2D.Angle(p1.ToPointD(), center.ToPointD(), p2.ToPointD());
        }

        public static Point RotatePoint(Point point, Point center, double angleDegrees)
        {
            return ImageViewer.Core.Geometry.Geometry2D.RotatePoint(
                point.ToPointD(), center.ToPointD(), angleDegrees).ToWpfPoint();
        }

        public static Rect GetBoundingBox(IEnumerable<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            RectD? bounds = ImageViewer.Core.Geometry.Geometry2D.GetBoundingBox(points.ToPointDs());
            return bounds is { } rect ? rect.ToWpfRect() : Rect.Empty;
        }

        public static Point GetCentroid(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return ImageViewer.Core.Geometry.Geometry2D.GetCentroid(points.ToPointDs().ToArray()).ToWpfPoint();
        }

        public static bool IsPointInPolygon(Point point, IReadOnlyList<Point> polygon)
        {
            ArgumentNullException.ThrowIfNull(polygon);
            return ImageViewer.Core.Geometry.Geometry2D.IsPointInPolygon(
                point.ToPointD(), polygon.ToPointDs().ToArray());
        }

        public static double PolygonPerimeter(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return ImageViewer.Core.Geometry.Geometry2D.PolygonPerimeter(points.ToPointDs().ToArray());
        }

        public static double PolygonArea(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            return ImageViewer.Core.Geometry.Geometry2D.PolygonArea(points.ToPointDs().ToArray());
        }

        public static (double Area, double Perimeter, Point Centroid) GetPolygonMetrics(IReadOnlyList<Point> points)
        {
            ArgumentNullException.ThrowIfNull(points);
            (double area, double perimeter, PointD centroid) =
                ImageViewer.Core.Geometry.Geometry2D.GetPolygonMetrics(points.ToPointDs().ToArray());
            return (area, perimeter, centroid.ToWpfPoint());
        }

        public static double SmallestAngle(Point p1, Point vertex, Point p2)
        {
            return ImageViewer.Core.Geometry.Geometry2D.SmallestAngle(
                p1.ToPointD(), vertex.ToPointD(), p2.ToPointD());
        }
        public static bool TryFitEllipse(IReadOnlyList<Point> points, out Point center, out double radiusX, out double radiusY, out double angleDegrees)
        {
            if (!TryFitEllipse(points, EllipseFitOptions.Default, out EllipseFitResult fit))
            {
                center = default;
                radiusX = 0;
                radiusY = 0;
                angleDegrees = 0;
                return false;
            }

            center = fit.Center;
            radiusX = fit.RadiusX;
            radiusY = fit.RadiusY;
            angleDegrees = fit.AngleDegrees;
            return true;
        }

        /// <summary>
        /// Robust geometric ellipse fit with deterministic RANSAC initialization and Huber/Tukey reweighting.
        /// The optimization uses a radial geometric residual and normalized coordinates to remain stable for
        /// large image coordinates and elongated ellipses.
        /// </summary>
        public static bool TryFitEllipse(IReadOnlyList<Point> points, EllipseFitOptions? options, out EllipseFitResult result)
        {
            result = null!;
            if (points == null || points.Count < 5)
            {
                return false;
            }

            List<(int Index, Point Point)> finitePoints = new(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (double.IsFinite(points[i].X) && double.IsFinite(points[i].Y))
                {
                    finitePoints.Add((i, points[i]));
                }
            }

            EllipseFitOptions fitOptions = (options ?? EllipseFitOptions.Default).Normalize();
            if (finitePoints.Count < fitOptions.MinimumInliers)
            {
                return false;
            }

            Point origin = new(
                Median(finitePoints.Select(item => item.Point.X).ToArray()),
                Median(finitePoints.Select(item => item.Point.Y).ToArray()));
            double scale = ComputeEllipseCoordinateScale(finitePoints, origin);
            if (!double.IsFinite(scale) || scale <= 1e-9)
            {
                return false;
            }

            List<Point> normalizedPoints = finitePoints
                .Select(item => new Point((item.Point.X - origin.X) / scale, (item.Point.Y - origin.Y) / scale))
                .ToList();
            if (!TryInitializeEllipse(normalizedPoints, out double[] parameters, out double conditionNumber))
            {
                return false;
            }

            double initialNoiseScale = ComputeResidualScale(normalizedPoints, parameters);
            double inlierThreshold = fitOptions.InlierThreshold > 0
                ? fitOptions.InlierThreshold / scale
                : Math.Max(0.02, initialNoiseScale * 3.0);

            if (fitOptions.MaxRansacSamples > 0 && normalizedPoints.Count > fitOptions.MinimumInliers)
            {
                parameters = SelectEllipseRansacSeed(normalizedPoints, parameters, fitOptions, inlierThreshold);
            }

            double finalNoiseScale = OptimizeEllipse(normalizedPoints, parameters, fitOptions);
            if (!TryGetEllipseGeometry(parameters, origin, scale, out Point center, out double radiusX, out double radiusY, out double angle))
            {
                return false;
            }

            if (!TryNormalizeEllipseParameters(ref center, ref radiusX, ref radiusY, ref angle))
            {
                return false;
            }

            double[] residuals = normalizedPoints
                .Select(point => Math.Abs(ComputeEllipseRadialResidual(point, parameters)) * scale)
                .ToArray();
            double noiseScale = Math.Max(1e-6, finalNoiseScale * scale);
            double distanceThreshold = fitOptions.InlierThreshold > 0
                ? fitOptions.InlierThreshold
                : Math.Max(0.75, noiseScale * (fitOptions.Loss == RobustFitLoss.Tukey ? fitOptions.TukeyK : 3.0));
            bool[] inlierMask = new bool[points.Count];
            int inlierCount = 0;
            double residualSum = 0;
            double residualMax = 0;
            for (int i = 0; i < residuals.Length; i++)
            {
                bool inlier = residuals[i] <= distanceThreshold;
                inlierMask[finitePoints[i].Index] = inlier;
                if (inlier)
                {
                    inlierCount++;
                }

                residualSum += residuals[i] * residuals[i];
                residualMax = Math.Max(residualMax, residuals[i]);
            }

            if (inlierCount < fitOptions.MinimumInliers)
            {
                return false;
            }

            double residualMedian = Median(residuals);
            result = new EllipseFitResult(
                center,
                radiusX,
                radiusY,
                NormalizeAngleDegrees(angle * 180 / Math.PI),
                Math.Sqrt(residualSum / residuals.Length),
                residualMedian,
                residualMax,
                noiseScale,
                inlierCount,
                points.Count - inlierCount,
                Math.Max(radiusX, radiusY) / Math.Max(1e-9, Math.Min(radiusX, radiusY)),
                inlierMask,
                $"{FittingAlgorithmMetadata.Ellipse}:{fitOptions.Loss}");
            return true;
        }

        private static double[] SelectEllipseRansacSeed(IReadOnlyList<Point> points, double[] fallback, EllipseFitOptions options, double threshold)
        {
            double[] best = (double[])fallback.Clone();
            int bestInliers = CountEllipseInliers(points, best, threshold, out double bestError);
            Random random = new(options.RandomSeed);
            int sampleCount = Math.Min(options.MaxRansacSamples, Math.Max(1, points.Count * 2));

            for (int sample = 0; sample < sampleCount; sample++)
            {
                HashSet<int> indices = [];
                while (indices.Count < options.MinimumInliers)
                {
                    indices.Add(random.Next(points.Count));
                }

                List<Point> candidatePoints = indices.Select(index => points[index]).ToList();
                if (!TryInitializeEllipse(candidatePoints, out double[] candidate, out _))
                {
                    continue;
                }

                int inliers = CountEllipseInliers(points, candidate, threshold, out double error);
                if (inliers > bestInliers || (inliers == bestInliers && error < bestError))
                {
                    best = candidate;
                    bestInliers = inliers;
                    bestError = error;
                }
            }

            return best;
        }

        private static int CountEllipseInliers(IReadOnlyList<Point> points, double[] parameters, double threshold, out double error)
        {
            int count = 0;
            error = 0;
            foreach (Point point in points)
            {
                double residual = Math.Abs(ComputeEllipseRadialResidual(point, parameters));
                if (residual <= threshold)
                {
                    count++;
                }

                error += residual * residual;
            }

            return count;
        }

        private static double OptimizeEllipse(IReadOnlyList<Point> points, double[] parameters, EllipseFitOptions options)
        {
            double damping = 1e-3;
            double noiseScale = ComputeResidualScale(points, parameters);
            double objective = ComputeRobustEllipseObjective(points, parameters, options, noiseScale);

            for (int iteration = 0; iteration < options.MaxIterations; iteration++)
            {
                double[] residuals = points.Select(point => ComputeEllipseRadialResidual(point, parameters)).ToArray();
                noiseScale = ComputeResidualScale(residuals);
                double[,] normal = new double[5, 5];
                double[] gradient = new double[5];
                for (int i = 0; i < points.Count; i++)
                {
                    double weight = ComputeRobustWeight(residuals[i], noiseScale, options);
                    if (weight <= 1e-9)
                    {
                        continue;
                    }

                    double[] jacobian = new double[5];
                    for (int parameterIndex = 0; parameterIndex < jacobian.Length; parameterIndex++)
                    {
                        double step = parameterIndex < 2 ? 1e-5 : parameterIndex < 4 ? 1e-4 : 1e-5;
                        double[] candidate = (double[])parameters.Clone();
                        candidate[parameterIndex] += step;
                        jacobian[parameterIndex] = (ComputeEllipseRadialResidual(points[i], candidate) - residuals[i]) / step;
                    }

                    for (int row = 0; row < jacobian.Length; row++)
                    {
                        gradient[row] -= weight * jacobian[row] * residuals[i];
                        for (int column = row; column < jacobian.Length; column++)
                        {
                            normal[row, column] += weight * jacobian[row] * jacobian[column];
                        }
                    }
                }

                for (int row = 0; row < 5; row++)
                {
                    for (int column = 0; column < row; column++)
                    {
                        normal[row, column] = normal[column, row];
                    }

                    normal[row, row] += damping;
                }

                if (!TrySolveLinearSystem(normal, gradient, out double[] delta))
                {
                    break;
                }

                if (delta.All(value => Math.Abs(value) < 1e-7))
                {
                    break;
                }

                double[] candidateParameters = new double[5];
                for (int i = 0; i < candidateParameters.Length; i++)
                {
                    candidateParameters[i] = parameters[i] + delta[i];
                }

                if (!IsValidEllipseParameters(candidateParameters))
                {
                    damping = Math.Min(1e8, damping * 4);
                    continue;
                }

                double candidateScale = ComputeResidualScale(points, candidateParameters);
                double candidateObjective = ComputeRobustEllipseObjective(points, candidateParameters, options, candidateScale);
                if (double.IsFinite(candidateObjective) && candidateObjective < objective)
                {
                    Array.Copy(candidateParameters, parameters, parameters.Length);
                    objective = candidateObjective;
                    noiseScale = candidateScale;
                    damping = Math.Max(1e-7, damping * 0.45);
                }
                else
                {
                    damping = Math.Min(1e8, damping * 4);
                }
            }

            return noiseScale;
        }

        private static double ComputeRobustEllipseObjective(IReadOnlyList<Point> points, double[] parameters, EllipseFitOptions options, double scale)
        {
            double objective = 0;
            foreach (Point point in points)
            {
                double residual = ComputeEllipseRadialResidual(point, parameters);
                double weight = ComputeRobustWeight(residual, scale, options);
                objective += weight * residual * residual;
            }

            return objective;
        }

        private static double ComputeRobustWeight(double residual, double scale, EllipseFitOptions options)
        {
            if (options.Loss == RobustFitLoss.LeastSquares || scale < 1e-8)
            {
                return 1;
            }

            double absolute = Math.Abs(residual);
            if (options.Loss == RobustFitLoss.Huber)
            {
                double limit = options.HuberK * scale;
                return absolute <= limit ? 1 : limit / Math.Max(absolute, 1e-12);
            }

            double u = absolute / Math.Max(1e-12, options.TukeyK * scale);
            if (u >= 1)
            {
                return 0;
            }

            double factor = 1 - u * u;
            return factor * factor;
        }

        private static bool TryInitializeEllipse(IReadOnlyList<Point> points, out double[] parameters, out double conditionNumber)
        {
            parameters = new double[5];
            conditionNumber = double.PositiveInfinity;
            if (points.Count < 5)
            {
                return false;
            }

            Point center = new(
                Median(points.Select(point => point.X).ToArray()),
                Median(points.Select(point => point.Y).ToArray()));
            double xx = 0;
            double xy = 0;
            double yy = 0;
            foreach (Point point in points)
            {
                double dx = point.X - center.X;
                double dy = point.Y - center.Y;
                xx += dx * dx;
                xy += dx * dy;
                yy += dy * dy;
            }

            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double sumLocalX2 = 0;
            double sumLocalY2 = 0;
            foreach (Point point in points)
            {
                double dx = point.X - center.X;
                double dy = point.Y - center.Y;
                double localX = dx * cos + dy * sin;
                double localY = -dx * sin + dy * cos;
                sumLocalX2 += localX * localX;
                sumLocalY2 += localY * localY;
            }

            double radiusX = Math.Sqrt(Math.Max(2 * sumLocalX2 / points.Count, 1e-8));
            double radiusY = Math.Sqrt(Math.Max(2 * sumLocalY2 / points.Count, 1e-8));
            if (radiusX <= 1e-4 || radiusY <= 1e-4)
            {
                return false;
            }

            conditionNumber = Math.Max(radiusX, radiusY) / Math.Max(1e-9, Math.Min(radiusX, radiusY));
            if (!double.IsFinite(conditionNumber) || conditionNumber > 1e6)
            {
                return false;
            }

            if (radiusY > radiusX)
            {
                (radiusX, radiusY) = (radiusY, radiusX);
                angle += Math.PI / 2;
            }

            parameters = [center.X, center.Y, Math.Log(radiusX), Math.Log(radiusY), NormalizeAngleRadians(angle)];
            return IsValidEllipseParameters(parameters);
        }

        private static bool IsValidEllipseParameters(double[] parameters)
        {
            if (parameters.Length != 5 || parameters.Any(value => !double.IsFinite(value)))
            {
                return false;
            }

            double radiusX = Math.Exp(parameters[2]);
            double radiusY = Math.Exp(parameters[3]);
            return radiusX >= 1e-6 && radiusY >= 1e-6 &&
                   Math.Max(radiusX, radiusY) / Math.Max(1e-9, Math.Min(radiusX, radiusY)) <= 1e6;
        }

        private static bool TryGetEllipseGeometry(double[] parameters, Point origin, double scale, out Point center, out double radiusX, out double radiusY, out double angle)
        {
            center = new Point(origin.X + parameters[0] * scale, origin.Y + parameters[1] * scale);
            radiusX = Math.Exp(parameters[2]) * scale;
            radiusY = Math.Exp(parameters[3]) * scale;
            angle = parameters[4];
            return IsValidEllipseParameters(parameters) &&
                   double.IsFinite(center.X) && double.IsFinite(center.Y) &&
                   double.IsFinite(radiusX) && double.IsFinite(radiusY);
        }

        private static double ComputeEllipseRadialResidual(Point point, double[] parameters)
        {
            double radiusX = Math.Exp(parameters[2]);
            double radiusY = Math.Exp(parameters[3]);
            double cos = Math.Cos(parameters[4]);
            double sin = Math.Sin(parameters[4]);
            double dx = point.X - parameters[0];
            double dy = point.Y - parameters[1];
            double localX = dx * cos + dy * sin;
            double localY = -dx * sin + dy * cos;
            double radial = Math.Sqrt(localX * localX + localY * localY);
            if (radial < 1e-10)
            {
                return 0;
            }

            double directionX = localX / radial;
            double directionY = localY / radial;
            double denominator = directionX * directionX / (radiusX * radiusX) + directionY * directionY / (radiusY * radiusY);
            double target = denominator > 1e-12 ? 1 / Math.Sqrt(denominator) : radial;
            return radial - target;
        }

        private static double ComputeResidualScale(IReadOnlyList<Point> points, double[] parameters)
        {
            return ComputeResidualScale(points.Select(point => ComputeEllipseRadialResidual(point, parameters)).ToArray());
        }

        private static double ComputeResidualScale(IReadOnlyList<double> residuals)
        {
            if (residuals.Count == 0)
            {
                return 1e-4;
            }

            double median = Median(residuals.ToArray());
            double[] deviations = residuals.Select(value => Math.Abs(value - median)).ToArray();
            double scale = 1.4826 * Median(deviations);
            if (scale < 1e-8)
            {
                scale = Math.Sqrt(residuals.Select(value => value * value).Average());
            }

            return Math.Max(1e-4, double.IsFinite(scale) ? scale : 1e-4);
        }

        private static double ComputeEllipseCoordinateScale(IReadOnlyList<(int Index, Point Point)> points, Point origin)
        {
            double maxDistance = 0;
            foreach (var item in points)
            {
                maxDistance = Math.Max(maxDistance, Distance(item.Point, origin));
            }

            return Math.Max(1, maxDistance);
        }

        private static double Median(double[] values)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            Array.Sort(values);
            int middle = values.Length / 2;
            return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        }

        private static bool TryNormalizeEllipseParameters(ref Point center, ref double radiusX, ref double radiusY, ref double angle)
        {
            if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
                !double.IsFinite(radiusX) || !double.IsFinite(radiusY) ||
                radiusX <= 0 || radiusY <= 0)
            {
                return false;
            }

            if (radiusY > radiusX)
            {
                (radiusX, radiusY) = (radiusY, radiusX);
                angle += Math.PI / 2;
            }

            angle = NormalizeAngleRadians(angle);
            return true;
        }

        private static double NormalizeAngleRadians(double angle)
        {
            while (angle <= -Math.PI / 2)
            {
                angle += Math.PI;
            }

            while (angle > Math.PI / 2)
            {
                angle -= Math.PI;
            }

            return angle;
        }

        private static double NormalizeAngleDegrees(double angle)
        {
            while (angle <= -90)
            {
                angle += 180;
            }

            while (angle > 90)
            {
                angle -= 180;
            }

            return angle;
        }

        private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution)
        {
            int size = rhs.Length;
            solution = new double[size];
            var augmented = new double[size, size + 1];
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    augmented[row, column] = matrix[row, column];
                }

                augmented[row, size] = rhs[row];
            }

            for (int pivot = 0; pivot < size; pivot++)
            {
                int bestRow = pivot;
                double bestValue = Math.Abs(augmented[pivot, pivot]);
                for (int row = pivot + 1; row < size; row++)
                {
                    double candidate = Math.Abs(augmented[row, pivot]);
                    if (candidate > bestValue)
                    {
                        bestValue = candidate;
                        bestRow = row;
                    }
                }

                if (bestValue < 1e-12)
                {
                    return false;
                }

                if (bestRow != pivot)
                {
                    for (int column = pivot; column <= size; column++)
                    {
                        (augmented[pivot, column], augmented[bestRow, column]) = (augmented[bestRow, column], augmented[pivot, column]);
                    }
                }

                double pivotValue = augmented[pivot, pivot];
                for (int column = pivot; column <= size; column++)
                {
                    augmented[pivot, column] /= pivotValue;
                }

                for (int row = 0; row < size; row++)
                {
                    if (row == pivot)
                    {
                        continue;
                    }

                    double factor = augmented[row, pivot];
                    if (Math.Abs(factor) < 1e-12)
                    {
                        continue;
                    }

                    for (int column = pivot; column <= size; column++)
                    {
                        augmented[row, column] -= factor * augmented[pivot, column];
                    }
                }
            }

            for (int row = 0; row < size; row++)
            {
                solution[row] = augmented[row, size];
            }

            return solution.All(double.IsFinite);
        }

        /// <summary>
        /// 计算给定点集的轴对齐包围盒（bounding box）。
        /// Chinese: 返回包含所有点的最小矩形（axis-aligned bounding box）。如果点集为空则返回 Rect.Empty。
        /// English: Returns the axis-aligned bounding box that contains all points; returns Rect.Empty if sequence is empty.
        /// </summary>
        /// <param name="points">点集合 / Collection of points</param>
        /// <returns>包围盒矩形 / Bounding rectangle that encloses all points, or Rect.Empty for empty input.</returns>
    }
}
