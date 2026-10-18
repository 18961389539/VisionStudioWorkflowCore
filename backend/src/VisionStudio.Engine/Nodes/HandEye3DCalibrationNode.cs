using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed record HandEye3DSample(double[] RobotToBase, double[] TargetToCamera);
public sealed record HandEye3DRequest(List<HandEye3DSample> Samples);

/// <summary>3D rigid hand-eye calibration from robot and calibration-target homogeneous transforms.</summary>
public sealed class HandEye3DCalibrationNode : IVisionNodeExecutor
{
    private const double Epsilon = 1e-9;
    public string Type => "calibration.handEye3D";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HandEye3DRequest request;
        try
        {
            request = JsonSerializer.Deserialize<HandEye3DRequest>(node.GetString("samplesJson", "{}"),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid samplesJson: {ex.Message}", ex); }

        var samples = request.Samples;
        if (samples is null || samples.Count < 6)
            throw new InvalidOperationException("3D hand-eye calibration requires at least six robot/target pose pairs; use varied rotations around at least two non-parallel axes.");
        if (samples.Count > 1000) throw new InvalidOperationException("3D hand-eye calibration accepts at most 1000 pose pairs.");
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i] is null) throw new InvalidOperationException($"Hand-eye sample {i} is null.");
            ValidateRigid(samples[i].RobotToBase, $"samples[{i}].robotToBase");
            ValidateRigid(samples[i].TargetToCamera, $"samples[{i}].targetToCamera");
        }

        var mode = node.GetString("mode", "eyeInHand").Trim();
        var eyeInHand = mode.Equals("eyeInHand", StringComparison.OrdinalIgnoreCase);
        if (!eyeInHand && !mode.Equals("eyeToHand", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mode must be eyeInHand or eyeToHand.");
        ValidateMotion(samples);
        var robotTranslationSpan = TranslationSpan(samples);
        cancellationToken.ThrowIfCancellationRequested();

        var methodName = node.GetString("method", "DANIILIDIS").Trim().ToUpperInvariant();
        var method = methodName switch
        {
            "TSAI" => HandEyeCalibrationMethod.TSAI,
            "PARK" => HandEyeCalibrationMethod.PARK,
            "HORAUD" => HandEyeCalibrationMethod.HORAUD,
            "ANDREFF" => HandEyeCalibrationMethod.ANDREFF,
            "DANIILIDIS" => HandEyeCalibrationMethod.DANIILIDIS,
            _ => throw new InvalidOperationException("method must be TSAI, PARK, HORAUD, ANDREFF, or DANIILIDIS.")
        };

        var rotations = new List<Mat>(samples.Count * 2);
        try
        {
            var robotRotations = new List<Mat>(samples.Count);
            var robotTranslations = new List<Mat>(samples.Count);
            var targetRotations = new List<Mat>(samples.Count);
            var targetTranslations = new List<Mat>(samples.Count);
            foreach (var sample in samples)
            {
                var robot = eyeInHand ? sample.RobotToBase : Inverse(sample.RobotToBase);
                var robotR = ToRotationMat(robot);
                var robotT = ToTranslationMat(robot);
                var targetR = ToRotationMat(sample.TargetToCamera);
                var targetT = ToTranslationMat(sample.TargetToCamera);
                robotRotations.Add(robotR); robotTranslations.Add(robotT);
                targetRotations.Add(targetR); targetTranslations.Add(targetT);
                rotations.AddRange([robotR, robotT, targetR, targetT]);
            }

            using var outputRotation = new Mat();
            using var outputTranslation = new Mat();
            Cv2.CalibrateHandEye(robotRotations, robotTranslations, targetRotations, targetTranslations,
                outputRotation, outputTranslation, method);
            cancellationToken.ThrowIfCancellationRequested();
            if (outputRotation.Empty() || outputTranslation.Empty())
                throw new InvalidOperationException("OpenCV returned an empty 3D hand-eye solution; collect more varied poses.");

            var cameraToRobot = Identity();
            for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++) cameraToRobot[row * 4 + col] = outputRotation.At<double>(row, col);
            cameraToRobot[3] = outputTranslation.At<double>(0, 0);
            cameraToRobot[7] = outputTranslation.Rows == 3 ? outputTranslation.At<double>(1, 0) : outputTranslation.At<double>(0, 1);
            cameraToRobot[11] = outputTranslation.Rows == 3 ? outputTranslation.At<double>(2, 0) : outputTranslation.At<double>(0, 2);
            ValidateRigid(cameraToRobot, "calibration output");

            // Recover the stationary calibration target pose in each sample, then measure scatter around
            // the mean pose. In eye-to-hand mode the target is rigidly attached to the robot tool.
            var stationary = samples.Select(sample => eyeInHand
                ? Multiply(Multiply(sample.RobotToBase, cameraToRobot), sample.TargetToCamera)
                : Multiply(Multiply(Inverse(sample.RobotToBase), cameraToRobot), sample.TargetToCamera)).ToArray();
            var meanPose = MeanRigid(stationary);
            var sampleResiduals = stationary.Select((pose, index) => new
            {
                index,
                translationError = TranslationDistance(pose, meanPose),
                rotationErrorDeg = RotationDistanceDeg(pose, meanPose),
                robotTranslation = new { x = samples[index].RobotToBase[3], y = samples[index].RobotToBase[7], z = samples[index].RobotToBase[11] },
                targetTranslation = new { x = samples[index].TargetToCamera[3], y = samples[index].TargetToCamera[7], z = samples[index].TargetToCamera[11] }
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
            var worstSampleIndex = sampleResiduals.OrderByDescending(x => Math.Sqrt(x.translationError * x.translationError + Math.Pow(x.rotationErrorDeg * Math.PI / 180 * rotationWeight, 2))).First().index;
            if (new[] { translationRms, rotationRmsDeg, maxTranslationError, maxRotationErrorDeg }.Any(x => !double.IsFinite(x)))
                throw new InvalidOperationException("3D hand-eye solve returned non-finite quality metrics.");

            var maxTranslationGate = node.GetDouble("maxTranslationError", 0);
            var maxRotationGate = node.GetDouble("maxRotationErrorDeg", 0);
            if (!double.IsFinite(maxTranslationGate) || maxTranslationGate is < 0 or > 1e6)
                throw new InvalidOperationException("maxTranslationError must be finite and between 0 and 1000000 (0 disables).");
            if (!double.IsFinite(maxRotationGate) || maxRotationGate is < 0 or > 180)
                throw new InvalidOperationException("maxRotationErrorDeg must be finite and between 0 and 180 (0 disables).");
            if (maxTranslationGate > 0 && maxTranslationError > maxTranslationGate)
                throw new InvalidOperationException($"Hand-eye sample {worstTranslationIndex} translation residual {maxTranslationError:0.####} exceeds maxTranslationError {maxTranslationGate:0.####}; remove or re-teach that pose pair.");
            if (maxRotationGate > 0 && maxRotationErrorDeg > maxRotationGate)
                throw new InvalidOperationException($"Hand-eye sample {worstRotationIndex} rotation residual {maxRotationErrorDeg:0.####} deg exceeds maxRotationErrorDeg {maxRotationGate:0.####}; remove or re-teach that pose pair.");

            var sourceFrame = CoordinateValidation.RequireFrame(node.GetString("sourceFrame", "Camera"), "sourceFrame");
            var defaultTarget = eyeInHand ? "RobotTool" : "RobotBase";
            var targetFrameValue = node.GetString("targetFrame", defaultTarget);
            if (string.IsNullOrWhiteSpace(targetFrameValue)) targetFrameValue = defaultTarget;
            var targetFrame = CoordinateValidation.RequireFrame(targetFrameValue, "targetFrame");
            var unit = CoordinateValidation.RequireUnit(node.GetString("unit", "mm"), "unit");
            var transform = new VisionTransform3D(sourceFrame, targetFrame, unit, cameraToRobot);
            var summary = new Dictionary<string, object?>
            {
                ["mode"] = eyeInHand ? "eyeInHand" : "eyeToHand", ["method"] = methodName,
                ["sampleCount"] = samples.Count, ["translationRms"] = translationRms,
                ["rotationRmsDeg"] = rotationRmsDeg, ["maxTranslationError"] = maxTranslationError,
                ["maxRotationErrorDeg"] = maxRotationErrorDeg, ["worstSampleIndex"] = worstSampleIndex,
                ["worstTranslationSampleIndex"] = worstTranslationIndex, ["worstRotationSampleIndex"] = worstRotationIndex,
                ["robotTranslationSpan"] = robotTranslationSpan, ["rotationWeight"] = rotationWeight,
                ["transform"] = transform, ["sampleResiduals"] = sampleResiduals
            };
            var outputs = new Dictionary<string, VisionValue>
            {
                ["transform"] = VisionValue.Transform3D(transform),
                ["translationRms"] = VisionValue.Double(translationRms),
                ["rotationRmsDeg"] = VisionValue.Double(rotationRmsDeg),
                ["robotTranslationSpan"] = VisionValue.Double(robotTranslationSpan),
                ["maxTranslationError"] = VisionValue.Double(maxTranslationError),
                ["maxRotationErrorDeg"] = VisionValue.Double(maxRotationErrorDeg),
                ["worstSampleIndex"] = VisionValue.Integer(worstSampleIndex),
                ["worstTranslationSampleIndex"] = VisionValue.Integer(worstTranslationIndex),
                ["worstRotationSampleIndex"] = VisionValue.Integer(worstRotationIndex),
                ["sampleResidualsJson"] = VisionValue.String(JsonSerializer.Serialize(sampleResiduals))
            };
            return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
        }
        finally
        {
            foreach (var mat in rotations) mat.Dispose();
        }
    }

    private static void ValidateMotion(IReadOnlyList<HandEye3DSample> samples)
    {
        var axes = new List<double[]>();
        var origin = samples[0].RobotToBase;
        for (var i = 1; i < samples.Count; i++)
        {
            var relative = Multiply(Inverse(origin), samples[i].RobotToBase);
            var angle = RotationAngle(relative);
            if (angle < 3 * Math.PI / 180) continue;
            var axis = RotationAxis(relative, angle);
            if (axis is not null) axes.Add(axis);
        }
        if (axes.Count < 2 || !axes.Any(a => axes.Any(b => Math.Abs(Dot(a, b)) < 0.95)))
            throw new InvalidOperationException("Robot samples must include at least two rotations of 3 degrees or more around non-parallel axes; translation-only or single-axis motions cannot determine a unique 3D hand-eye transform.");
    }

    private static double TranslationSpan(IReadOnlyList<HandEye3DSample> samples)
    {
        var span = 0d;
        for (var i = 0; i < samples.Count; i++)
        for (var j = i + 1; j < samples.Count; j++)
        {
            var dx = samples[i].RobotToBase[3] - samples[j].RobotToBase[3];
            var dy = samples[i].RobotToBase[7] - samples[j].RobotToBase[7];
            var dz = samples[i].RobotToBase[11] - samples[j].RobotToBase[11];
            span = Math.Max(span, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return span;
    }

    private static void ValidateRigid(double[]? matrix, string name)
    {
        if (matrix is not { Length: 16 } || matrix.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e9))
            throw new InvalidOperationException($"{name} must be a finite row-major 4x4 homogeneous matrix with values within +/-1e9.");
        if (Math.Abs(matrix[12]) > 1e-6 || Math.Abs(matrix[13]) > 1e-6 || Math.Abs(matrix[14]) > 1e-6 || Math.Abs(matrix[15] - 1) > 1e-6)
            throw new InvalidOperationException($"{name} must have homogeneous last row [0,0,0,1].");
        var error = 0d;
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
        {
            var dot = 0d;
            for (var k = 0; k < 3; k++) dot += matrix[k * 4 + r] * matrix[k * 4 + c];
            error = Math.Max(error, Math.Abs(dot - (r == c ? 1 : 0)));
        }
        var determinant = matrix[0] * (matrix[5] * matrix[10] - matrix[6] * matrix[9]) - matrix[1] * (matrix[4] * matrix[10] - matrix[6] * matrix[8]) + matrix[2] * (matrix[4] * matrix[9] - matrix[5] * matrix[8]);
        if (error > 1e-3 || Math.Abs(determinant - 1) > 1e-3)
            throw new InvalidOperationException($"{name} rotation must be orthonormal with determinant +1; measured orthogonality error {error:0.####}, determinant {determinant:0.####}.");
    }

    private static Mat ToRotationMat(double[] t)
    {
        var mat = new Mat(3, 3, MatType.CV_64FC1);
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) mat.Set(r, c, t[r * 4 + c]);
        return mat;
    }
    private static Mat ToTranslationMat(double[] t)
    {
        var mat = new Mat(3, 1, MatType.CV_64FC1);
        mat.Set(0, 0, t[3]); mat.Set(1, 0, t[7]); mat.Set(2, 0, t[11]);
        return mat;
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
    private static double[]? RotationAxis(double[] t, double angle)
    {
        var s = 2 * Math.Sin(angle);
        if (Math.Abs(s) < 1e-7) return null;
        var axis = new[] { (t[9] - t[6]) / s, (t[2] - t[8]) / s, (t[4] - t[1]) / s };
        var length = Math.Sqrt(Dot(axis, axis));
        return length < Epsilon ? null : axis.Select(x => x / length).ToArray();
    }
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
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
        if (norm < Epsilon) return poses[0];
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
}
