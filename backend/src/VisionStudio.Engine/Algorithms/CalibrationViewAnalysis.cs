namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure per-view calibration quality analysis: image coverage plus, for planar
/// (all-Z-equal) boards, a DLT homography reprojection error per view. This is the
/// multi-view error reporting mature calibrators show before accepting a model.</summary>
public static class CalibrationViewAnalysis
{
    public sealed record ViewStat(int PointCount, double CoverageX, double CoverageY, double? ReprojectionRms, double? ReprojectionMax);

    public static ViewStat Analyze(IReadOnlyList<IReadOnlyList<double>> objectPoints, IReadOnlyList<IReadOnlyList<double>> imagePoints, int imageWidth, int imageHeight)
    {
        var coverageX = imagePoints.Count == 0 ? 0 : (imagePoints.Max(p => p[0]) - imagePoints.Min(p => p[0])) / Math.Max(1, imageWidth - 1);
        var coverageY = imagePoints.Count == 0 ? 0 : (imagePoints.Max(p => p[1]) - imagePoints.Min(p => p[1])) / Math.Max(1, imageHeight - 1);
        double? rms = null, max = null;
        // Planar boards (constant Z, the chessboard/circle-grid norm) admit a homography.
        if (objectPoints.Count >= 4 && objectPoints.All(p => Math.Abs(p[2] - objectPoints[0][2]) < 1e-9))
        {
            var errors = HomographyErrors(objectPoints, imagePoints);
            if (errors is not null && errors.Length > 0) { rms = Math.Sqrt(errors.Average(e => e * e)); max = errors.Max(); }
        }
        return new(imagePoints.Count, coverageX, coverageY, rms, max);
    }

    private static double[]? HomographyErrors(IReadOnlyList<IReadOnlyList<double>> objectPoints, IReadOnlyList<IReadOnlyList<double>> imagePoints)
    {
        var n = objectPoints.Count;
        // Normalised DLT: build the 8x8 normal equations from point correspondences.
        var ata = new double[8, 8]; var atb = new double[8];
        for (var i = 0; i < n; i++)
        {
            var x = objectPoints[i][0]; var y = objectPoints[i][1];
            var u = imagePoints[i][0]; var v = imagePoints[i][1];
            var rows = new[]
            {
                new[] { x, y, 1d, 0, 0, 0, -u * x, -u * y },
                new[] { 0d, 0, 0, x, y, 1d, -v * x, -v * y }
            };
            var targets = new[] { u, v };
            for (var r = 0; r < 2; r++)
            for (var a = 0; a < 8; a++)
            {
                atb[a] += rows[r][a] * targets[r];
                for (var b = 0; b < 8; b++) ata[a, b] += rows[r][a] * rows[r][b];
            }
        }
        var h = Solve(ata, atb);
        if (h is null) return null;
        var errors = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x = objectPoints[i][0]; var y = objectPoints[i][1];
            var w = h[6] * x + h[7] * y + 1;
            if (Math.Abs(w) < 1e-12) return null;
            var u = (h[0] * x + h[1] * y + h[2]) / w;
            var v = (h[3] * x + h[4] * y + h[5]) / w;
            errors[i] = Math.Sqrt(Math.Pow(u - imagePoints[i][0], 2) + Math.Pow(v - imagePoints[i][1], 2));
        }
        return errors;
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = new double[n, n + 1];
        for (var r = 0; r < n; r++) { for (var c = 0; c < n; c++) m[r, c] = a[r, c]; m[r, n] = b[r]; }
        for (var col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(m[r, col]));
            if (Math.Abs(m[pivot, col]) < 1e-14) return null;
            for (var c = 0; c <= n; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = m[r, col] / m[col, col];
                for (var c = col; c <= n; c++) m[r, c] -= factor * m[col, c];
            }
        }
        return Enumerable.Range(0, n).Select(i => m[i, n] / m[i, i]).ToArray();
    }
}
