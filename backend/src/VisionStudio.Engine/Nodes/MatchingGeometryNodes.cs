using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 通用单应配准（match.homography）：从点对应求 3x3 投影变换。
/// 刚体配准表达不了"四条边不构成平行四边形"的工件，这正是单应的用武之地。
/// </summary>
public sealed class HomographyRegistrationNode : IVisionNodeExecutor
{
    public string Type => "match.homography";
    private const int MaxPairs = 20000;

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var sourcePoints = context.Require<IReadOnlyList<VisionPoint2D>>("sourcePoints");
        var targetPoints = context.Require<IReadOnlyList<VisionPoint2D>>("targetPoints");
        if (sourcePoints.Count != targetPoints.Count)
            throw new InvalidOperationException(
                $"match.homography needs one-to-one correspondences: source has {sourcePoints.Count} points, target has {targetPoints.Count}.");
        if (sourcePoints.Count > MaxPairs)
            throw new InvalidOperationException($"match.homography correspondence budget exceeded (max {MaxPairs}).");
        foreach (var point in sourcePoints) ValidatePoint(point, "sourcePoints");
        foreach (var point in targetPoints) ValidatePoint(point, "targetPoints");

        var methodName = (node.GetString("method", "ransac") ?? "ransac").Trim().ToLowerInvariant();
        var method = methodName switch
        {
            "ransac" => HomographyMethods.Ransac,
            "lmeds" => HomographyMethods.LMedS,
            "least-squares" => HomographyMethods.None,
            _ => throw new InvalidOperationException("match.homography method must be ransac, lmeds or least-squares.")
        };

        var threshold = node.GetDouble("ransacThresholdPx", 2);
        if (!double.IsFinite(threshold) || threshold is < 0.1 or > 100)
            throw new InvalidOperationException("match.homography ransacThresholdPx must be within 0.1..100.");
        var maxIterations = node.GetInt("maxIterations", 2000);
        if (maxIterations is < 50 or > 20000)
            throw new InvalidOperationException("match.homography maxIterations must be within 50..20000.");
        var confidence = node.GetDouble("confidence", 0.995);
        if (!double.IsFinite(confidence) || confidence is < 0.5 or > 0.9999)
            throw new InvalidOperationException("match.homography confidence must be within 0.5..0.9999.");
        var minInliers = node.GetInt("minInliers", 4);
        if (minInliers < 4)
            throw new InvalidOperationException("match.homography minInliers must be at least 4.");

        var warpImage = node.GetBool("warpImage", false);
        var outputWidth = node.GetInt("outputWidth", 0);
        var outputHeight = node.GetInt("outputHeight", 0);
        if (warpImage && (outputWidth is < 0 or > 16384 || outputHeight is < 0 or > 16384))
            throw new InvalidOperationException("match.homography output width/height must be within 0..16384 (0 = input size).");

        if (sourcePoints.Count < 4)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"homography needs at least 4 correspondences, got {sourcePoints.Count}",
                sourcePoints.Count, timer));
        }

        var source = sourcePoints.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        var target = targetPoints.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray();
        using var sourceMat = Mat.FromArray(source);
        using var targetMat = Mat.FromArray(target);
        using var mask = new Mat();
        using var homography = Cv2.FindHomography(
            sourceMat, targetMat, method, threshold, mask, maxIterations, confidence);
        cancellationToken.ThrowIfCancellationRequested();

        if (homography is null || homography.Empty() || homography.Rows != 3 || homography.Cols != 3)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                "homography solver returned no model (degenerate or collinear correspondences)", sourcePoints.Count, timer));
        }

        var h = new double[9];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                h[row * 3 + column] = homography.At<double>(row, column);
        if (!double.IsFinite(h[8]) || Math.Abs(h[8]) < 1e-12)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                "homography is degenerate (h22 is zero), the correspondences map a line to infinity", sourcePoints.Count, timer));
        }
        // 归一到 h22 = 1，后面的分解与 diagnostics 才有可比性。
        for (var index = 0; index < 9; index++) h[index] /= h[8];

        mask.GetArray(out byte[] maskData);
        var inlierCount = 0;
        var maskAvailable = !mask.Empty() && maskData.Length >= sourcePoints.Count;
        for (var index = 0; index < sourcePoints.Count; index++)
            if (!maskAvailable || maskData[index] != 0) inlierCount++;
        if (inlierCount < minInliers)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"only {inlierCount} inliers survived (minInliers={minInliers})", sourcePoints.Count, timer));
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
        var corners = new[]
        {
            Project(h, sourcePoints[0]),
            Project(h, sourcePoints[sourcePoints.Count - 1])
        };

        var transform = new VisionTransform2D(
            "source", "target", "px",
            h[0], h[1], h[2],
            h[3], h[4], h[5],
            h[6], h[7], h[8]);

        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["transform"] = VisionValue.Transform(transform),
            ["inlierCount"] = VisionValue.Integer(inlierCount),
            ["pointCount"] = VisionValue.Integer(sourcePoints.Count),
            ["reprojRms"] = VisionValue.Double(rms),
            ["maxError"] = VisionValue.Double(maxError),
            ["scaleX"] = VisionValue.Double(scaleX),
            ["scaleY"] = VisionValue.Double(scaleY),
            ["rotationDeg"] = VisionValue.Double(rotationDeg),
            ["skewDeg"] = VisionValue.Double(skewDeg),
            ["perspectiveX"] = VisionValue.Double(h[6]),
            ["perspectiveY"] = VisionValue.Double(h[7]),
            ["corners"] = VisionValue.String(JsonSerializer.Serialize(corners.Select(point => new
            {
                x = Math.Round(point.X, 4),
                y = Math.Round(point.Y, 4)
            }))),
            ["found"] = VisionValue.Boolean(true),
            ["diagnostics"] = VisionValue.String(string.Empty)
        };

        if (warpImage)
        {
            var sourceImage = context.RequireImage("image");
            using var matrix = new Mat(3, 3, MatType.CV_64F);
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                    matrix.Set(row, column, h[row * 3 + column]);
            var width = outputWidth > 0 ? outputWidth : sourceImage.Width;
            var height = outputHeight > 0 ? outputHeight : sourceImage.Height;
            var warped = new Mat();
            Cv2.WarpPerspective(sourceImage.Mat, warped, matrix, new Size(width, height));
            outputs["warped"] = VisionValue.Image(VisionImage.Own(warped));
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("method", methodName), ("inlierCount", inlierCount), ("pointCount", sourcePoints.Count),
            ("reprojRms", rms), ("maxError", maxError), ("scaleX", scaleX), ("scaleY", scaleY),
            ("rotationDeg", rotationDeg), ("skewDeg", skewDeg),
            ("perspectiveX", h[6]), ("perspectiveY", h[7]), ("warpImage", warpImage),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        outputs["diagnostics"] = VisionValue.String(diagnostics);

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["method"] = methodName, ["inlierCount"] = inlierCount, ["pointCount"] = sourcePoints.Count,
                ["reprojRms"] = Math.Round(rms, 5), ["maxError"] = Math.Round(maxError, 5),
                ["scaleX"] = Math.Round(scaleX, 6), ["scaleY"] = Math.Round(scaleY, 6),
                ["rotationDeg"] = Math.Round(rotationDeg, 4), ["skewDeg"] = Math.Round(skewDeg, 4),
                ["perspectiveX"] = Math.Round(h[6], 8), ["perspectiveY"] = Math.Round(h[7], 8),
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static void ValidatePoint(VisionPoint2D point, string port)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 100_000_000 || Math.Abs(point.Y) > 100_000_000)
            throw new InvalidOperationException($"match.homography {port} contains a non-finite or out-of-range coordinate.");
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

    /// <summary>在点集质心处取雅可比，得到局部尺度/旋转/错切——透视变换没有全局的单一角度。</summary>
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

    private static NodeExecutorResult Missing(string reason, int pointCount, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "match.homography", ("found", false), ("reason", reason), ("pointCount", pointCount),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(false),
                ["pointCount"] = VisionValue.Integer(pointCount),
                ["inlierCount"] = VisionValue.Integer(0),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["pointCount"] = pointCount,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            });
    }
}

/// <summary>
/// PnP 位姿（match.pose3d）：图像点 + 已知世界点 → 目标到相机的 SE(3)。
/// 这是 2D 检测与机器人抓取之间缺的那一环（ArUco 之外）。
/// </summary>
public sealed class PnpPoseNode : IVisionNodeExecutor
{
    public string Type => "match.pose3d";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var imagePoints = context.Require<IReadOnlyList<VisionPoint2D>>("imagePoints");
        foreach (var point in imagePoints)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidOperationException("match.pose3d imagePoints contains a non-finite coordinate.");
        }

        var objects = ParseObjectPoints(node.GetString("objectPointsJson", "[[0,0,0],[100,0,0],[100,60,0],[0,60,0]]"));
        if (objects.Length != imagePoints.Count)
            throw new InvalidOperationException(
                $"match.pose3d needs one object point per image point: object has {objects.Length}, image has {imagePoints.Count}. The correspondence order defines the pose.");
        if (objects.Length < 4)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"PnP needs at least 4 correspondences, got {objects.Length}", objects.Length, timer));
        }

        var camera = ParseNumberGrid(node.GetString("cameraMatrixJson", "[1000,0,640,0,1000,360,0,0,1]"), "cameraMatrixJson");
        if (camera.Length != 9)
            throw new InvalidOperationException("match.pose3d cameraMatrixJson must contain exactly 9 numbers (row-major 3x3).");
        var distortion = ParseNumberGrid(node.GetString("distortionJson", "[]"), "distortionJson");

        var solverName = (node.GetString("solver", "iterative") ?? "iterative").Trim().ToLowerInvariant();
        var solver = solverName switch
        {
            "iterative" => SolvePnPMethod.Iterative,
            "ippe" => SolvePnPMethod.IPPE,
            "ap3p" => SolvePnPMethod.AP3P,
            _ => throw new InvalidOperationException("match.pose3d solver must be iterative, ippe or ap3p.")
        };

        var useGuess = node.GetBool("useExtrinsicGuess", false);
        var guessRvec = ParseNumberGrid(node.GetString("guessRvecJson", "[0,0,0]"), "guessRvecJson");
        var guessTvec = ParseNumberGrid(node.GetString("guessTvecJson", "[0,0,1000]"), "guessTvecJson");
        if (guessRvec.Length != 3 || guessTvec.Length != 3)
            throw new InvalidOperationException("match.pose3d guessRvecJson / guessTvecJson must each contain 3 numbers.");

        var unit = (node.GetString("unit", "mm") ?? "mm").Trim();
        var objectFrame = (node.GetString("objectFrame", "object") ?? "object").Trim();
        var cameraFrame = (node.GetString("cameraFrame", "camera") ?? "camera").Trim();
        var maxRms = node.GetDouble("maxReprojectionRms", 0);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 1000)
            throw new InvalidOperationException("match.pose3d maxReprojectionRms must be within 0..1000 (0 disables).");

        using var cameraMat = new Mat(3, 3, MatType.CV_64F);
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cameraMat.Set(row, column, camera[row * 3 + column]);
        // 畸变矩阵要么是真正的空 Mat（无畸变），要么必须是 1xN 且 N∈{4,5,8,12,14}；1x1 会被 OpenCV 直接拒绝。
        using var distortionMat = distortion.Length == 0
            ? new Mat()
            : new Mat(1, distortion.Length, MatType.CV_64F);
        for (var index = 0; index < distortion.Length; index++)
            distortionMat.Set(0, index, distortion[index]);

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

        // ITERATIVE 内部先用 DLT 求初值，少于 6 点会抛出难懂的断言；提前给出可操作的提示。
        if (solverName == "iterative" && objects.Length < 6)
            throw new InvalidOperationException(
                $"match.pose3d iterative solver needs at least 6 points (got {objects.Length}) because OpenCV seeds it with DLT; use solver=ippe for a planar target or solver=ap3p for exactly 4.");

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

        // SolvePnP 不回报状态：输出里出现 NaN/Inf 就是没解出来。
        if (!IsFiniteVector(rvec) || !IsFiniteVector(tvec))
        {
            timer.Stop();
            return ValueTask.FromResult(Missing("PnP solver did not converge (degenerate or collinear object points)",
                objects.Length, timer));
        }

        using var rotationMat = new Mat();
        using var jacobian = new Mat();
        Cv2.Rodrigues(rvec, rotationMat, jacobian);
        var rvecValues = new[] { rvec.At<double>(0, 0), rvec.At<double>(1, 0), rvec.At<double>(2, 0) };
        var rotationMatrix = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                rotationMatrix[row, column] = rotationMat.At<double>(row, column);
        var matrix = new double[16];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                matrix[row * 4 + column] = rotationMatrix[row, column];
            matrix[row * 4 + 3] = tvec.At<double>(row, 0);
        }
        matrix[15] = 1;

        // 重投影自检：给机器人之前必须知道这套位姿到底对不对得上图像。
        var cameraArray = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cameraArray[row, column] = camera[row * 3 + column];
        var tvecValues = new[] { tvec.At<double>(0, 0), tvec.At<double>(1, 0), tvec.At<double>(2, 0) };
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
        var rms = Math.Sqrt(sumSquares / objects.Length);
        var withinGate = maxRms <= 0 || rms <= maxRms;

        var (rx, ry, rz) = EulerZyx(rotationMatrix);
        var x = matrix[3];
        var y = matrix[7];
        var z = matrix[11];
        var pose = new VisionTransform3D(objectFrame, cameraFrame, unit, matrix);

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("solver", solverName), ("pointCount", objects.Length), ("unit", unit),
            ("x", x), ("y", y), ("z", z), ("rxDeg", rx), ("ryDeg", ry), ("rzDeg", rz),
            ("reprojectionRms", rms), ("maxError", maxError), ("withinGate", withinGate),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["pose"] = VisionValue.Transform3D(pose),
                ["x"] = VisionValue.Double(x),
                ["y"] = VisionValue.Double(y),
                ["z"] = VisionValue.Double(z),
                ["rxDeg"] = VisionValue.Double(rx),
                ["ryDeg"] = VisionValue.Double(ry),
                ["rzDeg"] = VisionValue.Double(rz),
                ["rvecJson"] = VisionValue.String(JsonSerializer.Serialize(new[]
                {
                    Math.Round(rvec.At<double>(0, 0), 8),
                    Math.Round(rvec.At<double>(1, 0), 8),
                    Math.Round(rvec.At<double>(2, 0), 8)
                })),
                ["tvecJson"] = VisionValue.String(JsonSerializer.Serialize(new[]
                {
                    Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6)
                })),
                ["reprojectionRms"] = VisionValue.Double(rms),
                ["maxError"] = VisionValue.Double(maxError),
                ["pointCount"] = VisionValue.Integer(objects.Length),
                ["found"] = VisionValue.Boolean(true),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["solver"] = solverName, ["pointCount"] = objects.Length, ["unit"] = unit,
                ["x"] = Math.Round(x, 5), ["y"] = Math.Round(y, 5), ["z"] = Math.Round(z, 5),
                ["rxDeg"] = Math.Round(rx, 4), ["ryDeg"] = Math.Round(ry, 4), ["rzDeg"] = Math.Round(rz, 4),
                ["reprojectionRms"] = Math.Round(rms, 5), ["maxError"] = Math.Round(maxError, 5),
                ["withinGate"] = withinGate,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static double[] ParseNumberGrid(string? json, string parameter)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"match.pose3d {parameter} is required.");
        try
        {
            var values = JsonSerializer.Deserialize<double[]>(json);
            if (values is null)
                throw new InvalidOperationException($"match.pose3d {parameter} must be a JSON array of numbers.");
            if (values.Any(value => !double.IsFinite(value)))
                throw new InvalidOperationException($"match.pose3d {parameter} contains a non-finite number.");
            return values;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"match.pose3d {parameter} is not valid JSON: {exception.Message}", exception);
        }
    }

    private static Point3f[] ParseObjectPoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("match.pose3d objectPointsJson is required.");
        double[][]? rows;
        try
        {
            rows = JsonSerializer.Deserialize<double[][]>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"match.pose3d objectPointsJson is not valid JSON: {exception.Message}", exception);
        }
        if (rows is null || rows.Length == 0)
            throw new InvalidOperationException("match.pose3d objectPointsJson must contain at least one point.");
        if (rows.Length > 10000)
            throw new InvalidOperationException("match.pose3d objectPointsJson budget exceeded (max 10000 points).");

        var result = new Point3f[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row is null || row.Length != 3)
                throw new InvalidOperationException($"match.pose3d objectPointsJson row {index} must contain exactly 3 numbers (x,y,z).");
            if (row.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1e9))
                throw new InvalidOperationException($"match.pose3d objectPointsJson row {index} contains a non-finite or out-of-range coordinate.");
            result[index] = new Point3f((float)row[0], (float)row[1], (float)row[2]);
        }
        return result;
    }

    /// <summary>ZYX 欧拉角（先绕 Z、再 Y、最后 X），与机器人常用 RPY 口径一致。</summary>
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
        for (var row = 0; row < column.Rows; row++)
            if (!double.IsFinite(column.At<double>(row, 0))) return false;
        return true;
    }

    private static NodeExecutorResult Missing(string reason, int pointCount, Stopwatch timer)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "match.pose3d", ("found", false), ("reason", reason), ("pointCount", pointCount),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(false),
                ["pointCount"] = VisionValue.Integer(pointCount),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["pointCount"] = pointCount,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            });
    }
}
