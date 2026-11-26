using System;
using System.Buffers;
using System.Collections.Generic;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis;

/// <summary>
/// Managed one-dimensional caliper. Extracts a gray-value profile along the measure
/// axis, smooths it with a discrete Gaussian, and locates subpixel edges from the
/// parabolic-interpolated peaks of the central-difference derivative.
/// Chinese: 纯托管卡尺。沿测量轴提取灰度剖面，离散高斯平滑后对中心差分导数做抛物线亚像素定位。
/// English: The profile axis follows the native y-up angle convention, so a measurement
/// direction (x, y) samples along (x, -y) in image coordinates.
/// </summary>
public static class CaliperAlgorithm
{
    /// <summary>
    /// Finds all edges along a rectangular caliper, ordered by position along the axis.
    /// </summary>
    public static IReadOnlyList<CaliperEdge> MeasureEdges(
        IntensityImage image,
        PointD center,
        VectorD measurementDirection,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (measurementDirection.LengthSquared < 1e-9 || searchRange <= 0)
        {
            return [];
        }

        measurementDirection = measurementDirection.Normalized();
        VectorD axis = new(measurementDirection.X, -measurementDirection.Y);
        VectorD perpendicular = new(-axis.Y, axis.X);
        double effectiveSigma = Math.Max(0.5, sigma);
        double halfWidth = Math.Max(0.5, averagingHalfWidth);
        int averageCount = (2 * (int)Math.Floor(halfWidth)) + 1;
        int halfAverage = averageCount / 2;

        // The profile truncates to samples that fall inside the image instead of reading
        // border pixels as 0. A measure crossing the border of a uniform region therefore
        // yields no edge. The in-image samples of a straight profile form one contiguous
        // run, so subpixel spacing stays at one pixel.
        // Profile, smoothing and derivative buffers are pooled: one detection can run this
        // routine hundreds of times (radial/arc calipers), so per-call arrays would dominate
        // allocation churn in interactive refreshes.
        int sampleCount = (2 * searchRange) + 1;
        double[] profile = ArrayPool<double>.Shared.Rent(sampleCount);
        double[] smoothed = ArrayPool<double>.Shared.Rent(sampleCount);
        double[] derivative = ArrayPool<double>.Shared.Rent(sampleCount);
        try
        {
            int profileLength = 0;
            int firstValid = -1;
            for (int i = 0; i < sampleCount; i++)
            {
                double t = i - searchRange;
                PointD sampleCenter = center + (axis * t);
                if (!IsInBounds(image, sampleCenter.X, sampleCenter.Y))
                {
                    continue;
                }

                if (firstValid < 0)
                {
                    firstValid = i;
                }

                double sum = 0;
                int averaged = 0;
                for (int k = -halfAverage; k <= halfAverage; k++)
                {
                    PointD sample = sampleCenter + (perpendicular * k);
                    if (!IsInBounds(image, sample.X, sample.Y))
                    {
                        continue;
                    }

                    sum += image.SampleBilinear(sample.X, sample.Y);
                    averaged++;
                }

                profile[profileLength++] = averaged > 0 ? sum / averaged : 0;
            }

            if (profileLength < 3)
            {
                return [];
            }

            double profileStartOffset = firstValid - searchRange;
            Smooth(profile.AsSpan(0, profileLength), effectiveSigma, smoothed.AsSpan(0, profileLength));
            Span<double> smoothedValues = smoothed.AsSpan(0, profileLength);
            Span<double> derivativeValues = derivative.AsSpan(0, profileLength);
            // Pooled buffers are not zeroed; the derivative endpoints are never written by the
            // central difference below but are read as neighbour samples by the peak test.
            derivativeValues[0] = 0;
            derivativeValues[^1] = 0;
            for (int i = 1; i < profileLength - 1; i++)
            {
                derivativeValues[i] = (smoothedValues[i + 1] - smoothedValues[i - 1]) / 2;
            }

            double amplitudeWindow = Math.Max(0.5, 2 * effectiveSigma);
            double threshold = Math.Max(0, minimumGradient);
            var edges = new List<CaliperEdge>();
            for (int i = 1; i < profileLength - 1; i++)
            {
                double magnitude = Math.Abs(derivativeValues[i]);
                if (magnitude <= 0 ||
                    magnitude < Math.Abs(derivativeValues[i - 1]) ||
                    magnitude < Math.Abs(derivativeValues[i + 1]))
                {
                    continue;
                }

                // Skip one of two equal adjacent peaks so a flat top yields a single edge.
                if (magnitude == Math.Abs(derivativeValues[i - 1]) && derivativeValues[i - 1] != 0)
                {
                    continue;
                }

                bool positive = derivativeValues[i] > 0;
                if (polarity == CaliperEdgePolarity.DarkToLight && !positive)
                {
                    continue;
                }

                if (polarity == CaliperEdgePolarity.LightToDark && positive)
                {
                    continue;
                }

                double subpixel = SubpixelPeak(derivativeValues, i);
                double offset = profileStartOffset + subpixel;
                double amplitude = Math.Abs(
                    Interpolate(smoothedValues, subpixel + amplitudeWindow) -
                    Interpolate(smoothedValues, subpixel - amplitudeWindow));
                if (amplitude < threshold)
                {
                    continue;
                }

                PointD point = center + (axis * offset);
                edges.Add(new CaliperEdge(point, amplitude, offset));
            }

            edges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
            return edges;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(profile);
            ArrayPool<double>.Shared.Return(smoothed);
            ArrayPool<double>.Shared.Return(derivative);
        }
    }

    /// <summary>Selects a single edge by descending amplitude (1-based selection index).</summary>
    public static bool TryMeasureSingle(
        IntensityImage image,
        PointD center,
        VectorD measurementDirection,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        int edgeSelection,
        out CaliperEdge edge)
    {
        IReadOnlyList<CaliperEdge> edges = MeasureEdges(
            image, center, measurementDirection, searchRange, averagingHalfWidth, sigma, minimumGradient, polarity);
        if (edges.Count == 0)
        {
            edge = default;
            return false;
        }

        // MeasureEdges returns its own list, so sorting it in place avoids a per-call copy.
        var ordered = edges as List<CaliperEdge> ?? [.. edges];
        ordered.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        int index = Math.Clamp(edgeSelection, 1, ordered.Count) - 1;
        edge = ordered[index];
        return true;
    }

    /// <summary>
    /// Measures a pair of edges that straddle the measure center, preferring the pair with
    /// the strongest combined amplitude and the smallest center offset.
    /// </summary>
    public static bool TryMeasurePair(
        IntensityImage image,
        PointD center,
        VectorD measurementDirection,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        double minimumEdgeGap,
        double nominalEdgeGap,
        double nominalEdgeGapTolerance,
        out CaliperEdgePair pair)
    {
        pair = default;
        IReadOnlyList<CaliperEdge> edges = MeasureEdges(
            image, center, measurementDirection, searchRange, averagingHalfWidth,
            sigma, minimumGradient, polarity);
        if (edges.Count < 2)
        {
            return false;
        }

        PairCandidate? best = null;
        for (int i = 0; i + 1 < edges.Count; i++)
        {
            CaliperEdge first = edges[i];
            CaliperEdge second = edges[i + 1];
            double gap = second.Offset - first.Offset;
            if (gap < Math.Max(0, minimumEdgeGap))
            {
                continue;
            }

            bool straddlesCenter = first.Offset <= 0 && second.Offset >= 0;
            if (!straddlesCenter)
            {
                continue;
            }

            double score = 0.7 * (first.Score + second.Score) + 0.3 * Math.Min(first.Score, second.Score);
            if (nominalEdgeGap > 0)
            {
                double deviation = Math.Abs(gap - nominalEdgeGap) - Math.Max(0, nominalEdgeGapTolerance);
                if (deviation > 0)
                {
                    score -= deviation * 1.25;
                }
            }

            double centerOffset = Math.Abs((first.Offset + second.Offset) / 2);
            var candidate = new PairCandidate(first, second, gap, score, centerOffset);
            if (best is null || candidate.Score > best.Value.Score ||
                (candidate.Score == best.Value.Score && candidate.CenterOffset < best.Value.CenterOffset))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        pair = new CaliperEdgePair(best.Value.Edge1, best.Value.Edge2, best.Value.Gap);
        return true;
    }

    /// <summary>
    /// Measures one radial edge sample using a rectangular caliper oriented along the
    /// radial direction at the nominal radius.
    /// </summary>
    public static bool TryMeasureCircular(
        IntensityImage image,
        PointD center,
        double nominalRadius,
        double angleRadians,
        double angleExtent,
        int searchRange,
        int averagingHalfWidth,
        double sigma,
        double minimumGradient,
        CaliperEdgePolarity polarity,
        int edgeSelection,
        out CaliperEdge sample)
    {
        sample = default;
        if (!double.IsFinite(nominalRadius) || nominalRadius <= 0 ||
            !double.IsFinite(angleRadians) || searchRange <= 0)
        {
            return false;
        }

        VectorD radialDirection = new(Math.Cos(angleRadians), Math.Sin(angleRadians));
        PointD sampleCenter = center + (radialDirection * nominalRadius);
        // The caliper mirrors its measure direction into the native y-up axis (x, y) -> (x, -y).
        // radialDirection is already in image coordinates, so pre-mirror it to keep the profile
        // pointing truly outward; otherwise the gradient sign flips around the circle (cos 2θ).
        VectorD caliperDirection = new(radialDirection.X, -radialDirection.Y);
        return TryMeasureSingle(
            image, sampleCenter, caliperDirection, searchRange, averagingHalfWidth,
            sigma, minimumGradient, polarity, edgeSelection, out sample);
    }

    private static bool IsInBounds(IntensityImage image, double x, double y)
    {
        int ix = (int)Math.Round(x, MidpointRounding.AwayFromZero);
        int iy = (int)Math.Round(y, MidpointRounding.AwayFromZero);
        return (uint)ix < (uint)image.Width && (uint)iy < (uint)image.Height;
    }

    private static void Smooth(ReadOnlySpan<double> profile, double sigma, Span<double> destination)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        int kernelLength = (2 * radius) + 1;
        double[]? rentedKernel = null;
        Span<double> kernel = kernelLength <= 128
            ? stackalloc double[kernelLength]
            : (rentedKernel = ArrayPool<double>.Shared.Rent(kernelLength));
        try
        {
            double kernelSum = 0;
            for (int k = -radius; k <= radius; k++)
            {
                double weight = Math.Exp(-k * k / (2 * sigma * sigma));
                kernel[k + radius] = weight;
                kernelSum += weight;
            }

            for (int k = 0; k < kernelLength; k++)
            {
                kernel[k] /= kernelSum;
            }

            for (int i = 0; i < profile.Length; i++)
            {
                double sum = 0;
                double weightSum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int index = i + k;
                    if (index < 0 || index >= profile.Length)
                    {
                        continue;
                    }

                    double weight = kernel[k + radius];
                    sum += profile[index] * weight;
                    weightSum += weight;
                }

                destination[i] = weightSum > 0 ? sum / weightSum : profile[i];
            }
        }
        finally
        {
            if (rentedKernel is not null)
            {
                ArrayPool<double>.Shared.Return(rentedKernel);
            }
        }
    }

    private static double SubpixelPeak(ReadOnlySpan<double> derivative, int index)
    {
        double a = Math.Abs(derivative[index - 1]);
        double b = Math.Abs(derivative[index]);
        double c = Math.Abs(derivative[index + 1]);
        double denominator = a - (2 * b) + c;
        double offset = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return index + Math.Clamp(offset, -1, 1);
    }

    private static double Interpolate(ReadOnlySpan<double> values, double position)
    {
        int lower = (int)Math.Floor(position);
        if (lower < 0)
        {
            return values[0];
        }

        if (lower >= values.Length - 1)
        {
            return values[^1];
        }

        double fraction = position - lower;
        return (values[lower] * (1 - fraction)) + (values[lower + 1] * fraction);
    }

    private readonly record struct PairCandidate(
        CaliperEdge Edge1,
        CaliperEdge Edge2,
        double Gap,
        double Score,
        double CenterOffset);
}
