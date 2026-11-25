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

    /// <summary>
    /// Reprojection error using the actual pinhole camera matrix: recover the planar
    /// board pose from K^-1 * H (orthonormalized), project every object point through
    /// the camera matrix and measure pixel errors. Returns null when the view is not
    /// planar, the pose is behind the camera, or the decomposition is degenerate.
    /// Distortion is intentionally not modelled here (the native model stores its own
    /// polynomial coefficients), so this is a pose/K reprojection, still strictly more
    /// faithful than a raw homography fit which absorbs pose error.
    /// </summary>
    public static ViewStat AnalyzeWithCamera(IReadOnlyList<IReadOnlyList<double>> objectPoints, IReadOnlyList<IReadOnlyList<double>> imagePoints, int imageWidth, int imageHeight, double fx, double fy, double cx, double cy)
        => AnalyzeWithCamera(objectPoints, imagePoints, imageWidth, imageHeight, fx, fy, cx, cy, null);

    /// <summary>Distortion-aware variant: when OpenCV Brown-Conrady coefficients are
    /// supplied (k1,k2,p1,p2[,k3[,k4,k5,k6 rational]]), projected points are distorted
    /// before comparison, so the per-view RMS reflects the full legacy model instead of
    /// the pinhole part only. Native JLVision polynomial models pass null (their
    /// coefficients are not Brown-Conrady and must not be reinterpreted).</summary>
    public static ViewStat AnalyzeWithCamera(IReadOnlyList<IReadOnlyList<double>> objectPoints, IReadOnlyList<IReadOnlyList<double>> imagePoints, int imageWidth, int imageHeight, double fx, double fy, double cx, double cy, double[]? distCoeffs)
    {
        var baseline = Analyze(objectPoints, imagePoints, imageWidth, imageHeight);
        if (baseline.ReprojectionRms is null || !(fx > 0) || !(fy > 0)) return baseline;
        // With distortion coefficients, pose estimation must run on undistorted
        // observations: estimating the homography from distorted points lets it
        // absorb the distortion, and applying the model again would double-count it.
        var effectivePoints = distCoeffs is { Length: >= 4 }
            ? imagePoints.Select(pt => UndistortPoint(pt[0], pt[1], fx, fy, cx, cy, distCoeffs)).ToArray()
            : imagePoints;
        var h = EstimateHomography(objectPoints, effectivePoints);
        if (h is null) return baseline;
        // Hn = K^-1 H, columns scaled so |r1| = 1
        double[] Col(int c) => [(h[c] / fx), (h[3 + c] / fy), h[6 + c]];
        // K^-1 rows: [1/fx,0,-cx/fx],[0,1/fy,-cy/fy],[0,0,1]
        double[] HnCol(int c) => [(h[c] - cx * h[6 + c]) / fx, (h[3 + c] - cy * h[6 + c]) / fy, h[6 + c]];
        _ = Col(0);
        var r1 = HnCol(0); var r2 = HnCol(1); var translation = HnCol(2);
        var scale = Math.Sqrt(r1.Sum(v => v * v));
        if (scale < 1e-12) return baseline;
        r1 = r1.Select(v => v / scale).ToArray(); r2 = r2.Select(v => v / scale).ToArray(); translation = translation.Select(v => v / scale).ToArray();
        if (translation[2] < 0) { r1 = r1.Select(v => -v).ToArray(); r2 = r2.Select(v => -v).ToArray(); translation = translation.Select(v => -v).ToArray(); }
        // Orthonormalize: r1 stays, r2 loses its r1 component, r3 = r1 x r2.
        var dot = r1.Zip(r2, (a, b) => a * b).Sum();
        r2 = r2.Zip(r1, (b, a) => b - dot * a).ToArray();
        var norm2 = Math.Sqrt(r2.Sum(v => v * v));
        if (norm2 < 1e-12) return baseline;
        r2 = r2.Select(v => v / norm2).ToArray();
        var errors = new double[objectPoints.Count];
        for (var i = 0; i < objectPoints.Count; i++)
        {
            var x = objectPoints[i][0]; var y = objectPoints[i][1];
            var camX = r1[0] * x + r2[0] * y + translation[0];
            var camY = r1[1] * x + r2[1] * y + translation[1];
            var camZ = r1[2] * x + r2[2] * y + translation[2];
            if (camZ <= 1e-12) return baseline;
            var xn = camX / camZ; var yn = camY / camZ;
            if (distCoeffs is { Length: >= 4 })
            {
                var k1 = distCoeffs[0]; var k2 = distCoeffs[1]; var p1 = distCoeffs[2]; var p2 = distCoeffs[3];
                var k3 = distCoeffs.Length >= 5 ? distCoeffs[4] : 0d;
                var rr2 = xn * xn + yn * yn; var rr4 = rr2 * rr2; var rr6 = rr4 * rr2;
                var radial = 1 + k1 * rr2 + k2 * rr4 + k3 * rr6;
                if (distCoeffs.Length >= 8)
                    radial /= 1 + distCoeffs[5] * rr2 + distCoeffs[6] * rr4 + distCoeffs[7] * rr6;
                var xd = xn * radial + 2 * p1 * xn * yn + p2 * (rr2 + 2 * xn * xn);
                var yd = yn * radial + p1 * (rr2 + 2 * yn * yn) + 2 * p2 * xn * yn;
                xn = xd; yn = yd;
            }
            var u = fx * xn + cx; var v = fy * yn + cy;
            errors[i] = Math.Sqrt(Math.Pow(u - imagePoints[i][0], 2) + Math.Pow(v - imagePoints[i][1], 2));
        }
        return baseline with { ReprojectionRms = Math.Sqrt(errors.Average(e => e * e)), ReprojectionMax = errors.Max() };
    }

    /// <summary>Iteratively invert Brown-Conrady distortion for one observed point,
    /// returning the equivalent pinhole pixel coordinates.</summary>
    private static double[] UndistortPoint(double u, double v, double fx, double fy, double cx, double cy, double[] d)
    {
        var k1 = d[0]; var k2 = d[1]; var p1 = d[2]; var p2 = d[3];
        var k3 = d.Length >= 5 ? d[4] : 0d;
        var xd = (u - cx) / fx; var yd = (v - cy) / fy;
        var xn = xd; var yn = yd;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var rr2 = xn * xn + yn * yn; var rr4 = rr2 * rr2; var rr6 = rr4 * rr2;
            var radial = 1 + k1 * rr2 + k2 * rr4 + k3 * rr6;
            if (d.Length >= 8) radial /= 1 + d[5] * rr2 + d[6] * rr4 + d[7] * rr6;
            if (Math.Abs(radial) < 1e-12) break;
            xn = (xd - (2 * p1 * xn * yn + p2 * (rr2 + 2 * xn * xn))) / radial;
            yn = (yd - (p1 * (rr2 + 2 * yn * yn) + 2 * p2 * xn * yn)) / radial;
        }
        return [fx * xn + cx, fy * yn + cy];
    }

    private static double[]? EstimateHomography(IReadOnlyList<IReadOnlyList<double>> objectPoints, IReadOnlyList<IReadOnlyList<double>> imagePoints)
    {
        var ata = new double[8, 8]; var atb = new double[8];
        for (var i = 0; i < objectPoints.Count; i++)
        {
            var x = objectPoints[i][0]; var y = objectPoints[i][1];
            var u = imagePoints[i][0]; var v = imagePoints[i][1];
            var rows = new[] { new[] { x, y, 1d, 0, 0, 0, -u * x, -u * y }, new[] { 0d, 0, 0, x, y, 1d, -v * x, -v * y } };
            var targets = new[] { u, v };
            for (var r = 0; r < 2; r++)
            for (var a = 0; a < 8; a++) { atb[a] += rows[r][a] * targets[r]; for (var b = 0; b < 8; b++) ata[a, b] += rows[r][a] * rows[r][b]; }
        }
        var solved = Solve(ata, atb);
        return solved is null ? null : [solved[0], solved[1], solved[2], solved[3], solved[4], solved[5], solved[6], solved[7], 1d];
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
