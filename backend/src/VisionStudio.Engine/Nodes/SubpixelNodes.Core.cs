using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 第五批（2026-10-14）· 5B 亚像素测量族共享核心。
/// 1D 剖面采样（沿线 + 垂直条带平均）→ 梯度 → 极性/对比度候选 → 抛物线/质心亚像素细化。
/// measure.caliper 与亚像素测量节点共享采样、极性与选边规则；是否细化由 refine 参数决定。
/// 输出拟合误差与有效采样点数，且"未找到合格边缘"返回 found=false（业务结果）而非异常。
/// </summary>
internal static class SubpixelEdgeCore
{
    /// <summary>沿搜索线采样 1D 灰度剖面（垂直条带平均）。ROI 掩膜外采样点跳过；整点无效时为 NaN。</summary>
    public static double[] SampleProfile(Mat gray, Mat? mask, VisionPoint2D start, VisionPoint2D end, int sampleWidth,
        out VisionPoint2D[] points, out double spacing, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sampleWidth = NormalizeSampleWidth(sampleWidth);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 2)
            throw new InvalidOperationException("The search line is too short (minimum 2 px).");

        var nx = -dy / length;
        var ny = dx / length;
        var steps = Math.Max(2, (int)Math.Ceiling(length));
        spacing = length / steps;
        var profile = new double[steps + 1];
        points = new VisionPoint2D[steps + 1];
        var half = sampleWidth / 2;
        var rows = gray.Rows;
        var columns = gray.Cols;

        for (var i = 0; i <= steps; i++)
        {
            if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var t = i / (double)steps;
            var x = start.X + dx * t;
            var y = start.Y + dy * t;
            points[i] = new VisionPoint2D(x, y);

            double sum = 0;
            var count = 0;
            for (var w = -half; w <= half; w++)
            {
                var sampleX = x + nx * w;
                var sampleY = y + ny * w;
                if (!TryBilinearSample(gray, mask, sampleX, sampleY, rows, columns, out var intensity)) continue;
                sum += intensity;
                count++;
            }
            profile[i] = count > 0 ? sum / count : double.NaN;
        }

        return profile;
    }

    private static bool TryBilinearSample(Mat gray, Mat? mask, double x, double y, int rows, int columns, out double intensity)
    {
        intensity = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        var nearestX = (int)Math.Round(x);
        var nearestY = (int)Math.Round(y);
        if (nearestX < 0 || nearestY < 0 || nearestX >= columns || nearestY >= rows
            || mask is not null && mask.At<byte>(nearestY, nearestX) == 0)
            return false;

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var xFraction = x - x0;
        var yFraction = y - y0;
        double weightedSum = 0;
        double totalWeight = 0;
        for (var dy = 0; dy <= 1; dy++)
        for (var dx = 0; dx <= 1; dx++)
        {
            var weight = (dx == 0 ? 1 - xFraction : xFraction) * (dy == 0 ? 1 - yFraction : yFraction);
            if (weight <= 0) continue;
            var px = x0 + dx;
            var py = y0 + dy;
            if (px < 0 || py < 0 || px >= columns || py >= rows
                || mask is not null && mask.At<byte>(py, px) == 0)
                continue;
            weightedSum += gray.At<byte>(py, px) * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0) return false;
        intensity = weightedSum / totalWeight;
        return double.IsFinite(intensity);
    }

    public static int NormalizeSampleWidth(int sampleWidth)
    {
        var normalized = Math.Clamp(sampleWidth, 1, 99);
        return normalized % 2 == 0 ? Math.Min(99, normalized + 1) : normalized;
    }

    public static string ChoiceParameter(NodeDefinition node, string name, string fallback, params string[] allowed)
    {
        var value = node.GetString(name, fallback).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)) value = fallback.Trim().ToLowerInvariant();
        if (allowed.Contains(value, StringComparer.Ordinal)) return value;
        throw new InvalidOperationException($"Parameter '{name}' must be one of [{string.Join(", ", allowed)}].");
    }

    /// <summary>
    /// 边缘搜索结果。Position 为沿搜索线距起点的亚像素距离（px，已按采样间距换算）；
    /// FitError 为五个梯度采样点对最小二乘抛物线的 RMS 残差（灰度级，-1 表示不可可靠拟合）；
    /// SampleCount 为有效梯度采样数。
    /// </summary>
    public readonly record struct EdgeResult(
        bool Found,
        double Position,
        double Strength,
        double FitError,
        int SampleCount,
        string Reason)
    {
        public static EdgeResult NotFound(string reason) => new(false, 0, 0, 0, 0, reason);
    }

    public static EdgeResult FindEdge(double[] profile, double spacing, string polarity, double minContrast, string direction, string refine,
        double sigma, CancellationToken cancellationToken = default, List<CaliperEdgeCandidate>? diagnosticCandidates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edge = JLVisionEdgeMeasurement.Find(profile, spacing, polarity, minContrast, direction, refine, sigma, cancellationToken, diagnosticCandidates);
        return new(edge.Found, edge.Position, edge.Strength, edge.Error, edge.Count, edge.Reason);
    }

    public static double SigmaParameter(NodeDefinition node)
    {
        var sigma = RequireFiniteParameter(node, "sigma", 1);
        if (sigma is < 0.4 or > 100)
            throw new InvalidOperationException("Parameter 'sigma' must be between 0.4 and 100.");
        return sigma;
    }

    public static double RequireFiniteParameter(NodeDefinition node, string name, double fallback)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"Parameter '{name}' must be a finite number.");
        return value;
    }

    public static int ClampedIntParameter(NodeDefinition node, string name, int fallback, int minimum, int maximum)
    {
        var value = RequireFiniteParameter(node, name, fallback);
        // Match the frontend's Math.round so fractional legacy/workflow values preview and execute identically.
        return (int)Math.Clamp(Math.Floor(value + 0.5), minimum, maximum);
    }

    /// <summary>沿搜索线的坐标 → 图像点。</summary>
    public static VisionPoint2D Locate(VisionPoint2D start, VisionPoint2D end, double position)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return new VisionPoint2D(start.X + dx * position / length, start.Y + dy * position / length);
    }

    /// <summary>True when the whole search segment lies outside the image rectangle; no clipping happens here.</summary>
    public static bool SegmentBoundsOutsideImage(VisionPoint2D start, VisionPoint2D end, int width, int height) =>
        Math.Max(start.X, end.X) < 0 || Math.Min(start.X, end.X) > width - 1
        || Math.Max(start.Y, end.Y) < 0 || Math.Min(start.Y, end.Y) > height - 1;

    public static (VisionPoint2D Start, VisionPoint2D End, bool Clamped, bool Intersects) ClampLine(
        VisionPoint2D start, VisionPoint2D end, int width, int height, double maximumCoordinate = 10_000_000)
    {
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Cannot clip a search line to an empty image.");
        if (!double.IsFinite(maximumCoordinate) || maximumCoordinate <= 0
            || new[] { start.X, start.Y, end.X, end.Y }.Any(value => !double.IsFinite(value) || Math.Abs(value) > maximumCoordinate))
            throw new InvalidOperationException($"Search line coordinates must be finite and within +/-{maximumCoordinate:0} pixels.");

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var t0 = 0d;
        var t1 = 1d;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-15) return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0) return false;
                t1 = Math.Min(t1, r);
            }
            return t0 <= t1;
        }

        if (!Clip(-dx, start.X) || !Clip(dx, width - 1d - start.X)
            || !Clip(-dy, start.Y) || !Clip(dy, height - 1d - start.Y) || t0 > t1)
            return (start, end, true, false);

        var clippedStart = new VisionPoint2D(
            Math.Clamp(start.X + t0 * dx, 0, width - 1),
            Math.Clamp(start.Y + t0 * dy, 0, height - 1));
        var clippedEnd = new VisionPoint2D(
            Math.Clamp(start.X + t1 * dx, 0, width - 1),
            Math.Clamp(start.Y + t1 * dy, 0, height - 1));
        var clamped = Math.Abs(clippedStart.X - start.X) > 1e-9 || Math.Abs(clippedStart.Y - start.Y) > 1e-9
            || Math.Abs(clippedEnd.X - end.X) > 1e-9 || Math.Abs(clippedEnd.Y - end.Y) > 1e-9;
        return (clippedStart, clippedEnd, clamped, true);
    }

    /// <summary>解析搜索线：searchLine 输入端口优先，否则用参数坐标。</summary>
    public static VisionLine2D ResolveSearchLine(NodeExecutionContext context, NodeDefinition node)
    {
        if (context.TryGetValue("searchLine", out var value))
        {
            var line = value.Require<VisionLine2D>("searchLine");
            if (!double.IsFinite(line.Start.X) || !double.IsFinite(line.Start.Y) || !double.IsFinite(line.End.X) || !double.IsFinite(line.End.Y))
                throw new InvalidOperationException("The connected search line has non-finite endpoints.");
            return MeasurementFixture.Apply(line, MeasurementFixture.Resolve(context));
        }
        var start = new VisionPoint2D(node.GetDouble("startX", 120), node.GetDouble("startY", 240));
        var end = new VisionPoint2D(node.GetDouble("endX", 520), node.GetDouble("endY", 240));
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y))
            throw new InvalidOperationException("Search line parameters must be finite numbers.");
        return MeasurementFixture.Apply(new VisionLine2D(start, end), MeasurementFixture.Resolve(context));
    }
}

