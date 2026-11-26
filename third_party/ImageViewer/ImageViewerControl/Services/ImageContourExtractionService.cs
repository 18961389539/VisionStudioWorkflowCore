using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>Ordered pixel-boundary contour of the largest thresholded component inside a search ROI.</summary>
public sealed record ImageContourExtractionResult(bool Success, string Error, PointD[] OuterContour, PointD[][] Holes);

/// <summary>Raster contour extraction; retains 16-bit intensity for threshold selection.</summary>
public static class ImageContourExtractionService
{
    // Bounded memory and runtime. Large images can be searched with a smaller ROI.
    private const int MaximumSearchPixels = 4_000_000;

    /// <summary>Extracts the largest four-connected component, rejecting objects clipped by the ROI/image boundary.</summary>
    public static ImageContourExtractionResult Extract(BitmapSource bitmap, RotatedRect roi)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(roi);
        if (!double.IsFinite(roi.Width) || !double.IsFinite(roi.Height) || roi.Width <= 0 || roi.Height <= 0 ||
            !double.IsFinite(roi.Center.X) || !double.IsFinite(roi.Center.Y) || !double.IsFinite(roi.Angle))
            return Fail("Invalid search ROI.");
        double radians = roi.Angle * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double halfX = (Math.Abs(cos) * roi.Width + Math.Abs(sin) * roi.Height) / 2;
        double halfY = (Math.Abs(sin) * roi.Width + Math.Abs(cos) * roi.Height) / 2;
        int left = (int)Math.Clamp(Math.Floor(roi.Center.X - halfX), 0, bitmap.PixelWidth);
        int top = (int)Math.Clamp(Math.Floor(roi.Center.Y - halfY), 0, bitmap.PixelHeight);
        int right = (int)Math.Clamp(Math.Ceiling(roi.Center.X + halfX), 0, bitmap.PixelWidth);
        int bottom = (int)Math.Clamp(Math.Ceiling(roi.Center.Y + halfY), 0, bitmap.PixelHeight);
        int width = right - left, height = bottom - top;
        if (width < 3 || height < 3 || (long)width * height > MaximumSearchPixels)
            return Fail("Search ROI must contain 3 to 4,000,000 pixels; reduce the ROI for large images.");
        BitmapSource image = ImageViewerPixelAccess.NormalizeForIntensity(bitmap);
        int bytesPerPixel = ImageViewerPixelAccess.GetBytesPerPixel(image);
        int stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        image.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        var intensities = new ushort[width * height];
        var inside = new bool[intensities.Length];
        int maximum = ImageViewerPixelAccess.GetIntensityMaximum(image.Format);
        var histogram = new int[maximum + 1];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double dx = left + x + 0.5 - roi.Center.X, dy = top + y + 0.5 - roi.Center.Y;
            int index = y * width + x;
            inside[index] = Math.Abs(dx * cos + dy * sin) < roi.Width / 2 && Math.Abs(-dx * sin + dy * cos) < roi.Height / 2;
            intensities[index] = ImageViewerPixelAccess.ReadIntensity(pixels, index * bytesPerPixel, bytesPerPixel, image.Format);
            if (inside[index]) histogram[intensities[index]]++;
        }
        BlobAnalysisRoi? settings = roi as BlobAnalysisRoi;
        int threshold = settings is { UseOtsu: false }
            ? (int)Math.Round(settings.ManualThreshold * maximum / 255.0)
            : Otsu(histogram);
        bool dark = settings?.DetectDark ?? false;
        var mask = new bool[inside.Length];
        for (int i = 0; i < mask.Length; i++) mask[i] = inside[i] && (dark ? intensities[i] <= threshold : intensities[i] > threshold);
        var visited = new bool[mask.Length];
        var queue = new Queue<int>();
        List<int> largest = [];
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start]) continue;
            List<int> component = [];
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int index))
            {
                component.Add(index);
                int x = index % width, y = index / width;
                if (x > 0) Visit(index - 1);
                if (x + 1 < width) Visit(index + 1);
                if (y > 0) Visit(index - width);
                if (y + 1 < height) Visit(index + width);
            }
            if (component.Count > largest.Count) largest = component;
        }
        if (largest.Count < Math.Max(3, settings?.MinArea ?? 10)) return Fail("No component satisfies the minimum area.");
        foreach (int index in largest)
        {
            int x = index % width, y = index / width;
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1 ||
                !inside[index - 1] || !inside[index + 1] || !inside[index - width] || !inside[index + width])
                return Fail("Detected object touches the search/image boundary; enlarge the ROI.");
        }
        Array.Clear(mask);
        foreach (int index in largest) mask[index] = true;
        var edges = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        foreach (int index in largest)
        {
            int x = index % width, y = index / width;
            if (!mask[index - width]) Edge((x, y), (x + 1, y));
            if (!mask[index + 1]) Edge((x + 1, y), (x + 1, y + 1));
            if (!mask[index + width]) Edge((x + 1, y + 1), (x, y + 1));
            if (!mask[index - 1]) Edge((x, y + 1), (x, y));
        }
        List<PointD[]> loops = [];
        while (edges.Count > 0)
        {
            var first = edges.First().Key;
            var current = first;
            List<PointD> loop = [];
            do
            {
                loop.Add(new PointD(current.X + left, current.Y + top));
                if (!edges.TryGetValue(current, out var successors) || successors.Count != 1)
                    return Fail("Ambiguous threshold boundary; adjust threshold or search ROI.");
                var next = successors[0];
                edges.Remove(current);
                current = next;
            } while (current != first && loop.Count <= mask.Length * 4);
            if (current != first || loop.Count < 4) return Fail("Open or degenerate threshold boundary.");
            loops.Add(Simplify(loop));
        }
        PointD[] outer = loops.OrderByDescending(loop => Math.Abs(SignedArea(loop))).First();
        if (loops.Sum(loop => (long)loop.Length) > 4096)
            return Fail("Contour complexity exceeds 4096 vertices; reduce the search ROI or adjust the threshold.");
        return new(true, string.Empty, outer, loops.Where(loop => !ReferenceEquals(loop, outer)).ToArray());

        void Visit(int index)
        {
            if (mask[index] && !visited[index]) { visited[index] = true; queue.Enqueue(index); }
        }
        void Edge((int X, int Y) from, (int X, int Y) to)
        {
            if (!edges.TryGetValue(from, out var list)) edges[from] = list = [];
            list.Add(to);
        }
    }

    private static ImageContourExtractionResult Fail(string error) => new(false, error, [], []);

    private static int Otsu(int[] histogram)
    {
        long total = histogram.Sum(value => (long)value);
        double sum = 0;
        for (int i = 0; i < histogram.Length; i++) sum += (double)i * histogram[i];
        long count = 0;
        double partial = 0, best = -1;
        int result = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            count += histogram[i]; partial += (double)i * histogram[i];
            if (count == 0 || count == total) continue;
            double difference = partial / count - (sum - partial) / (total - count);
            double variance = (double)count * (total - count) * difference * difference;
            if (variance > best) { best = variance; result = i; }
        }
        return result;
    }

    private static PointD[] Simplify(List<PointD> points) => points.Where((point, i) =>
    {
        PointD previous = points[(i + points.Count - 1) % points.Count];
        PointD next = points[(i + 1) % points.Count];
        return (point.X - previous.X) * (next.Y - point.Y) != (point.Y - previous.Y) * (next.X - point.X);
    }).ToArray();

    private static double SignedArea(PointD[] points)
    {
        double area = 0;
        for (int i = 0; i < points.Length; i++)
        {
            PointD next = points[(i + 1) % points.Length];
            area += points[i].X * next.Y - next.X * points[i].Y;
        }
        return area / 2;
    }
}
