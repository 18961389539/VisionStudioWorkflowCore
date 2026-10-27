using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// ArUco / AprilTag 标记识别（recognition.aruco，2026-10-27）。
///
/// 标定板检测节点是"整块棋盘/圆点阵"语义，不能识别单个 ArUco 标记；而现场常用 ArUco
/// 做工件定位、机器人末端标定和工位编号。本节点输出标记 ID、四角、2D 检测框和可选 3D 位姿：
/// 只有连接/填写相机标定 JSON 时才计算位姿，未标定时仍可用于"有没有标记、ID 是多少"。
///
/// 位姿使用 OpenCV solvePnP（针孔 + Brown-Conrady 畸变），输出标记坐标系到相机坐标系的
/// 4×4 变换；鱼眼/远心模型不做位姿，以免给出无法解释的 Z 值。
/// </summary>
public sealed class ArucoMarkerNode : IVisionNodeExecutor
{
    public string Type => "recognition.aruco";

    private readonly record struct MarkerPose(int Id, double[] Rvec, double[] Tvec, double[] Matrix, double Distance);

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        using var gray = RecognitionImageHelpers.ToGray8(source.Mat);

        var dictionaryType = ParseDictionary(node.GetString("dictionary", "Dict4X4_50"));
        var maxMarkers = node.GetInt("maxMarkers", 100);
        if (maxMarkers is < 1 or > 1000)
            throw new InvalidOperationException("recognition.aruco maxMarkers must be within 1..1000.");
        var parameters = BuildDetectorParameters(node);
        var calibration = ReadCalibration(context, node);

        Point2f[][] corners;
        int[] ids;
        float[] confidences;
        Point2f[][] rejected;
        using (var dictionary = Cv2.Aruco.GetPredefinedDictionary(dictionaryType))
        using (var detector = new ArucoDetector(dictionary, parameters, new RefineParameters(10f, 3f, true)))
        {
            detector.DetectMarkersWithConfidence(gray, out corners, out ids, out confidences, out rejected);
            cancellationToken.ThrowIfCancellationRequested();
        }

        corners ??= [];
        ids ??= [];
        confidences ??= [];
        if (ids.Length > maxMarkers)
            throw new InvalidOperationException(
                $"recognition.aruco found {ids.Length} markers above maxMarkers={maxMarkers}; raise the limit instead of silently dropping markers.");

        var detections = BuildDetections(corners, ids, confidences);
        var cornersJson = JsonSerializer.Serialize(corners.Select(quad => quad.Select(point => new { x = point.X, y = point.Y })));

        var poses = new List<MarkerPose>();
        var poseFailures = 0;
        var poseUnit = "mm";
        if (calibration is not null)
        {
            var markerSize = node.GetDouble("markerSize", 50);
            if (!double.IsFinite(markerSize) || markerSize is < 0.001 or > 1_000_000)
                throw new InvalidOperationException("recognition.aruco markerSize must be finite and within 0.001..1000000.");
            poseUnit = node.GetString("markerSizeUnit", "mm").Trim();
            if (poseUnit.Length is < 1 or > 16)
                throw new InvalidOperationException("recognition.aruco markerSizeUnit must be 1..16 characters.");
            for (var index = 0; index < ids.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TrySolvePose(calibration, corners[index], markerSize, ids[index], out var pose))
                    poses.Add(pose);
                else
                    poseFailures++;
            }
        }

        var poseAvailable = poses.Count > 0;
        Mat? overlay = BuildOverlay(gray, corners, ids);
        try
        {
            var image = VisionImage.Own(overlay!);
            overlay = null;
            timer.Stop();
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(
                    Type,
                    ("dictionary", dictionaryType.ToString()),
                    ("count", ids.Length),
                    ("rejectedCount", rejected?.Length ?? 0),
                    ("poseAvailable", poseAvailable),
                    ("poseCount", poses.Count),
                    ("poseFailures", poseFailures),
                    ("elapsedMs", timer.Elapsed.TotalMilliseconds));
                var outputs = new Dictionary<string, VisionValue>
                {
                    ["found"] = VisionValue.Boolean(ids.Length > 0),
                    ["count"] = VisionValue.Integer(ids.Length),
                    ["markerIds"] = VisionValue.String(JsonSerializer.Serialize(ids)),
                    ["detections"] = VisionValue.Detections(detections),
                    ["corners"] = VisionValue.String(cornersJson),
                    ["poseAvailable"] = VisionValue.Boolean(poseAvailable),
                    ["rejectedCount"] = VisionValue.Integer(rejected?.Length ?? 0),
                    ["overlay"] = VisionValue.Image(image),
                    ["diagnostics"] = VisionValue.String(diagnostics)
                };
                if (poseAvailable)
                {
                    outputs["poses"] = VisionValue.String(JsonSerializer.Serialize(poses.Select(pose => new
                    {
                        id = pose.Id,
                        rvec = pose.Rvec,
                        tvec = pose.Tvec,
                        distance = pose.Distance,
                        matrix = pose.Matrix
                    })));
                    var first = poses[0];
                    outputs["pose"] = VisionValue.Transform3D(new VisionTransform3D(
                        $"marker-{first.Id}", "Camera", poseUnit, first.Matrix));
                }
                return new NodeExecutorResult(outputs, new Dictionary<string, object?>
                {
                    ["dictionary"] = dictionaryType.ToString(),
                    ["count"] = ids.Length,
                    ["rejectedCount"] = rejected?.Length ?? 0,
                    ["poseAvailable"] = poseAvailable,
                    ["poseCount"] = poses.Count,
                    ["poseFailures"] = poseFailures,
                    ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
                    ["algorithmBackend"] = "opencv",
                    ["recognitionBackend"] = "opencv",
                    ["diagnostics"] = diagnostics
                });
            }, cancellationToken));
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    private static DetectorParameters BuildDetectorParameters(NodeDefinition node)
    {
        var minWindow = node.GetInt("adaptiveThreshWinSizeMin", 3);
        var maxWindow = node.GetInt("adaptiveThreshWinSizeMax", 23);
        var step = node.GetInt("adaptiveThreshWinSizeStep", 10);
        var constant = node.GetDouble("adaptiveThreshConstant", 7);
        var minPerimeter = node.GetDouble("minMarkerPerimeterRate", 0.03);
        var minBorder = node.GetInt("minDistanceToBorder", 3);
        var errorCorrection = node.GetDouble("errorCorrectionRate", 0.6);
        if (minWindow is < 3 or > 101 || maxWindow is < 3 or > 201 || maxWindow < minWindow)
            throw new InvalidOperationException("recognition.aruco adaptive threshold windows must be 3..201 and max must be at least min.");
        if (step is < 1 or > 50)
            throw new InvalidOperationException("recognition.aruco adaptiveThreshWinSizeStep must be within 1..50.");
        if (!double.IsFinite(constant) || constant is < -100 or > 100)
            throw new InvalidOperationException("recognition.aruco adaptiveThreshConstant must be finite and within -100..100.");
        if (!double.IsFinite(minPerimeter) || minPerimeter is < 0 or > 1)
            throw new InvalidOperationException("recognition.aruco minMarkerPerimeterRate must be finite and within 0..1.");
        if (minBorder is < 0 or > 200)
            throw new InvalidOperationException("recognition.aruco minDistanceToBorder must be within 0..200.");
        if (!double.IsFinite(errorCorrection) || errorCorrection is < 0 or > 1)
            throw new InvalidOperationException("recognition.aruco errorCorrectionRate must be finite and within 0..1.");
        var refinement = node.GetString("cornerRefinement", "none").Trim().ToLowerInvariant() switch
        {
            "none" => CornerRefineMethod.None,
            "subpix" => CornerRefineMethod.Subpix,
            "contour" => CornerRefineMethod.Contour,
            _ => throw new InvalidOperationException("recognition.aruco cornerRefinement must be none, subpix or contour.")
        };

        // Set the OpenCV defaults explicitly: DetectorParameters is a managed struct and a default
        // value must not accidentally turn detection off (e.g. zero windows or zero rates).
        return new DetectorParameters
        {
            AdaptiveThreshWinSizeMin = minWindow,
            AdaptiveThreshWinSizeMax = maxWindow,
            AdaptiveThreshWinSizeStep = step,
            AdaptiveThreshConstant = constant,
            MinMarkerPerimeterRate = minPerimeter,
            MaxMarkerPerimeterRate = 4.0,
            PolygonalApproxAccuracyRate = 0.03,
            MinCornerDistanceRate = 0.05,
            MinDistanceToBorder = minBorder,
            MinMarkerDistanceRate = 0.05,
            CornerRefinementMethod = refinement,
            ErrorCorrectionRate = errorCorrection,
            MarkerBorderBits = 1,
            PerspectiveRemovePixelPerCell = 4,
            PerspectiveRemoveIgnoredMarginPerCell = 0.13,
            MaxErroneousBitsInBorderRate = 0.35,
            MinOtsuStdDev = 5,
            MinMarkerLengthRatioOriginalImg = 0
        };
    }

    internal static PredefinedDictionaryType ParseDictionary(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "dict4x4_50" => PredefinedDictionaryType.Dict4X4_50,
            "dict5x5_100" => PredefinedDictionaryType.Dict5X5_100,
            "dict6x6_250" => PredefinedDictionaryType.Dict6X6_250,
            "dict7x7_50" => PredefinedDictionaryType.Dict7X7_50,
            "dictarucooriginal" => PredefinedDictionaryType.DictArucoOriginal,
            "dictapriltag_36h10" => PredefinedDictionaryType.DictAprilTag_36h10,
            "dictapriltag_36h11" => PredefinedDictionaryType.DictAprilTag_36h11,
            _ => throw new InvalidOperationException(
                "recognition.aruco dictionary must be Dict4X4_50, Dict5X5_100, Dict6X6_250, Dict7X7_50, DictArucoOriginal, DictAprilTag_36h10 or DictAprilTag_36h11.")
        };

    private static CameraCalibrationModel? ReadCalibration(NodeExecutionContext context, NodeDefinition node)
    {
        var json = context.TryGetValue("calibrationJson", out var connected)
            ? connected.Require<string>("calibrationJson")
            : node.GetString("calibrationJson", string.Empty);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return null;
        CameraCalibrationModel model;
        try
        {
            model = JsonSerializer.Deserialize<CameraCalibrationModel>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"recognition.aruco calibrationJson is invalid: {ex.Message}", ex);
        }

        if (model.CameraMatrix is not { Length: 3 } ||
            model.CameraMatrix.Any(row => row is not { Length: 3 } || row.Any(value => !double.IsFinite(value))) ||
            model.CameraMatrix[0][0] <= 0 || model.CameraMatrix[1][1] <= 0 ||
            model.DistCoeffs is null || model.DistCoeffs.Length is not (4 or 5 or 8 or 12 or 14) ||
            model.DistCoeffs.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("recognition.aruco calibrationJson must contain a finite 3x3 cameraMatrix with positive focal lengths and 4/5/8/12/14 distCoeffs.");
        if (model.LensModel == "opencv_fisheye" || model.NativeCameraType == "area_scan_telecentric_polynomial")
            throw new InvalidOperationException("recognition.aruco pose supports pinhole perspective calibration only; undistort/rectify fisheye or telecentric images first.");
        return model;
    }

    private static bool TrySolvePose(
        CameraCalibrationModel calibration, Point2f[] quad, double markerSize, int id, out MarkerPose pose)
    {
        pose = default;
        var half = markerSize / 2;
        var objectPoints = new[]
        {
            new Vec3f((float)-half, (float)half, 0),
            new Vec3f((float)half, (float)half, 0),
            new Vec3f((float)half, (float)-half, 0),
            new Vec3f((float)-half, (float)-half, 0)
        };
        using var objectMat = new Mat(4, 1, MatType.CV_32FC3);
        using var imageMat = new Mat(4, 1, MatType.CV_32FC2);
        using var cameraMat = new Mat(3, 3, MatType.CV_64F);
        using var distMat = new Mat(1, calibration.DistCoeffs.Length, MatType.CV_64F);
        for (var index = 0; index < 4; index++)
        {
            objectMat.Set(index, 0, objectPoints[index]);
            imageMat.Set(index, 0, new Vec2f(quad[index].X, quad[index].Y));
        }
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                cameraMat.Set(row, column, calibration.CameraMatrix[row][column]);
        for (var index = 0; index < calibration.DistCoeffs.Length; index++)
            distMat.Set(0, index, calibration.DistCoeffs[index]);

        using var rvecMat = new Mat();
        using var tvecMat = new Mat();
        Cv2.SolvePnP(objectMat, imageMat, cameraMat, distMat, rvecMat, tvecMat, false, SolvePnPMethod.Iterative);
        if (rvecMat.Empty() || tvecMat.Empty()) return false;
        rvecMat.GetArray(out double[] rvec);
        tvecMat.GetArray(out double[] tvec);
        if (rvec.Length != 3 || tvec.Length != 3 || rvec.Any(value => !double.IsFinite(value)) || tvec.Any(value => !double.IsFinite(value)))
            return false;
        Cv2.Rodrigues(rvec, out var rotation, out _);
        var matrix = new double[16];
        matrix[0] = rotation[0, 0]; matrix[1] = rotation[0, 1]; matrix[2] = rotation[0, 2]; matrix[3] = tvec[0];
        matrix[4] = rotation[1, 0]; matrix[5] = rotation[1, 1]; matrix[6] = rotation[1, 2]; matrix[7] = tvec[1];
        matrix[8] = rotation[2, 0]; matrix[9] = rotation[2, 1]; matrix[10] = rotation[2, 2]; matrix[11] = tvec[2];
        matrix[15] = 1;
        if (matrix.Any(value => !double.IsFinite(value))) return false;
        var distance = Math.Sqrt(tvec[0] * tvec[0] + tvec[1] * tvec[1] + tvec[2] * tvec[2]);
        pose = new MarkerPose(id, rvec, tvec, matrix, distance);
        return true;
    }

    private static List<VisionDetection> BuildDetections(Point2f[][] corners, int[] ids, float[] confidences)
    {
        var detections = new List<VisionDetection>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            var quad = corners[index];
            var minX = quad.Min(point => point.X);
            var minY = quad.Min(point => point.Y);
            var maxX = quad.Max(point => point.X);
            var maxY = quad.Max(point => point.Y);
            var width = maxX - minX;
            var height = maxY - minY;
            var area = PolygonArea(quad);
            var perimeter = PolygonPerimeter(quad);
            var circularity = perimeter > 0 && area > 0 ? 4 * Math.PI * area / (perimeter * perimeter) : 0;
            detections.Add(new VisionDetection(
                index, minX, minY, width, height, area, perimeter, circularity,
                new VisionCircle((minX + maxX) / 2, (minY + maxY) / 2, Math.Sqrt(width * width + height * height) / 2),
                new VisionRectangle2D(minX, minY, width, height),
                index < confidences.Length ? confidences[index] : null,
                ids[index]));
        }
        return detections;
    }

    private static double PolygonArea(Point2f[] points)
    {
        double sum = 0;
        for (var index = 0; index < points.Length; index++)
        {
            var next = (index + 1) % points.Length;
            sum += (double)points[index].X * points[next].Y - (double)points[next].X * points[index].Y;
        }
        return Math.Abs(sum) / 2;
    }

    private static double PolygonPerimeter(Point2f[] points)
    {
        double sum = 0;
        for (var index = 0; index < points.Length; index++)
        {
            var next = (index + 1) % points.Length;
            sum += Math.Sqrt(Math.Pow((double)points[next].X - points[index].X, 2) +
                             Math.Pow((double)points[next].Y - points[index].Y, 2));
        }
        return sum;
    }

    private static Mat BuildOverlay(Mat gray, Point2f[][] corners, int[] ids)
    {
        var overlay = new Mat();
        try
        {
            JLVisionOperators.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);
            for (var index = 0; index < ids.Length; index++)
            {
                var points = corners[index]
                    .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                    .ToArray();
                Cv2.Polylines(overlay, new[] { points }, true, Scalar.LimeGreen, 2);
                var labelPoint = new Point(points.Min(point => point.X), Math.Max(0, points.Min(point => point.Y) - 6));
                Cv2.PutText(overlay, $"ID {ids[index]}", labelPoint, HersheyFonts.HersheySimplex, 0.6, Scalar.LimeGreen, 2);
            }
            return overlay;
        }
        catch
        {
            overlay.Dispose();
            throw;
        }
    }
}
