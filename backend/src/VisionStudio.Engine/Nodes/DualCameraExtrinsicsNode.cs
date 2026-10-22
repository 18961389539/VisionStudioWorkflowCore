using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Dual / shared-board multi-camera extrinsics (PD-2): solve the rigid SE(3) transform that maps
/// coordinates from camera A into camera B from synchronized calibration-board observations.
/// Backend: OpenCV (SolvePnP / StereoCalibrate / pose average). JLVision in this package exposes
/// calibrate_cameras for intrinsics only — no stereo extrinsics or multi-cam hand-eye API to wire.
/// Supports pose-pair mode (targetToCamera 4x4 per camera) and points mode (object/image points +
/// intrinsics via SolvePnP or OpenCV StereoCalibrate). Summary always sets calibrationBackend=opencv.
/// Honest limits vs HALCON calibrate_cameras / Cognex multi-camera: no N-camera bundle adjustment,
/// no online live sync capture session, no distortion re-estimation in average mode, no stereo
/// rectification maps here (use image.undistort + custom remap), and no versioned dual-cam asset
/// store beyond exporting Transform3D / matrixJson for frame-tree use.
/// </summary>
public sealed class DualCameraExtrinsicsNode : IVisionNodeExecutor
{
    public string Type => "calibration.dualCameraExtrinsics";
    private const double Epsilon = 1e-9;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var pairsJson = context.TryGetValue("pairsJson", out var connected)
            ? connected.Require<string>("pairsJson")
            : node.GetString("pairsJson", "{}");
        DualCameraRequest request;
        try
        {
            request = JsonSerializer.Deserialize<DualCameraRequest>(pairsJson, JsonOpts)
                ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid pairsJson: {ex.Message}", ex);
        }

        var inputMode = node.GetString("inputMode", "poses").Trim().ToLowerInvariant();
        if (inputMode is not ("poses" or "points"))
            throw new InvalidOperationException("inputMode must be poses or points.");
        var method = inputMode == "poses" ? "average" : node.GetString("method", "average").Trim().ToLowerInvariant();
        if (method is not ("average" or "stereocalibrate"))
            throw new InvalidOperationException("method must be average or stereoCalibrate.");

        var pairs = request.Pairs ?? [];
        if (pairs.Count < 3) throw new InvalidOperationException("Dual-camera extrinsics require at least three synchronized board pairs.");
        if (pairs.Count > 500) throw new InvalidOperationException("Dual-camera extrinsics accept at most 500 pairs.");

        List<double[]> relatives;
        string solvePath;
        if (inputMode == "poses")
        {
            relatives = SolveFromPoses(pairs, cancellationToken);
            solvePath = "poses-average";
        }
        else if (method == "stereocalibrate")
        {
            var stereo = SolveStereoCalibrate(request, pairs, node, cancellationToken);
            relatives = [stereo];
            solvePath = "stereoCalibrate";
        }
        else
        {
            relatives = SolveFromPointsAverage(request, pairs, cancellationToken);
            solvePath = "points-pnp-average";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mean = method == "stereocalibrate" ? relatives[0] : MeanRigid(relatives);
        ValidateRigid(mean, "dual-camera solution");

        // Residuals: for stereoCalibrate with a single solution, recompute per-pair relatives via PnP for diagnostics.
        var diagnosticRelatives = method == "stereocalibrate"
            ? SolveFromPointsAverage(request, pairs, cancellationToken)
            : relatives;
        var sampleResiduals = diagnosticRelatives.Select((pose, index) => new
        {
            index,
            translationError = TranslationDistance(pose, mean),
            rotationErrorDeg = RotationDistanceDeg(pose, mean),
            translation = new { x = pose[3], y = pose[7], z = pose[11] }
        }).ToArray();
        var translationRms = Math.Sqrt(sampleResiduals.Average(x => x.translationError * x.translationError));
        var rotationRmsDeg = Math.Sqrt(sampleResiduals.Average(x => x.rotationErrorDeg * x.rotationErrorDeg));
        var maxTranslationError = sampleResiduals.Max(x => x.translationError);
        var maxRotationErrorDeg = sampleResiduals.Max(x => x.rotationErrorDeg);
        var worstTranslationIndex = sampleResiduals.OrderByDescending(x => x.translationError).First().index;
        var worstRotationIndex = sampleResiduals.OrderByDescending(x => x.rotationErrorDeg).First().index;
        var rotationWeight = node.GetDouble("rotationWeight", 50);
        if (!double.IsFinite(rotationWeight) || rotationWeight is < 0.001 or > 1e6)
            throw new InvalidOperationException("rotationWeight must be finite and between 0.001 and 1000000 translation units per radian.");
        var worstSampleIndex = sampleResiduals
            .OrderByDescending(x => Math.Sqrt(x.translationError * x.translationError + Math.Pow(x.rotationErrorDeg * Math.PI / 180 * rotationWeight, 2)))
            .First().index;
        if (new[] { translationRms, rotationRmsDeg, maxTranslationError, maxRotationErrorDeg }.Any(x => !double.IsFinite(x)))
            throw new InvalidOperationException("Dual-camera solve returned non-finite quality metrics.");

        var maxTranslationGate = node.GetDouble("maxTranslationError", 0);
        var maxRotationGate = node.GetDouble("maxRotationErrorDeg", 0);
        if (!double.IsFinite(maxTranslationGate) || maxTranslationGate is < 0 or > 1e6)
            throw new InvalidOperationException("maxTranslationError must be finite and between 0 and 1000000 (0 disables).");
        if (!double.IsFinite(maxRotationGate) || maxRotationGate is < 0 or > 180)
            throw new InvalidOperationException("maxRotationErrorDeg must be finite and between 0 and 180 (0 disables).");
        if (maxTranslationGate > 0 && maxTranslationError > maxTranslationGate)
            throw new InvalidOperationException($"Dual-camera pair {worstTranslationIndex} translation residual {maxTranslationError:0.####} exceeds maxTranslationError {maxTranslationGate:0.####}; remove or re-capture that pair.");
        if (maxRotationGate > 0 && maxRotationErrorDeg > maxRotationGate)
            throw new InvalidOperationException($"Dual-camera pair {worstRotationIndex} rotation residual {maxRotationErrorDeg:0.####} deg exceeds maxRotationErrorDeg {maxRotationGate:0.####}; remove or re-capture that pair.");

        var baseline = Math.Sqrt(mean[3] * mean[3] + mean[7] * mean[7] + mean[11] * mean[11]);
        var sourceFrame = CoordinateValidation.RequireFrame(node.GetString("sourceFrame", "CameraA"), "sourceFrame");
        var targetFrame = CoordinateValidation.RequireFrame(node.GetString("targetFrame", "CameraB"), "targetFrame");
        if (sourceFrame.Equals(targetFrame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("sourceFrame and targetFrame must name different cameras.");
        var unit = CoordinateValidation.RequireUnit(node.GetString("unit", "mm"), "unit");
        var transform = new VisionTransform3D(sourceFrame, targetFrame, unit, mean);
        var matrixJson = JsonSerializer.Serialize(new
        {
            sourceFrame,
            targetFrame,
            unit,
            matrix = mean,
            baseline,
            solvePath,
            pairCount = pairs.Count
        });

        var summary = new Dictionary<string, object?>
        {
            ["inputMode"] = inputMode,
            ["method"] = method,
            ["solvePath"] = solvePath,
            ["pairCount"] = pairs.Count,
            ["translationRms"] = translationRms,
            ["rotationRmsDeg"] = rotationRmsDeg,
            ["maxTranslationError"] = maxTranslationError,
            ["maxRotationErrorDeg"] = maxRotationErrorDeg,
            ["worstSampleIndex"] = worstSampleIndex,
            ["worstTranslationSampleIndex"] = worstTranslationIndex,
            ["worstRotationSampleIndex"] = worstRotationIndex,
            ["baseline"] = baseline,
            ["rotationWeight"] = rotationWeight,
            ["transform"] = transform,
            ["calibrationBackend"] = "opencv",
            ["algorithmBackend"] = "opencv",
            ["algorithmDetail"] = method == "stereocalibrate" ? "opencv-stereoCalibrate" : (inputMode == "points" ? "opencv-solvePnP+average" : "opencv-pose-average"),
            ["limits"] = "OpenCV backend (no JLVision stereo/hand-eye API in this package). No N-camera bundle; no live multi-cam capture session; average mode does not refine intrinsics; no stereo rectification maps."
        };
        var outputs = new Dictionary<string, VisionValue>
        {
            ["transform"] = VisionValue.Transform3D(transform),
            ["matrixJson"] = VisionValue.String(matrixJson),
            ["translationRms"] = VisionValue.Double(translationRms),
            ["rotationRmsDeg"] = VisionValue.Double(rotationRmsDeg),
            ["baseline"] = VisionValue.Double(baseline),
            ["maxTranslationError"] = VisionValue.Double(maxTranslationError),
            ["maxRotationErrorDeg"] = VisionValue.Double(maxRotationErrorDeg),
            ["worstSampleIndex"] = VisionValue.Integer(worstSampleIndex),
            ["worstTranslationSampleIndex"] = VisionValue.Integer(worstTranslationIndex),
            ["worstRotationSampleIndex"] = VisionValue.Integer(worstRotationIndex),
            ["pairCount"] = VisionValue.Integer(pairs.Count),
            ["sampleResidualsJson"] = VisionValue.String(JsonSerializer.Serialize(sampleResiduals))
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
    }

    private static List<double[]> SolveFromPoses(IReadOnlyList<DualCameraPair> pairs, CancellationToken ct)
    {
        var relatives = new List<double[]>(pairs.Count);
        for (var i = 0; i < pairs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = pairs[i] ?? throw new InvalidOperationException($"Pair {i} is null.");
            var a = RequireMatrix(pair.TargetToCameraA, $"pairs[{i}].targetToCameraA");
            var b = RequireMatrix(pair.TargetToCameraB, $"pairs[{i}].targetToCameraB");
            ValidateRigid(a, $"pairs[{i}].targetToCameraA");
            ValidateRigid(b, $"pairs[{i}].targetToCameraB");
            // P_camB = T_B * P_target; P_camA = T_A * P_target ⇒ T_A→B = T_B * inv(T_A)
            relatives.Add(Multiply(b, Inverse(a)));
        }
        return relatives;
    }

    private static List<double[]> SolveFromPointsAverage(DualCameraRequest request, IReadOnlyList<DualCameraPair> pairs, CancellationToken ct)
    {
        var cameraA = RequireCameraMatrix(request.CameraMatrixA, "cameraMatrixA");
        var cameraB = RequireCameraMatrix(request.CameraMatrixB, "cameraMatrixB");
        var distA = RequireDistCoeffs(request.DistCoeffsA, "distCoeffsA");
        var distB = RequireDistCoeffs(request.DistCoeffsB, "distCoeffsB");
        var relatives = new List<double[]>(pairs.Count);
        for (var i = 0; i < pairs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = pairs[i] ?? throw new InvalidOperationException($"Pair {i} is null.");
            var objects = RequireObjectPoints(pair.ObjectPoints, i);
            var imageA = RequireImagePoints(pair.ImagePointsA, objects.Length, i, "A");
            var imageB = RequireImagePoints(pair.ImagePointsB, objects.Length, i, "B");
            var ta = SolvePnPMatrix(objects, imageA, cameraA, distA, i, "A");
            var tb = SolvePnPMatrix(objects, imageB, cameraB, distB, i, "B");
            relatives.Add(Multiply(tb, Inverse(ta)));
        }
        return relatives;
    }

    private static double[] SolveStereoCalibrate(DualCameraRequest request, IReadOnlyList<DualCameraPair> pairs, NodeDefinition node, CancellationToken ct)
    {
        var width = node.GetInt("imageWidth", request.ImageWidth ?? 0);
        var height = node.GetInt("imageHeight", request.ImageHeight ?? 0);
        if (width is < 16 or > 32768 || height is < 16 or > 32768)
            throw new InvalidOperationException("stereoCalibrate requires imageWidth/imageHeight (or pairsJson imageWidth/Height) between 16 and 32768.");

        var cameraA = RequireCameraMatrix(request.CameraMatrixA, "cameraMatrixA");
        var cameraB = RequireCameraMatrix(request.CameraMatrixB, "cameraMatrixB");
        var distA = RequireDistCoeffs(request.DistCoeffsA, "distCoeffsA");
        var distB = RequireDistCoeffs(request.DistCoeffsB, "distCoeffsB");

        var objectPoints = new List<Mat>();
        var imagePointsA = new List<Mat>();
        var imagePointsB = new List<Mat>();
        try
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pair = pairs[i] ?? throw new InvalidOperationException($"Pair {i} is null.");
                var objects = RequireObjectPoints(pair.ObjectPoints, i);
                var ia = RequireImagePoints(pair.ImagePointsA, objects.Length, i, "A");
                var ib = RequireImagePoints(pair.ImagePointsB, objects.Length, i, "B");
                objectPoints.Add(MatOfPoint3f(objects));
                imagePointsA.Add(MatOfPoint2f(ia));
                imagePointsB.Add(MatOfPoint2f(ib));
            }

            using var camMatA = MatFrom3x3(cameraA);
            using var camMatB = MatFrom3x3(cameraB);
            using var distMatA = MatFromDist(distA);
            using var distMatB = MatFromDist(distB);
            using var r = new Mat();
            using var t = new Mat();
            using var e = new Mat();
            using var f = new Mat();
            // Fix intrinsics: callers should solve each camera with calibration.cameraIntrinsics first.
            Cv2.StereoCalibrate(
                objectPoints, imagePointsA, imagePointsB,
                camMatA, distMatA, camMatB, distMatB,
                new Size(width, height),
                r, t, e, f,
                CalibrationFlags.FixIntrinsic);

            ct.ThrowIfCancellationRequested();
            if (r.Empty() || t.Empty())
                throw new InvalidOperationException("OpenCV StereoCalibrate returned an empty solution; check board coverage and pair count.");

            var matrix = Identity();
            for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                matrix[row * 4 + col] = r.At<double>(row, col);
            matrix[3] = t.At<double>(0, 0);
            matrix[7] = t.Rows == 3 ? t.At<double>(1, 0) : t.At<double>(0, 1);
            matrix[11] = t.Rows == 3 ? t.At<double>(2, 0) : t.At<double>(0, 2);
            return matrix;
        }
        finally
        {
            foreach (var mat in objectPoints.Concat(imagePointsA).Concat(imagePointsB)) mat.Dispose();
        }
    }

    private static double[] SolvePnPMatrix(Point3f[] objects, Point2f[] image, double[] cameraMatrix, double[] dist, int pairIndex, string camera)
    {
        using var cameraMat = MatFrom3x3(cameraMatrix);
        using var distMat = MatFromDist(dist);
        using var rvec = new Mat();
        using var tvec = new Mat();
        using var objectMat = MatOfPoint3f(objects);
        using var imageMat = MatOfPoint2f(image);
        try { Cv2.SolvePnP(objectMat, imageMat, cameraMat, distMat, rvec, tvec); }
        catch (OpenCVException ex) { throw new InvalidOperationException($"SolvePnP failed for pair {pairIndex} camera {camera}: {ex.Message}", ex); }
        if (rvec.Empty() || tvec.Empty())
            throw new InvalidOperationException($"SolvePnP failed for pair {pairIndex} camera {camera}; check points and intrinsics.");
        using var rotation = new Mat();
        Cv2.Rodrigues(rvec, rotation);
        var matrix = Identity();
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++)
            matrix[row * 4 + col] = rotation.At<double>(row, col);
        matrix[3] = tvec.At<double>(0, 0);
        matrix[7] = tvec.At<double>(1, 0);
        matrix[11] = tvec.At<double>(2, 0);
        ValidateRigid(matrix, $"pair {pairIndex} camera {camera} PnP");
        return matrix;
    }

    private static Point3f[] RequireObjectPoints(List<List<double>>? points, int pairIndex)
    {
        if (points is null || points.Count < 6)
            throw new InvalidOperationException($"Pair {pairIndex} objectPoints require at least six [X,Y,Z] points.");
        if (points.Count > 5000)
            throw new InvalidOperationException($"Pair {pairIndex} exceeds 5000 object points.");
        if (points.Any(p => p is null || p.Count != 3 || p.Any(v => !double.IsFinite(v))))
            throw new InvalidOperationException($"Pair {pairIndex} objectPoints must be finite [X,Y,Z] arrays.");
        return points.Select(p => new Point3f((float)p[0], (float)p[1], (float)p[2])).ToArray();
    }

    private static Point2f[] RequireImagePoints(List<List<double>>? points, int expected, int pairIndex, string camera)
    {
        if (points is null || points.Count != expected)
            throw new InvalidOperationException($"Pair {pairIndex} imagePoints{camera} must match objectPoints count ({expected}).");
        if (points.Any(p => p is null || p.Count != 2 || p.Any(v => !double.IsFinite(v))))
            throw new InvalidOperationException($"Pair {pairIndex} imagePoints{camera} must be finite [u,v] arrays.");
        return points.Select(p => new Point2f((float)p[0], (float)p[1])).ToArray();
    }

    private static double[] RequireMatrix(double[]? matrix, string name)
    {
        if (matrix is not { Length: 16 } || matrix.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e9))
            throw new InvalidOperationException($"{name} must be a finite row-major 4x4 matrix.");
        return matrix;
    }

    private static double[] RequireCameraMatrix(double[]? matrix, string name)
    {
        if (matrix is not { Length: 9 } || matrix.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException($"{name} must be a finite row-major 3x3 camera matrix.");
        if (matrix[0] <= 0 || matrix[4] <= 0)
            throw new InvalidOperationException($"{name} focal lengths (fx, fy) must be positive.");
        return matrix;
    }

    private static double[] RequireDistCoeffs(double[]? coeffs, string name)
    {
        if (coeffs is null) return [0, 0, 0, 0, 0];
        if (coeffs.Length is < 4 or > 14 || coeffs.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException($"{name} must be 4..14 finite distortion coefficients (or omit for zeros).");
        return coeffs;
    }

    private static Mat MatFrom3x3(double[] m)
    {
        var mat = new Mat(3, 3, MatType.CV_64FC1);
        for (var i = 0; i < 9; i++) mat.Set(i / 3, i % 3, m[i]);
        return mat;
    }

    private static Mat MatFromDist(double[] d)
    {
        var mat = new Mat(d.Length, 1, MatType.CV_64FC1);
        for (var i = 0; i < d.Length; i++) mat.Set(i, 0, d[i]);
        return mat;
    }

    private static Mat MatOfPoint3f(Point3f[] points)
    {
        var mat = new Mat(points.Length, 1, MatType.CV_32FC3);
        for (var i = 0; i < points.Length; i++) mat.Set(i, 0, new Vec3f(points[i].X, points[i].Y, points[i].Z));
        return mat;
    }

    private static Mat MatOfPoint2f(Point2f[] points)
    {
        var mat = new Mat(points.Length, 1, MatType.CV_32FC2);
        for (var i = 0; i < points.Length; i++) mat.Set(i, 0, new Vec2f(points[i].X, points[i].Y));
        return mat;
    }

    private static void ValidateRigid(double[] t, string label)
    {
        if (Math.Abs(t[12]) > 1e-6 || Math.Abs(t[13]) > 1e-6 || Math.Abs(t[14]) > 1e-6 || Math.Abs(t[15] - 1) > 1e-6)
            throw new InvalidOperationException($"{label} must have homogeneous last row [0,0,0,1].");
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
        {
            var dot = 0d;
            for (var k = 0; k < 3; k++) dot += t[k * 4 + r] * t[k * 4 + c];
            if (Math.Abs(dot - (r == c ? 1 : 0)) > 1e-3)
                throw new InvalidOperationException($"{label} rotation must be orthonormal.");
        }
        var det = t[0] * (t[5] * t[10] - t[6] * t[9]) - t[1] * (t[4] * t[10] - t[6] * t[8]) + t[2] * (t[4] * t[9] - t[5] * t[8]);
        if (Math.Abs(det - 1) > 1e-3)
            throw new InvalidOperationException($"{label} rotation determinant must be +1; measured {det:0.####}.");
    }

    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    private static double[] Inverse(double[] t)
    {
        var result = Identity();
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) result[r * 4 + c] = t[c * 4 + r];
        for (var r = 0; r < 3; r++) result[r * 4 + 3] = -(result[r * 4] * t[3] + result[r * 4 + 1] * t[7] + result[r * 4 + 2] * t[11]);
        return result;
    }
    private static double[] Multiply(double[] a, double[] b)
    {
        var result = Identity();
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++) result[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c];
            result[r * 4 + 3] = a[r * 4] * b[3] + a[r * 4 + 1] * b[7] + a[r * 4 + 2] * b[11] + a[r * 4 + 3];
        }
        return result;
    }
    private static double RotationAngle(double[] t) => Math.Acos(Math.Clamp((t[0] + t[5] + t[10] - 1) / 2, -1, 1));
    private static double TranslationDistance(double[] a, double[] b)
    {
        var x = a[3] - b[3]; var y = a[7] - b[7]; var z = a[11] - b[11];
        return Math.Sqrt(x * x + y * y + z * z);
    }
    private static double RotationDistanceDeg(double[] a, double[] b)
    {
        var relative = Multiply(InverseRotation(a), b);
        return RotationAngle(relative) * 180 / Math.PI;
    }
    private static double[] InverseRotation(double[] t)
    {
        var result = Identity();
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) result[r * 4 + c] = t[c * 4 + r];
        return result;
    }
    private static double[] MeanRigid(IReadOnlyList<double[]> poses)
    {
        var mean = Identity();
        for (var r = 0; r < 3; r++) mean[r * 4 + 3] = poses.Average(p => p[r * 4 + 3]);
        var reference = ToQuaternion(poses[0]);
        var sum = new double[4];
        foreach (var pose in poses)
        {
            var q = ToQuaternion(pose);
            if (Dot4(q, reference) < 0) for (var i = 0; i < 4; i++) q[i] = -q[i];
            for (var i = 0; i < 4; i++) sum[i] += q[i];
        }
        var norm = Math.Sqrt(sum.Sum(x => x * x));
        if (norm < Epsilon) return (double[])poses[0].Clone();
        for (var i = 0; i < 4; i++) sum[i] /= norm;
        var w = sum[0]; var x = sum[1]; var y = sum[2]; var z = sum[3];
        mean[0] = 1 - 2 * (y * y + z * z); mean[1] = 2 * (x * y - z * w); mean[2] = 2 * (x * z + y * w);
        mean[4] = 2 * (x * y + z * w); mean[5] = 1 - 2 * (x * x + z * z); mean[6] = 2 * (y * z - x * w);
        mean[8] = 2 * (x * z - y * w); mean[9] = 2 * (y * z + x * w); mean[10] = 1 - 2 * (x * x + y * y);
        return mean;
    }
    private static double[] ToQuaternion(double[] m)
    {
        var trace = m[0] + m[5] + m[10];
        double w, x, y, z;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2; w = 0.25 * s; x = (m[9] - m[6]) / s; y = (m[2] - m[8]) / s; z = (m[4] - m[1]) / s;
        }
        else if (m[0] > m[5] && m[0] > m[10])
        {
            var s = Math.Sqrt(1 + m[0] - m[5] - m[10]) * 2; w = (m[9] - m[6]) / s; x = 0.25 * s; y = (m[1] + m[4]) / s; z = (m[2] + m[8]) / s;
        }
        else if (m[5] > m[10])
        {
            var s = Math.Sqrt(1 + m[5] - m[0] - m[10]) * 2; w = (m[2] - m[8]) / s; x = (m[1] + m[4]) / s; y = 0.25 * s; z = (m[6] + m[9]) / s;
        }
        else
        {
            var s = Math.Sqrt(1 + m[10] - m[0] - m[5]) * 2; w = (m[4] - m[1]) / s; x = (m[2] + m[8]) / s; y = (m[6] + m[9]) / s; z = 0.25 * s;
        }
        var norm = Math.Sqrt(w * w + x * x + y * y + z * z);
        return [w / norm, x / norm, y / norm, z / norm];
    }
    private static double Dot4(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];

    private sealed record DualCameraRequest(
        List<DualCameraPair>? Pairs,
        double[]? CameraMatrixA = null,
        double[]? CameraMatrixB = null,
        double[]? DistCoeffsA = null,
        double[]? DistCoeffsB = null,
        int? ImageWidth = null,
        int? ImageHeight = null);

    private sealed record DualCameraPair(
        double[]? TargetToCameraA = null,
        double[]? TargetToCameraB = null,
        List<List<double>>? ObjectPoints = null,
        List<List<double>>? ImagePointsA = null,
        List<List<double>>? ImagePointsB = null);
}
