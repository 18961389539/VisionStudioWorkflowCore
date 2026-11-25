using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class MatchingGeometryAlgorithms
{
    internal sealed record HomographyResult(
        double[] Matrix, int InlierCount, double Rms, double MaxError,
        double ScaleX, double ScaleY, double RotationDeg, double SkewDeg, VisionPoint2D[] Corners);

    internal sealed record PoseResult(
        double[] Matrix, double[] Rvec, double Rx, double Ry, double Rz, double Rms, double MaxError);

    internal static HomographyResult? Homography(
        IReadOnlyList<VisionPoint2D> sourcePoints, IReadOnlyList<VisionPoint2D> targetPoints,
        HomographyMethods method, double threshold, int maxIterations, double confidence, int minInliers,
        CancellationToken cancellationToken, out string failureReason)
    {
        var source = sourcePoints.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        var target = targetPoints.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        using var sourceMat = Mat.FromArray(source);
        using var targetMat = Mat.FromArray(target);
        using var mask = new Mat();
        using var homography = Cv2.FindHomography(sourceMat, targetMat, method, threshold, mask, maxIterations, confidence);
        cancellationToken.ThrowIfCancellationRequested();
        if (homography is null || homography.Empty() || homography.Rows != 3 || homography.Cols != 3)
        {
            failureReason = "homography solver returned no model (degenerate or collinear correspondences)";
            return null;
        }

        var h = new double[9];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                h[row * 3 + column] = homography.At<double>(row, column);
        if (!double.IsFinite(h[8]) || Math.Abs(h[8]) < 1e-12)
        {
            failureReason = "homography is degenerate (h22 is zero), the correspondences map a line to infinity";
            return null;
        }
        for (var index = 0; index < 9; index++) h[index] /= h[8];

        mask.GetArray(out byte[] maskData);
        var inlierCount = 0;
        var maskAvailable = !mask.Empty() && maskData.Length >= sourcePoints.Count;
        for (var index = 0; index < sourcePoints.Count; index++)
            if (!maskAvailable || maskData[index] != 0) inlierCount++;
        if (inlierCount < minInliers)
        {
            failureReason = $"only {inlierCount} inliers survived (minInliers={minInliers})";
            return null;
        }

        double sumSquares = 0;
        var maxError = 0d;
        for (var index = 0; index < sourcePoints.Count; index++)
        {
            var projected = Project(h, sourcePoints[index]);
            var dx = projected.X - targetPoints[index].X;
            var dy = projected.Y - targetPoints[index].Y;
            var error = Math.Sqrt(dx * dx + dy * dy);
            sumSquares += error * error;
            if (error > maxError) maxError = error;
        }
        var rms = Math.Sqrt(sumSquares / sourcePoints.Count);
        var (scaleX, scaleY, rotationDeg, skewDeg) = Decompose(h, sourcePoints);
        var corners = new[] { Project(h, sourcePoints[0]), Project(h, sourcePoints[sourcePoints.Count - 1]) };
        failureReason = string.Empty;
        return new HomographyResult(h, inlierCount, rms, maxError, scaleX, scaleY, rotationDeg, skewDeg, corners);
    }

    internal static Mat Warp(Mat source, double[] homography, int width, int height)
    {
        using var matrix = new Mat(3, 3, MatType.CV_64F);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                matrix.Set(row, column, homography[row * 3 + column]);
        var warped = new Mat();
        try { Cv2.WarpPerspective(source, warped, matrix, new Size(width, height)); return warped; }
        catch { warped.Dispose(); throw; }
    }

    internal static PoseResult? Pose(
        Point3f[] objects, IReadOnlyList<VisionPoint2D> imagePoints, double[] camera, double[] distortion,
        SolvePnPMethod solver, bool useGuess, double[] guessRvec, double[] guessTvec)
    {
        using var cameraMat = new Mat(3, 3, MatType.CV_64F);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cameraMat.Set(row, column, camera[row * 3 + column]);
        using var distortionMat = distortion.Length == 0 ? new Mat() : new Mat(1, distortion.Length, MatType.CV_64F);
        for (var index = 0; index < distortion.Length; index++) distortionMat.Set(0, index, distortion[index]);
        using var rvec = new Mat(3, 1, MatType.CV_64F, Scalar.All(0));
        using var tvec = new Mat(3, 1, MatType.CV_64F, Scalar.All(0));
        if (useGuess)
        {
            for (var index = 0; index < 3; index++)
            {
                rvec.Set(index, 0, guessRvec[index]);
                tvec.Set(index, 0, guessTvec[index]);
            }
        }
        try
        {
            using var objectMat = Mat.FromArray(objects);
            using var imageMat = Mat.FromArray(imagePoints.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray());
            Cv2.SolvePnP(objectMat, imageMat, cameraMat, distortionMat, rvec, tvec, useGuess, solver);
        }
        catch (OpenCVException exception)
        {
            throw new InvalidOperationException($"match.pose3d PnP failed: {exception.Message}", exception);
        }
        if (!IsFiniteVector(rvec) || !IsFiniteVector(tvec)) return null;

        using var rotationMat = new Mat();
        using var jacobian = new Mat();
        Cv2.Rodrigues(rvec, rotationMat, jacobian);
        var rvecValues = new[] { rvec.At<double>(0, 0), rvec.At<double>(1, 0), rvec.At<double>(2, 0) };
        var tvecValues = new[] { tvec.At<double>(0, 0), tvec.At<double>(1, 0), tvec.At<double>(2, 0) };
        var rotationMatrix = new double[3, 3];
        var matrix = new double[16];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                rotationMatrix[row, column] = rotationMat.At<double>(row, column);
                matrix[row * 4 + column] = rotationMatrix[row, column];
            }
            matrix[row * 4 + 3] = tvecValues[row];
        }
        matrix[15] = 1;
        var cameraArray = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cameraArray[row, column] = camera[row * 3 + column];
        Cv2.ProjectPoints(objects, rvecValues, tvecValues, cameraArray, distortion, out Point2f[] reprojected, out _, 0);
        double sumSquares = 0;
        var maxError = 0d;
        for (var index = 0; index < objects.Length; index++)
        {
            var dx = reprojected[index].X - imagePoints[index].X;
            var dy = reprojected[index].Y - imagePoints[index].Y;
            var error = Math.Sqrt(dx * dx + dy * dy);
            sumSquares += error * error;
            if (error > maxError) maxError = error;
        }
        var (rx, ry, rz) = EulerZyx(rotationMatrix);
        return new PoseResult(matrix, rvecValues, rx, ry, rz, Math.Sqrt(sumSquares / objects.Length), maxError);
    }

    private static VisionPoint2D Project(double[] h, VisionPoint2D point)
    {
        var denominator = h[6] * point.X + h[7] * point.Y + h[8];
        if (Math.Abs(denominator) < 1e-12)
            throw new InvalidOperationException("match.homography maps a correspondence point to infinity.");
        return new VisionPoint2D(
            (h[0] * point.X + h[1] * point.Y + h[2]) / denominator,
            (h[3] * point.X + h[4] * point.Y + h[5]) / denominator);
    }

    private static (double ScaleX, double ScaleY, double RotationDeg, double SkewDeg) Decompose(
        double[] h, IReadOnlyList<VisionPoint2D> points)
    {
        double cx = 0, cy = 0;
        foreach (var point in points) { cx += point.X; cy += point.Y; }
        cx /= points.Count; cy /= points.Count;
        var w = h[6] * cx + h[7] * cy + h[8];
        if (Math.Abs(w) < 1e-12) return (0, 0, 0, 0);
        var numeratorX = h[0] * cx + h[1] * cy + h[2];
        var numeratorY = h[3] * cx + h[4] * cy + h[5];
        var denominator2 = w * w;
        var duDx = (h[0] * w - numeratorX * h[6]) / denominator2;
        var dvDx = (h[3] * w - numeratorY * h[6]) / denominator2;
        var duDy = (h[1] * w - numeratorX * h[7]) / denominator2;
        var dvDy = (h[4] * w - numeratorY * h[7]) / denominator2;
        var scaleX = Math.Sqrt(duDx * duDx + dvDx * dvDx);
        var scaleY = Math.Sqrt(duDy * duDy + dvDy * dvDy);
        var rotation = Math.Atan2(dvDx, duDx) * 180.0 / Math.PI;
        var dot = duDx * duDy + dvDx * dvDy;
        var cosine = scaleX > 1e-12 && scaleY > 1e-12 ? dot / (scaleX * scaleY) : 0;
        var angle = Math.Acos(Math.Clamp(cosine, -1, 1)) * 180.0 / Math.PI;
        return (scaleX, scaleY, rotation, 90.0 - angle);
    }

    private static (double Rx, double Ry, double Rz) EulerZyx(double[,] rotation)
    {
        double R(int row, int column) => rotation[row, column];
        var sy = Math.Sqrt(R(0, 0) * R(0, 0) + R(1, 0) * R(1, 0));
        double rx, ry, rz;
        if (sy > 1e-6)
        {
            rx = Math.Atan2(R(2, 1), R(2, 2));
            ry = Math.Atan2(-R(2, 0), sy);
            rz = Math.Atan2(R(1, 0), R(0, 0));
        }
        else
        {
            rx = Math.Atan2(-R(1, 2), R(1, 1));
            ry = Math.Atan2(-R(2, 0), sy);
            rz = 0;
        }
        const double toDegrees = 180.0 / Math.PI;
        return (rx * toDegrees, ry * toDegrees, rz * toDegrees);
    }

    private static bool IsFiniteVector(Mat column)
    {
        var rows = column.Rows;
        for (var row = 0; row < rows; row++)
            if (!double.IsFinite(column.At<double>(row, 0))) return false;
        return true;
    }
}
