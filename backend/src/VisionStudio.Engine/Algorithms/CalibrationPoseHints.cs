using System.Text;

namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Rule-based next-pose hints for the intrinsics wizard. No ML: inspect coverage
/// occupancy in a 3x3 FOV grid and a simple perspective-skew proxy, then suggest
/// concrete board placements an operator can follow.
/// </summary>
public static class CalibrationPoseHints
{
    public sealed record Hint(string Code, string Message, string Region);

    public static IReadOnlyList<Hint> Suggest(
        IReadOnlyList<(IReadOnlyList<IReadOnlyList<double>> ImagePoints, bool Enabled)> views,
        int imageWidth,
        int imageHeight,
        int targetViews = 12)
    {
        if (imageWidth < 16 || imageHeight < 16)
            throw new InvalidOperationException("Pose hints require a known image size (>= 16x16).");
        if (targetViews < 3) targetViews = 3;

        var enabled = views.Where(v => v.Enabled && v.ImagePoints is { Count: > 0 }).ToList();
        var hints = new List<Hint>();
        if (enabled.Count == 0)
        {
            hints.Add(new("start-center", "Place the board roughly centered and parallel to the camera; fill ~40% of the FOV, then capture.", "center"));
            hints.Add(new("then-edges", "Next, move the board toward each FOV corner (top-left, top-right, bottom-left, bottom-right) with mild tilt.", "corners"));
            return hints;
        }

        // Occupancy of board centroids in a 3x3 FOV grid.
        var occupied = new bool[3, 3];
        var skews = new List<double>();
        foreach (var (pts, _) in enabled)
        {
            var cx = pts.Average(p => p[0]) / Math.Max(1, imageWidth - 1);
            var cy = pts.Average(p => p[1]) / Math.Max(1, imageHeight - 1);
            var gx = Math.Clamp((int)(cx * 3), 0, 2);
            var gy = Math.Clamp((int)(cy * 3), 0, 2);
            occupied[gy, gx] = true;
            skews.Add(PerspectiveSkew(pts));
        }

        string[] cellNames =
        [
            "top-left", "top-center", "top-right",
            "mid-left", "center", "mid-right",
            "bottom-left", "bottom-center", "bottom-right"
        ];
        for (var gy = 0; gy < 3; gy++)
        for (var gx = 0; gx < 3; gx++)
        {
            if (occupied[gy, gx]) continue;
            var name = cellNames[gy * 3 + gx];
            hints.Add(new(
                $"cover-{name}",
                $"No enabled view is centered in the {name} FOV cell. Move the board there and capture (keep all corners visible).",
                name));
        }

        var meanSkew = skews.Count == 0 ? 0 : skews.Average();
        if (meanSkew < 0.08)
            hints.Add(new("add-tilt", "Views look mostly frontal (low perspective skew). Tilt the board ~20-40 deg about X and Y on the next 2-3 captures.", "tilt"));
        else if (meanSkew < 0.15)
            hints.Add(new("more-tilt", "Add at least one stronger tilt (~40-55 deg) so focal length is better constrained.", "tilt"));

        if (enabled.Count < targetViews)
            hints.Add(new("more-views", $"Enabled views: {enabled.Count}/{targetViews}. Aim for {targetViews}+ well-distributed poses before trusting RMS.", "count"));

        // Prefer empty corners, then mid cells, then tilt / count. Always keep tilt/count if present.
        var corners = hints.Where(h => h.Code.StartsWith("cover-", StringComparison.Ordinal)).Take(4).ToList();
        var extras = hints.Where(h => h.Region is "tilt" or "count").ToList();
        var mid = hints.Where(h => h.Code.StartsWith("cover-", StringComparison.Ordinal) && !corners.Contains(h)).Take(2).ToList();
        return corners.Concat(mid).Concat(extras).Take(7).ToList();
    }

    /// <summary>
    /// Cheap skew proxy: normalized difference between opposite edge lengths of the
    /// axis-aligned extremes of the detected points. Frontal boards ~0; strong tilt higher.
    /// </summary>
    public static double PerspectiveSkew(IReadOnlyList<IReadOnlyList<double>> imagePoints)
    {
        if (imagePoints is null || imagePoints.Count < 4) return 0;
        var minX = imagePoints.Min(p => p[0]); var maxX = imagePoints.Max(p => p[0]);
        var minY = imagePoints.Min(p => p[1]); var maxY = imagePoints.Max(p => p[1]);
        var width = Math.Max(1e-6, maxX - minX);
        var height = Math.Max(1e-6, maxY - minY);
        // Top vs bottom row span using points near minY / maxY.
        var top = imagePoints.Where(p => p[1] <= minY + 0.15 * height).ToList();
        var bottom = imagePoints.Where(p => p[1] >= maxY - 0.15 * height).ToList();
        var left = imagePoints.Where(p => p[0] <= minX + 0.15 * width).ToList();
        var right = imagePoints.Where(p => p[0] >= maxX - 0.15 * width).ToList();
        double SpanX(IReadOnlyList<IReadOnlyList<double>> row) => row.Count < 2 ? width : row.Max(p => p[0]) - row.Min(p => p[0]);
        double SpanY(IReadOnlyList<IReadOnlyList<double>> col) => col.Count < 2 ? height : col.Max(p => p[1]) - col.Min(p => p[1]);
        var topSpan = SpanX(top); var bottomSpan = SpanX(bottom);
        var leftSpan = SpanY(left); var rightSpan = SpanY(right);
        var hSkew = Math.Abs(topSpan - bottomSpan) / Math.Max(topSpan, bottomSpan);
        var vSkew = Math.Abs(leftSpan - rightSpan) / Math.Max(leftSpan, rightSpan);
        return Math.Clamp(0.5 * (hSkew + vSkew), 0, 1);
    }
}
