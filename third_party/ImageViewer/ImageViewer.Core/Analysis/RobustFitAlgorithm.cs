using System;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis;

/// <summary>
/// Managed robust geometry fitting for edge-point sequences.
/// Chinese: 直线拟合采用正交（总体最小二乘）回归；稳健模式先用"最小中位数残差"种子线定向，
/// 再做 IRLS 重加权，避免单个远端点把 PCA 主轴拉斜后连带污染残差尺度。
/// 圆拟合以 Kasa 代数拟合初始化，再做 Gauss-Newton 几何距离迭代。
/// English: Lines use orthogonal regression; robust modes seed the IRLS loop with a
/// least-median-of-squares line so one far outlier cannot tilt the principal axis before the
/// residual scale is estimated. Circles use a Kasa algebraic seed followed by Gauss-Newton
/// geometric refinement.
/// </summary>
public static class RobustFitAlgorithm
{
    /// <summary>
    /// Fits a line to the points. <paramref name="clippingEndPoints"/> removes that many
    /// points from each end before fitting, but the returned segment endpoints are the
    /// projections of the ORIGINAL first and last points onto the fitted line.
    /// </summary>
    public static bool TryFitLine(
        PointD[] points,
        LineFitMode fitMode,
        int clippingEndPoints,
        out DetectedLineSegment segment)
    {
        ArgumentNullException.ThrowIfNull(points);
        segment = default;
        if (points.Length < 2)
        {
            return false;
        }

        int clip = Math.Max(0, clippingEndPoints);
        int begin = clip;
        int count = points.Length - (2 * clip);
        if (count < 2)
        {
            begin = 0;
            count = points.Length;
        }

        if (!FitOrthogonalLine(points, begin, count, fitMode, out PointD linePoint, out VectorD direction))
        {
            return false;
        }

        PointD start = Project(points[0], linePoint, direction);
        PointD end = Project(points[^1], linePoint, direction);
        if (!IsFinite(start) || !IsFinite(end))
        {
            return false;
        }

        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        if ((dx * dx) + (dy * dy) <= 1e-12)
        {
            return false;
        }

        segment = new DetectedLineSegment(start, end);
        return true;
    }

    /// <summary>
    /// Fits a circle by geometric (radial) distance minimization. Robust modes run an
    /// IRLS reweighting loop on top of a Levenberg-Marquardt refined Gauss-Newton fit.
    /// </summary>
    public static bool TryFitCircle(PointD[] points, LineFitMode fitMode, out PointD center, out double radius)
    {
        ArgumentNullException.ThrowIfNull(points);
        center = default;
        radius = 0;
        if (points.Length < 3)
        {
            return false;
        }

        // Fit in a normalized frame (centroid at the origin, unit maximum radius) so the
        // algebraic seed and the normal equations stay well conditioned at any image
        // scale; a circle fit is similarity-equivariant, so the result maps straight back.
        if (!TryNormalize(points, out double meanX, out double meanY, out double scale))
        {
            return false;
        }

        var normalized = new PointD[points.Length];
        for (int i = 0; i < normalized.Length; i++)
        {
            normalized[i] = new PointD((points[i].X - meanX) / scale, (points[i].Y - meanY) / scale);
        }

        if (!KasaCircle(normalized, out double cx, out double cy, out double r))
        {
            return false;
        }

        int iterations = fitMode == LineFitMode.Regression ? 1 : 3;
        double[] weights = new double[normalized.Length];
        Array.Fill(weights, 1.0);

        bool fitSucceeded = RefineCircleWeighted(normalized, weights, ref cx, ref cy, ref r);
        for (int iteration = 1; fitSucceeded && iteration < iterations; iteration++)
        {
            double[] residuals = new double[normalized.Length];
            for (int i = 0; i < normalized.Length; i++)
            {
                residuals[i] = Math.Abs(DistanceTo(normalized[i], cx, cy) - r);
            }

            double scaleEstimate = RobustResidualScale(residuals);
            if (scaleEstimate <= 0)
            {
                break;
            }

            double clippingFactor = fitMode == LineFitMode.Huber ? 1.0 : 2.0;
            double threshold = clippingFactor * scaleEstimate;
            for (int i = 0; i < normalized.Length; i++)
            {
                double residual = residuals[i];
                weights[i] = fitMode switch
                {
                    LineFitMode.Huber => residual <= threshold ? 1.0 : threshold / residual,
                    LineFitMode.Tukey => residual <= threshold ? Square(1 - Square(residual / threshold)) : 0.0,
                    LineFitMode.Drop => residual <= threshold ? 1.0 : 0.0,
                    LineFitMode.Gauss => Math.Exp(-Square(residual / threshold)),
                    _ => 1.0
                };
            }

            fitSucceeded = RefineCircleWeighted(normalized, weights, ref cx, ref cy, ref r);
        }

        if (!fitSucceeded)
        {
            return false;
        }

        double finalCenterX = meanX + (cx * scale);
        double finalCenterY = meanY + (cy * scale);
        double finalRadius = r * scale;
        if (!double.IsFinite(finalCenterX) || !double.IsFinite(finalCenterY) ||
            !double.IsFinite(finalRadius) || finalRadius <= 0)
        {
            return false;
        }

        center = new PointD(finalCenterX, finalCenterY);
        radius = finalRadius;
        return true;
    }

    private static bool TryNormalize(PointD[] points, out double meanX, out double meanY, out double scale)
    {
        meanX = meanY = scale = 0;
        double sumX = 0, sumY = 0;
        foreach (PointD point in points)
        {
            sumX += point.X;
            sumY += point.Y;
        }

        meanX = sumX / points.Length;
        meanY = sumY / points.Length;
        double maxSquaredDistance = 0;
        foreach (PointD point in points)
        {
            double dx = point.X - meanX;
            double dy = point.Y - meanY;
            maxSquaredDistance = Math.Max(maxSquaredDistance, (dx * dx) + (dy * dy));
        }

        if (maxSquaredDistance <= 1e-18 || !double.IsFinite(maxSquaredDistance))
        {
            return false;
        }

        scale = Math.Sqrt(maxSquaredDistance);
        return double.IsFinite(scale) && scale > 0;
    }

    /// <summary>
    /// Levenberg-Marquardt refinement of a weighted geometric circle fit. Damping keeps
    /// the step finite and descending when the point set is close to degenerate.
    /// </summary>
    private static bool RefineCircleWeighted(PointD[] points, double[] weights, ref double cx, ref double cy, ref double r)
    {
        double cost = CircleCost(points, weights, cx, cy, r);
        if (!double.IsFinite(cost))
        {
            return false;
        }

        double lambda = 1e-3;
        const int maxSteps = 25;
        for (int step = 0; step < maxSteps; step++)
        {
            double j00 = 0, j01 = 0, j02 = 0, j11 = 0, j12 = 0, j22 = 0;
            double g0 = 0, g1 = 0, g2 = 0;
            int used = 0;
            for (int i = 0; i < points.Length; i++)
            {
                double weight = weights[i];
                if (weight <= 0)
                {
                    continue;
                }

                double dx = points[i].X - cx;
                double dy = points[i].Y - cy;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance < 1e-12)
                {
                    continue;
                }

                double residual = distance - r;
                double a = -dx / distance;
                double b = -dy / distance;
                const double c = -1;
                j00 += weight * a * a; j01 += weight * a * b; j02 += weight * a * c;
                j11 += weight * b * b; j12 += weight * b * c; j22 += weight * c * c;
                g0 += weight * a * residual; g1 += weight * b * residual; g2 += weight * c * residual;
                used++;
            }

            if (used < 3)
            {
                return false;
            }

            bool accepted = false;
            for (int attempt = 0; attempt < 8 && !accepted; attempt++)
            {
                // Marquardt scaling: damp each normal-equation diagonal relative to its
                // own magnitude, with a unit floor for parameters that carried no weight.
                double d0 = j00 > 1e-12 ? j00 : 1.0;
                double d1 = j11 > 1e-12 ? j11 : 1.0;
                double d2 = j22 > 1e-12 ? j22 : 1.0;
                if (!Solve3x3(
                        j00 + (lambda * d0), j01, j02,
                        j11 + (lambda * d1), j12,
                        j22 + (lambda * d2),
                        -g0, -g1, -g2,
                        out double stepX, out double stepY, out double stepR))
                {
                    lambda *= 10;
                    continue;
                }

                double nextX = cx + stepX;
                double nextY = cy + stepY;
                double nextR = r + stepR;
                double nextCost = CircleCost(points, weights, nextX, nextY, nextR);
                if (double.IsFinite(nextCost) && nextCost <= cost)
                {
                    bool converged = Math.Abs(stepX) + Math.Abs(stepY) + Math.Abs(stepR) < 1e-12;
                    cx = nextX;
                    cy = nextY;
                    r = nextR;
                    cost = nextCost;
                    lambda = Math.Max(lambda / 3, 1e-12);
                    accepted = true;
                    if (converged)
                    {
                        return true;
                    }
                }
                else
                {
                    lambda = Math.Min(lambda * 10, 1e12);
                }
            }

            if (!accepted)
            {
                // No descending step remains; the current estimate is a local minimum.
                return true;
            }
        }

        return true;
    }

    private static double CircleCost(PointD[] points, double[] weights, double cx, double cy, double r)
    {
        double sum = 0;
        for (int i = 0; i < points.Length; i++)
        {
            double weight = weights[i];
            if (weight <= 0)
            {
                continue;
            }

            double dx = points[i].X - cx;
            double dy = points[i].Y - cy;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            double residual = distance - r;
            sum += weight * residual * residual;
        }

        return sum;
    }

    private static double DistanceTo(PointD point, double cx, double cy)
    {
        double dx = point.X - cx;
        double dy = point.Y - cy;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool FitOrthogonalLine(
        PointD[] points,
        int begin,
        int count,
        LineFitMode fitMode,
        out PointD linePoint,
        out VectorD direction)
    {
        linePoint = default;
        direction = default;
        double[] weights = new double[count];
        Array.Fill(weights, 1.0);

        int iterations = fitMode == LineFitMode.Regression ? 1 : 3;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // Robust modes must not start from the unweighted PCA fit: one far outlier adds
            // O(d^2) variance, tilts the principal axis, and inflates the clean-point residuals
            // before the scale is estimated. The LMedS seed locates the dominant line first;
            // the IRLS loop then refines it with data-driven weights.
            bool fitted = iteration == 0 && fitMode != LineFitMode.Regression
                ? TrySeedRobustLine(points, begin, count, out linePoint, out direction)
                : WeightedOrthogonal(points, begin, count, weights, out linePoint, out direction);
            if (!fitted)
            {
                return false;
            }

            if (fitMode == LineFitMode.Regression)
            {
                break;
            }

            double[] residuals = new double[count];
            for (int i = 0; i < count; i++)
            {
                VectorD delta = points[begin + i] - linePoint;
                residuals[i] = Math.Abs((delta.X * direction.Y) - (delta.Y * direction.X));
            }

            double scale = RobustResidualScale(residuals);
            if (scale <= 0)
            {
                break;
            }

            double clippingFactor = fitMode == LineFitMode.Huber ? 1.0 : 2.0;
            double threshold = clippingFactor * scale;
            for (int i = 0; i < count; i++)
            {
                double r = residuals[i];
                weights[i] = fitMode switch
                {
                    LineFitMode.Huber => r <= threshold ? 1.0 : threshold / r,
                    LineFitMode.Tukey => r <= threshold ? Square(1 - Square(r / threshold)) : 0.0,
                    LineFitMode.Drop => r <= threshold ? 1.0 : 0.0,
                    LineFitMode.Gauss => Math.Exp(-Square(r / threshold)),
                    _ => 1.0
                };
            }
        }

        return WeightedOrthogonal(points, begin, count, weights, out linePoint, out direction);
    }

    private static bool WeightedOrthogonal(
        PointD[] points,
        int begin,
        int count,
        double[] weights,
        out PointD linePoint,
        out VectorD direction)
    {
        linePoint = default;
        direction = default;
        double weightSum = 0, meanX = 0, meanY = 0;
        for (int i = 0; i < count; i++)
        {
            double w = weights[i];
            weightSum += w;
            meanX += w * points[begin + i].X;
            meanY += w * points[begin + i].Y;
        }

        if (weightSum <= 1e-12)
        {
            return false;
        }

        meanX /= weightSum;
        meanY /= weightSum;
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < count; i++)
        {
            double w = weights[i];
            double dx = points[begin + i].X - meanX;
            double dy = points[begin + i].Y - meanY;
            sxx += w * dx * dx;
            sxy += w * dx * dy;
            syy += w * dy * dy;
        }

        // Principal eigenvector of the 2x2 scatter matrix.
        double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        direction = new VectorD(Math.Cos(theta), Math.Sin(theta));
        linePoint = new PointD(meanX, meanY);
        return direction.LengthSquared > 1e-12;
    }

    /// <summary>上限：LMedS 种子只评这么多条锚点候选线，保证大点集下仍是 O(n log n) 级别的额外开销。</summary>
    private const int MaxSeedCandidateCount = 128;

    /// <summary>
    /// Deterministic least-median-of-squares seed for robust line modes. The anchor is the
    /// coordinate-wise median sample, which ignores a minority of gross outliers; each
    /// candidate line runs from the anchor to one evenly spaced sample, and the candidate with
    /// the smallest median absolute orthogonal residual becomes the seed. Scoring by a median
    /// needs no residual scale, so the contamination that defeats the PCA initialization cannot
    /// move the seed by more than it moves the median.
    /// </summary>
    private static bool TrySeedRobustLine(
        PointD[] points,
        int begin,
        int count,
        out PointD linePoint,
        out VectorD direction)
    {
        linePoint = default;
        direction = default;
        if (count < 2)
        {
            return false;
        }

        var xValues = new double[count];
        var yValues = new double[count];
        for (int i = 0; i < count; i++)
        {
            PointD point = points[begin + i];
            xValues[i] = point.X;
            yValues[i] = point.Y;
        }

        Array.Sort(xValues);
        Array.Sort(yValues);
        double anchorX = MedianOfSorted(xValues);
        double anchorY = MedianOfSorted(yValues);
        if (!double.IsFinite(anchorX) || !double.IsFinite(anchorY))
        {
            return false;
        }

        PointD anchor = new(anchorX, anchorY);
        int candidateCount = Math.Min(count, MaxSeedCandidateCount);
        var residuals = new double[count];
        var sortedResiduals = new double[count];
        double bestMedian = double.PositiveInfinity;
        double bestDirectionX = 0;
        double bestDirectionY = 0;
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            int index = candidateCount == 1
                ? 0
                : (int)(((long)candidate * (count - 1)) / (candidateCount - 1));
            VectorD delta = points[begin + index] - anchor;
            double length = delta.Length;
            if (length <= 1e-12)
            {
                continue;
            }

            double ux = delta.X / length;
            double uy = delta.Y / length;
            for (int i = 0; i < count; i++)
            {
                VectorD sampleDelta = points[begin + i] - anchor;
                residuals[i] = Math.Abs((sampleDelta.X * uy) - (sampleDelta.Y * ux));
            }

            Array.Copy(residuals, sortedResiduals, count);
            Array.Sort(sortedResiduals, 0, count);
            double median = MedianOfSorted(sortedResiduals);
            if (median < bestMedian)
            {
                bestMedian = median;
                bestDirectionX = ux;
                bestDirectionY = uy;
            }
        }

        if (!double.IsFinite(bestMedian))
        {
            return false;
        }

        linePoint = anchor;
        direction = new VectorD(bestDirectionX, bestDirectionY);
        return true;
    }

    private static double MedianOfSorted(double[] sorted)
    {
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double RobustResidualScale(double[] residuals)
    {
        // Robust sigma from the median absolute residual. The median (rather than the MAD
        // around the median) keeps at least half the points below the clipping threshold,
        // so Tukey/Drop weights never collapse to all-zero once the inlier set fits well.
        double scale = 1.4826 * Median(residuals);
        if (scale > 1e-9)
        {
            return scale;
        }

        // The median collapsed because more than half the points sit on the model. Any
        // nonzero residual is then an outlier, so floor the scale instead of letting the
        // weights silently degenerate to "keep everything"; only an all-zero residual
        // set (nothing to reweight) reports 0.
        foreach (double residual in residuals)
        {
            if (residual > 1e-9)
            {
                return 1e-9;
            }
        }

        return 0;
    }

    private static bool KasaCircle(PointD[] points, out double cx, out double cy, out double r)
    {
        cx = cy = r = 0;
        // Solve the linear system for (x^2+y^2) = A*x + B*y + C in least squares.
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sz = 0, sxz = 0, syz = 0;
        int n = points.Length;
        foreach (PointD point in points)
        {
            double x = point.X;
            double y = point.Y;
            double z = (x * x) + (y * y);
            sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
            sz += z; sxz += x * z; syz += y * z;
        }

        if (!Solve3x3(sxx, sxy, sx, syy, sy, n, sxz, syz, sz, out double a, out double b, out double c))
        {
            return false;
        }

        cx = a / 2;
        cy = b / 2;
        double r2 = c + (cx * cx) + (cy * cy);
        if (r2 <= 0 || !double.IsFinite(r2))
        {
            return false;
        }

        r = Math.Sqrt(r2);
        return double.IsFinite(cx) && double.IsFinite(cy);
    }

    private static bool Solve3x3(
        double a00, double a01, double a02,
        double a11, double a12, double a22,
        double b0, double b1, double b2,
        out double x0, out double x1, out double x2)
    {
        // Symmetric matrix [[a00,a01,a02],[a01,a11,a12],[a02,a12,a22]] and right-hand side b.
        x0 = x1 = x2 = 0;
        double[,] m =
        {
            { a00, a01, a02, b0 },
            { a01, a11, a12, b1 },
            { a02, a12, a22, b2 }
        };

        double scale = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                scale = Math.Max(scale, Math.Abs(m[i, j]));
            }
        }

        if (!double.IsFinite(scale) || scale <= 0)
        {
            return false;
        }

        // A 3x3 determinant scales with scale^3, so judge each pivot against the data
        // scale instead of an absolute epsilon; otherwise large coordinates always pass
        // and tiny ones always fail regardless of conditioning.
        double pivotFloor = 1e-12 * scale;

        for (int column = 0; column < 3; column++)
        {
            int pivotRow = column;
            double pivotMagnitude = Math.Abs(m[column, column]);
            for (int row = column + 1; row < 3; row++)
            {
                double magnitude = Math.Abs(m[row, column]);
                if (magnitude > pivotMagnitude)
                {
                    pivotMagnitude = magnitude;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude <= pivotFloor)
            {
                return false;
            }

            if (pivotRow != column)
            {
                for (int j = column; j < 4; j++)
                {
                    (m[column, j], m[pivotRow, j]) = (m[pivotRow, j], m[column, j]);
                }
            }

            for (int row = column + 1; row < 3; row++)
            {
                double factor = m[row, column] / m[column, column];
                if (factor == 0)
                {
                    continue;
                }

                for (int j = column; j < 4; j++)
                {
                    m[row, j] -= factor * m[column, j];
                }
            }
        }

        x2 = m[2, 3] / m[2, 2];
        x1 = (m[1, 3] - (m[1, 2] * x2)) / m[1, 1];
        x0 = (m[0, 3] - (m[0, 1] * x1) - (m[0, 2] * x2)) / m[0, 0];
        return double.IsFinite(x0) && double.IsFinite(x1) && double.IsFinite(x2);
    }

    private static PointD Project(PointD point, PointD linePoint, VectorD direction)
    {
        VectorD delta = point - linePoint;
        double t = (delta.X * direction.X) + (delta.Y * direction.Y);
        return linePoint + (direction * t);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double Square(double value) => value * value;

    private static bool IsFinite(PointD point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
