using System;
using System.Collections.Generic;
using ImageViewer.Models;

namespace ImageViewer.Core.Analysis;

/// <summary>
/// Managed threshold segmentation and connected-component analysis.
/// Chinese: Otsu 取最大类间方差的最小灰度阈值；亮目标取 g&gt;T，暗目标取 g≤T。
/// 手动阈值上下界均含等号；Gray16 阈值按 ×257 缩放。连通域为 8 邻接，按首像素行优先排序。
/// English: Otsu picks the smallest gray maximizing between-class variance; light targets
/// select g&gt;T and dark targets g≤T. Manual thresholds include both bounds. Components are
/// 8-connected and ordered row-major by their first pixel.
/// </summary>
/// <remarks>
/// Chinese: 可选的 <c>inside</c> 掩码把搜索区限制在任意形状内（如旋转矩形）。
/// 掩码外的像素既不进入 Otsu 直方图也不参与连通域。
/// English: An optional <c>inside</c> mask confines the search region to an arbitrary shape
/// such as a rotated rectangle. Masked-out pixels enter neither the Otsu histogram nor the
/// connected-component pass.
/// </remarks>
public static class BlobAlgorithm
{
    /// <summary>Produces the foreground mask for the requested threshold mode.</summary>
    public static bool[] Segment(IntensityImage image, bool useOtsu, int threshold, bool detectDark)
    {
        return Segment(image, inside: null, useOtsu, threshold, detectDark);
    }

    /// <summary>
    /// Produces the foreground mask restricted to <paramref name="inside"/>.
    /// Chinese: 掩码外的像素一律记为背景；<paramref name="inside"/> 为 null 时等价于全图搜索。
    /// </summary>
    /// <param name="image">Source intensity plane.</param>
    /// <param name="inside">Row-major search mask, or null to search the whole plane.</param>
    /// <param name="useOtsu">Select the threshold with Otsu's method.</param>
    /// <param name="threshold">Manual 8-bit threshold, ignored when <paramref name="useOtsu"/> is true.</param>
    /// <param name="detectDark">Select dark targets instead of light ones.</param>
    public static bool[] Segment(IntensityImage image, bool[]? inside, bool useOtsu, int threshold, bool detectDark)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.Width;
        int height = image.Height;
        int maximum = image.MaxValue;
        int pixelCount = checked(width * height);
        if (inside is not null && inside.Length != pixelCount)
        {
            throw new ArgumentException("Mask length must equal image width * height.", nameof(inside));
        }

        var mask = new bool[pixelCount];

        double effectiveThreshold;
        bool lightExclusive;
        if (useOtsu)
        {
            effectiveThreshold = OtsuThreshold(image, inside);
            lightExclusive = true;
        }
        else
        {
            effectiveThreshold = Math.Clamp(threshold, 0, byte.MaxValue);
            if (maximum == ushort.MaxValue)
            {
                effectiveThreshold *= 257.0;
            }

            lightExclusive = false;
        }

        bool IsForeground(double value) => detectDark
            ? value <= effectiveThreshold
            : lightExclusive ? value > effectiveThreshold : value >= effectiveThreshold;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = row + x;
                if (inside is not null && !inside[index])
                {
                    continue;
                }

                mask[index] = IsForeground(image[x, y]);
            }
        }

        return mask;
    }

    /// <summary>
    /// Labels 8-connected components of the mask, keeps those with area ≥ <paramref name="minArea"/>,
    /// and returns them ordered row-major by first pixel.
    /// </summary>
    public static IReadOnlyList<BlobFeature> Connect(bool[] mask, int width, int height, int minArea)
    {
        return Connect(mask, width, height, minArea, includeContours: false);
    }

    /// <summary>
    /// Labels 8-connected components and optionally traces each component's pixel-boundary contour.
    /// Chinese: <paramref name="includeContours"/> 为 true 时，每个斑点的 <c>Contour</c> 填入
    /// 沿像素格点描出的闭合多边形；顶点数超过 <paramref name="maximumContourVertices"/> 时按步长抽稀。
    /// English: When <paramref name="includeContours"/> is true each blob gets a closed polygon traced
    /// along the pixel grid; contours longer than <paramref name="maximumContourVertices"/> are decimated.
    /// </summary>
    /// <param name="mask">Row-major foreground mask.</param>
    /// <param name="width">Mask column count.</param>
    /// <param name="height">Mask row count.</param>
    /// <param name="minArea">Components smaller than this pixel count are dropped.</param>
    /// <param name="includeContours">Trace a boundary polygon for every surviving component.</param>
    /// <param name="maximumContourVertices">Upper bound on contour vertices per blob.</param>
    public static IReadOnlyList<BlobFeature> Connect(
        bool[] mask,
        int width,
        int height,
        int minArea,
        bool includeContours,
        int maximumContourVertices = 512)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (maximumContourVertices < 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumContourVertices), maximumContourVertices, "A closed contour needs at least three vertices.");
        }

        var labels = new int[mask.Length];
        var regions = new List<BlobFeature>();
        int nextLabel = 0;
        int areaThreshold = Math.Max(1, minArea);
        var stack = new Stack<int>();
        List<int>? component = includeContours ? [] : null;

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0)
            {
                continue;
            }

            nextLabel++;
            long area = 0;
            double sumRow = 0, sumColumn = 0;
            int row1 = int.MaxValue, column1 = int.MaxValue, row2 = int.MinValue, column2 = int.MinValue;
            component?.Clear();
            stack.Push(start);
            labels[start] = nextLabel;
            while (stack.Count > 0)
            {
                int index = stack.Pop();
                int row = index / width;
                int column = index % width;
                area++;
                sumRow += row;
                sumColumn += column;
                row1 = Math.Min(row1, row);
                column1 = Math.Min(column1, column);
                row2 = Math.Max(row2, row);
                column2 = Math.Max(column2, column);

                for (int dy = -1; dy <= 1; dy++)
                {
                    int neighborRow = row + dy;
                    if (neighborRow < 0 || neighborRow >= height)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int neighborColumn = column + dx;
                        if (neighborColumn < 0 || neighborColumn >= width)
                        {
                            continue;
                        }

                        int neighbor = (neighborRow * width) + neighborColumn;
                        if (mask[neighbor] && labels[neighbor] == 0)
                        {
                            labels[neighbor] = nextLabel;
                            stack.Push(neighbor);
                        }
                    }
                }

                component?.Add(index);
            }

            if (area >= areaThreshold)
            {
                PointD[]? contour = component is null
                    ? null
                    : TraceContour(mask, width, height, component, maximumContourVertices);
                regions.Add(new BlobFeature(
                    nextLabel,
                    (int)area,
                    new PointD(sumColumn / area, sumRow / area),
                    new RectD(column1, row1, column2 - column1 + 1, row2 - row1 + 1),
                    contour));
            }
        }

        return regions;
    }

    /// <summary>
    /// Traces the pixel-boundary polygon of one component. Chinese: 沿像素格点收集前景/背景之间的
    /// 单位边组成环，取面积最大的环；对角相接处若出现分叉则拆成多个环并保留最大者。
    /// </summary>
    private static PointD[]? TraceContour(
        bool[] mask,
        int width,
        int height,
        List<int> component,
        int maximumVertices)
    {
        int gridStride = width + 1;
        var edges = new Dictionary<int, List<int>>(component.Count);
        foreach (int index in component)
        {
            int x = index % width;
            int y = index / width;
            if (y == 0 || !mask[index - width])
            {
                AddEdge(edges, (y * gridStride) + x, (y * gridStride) + x + 1);
            }

            if (x + 1 >= width || !mask[index + 1])
            {
                AddEdge(edges, (y * gridStride) + x + 1, ((y + 1) * gridStride) + x + 1);
            }

            if (y + 1 >= height || !mask[index + width])
            {
                AddEdge(edges, ((y + 1) * gridStride) + x + 1, ((y + 1) * gridStride) + x);
            }

            if (x == 0 || !mask[index - 1])
            {
                AddEdge(edges, ((y + 1) * gridStride) + x, (y * gridStride) + x);
            }
        }

        List<int>? bestLoop = null;
        double bestArea = 0;
        int remaining = component.Count * 4 + 8;
        while (edges.Count > 0 && remaining-- > 0)
        {
            int first = edges.First().Key;
            int current = first;
            var loop = new List<int>();
            int steps = 0;
            bool closed = false;
            while (steps++ <= component.Count * 4)
            {
                if (!edges.TryGetValue(current, out List<int>? successors) || successors.Count == 0)
                {
                    break;
                }

                loop.Add(current);
                int next = successors[0];
                successors.RemoveAt(0);
                if (successors.Count == 0)
                {
                    edges.Remove(current);
                }

                current = next;
                if (current == first)
                {
                    closed = true;
                    break;
                }
            }

            if (!closed || loop.Count < 4)
            {
                // A broken walk means the boundary is not a simple ring; keep whatever closed loops
                // were already collected instead of emitting a malformed polygon.
                continue;
            }

            double loopArea = Math.Abs(PolygonArea(loop, gridStride));
            if (loopArea > bestArea)
            {
                bestArea = loopArea;
                bestLoop = loop;
            }
        }

        if (bestLoop is null)
        {
            return null;
        }

        var points = new PointD[bestLoop.Count];
        for (int i = 0; i < bestLoop.Count; i++)
        {
            points[i] = new PointD(bestLoop[i] % gridStride, bestLoop[i] / gridStride);
        }

        PointD[] simplified = Simplify(points);
        if (simplified.Length < 3)
        {
            return null;
        }

        return simplified.Length <= maximumVertices ? simplified : Decimate(simplified, maximumVertices);
    }

    private static void AddEdge(Dictionary<int, List<int>> edges, int from, int to)
    {
        if (!edges.TryGetValue(from, out List<int>? successors))
        {
            edges[from] = successors = [];
        }

        successors.Add(to);
    }

    private static double PolygonArea(List<int> loop, int gridStride)
    {
        double area = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            int current = loop[i];
            int next = loop[(i + 1) % loop.Count];
            double currentX = current % gridStride, currentY = current / gridStride;
            double nextX = next % gridStride, nextY = next / gridStride;
            area += (currentX * nextY) - (nextX * currentY);
        }

        return area / 2;
    }

    private static PointD[] Simplify(PointD[] points)
    {
        var result = new List<PointD>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            PointD previous = points[(i + points.Length - 1) % points.Length];
            PointD current = points[i];
            PointD next = points[(i + 1) % points.Length];
            if ((current.X - previous.X) * (next.Y - current.Y) != (current.Y - previous.Y) * (next.X - current.X))
            {
                result.Add(current);
            }
        }

        return [.. result];
    }

    private static PointD[] Decimate(PointD[] points, int maximumVertices)
    {
        int stride = (int)Math.Ceiling(points.Length / (double)maximumVertices);
        var reduced = new List<PointD>(maximumVertices);
        for (int i = 0; i < points.Length; i += stride)
        {
            reduced.Add(points[i]);
        }

        return reduced.Count >= 3 ? [.. reduced] : points;
    }

    /// <summary>Segments the image and returns the connected components above the area floor.</summary>
    public static IReadOnlyList<BlobFeature> ExtractRegions(
        IntensityImage image,
        bool useOtsu,
        int threshold,
        bool detectDark,
        int minArea)
    {
        return ExtractRegions(image, inside: null, useOtsu, threshold, detectDark, minArea);
    }

    /// <summary>
    /// Segments the image inside <paramref name="inside"/> and returns the connected components
    /// above the area floor.
    /// Chinese: 只有掩码内的像素参与阈值统计、分割与连通域，掩码外不会产生斑点。
    /// </summary>
    /// <param name="image">Source intensity plane.</param>
    /// <param name="inside">Row-major search mask, or null to search the whole plane.</param>
    /// <param name="useOtsu">Select the threshold with Otsu's method.</param>
    /// <param name="threshold">Manual 8-bit threshold, ignored when <paramref name="useOtsu"/> is true.</param>
    /// <param name="detectDark">Select dark targets instead of light ones.</param>
    /// <param name="minArea">Components smaller than this pixel count are dropped.</param>
    /// <param name="includeContours">Trace a boundary polygon for every surviving component.</param>
    /// <param name="maximumContourVertices">Upper bound on contour vertices per blob.</param>
    public static IReadOnlyList<BlobFeature> ExtractRegions(
        IntensityImage image,
        bool[]? inside,
        bool useOtsu,
        int threshold,
        bool detectDark,
        int minArea,
        bool includeContours = false,
        int maximumContourVertices = 512)
    {
        bool[] mask = Segment(image, inside, useOtsu, threshold, detectDark);
        return Connect(mask, image.Width, image.Height, minArea, includeContours, maximumContourVertices);
    }

    private static double OtsuThreshold(IntensityImage image, bool[]? inside)
    {
        int maximum = image.MaxValue;
        // Pixel count is bounded by the intensity buffer length (an int), so an int histogram
        // cannot overflow and avoids the extra 512 KB a long histogram would allocate per call.
        var histogram = new int[maximum + 1];
        long total = 0;
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Width;
            for (int x = 0; x < image.Width; x++)
            {
                int index = row + x;
                if (inside is not null && !inside[index])
                {
                    continue;
                }

                histogram[image[x, y]]++;
                total++;
            }
        }

        double sum = 0;
        for (int i = 0; i <= maximum; i++)
        {
            sum += (double)i * histogram[i];
        }

        double sumB = 0;
        long weightB = 0;
        double bestVariance = -1;
        int bestThreshold = 0;
        for (int t = 0; t <= maximum; t++)
        {
            weightB += histogram[t];
            if (weightB == 0)
            {
                continue;
            }

            long weightF = total - weightB;
            if (weightF == 0)
            {
                break;
            }

            sumB += (double)t * histogram[t];
            double meanB = sumB / weightB;
            double meanF = (sum - sumB) / weightF;
            double variance = (double)weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }
}
