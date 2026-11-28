using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

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
        var maxErrorPx = node.GetDouble("maxErrorPx", 0);
        var maxConditionNumber = node.GetDouble("maxConditionNumber", 1e12);
        if (!double.IsFinite(maxErrorPx) || maxErrorPx is < 0 or > 100000)
            throw new InvalidOperationException("match.homography maxErrorPx must be within 0..100000 (0 disables the rejection gate).");
        if (!double.IsFinite(maxConditionNumber) || maxConditionNumber < 0 || maxConditionNumber > 1e15)
            throw new InvalidOperationException("match.homography maxConditionNumber must be within 0..1e15 (0 disables the gate).");

        var warpImage = node.GetBool("warpImage", false);
        var robotUse = node.GetBool("robotUse", false);
        var planarityConfirmed = node.GetBool("planarityConfirmed", false);
        if (robotUse && !planarityConfirmed)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                "robot use requires explicit planarityConfirmed=true; 2D low residual cannot prove correspondences share one depth plane",
                sourcePoints.Count, timer, planarityConfirmed));
        }
        var outputWidth = node.GetInt("outputWidth", 0);
        var outputHeight = node.GetInt("outputHeight", 0);
        if (warpImage && (outputWidth is < 0 or > 16384 || outputHeight is < 0 or > 16384))
            throw new InvalidOperationException("match.homography output width/height must be within 0..16384 (0 = input size).");

        if (sourcePoints.Count < 4)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"homography needs at least 4 correspondences, got {sourcePoints.Count}",
                sourcePoints.Count, timer, planarityConfirmed));
        }
        if (HasDuplicatePoints(sourcePoints) || HasDuplicatePoints(targetPoints))
        {
            timer.Stop();
            return ValueTask.FromResult(Missing("duplicate or near-duplicate correspondences", sourcePoints.Count, timer, planarityConfirmed));
        }
        if (IsNearlyCollinear(sourcePoints) || IsNearlyCollinear(targetPoints))
        {
            timer.Stop();
            return ValueTask.FromResult(Missing("source or target points are collinear or nearly collinear", sourcePoints.Count, timer, planarityConfirmed));
        }

        var solution = MatchingGeometryAlgorithms.Homography(sourcePoints, targetPoints, method, threshold,
            maxIterations, confidence, minInliers, cancellationToken, out var failureReason);
        if (solution is null)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(failureReason, sourcePoints.Count, timer, planarityConfirmed));
        }
        var (h, inlierCount, rms, maxError, scaleX, scaleY, rotationDeg, skewDeg, corners) = solution;
        var conditionNumber = MatrixConditionNumber(h);
        if (!double.IsFinite(conditionNumber) || maxConditionNumber > 0 && conditionNumber > maxConditionNumber)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"homography condition number {conditionNumber:0.###E+0} exceeds maxConditionNumber={maxConditionNumber:0.###E+0}",
                sourcePoints.Count, timer, planarityConfirmed, rms, maxError, conditionNumber));
        }
        if (maxErrorPx > 0 && maxError > maxErrorPx + 1e-9)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"maximum reprojection error {maxError:0.####} px exceeds maxErrorPx={maxErrorPx} px",
                sourcePoints.Count, timer, planarityConfirmed, rms, maxError, conditionNumber));
        }

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
            ["conditionNumber"] = VisionValue.Double(conditionNumber),
            ["maxErrorPx"] = VisionValue.Double(maxErrorPx),
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
            ["planarityConfirmed"] = VisionValue.Boolean(planarityConfirmed),
            ["robotSafe"] = VisionValue.Boolean(planarityConfirmed),
            ["diagnostics"] = VisionValue.String(string.Empty)
        };

        if (warpImage)
        {
            var sourceImage = context.RequireImage("image");
            var width = outputWidth > 0 ? outputWidth : sourceImage.Width;
            var height = outputHeight > 0 ? outputHeight : sourceImage.Height;
            var warped = MatchingGeometryAlgorithms.Warp(sourceImage.Mat, h, width, height);
            outputs["warped"] = VisionValue.Image(VisionImage.Own(warped));
        }

        timer.Stop();
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("method", methodName), ("inlierCount", inlierCount), ("pointCount", sourcePoints.Count),
            ("reprojRms", rms), ("maxError", maxError), ("conditionNumber", conditionNumber),
            ("maxErrorPx", maxErrorPx), ("maxConditionNumber", maxConditionNumber), ("scaleX", scaleX), ("scaleY", scaleY),
            ("rotationDeg", rotationDeg), ("skewDeg", skewDeg),
            ("perspectiveX", h[6]), ("perspectiveY", h[7]), ("warpImage", warpImage),
            ("robotUse", robotUse), ("planarityConfirmed", planarityConfirmed), ("robotSafe", planarityConfirmed),
            ("planarityWarning", planarityConfirmed ? null : "2D correspondences cannot prove a single depth plane; do not use as a robot grasp transform"),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        outputs["diagnostics"] = VisionValue.String(diagnostics);

        return ValueTask.FromResult(new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["method"] = methodName, ["inlierCount"] = inlierCount, ["pointCount"] = sourcePoints.Count,
                ["reprojRms"] = Math.Round(rms, 5), ["maxError"] = Math.Round(maxError, 5),
                ["conditionNumber"] = conditionNumber, ["maxErrorPx"] = maxErrorPx, ["maxConditionNumber"] = maxConditionNumber,
                ["scaleX"] = Math.Round(scaleX, 6), ["scaleY"] = Math.Round(scaleY, 6),
                ["rotationDeg"] = Math.Round(rotationDeg, 4), ["skewDeg"] = Math.Round(skewDeg, 4),
                ["perspectiveX"] = Math.Round(h[6], 8), ["perspectiveY"] = Math.Round(h[7], 8),
                ["robotUse"] = robotUse, ["planarityConfirmed"] = planarityConfirmed, ["robotSafe"] = planarityConfirmed,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static bool HasDuplicatePoints(IReadOnlyList<VisionPoint2D> points)
    {
        var scale = Math.Max(1e-6, Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)));
        var tolerance = scale * 1e-7;
        return points.Select(point => ((long)Math.Round(point.X / tolerance), (long)Math.Round(point.Y / tolerance)))
            .GroupBy(key => key).Any(group => group.Count() > 1);
    }

    private static bool IsNearlyCollinear(IReadOnlyList<VisionPoint2D> points)
    {
        var origin = points[0];
        var scale = Math.Max(1e-6, Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)));
        var tolerance = scale * 1e-7;
        var direction = points.Select(point => new { X = point.X - origin.X, Y = point.Y - origin.Y })
            .OrderByDescending(vector => vector.X * vector.X + vector.Y * vector.Y).First();
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length <= tolerance) return true;
        // The origin defines the line and therefore always has zero distance. Only the
        // remaining points can prove that the whole set is (nearly) collinear.
        return points.Skip(1).All(point =>
            Math.Abs((point.X - origin.X) * direction.Y - (point.Y - origin.Y) * direction.X) / length <= tolerance);
    }

    private static double MatrixConditionNumber(double[] matrix)
    {
        // Infinity-norm condition estimate for a normalized 3x3 matrix. Explicit inverse keeps
        // this deterministic and avoids relying on native SVD builds for a small fixed matrix.
        var a = matrix[0]; var b = matrix[1]; var c = matrix[2];
        var d = matrix[3]; var e = matrix[4]; var f = matrix[5];
        var g = matrix[6]; var h = matrix[7]; var i = matrix[8];
        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-15) return double.PositiveInfinity;
        var inverse = new[]
        {
            (e * i - f * h) / determinant, (c * h - b * i) / determinant, (b * f - c * e) / determinant,
            (f * g - d * i) / determinant, (a * i - c * g) / determinant, (c * d - a * f) / determinant,
            (d * h - e * g) / determinant, (b * g - a * h) / determinant, (a * e - b * d) / determinant
        };
        if (inverse.Any(value => !double.IsFinite(value))) return double.PositiveInfinity;
        static double Norm(double[] values) => Enumerable.Range(0, 3).Max(row =>
            Math.Abs(values[row * 3]) + Math.Abs(values[row * 3 + 1]) + Math.Abs(values[row * 3 + 2]));
        return Norm(matrix) * Norm(inverse);
    }

    private static void ValidatePoint(VisionPoint2D point, string port)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 100_000_000 || Math.Abs(point.Y) > 100_000_000)
            throw new InvalidOperationException($"match.homography {port} contains a non-finite or out-of-range coordinate.");
    }

    private static NodeExecutorResult Missing(string reason, int pointCount, Stopwatch timer, bool planarityConfirmed = false,
        double? reprojectionRms = null, double? maxError = null, double? conditionNumber = null)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "match.homography", ("found", false), ("reason", reason), ("pointCount", pointCount),
            ("reprojectionRms", reprojectionRms), ("maxError", maxError), ("conditionNumber", conditionNumber),
            ("planarityConfirmed", planarityConfirmed), ("robotSafe", false),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(false),
            ["pointCount"] = VisionValue.Integer(pointCount),
            ["inlierCount"] = VisionValue.Integer(0),
            ["planarityConfirmed"] = VisionValue.Boolean(planarityConfirmed),
            ["robotSafe"] = VisionValue.Boolean(false),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (reprojectionRms is { } rms) outputs["reprojRms"] = VisionValue.Double(rms);
        if (maxError is { } error) outputs["maxError"] = VisionValue.Double(error);
        if (conditionNumber is { } condition) outputs["conditionNumber"] = VisionValue.Double(condition);
        return new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["pointCount"] = pointCount,
                ["planarityConfirmed"] = planarityConfirmed, ["robotSafe"] = false,
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
        if (camera[0] <= 0 || camera[4] <= 0 || Math.Abs(camera[8]) < 1e-12)
            throw new InvalidOperationException("match.pose3d cameraMatrixJson must have positive fx/fy and a non-zero homogeneous scale.");
        var distortion = ParseNumberGrid(node.GetString("distortionJson", "[]"), "distortionJson");
        // OpenCV accepts 0, 4 (k1,k2,p1,p2), 5 (+k3), 8 (+k4..k6), 12 (+p3,s1..s4) or 14 (+tauX,tauY).
        if (distortion.Length is not (0 or 4 or 5 or 8 or 12 or 14))
            throw new InvalidOperationException(
                $"match.pose3d distortionJson must contain 0, 4, 5, 8, 12 or 14 numbers; got {distortion.Length}.");

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

        var unit = CoordinateValidation.RequireUnit(
            node.GetString("unit", "mm"), "match.pose3d unit", "mm", "cm", "m", "um", "in");
        var objectFrame = CoordinateValidation.RequireFrame(node.GetString("objectFrame", "object"), "match.pose3d objectFrame");
        var cameraFrame = CoordinateValidation.RequireFrame(node.GetString("cameraFrame", "camera"), "match.pose3d cameraFrame");
        if (string.Equals(objectFrame, cameraFrame, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("match.pose3d objectFrame and cameraFrame must be different coordinate frames.");
        var coordinateSystemConfirmed = node.GetBool("coordinateSystemConfirmed", false);
        if (!coordinateSystemConfirmed)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                "coordinate system/unit correspondence is not confirmed; object unit and object/camera frames must be commissioned before pose output",
                objects.Length, timer));
        }
        var allowUnverifiedPose = node.GetBool("allowUnverifiedPose", false);
        var maxRms = node.GetDouble("maxReprojectionRms", 2);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 1000)
            throw new InvalidOperationException("match.pose3d maxReprojectionRms must be within 0..1000 (0 requires allowUnverifiedPose=true).");
        if (maxRms <= 0 && !allowUnverifiedPose)
            throw new InvalidOperationException("match.pose3d maxReprojectionRms=0 disables the correspondence gate; set a positive threshold or explicitly allow an unverified pose.");

        ValidateDistribution(objects, imagePoints);

        // ITERATIVE 内部先用 DLT 求初值，少于 6 点会抛出难懂的断言；提前给出可操作的提示。
        if (solverName == "iterative" && objects.Length < 6)
            throw new InvalidOperationException(
                $"match.pose3d iterative solver needs at least 6 points (got {objects.Length}) because OpenCV seeds it with DLT; use solver=ippe for a planar target or solver=ap3p for exactly 4.");

        var solution = MatchingGeometryAlgorithms.Pose(objects, imagePoints, camera, distortion, solver, useGuess, guessRvec, guessTvec);
        if (solution is null)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing("PnP solver did not converge (degenerate or collinear object points)",
                objects.Length, timer));
        }
        var (matrix, rvecValues, rx, ry, rz, rms, maxError, pointErrors) = solution;
        var qualityGate = maxRms <= 0 ? "disabled" : "enforced";
        var outlierIndices = maxRms <= 0 ? Array.Empty<int>() : pointErrors.Select((error, index) => (error, index))
            .Where(item => item.error > maxRms + 1e-9).Select(item => item.index).ToArray();
        var withinGate = maxRms <= 0 || rms <= maxRms;
        if (!withinGate)
        {
            timer.Stop();
            return ValueTask.FromResult(Missing(
                $"reprojection RMS {rms:0.####} px exceeds maxReprojectionRms={maxRms} px (max error {maxError:0.####} px); correspondence order/unit/frame is unsafe",
                objects.Length, timer, rms, maxError, qualityGate, outlierIndices));
        }

        var x = matrix[3];
        var y = matrix[7];
        var z = matrix[11];
        var pose = new VisionTransform3D(objectFrame, cameraFrame, unit, [.. matrix]);

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("solver", solverName), ("pointCount", objects.Length), ("unit", unit),
            ("x", x), ("y", y), ("z", z), ("rxDeg", rx), ("ryDeg", ry), ("rzDeg", rz),
            ("reprojectionRms", rms), ("maxError", maxError), ("withinGate", withinGate),
            ("qualityGate", qualityGate), ("maxReprojectionRms", maxRms), ("outlierIndices", outlierIndices),
            ("coordinateSystemConfirmed", coordinateSystemConfirmed), ("allowUnverifiedPose", allowUnverifiedPose),
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
                    Math.Round(rvecValues[0], 8),
                    Math.Round(rvecValues[1], 8),
                    Math.Round(rvecValues[2], 8)
                })),
                ["tvecJson"] = VisionValue.String(JsonSerializer.Serialize(new[]
                {
                    Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6)
                })),
                ["reprojectionRms"] = VisionValue.Double(rms),
                ["maxError"] = VisionValue.Double(maxError),
                ["pointCount"] = VisionValue.Integer(objects.Length),
                ["found"] = VisionValue.Boolean(true),
                ["qualityGatePassed"] = VisionValue.Boolean(withinGate),
                ["qualityGate"] = VisionValue.String(qualityGate),
                ["outlierIndices"] = VisionValue.String(JsonSerializer.Serialize(outlierIndices)),
                ["coordinateSystemConfirmed"] = VisionValue.Boolean(coordinateSystemConfirmed),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["solver"] = solverName, ["pointCount"] = objects.Length, ["unit"] = unit,
                ["x"] = Math.Round(x, 5), ["y"] = Math.Round(y, 5), ["z"] = Math.Round(z, 5),
                ["rxDeg"] = Math.Round(rx, 4), ["ryDeg"] = Math.Round(ry, 4), ["rzDeg"] = Math.Round(rz, 4),
                ["reprojectionRms"] = Math.Round(rms, 5), ["maxError"] = Math.Round(maxError, 5),
                ["withinGate"] = withinGate,
                ["qualityGate"] = qualityGate, ["maxReprojectionRms"] = maxRms, ["outlierIndices"] = outlierIndices,
                ["coordinateSystemConfirmed"] = coordinateSystemConfirmed,
                ["allowUnverifiedPose"] = allowUnverifiedPose,
                ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            }));
    }

    private static void ValidateDistribution(Point3f[] objects, IReadOnlyList<VisionPoint2D> imagePoints)
    {
        var objectScale = Math.Max(1e-6,
            Math.Max(objects.Max(p => p.X) - objects.Min(p => p.X),
                Math.Max(objects.Max(p => p.Y) - objects.Min(p => p.Y),
                    objects.Max(p => p.Z) - objects.Min(p => p.Z))));
        var objectTolerance = objectScale * 1e-7;
        var objectKeys = new HashSet<(long, long, long)>();
        foreach (var point in objects)
        {
            if (!objectKeys.Add((Quantize(point.X, objectTolerance), Quantize(point.Y, objectTolerance), Quantize(point.Z, objectTolerance))))
                throw new InvalidOperationException("match.pose3d objectPointsJson contains duplicate or near-duplicate points.");
        }
        if (objectKeys.Count < 4)
            throw new InvalidOperationException("match.pose3d needs at least four distinct object points.");

        var imageScale = Math.Max(1e-6,
            Math.Max(imagePoints.Max(p => p.X) - imagePoints.Min(p => p.X),
                imagePoints.Max(p => p.Y) - imagePoints.Min(p => p.Y)));
        var imageTolerance = imageScale * 1e-7;
        var imageKeys = new HashSet<(long, long)>();
        foreach (var point in imagePoints)
        {
            if (!imageKeys.Add((Quantize(point.X, imageTolerance), Quantize(point.Y, imageTolerance))))
                throw new InvalidOperationException("match.pose3d imagePoints contains duplicate or near-duplicate points.");
        }
        if (imageKeys.Count < 4)
            throw new InvalidOperationException("match.pose3d needs at least four distinct image points.");

        if (IsCollinear3D(objects, objectTolerance))
            throw new InvalidOperationException("match.pose3d object points are collinear or nearly collinear; pose is degenerate.");
        if (IsCollinear2D(imagePoints, imageTolerance))
            throw new InvalidOperationException("match.pose3d image points are collinear or nearly collinear; pose is degenerate.");
    }

    private static bool IsCollinear3D(Point3f[] points, double tolerance)
    {
        var origin = points[0];
        var direction = new double[3];
        var farthest = 0d;
        for (var index = 1; index < points.Length; index++)
        {
            var dx = points[index].X - origin.X;
            var dy = points[index].Y - origin.Y;
            var dz = points[index].Z - origin.Z;
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= farthest) continue;
            farthest = length;
            direction = [dx / length, dy / length, dz / length];
        }
        if (farthest <= tolerance) return true;
        for (var index = 1; index < points.Length; index++)
        {
            var dx = points[index].X - origin.X;
            var dy = points[index].Y - origin.Y;
            var dz = points[index].Z - origin.Z;
            var crossX = dy * direction[2] - dz * direction[1];
            var crossY = dz * direction[0] - dx * direction[2];
            var crossZ = dx * direction[1] - dy * direction[0];
            if (Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ) > tolerance) return false;
        }
        return true;
    }

    private static bool IsCollinear2D(IReadOnlyList<VisionPoint2D> points, double tolerance)
    {
        var origin = points[0];
        var direction = new double[2];
        var farthest = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var dx = points[index].X - origin.X;
            var dy = points[index].Y - origin.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= farthest) continue;
            farthest = length;
            direction = [dx / length, dy / length];
        }
        if (farthest <= tolerance) return true;
        for (var index = 1; index < points.Count; index++)
        {
            var dx = points[index].X - origin.X;
            var dy = points[index].Y - origin.Y;
            if (Math.Abs(dx * direction[1] - dy * direction[0]) > tolerance) return false;
        }
        return true;
    }

    private static long Quantize(double value, double tolerance)
        => (long)Math.Round(value / tolerance, MidpointRounding.AwayFromZero);

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

    private static NodeExecutorResult Missing(string reason, int pointCount, Stopwatch timer,
        double? reprojectionRms = null, double? maxError = null, string qualityGate = "enforced", IReadOnlyList<int>? outlierIndices = null)
    {
        var diagnostics = CoordinateValidation.Diagnostics(
            "match.pose3d", ("found", false), ("reason", reason), ("pointCount", pointCount),
            ("reprojectionRms", reprojectionRms), ("maxError", maxError), ("qualityGatePassed", false),
            ("qualityGate", qualityGate), ("outlierIndices", outlierIndices ?? Array.Empty<int>()),
            ("elapsedMs", timer.Elapsed.TotalMilliseconds));
        var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["found"] = VisionValue.Boolean(false),
            ["pointCount"] = VisionValue.Integer(pointCount),
            ["qualityGatePassed"] = VisionValue.Boolean(false),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        if (reprojectionRms is { } rms) outputs["reprojectionRms"] = VisionValue.Double(rms);
        if (maxError is { } error) outputs["maxError"] = VisionValue.Double(error);
        outputs["qualityGate"] = VisionValue.String(qualityGate);
        outputs["outlierIndices"] = VisionValue.String(JsonSerializer.Serialize(outlierIndices ?? Array.Empty<int>()));
        return new NodeExecutorResult(outputs,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = false, ["reason"] = reason, ["pointCount"] = pointCount,
                ["reprojectionRms"] = reprojectionRms, ["maxError"] = maxError,
                ["qualityGate"] = qualityGate, ["outlierIndices"] = outlierIndices ?? Array.Empty<int>(),
                ["qualityGatePassed"] = false,
                ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
            });
    }
}
