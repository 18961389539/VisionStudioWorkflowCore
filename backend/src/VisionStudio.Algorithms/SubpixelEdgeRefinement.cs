namespace VisionStudio.Engine.Algorithms;

/// <summary>
/// Pure subpixel edge refinement (HALCON edges_sub_pix idea, scoped): at an integer
/// edge pixel, sample the Sobel gradient magnitude along the local gradient direction
/// at -1/0/+1 px (bilinear) and parabolically interpolate the peak position.
/// Returns the input point unchanged when the neighbourhood is flat or out of bounds.
/// </summary>
public static class SubpixelEdgeRefinement
{
    public static VisionPoint2D Refine(byte[] gray, int width, int height, double x, double y)
    {
        var ix = (int)Math.Round(x); var iy = (int)Math.Round(y);
        if (ix < 1 || iy < 1 || ix >= width - 1 || iy >= height - 1) return new(x, y);
        var (gx, gy) = Gradient(gray, width, ix, iy);
        var magnitude = Math.Sqrt(gx * gx + gy * gy);
        if (magnitude < 1e-9) return new(x, y);
        var dx = gx / magnitude; var dy = gy / magnitude;
        var m0 = GradientMagnitudeAt(gray, width, height, ix - dx, iy - dy);
        var m1 = GradientMagnitudeAt(gray, width, height, ix, iy);
        var m2 = GradientMagnitudeAt(gray, width, height, ix + dx, iy + dy);
        var denominator = m0 - 2 * m1 + m2;
        if (Math.Abs(denominator) < 1e-12) return new(x, y);
        var offset = Math.Clamp(0.5 * (m0 - m2) / denominator, -1, 1);
        return new(ix + dx * offset, iy + dy * offset);
    }

    private static (double Gx, double Gy) Gradient(byte[] gray, int width, int x, int y)
    {
        double At(int px, int py) => gray[py * width + px];
        var gx = -At(x - 1, y - 1) - 2 * At(x - 1, y) - At(x - 1, y + 1) + At(x + 1, y - 1) + 2 * At(x + 1, y) + At(x + 1, y + 1);
        var gy = -At(x - 1, y - 1) - 2 * At(x, y - 1) - At(x + 1, y - 1) + At(x - 1, y + 1) + 2 * At(x, y + 1) + At(x + 1, y + 1);
        return (gx, gy);
    }

    private static double GradientMagnitudeAt(byte[] gray, int width, int height, double x, double y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 1, width - 2); var y0 = Math.Clamp((int)Math.Floor(y), 1, height - 2);
        var fx = Math.Clamp(x - x0, 0, 1); var fy = Math.Clamp(y - y0, 0, 1);
        var top = Gradient(gray, width, x0, y0);
        // Bilinear over the four surrounding integer gradients.
        var g00 = Gradient(gray, width, x0, y0); var g10 = Gradient(gray, width, x0 + 1, y0);
        var g01 = Gradient(gray, width, x0, y0 + 1); var g11 = Gradient(gray, width, x0 + 1, y0 + 1);
        _ = top;
        var gx = (g00.Gx * (1 - fx) + g10.Gx * fx) * (1 - fy) + (g01.Gx * (1 - fx) + g11.Gx * fx) * fy;
        var gy = (g00.Gy * (1 - fx) + g10.Gy * fx) * (1 - fy) + (g01.Gy * (1 - fx) + g11.Gy * fx) * fy;
        return Math.Sqrt(gx * gx + gy * gy);
    }

    /// <summary>Angular coverage in degrees of points around a centre (360 minus the largest gap).</summary>
    public static double AngularCoverageDeg(IReadOnlyList<VisionPoint2D> points, double centerX, double centerY)
    {
        if (points.Count < 2) return 0;
        var angles = points.Select(p => Math.Atan2(p.Y - centerY, p.X - centerX) * 180 / Math.PI).OrderBy(a => a).ToArray();
        var maxGap = 0d;
        for (var i = 0; i < angles.Length; i++)
        {
            var next = i + 1 < angles.Length ? angles[i + 1] : angles[0] + 360;
            maxGap = Math.Max(maxGap, next - angles[i]);
        }
        return 360 - maxGap;
    }
}
