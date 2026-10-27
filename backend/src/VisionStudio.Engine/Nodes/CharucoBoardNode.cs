using System.Text.Json;
using JLVisionLib;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// ChArUco 标定板检测（calibration.charuco，2026-10-27）。
///
/// `calibration.detectBoard` 只支持整块棋盘/圆点阵，`recognition.aruco` 只识别单个标记；
/// ChArUco 板介于两者之间：用 ArUco 标记定位棋盘格角点，遮挡和部分可见时仍能提供高质量
/// 亚像素角点。本节点把检测到的角点按 id 映射到板坐标系，并追加到与
/// `calibration.cameraIntrinsics` 共用的 calibrationDataJson 视图列表。
///
/// 检测本身使用 OpenCV 5 CharucoDetector；质量门限复用 CalibrationViewQuality，保持与
/// chessboard 检测相同的模糊度/覆盖率语义，避免两条标定入口给出互相矛盾的视图判定。
/// </summary>
public sealed class CharucoBoardNode : IVisionNodeExecutor
{
    public string Type => "calibration.charuco";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        if (source.Mat.Empty())
            throw new InvalidOperationException("calibration.charuco requires a non-empty image.");
        if (source.Mat.Channels() is not (1 or 3 or 4))
            throw new InvalidOperationException($"calibration.charuco supports 1, 3, or 4 channel images; received {source.Mat.Channels()}.");

        var squaresX = node.GetInt("squaresX", 5);
        var squaresY = node.GetInt("squaresY", 7);
        if (squaresX is < 2 or > 30 || squaresY is < 2 or > 30)
            throw new InvalidOperationException("calibration.charuco squaresX and squaresY must be within 2..30.");
        var squareLength = node.GetDouble("squareLengthMm", 30);
        var markerLength = node.GetDouble("markerLengthMm", 15);
        if (!double.IsFinite(squareLength) || squareLength is < 0.1 or > 10000 ||
            !double.IsFinite(markerLength) || markerLength is < 0.1 or > 10000 ||
            markerLength >= squareLength)
            throw new InvalidOperationException("calibration.charuco requires squareLengthMm > markerLengthMm >= 0.1 and both within 10000 mm.");
        var dictionaryType = ArucoMarkerNode.ParseDictionary(node.GetString("dictionary", "Dict4X4_50"));
        var legacyPattern = node.GetBool("legacyPattern", false);
        var minCorners = node.GetInt("minCorners", 6);
        if (minCorners is < 3 or > 1000)
            throw new InvalidOperationException("calibration.charuco minCorners must be within 3..1000.");
        var minMarkers = node.GetInt("minMarkers", 1);
        if (minMarkers is < 0 or > 1000)
            throw new InvalidOperationException("calibration.charuco minMarkers must be within 0..1000.");
        var refineMarkers = node.GetBool("refineMarkers", true);
        var minBlur = node.GetDouble("minBlurVariance", 0);
        if (!double.IsFinite(minBlur) || minBlur < 0)
            throw new InvalidOperationException("calibration.charuco minBlurVariance must be finite and non-negative (0 disables).");
        var minCoverage = node.GetDouble("minCoverage", 0);
        if (!double.IsFinite(minCoverage) || minCoverage is < 0 or > 1)
            throw new InvalidOperationException("calibration.charuco minCoverage must be within 0..1 (0 disables).");
        var rejectWeak = node.GetBool("rejectWeakViews", false);

        var request = new CameraCalibrationRequest(source.Width, source.Height, []);
        if (context.TryGetValue("calibrationDataJson", out var previous))
        {
            try
            {
                request = JsonSerializer.Deserialize<CameraCalibrationRequest>(previous.Require<string>("calibrationDataJson"), Json)
                    ?? throw new JsonException("empty request");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Invalid calibrationDataJson.", ex);
            }
            if (request.ImageWidth != source.Width || request.ImageHeight != source.Height ||
                request.Views is null || request.Views.Count > 100)
                throw new InvalidOperationException("Accumulated ChArUco views must use the same image dimensions and contain at most 100 views.");
            request = request with { Views = request.Views.ToList() };
        }

        using var native = JLVisionImages.FromMat(source.Mat);
        using var channels = native.CountChannels();
        using var grayNative = channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
        using var gray = JLVisionImages.ToMat(grayNative);
        using var blurGray = gray.Clone();
        var blurVariance = CalibrationViewQuality.LaplacianVariance(blurGray);

        Point2f[] charucoCorners;
        int[] charucoIds;
        Point2f[][] markerCorners;
        int[] markerIds;
        using (var dictionary = Cv2.Aruco.GetPredefinedDictionary(dictionaryType))
        using (var board = new CharucoBoard(squaresX, squaresY, (float)squareLength, (float)markerLength, dictionary, null))
        {
            board.LegacyPattern = legacyPattern;
            var boardCorners = board.GetChessboardCorners();
            if (minCorners > boardCorners.Length)
                throw new InvalidOperationException(
                    $"calibration.charuco minCorners={minCorners} exceeds the {boardCorners.Length} chessboard corners on a {squaresX}x{squaresY} board.");

            using var detector = new CharucoDetector(board);
            using var charucoParameters = detector.GetCharucoParameters();
            charucoParameters.MinMarkers = minMarkers;
            charucoParameters.TryRefineMarkers = refineMarkers;
            charucoParameters.CheckMarkers = true;
            detector.SetCharucoParameters(charucoParameters);
            detector.DetectBoard(gray, out charucoCorners, out charucoIds, out markerCorners, out markerIds);
            cancellationToken.ThrowIfCancellationRequested();

            charucoCorners ??= [];
            charucoIds ??= [];
            markerCorners ??= [];
            markerIds ??= [];

            if (charucoIds.Length != charucoCorners.Length)
                throw new InvalidOperationException("calibration.charuco detector returned mismatched ChArUco corner and id counts.");
            if (charucoIds.Any(id => id < 0 || id >= boardCorners.Length) || charucoIds.Distinct().Count() != charucoIds.Length)
                throw new InvalidOperationException("calibration.charuco detector returned invalid or duplicate ChArUco ids.");

            CameraCalibrationView? view = null;
            string? reason = null;
            CalibrationViewQuality.Report? quality = null;
            var collinear = false;
            if (charucoCorners.Length < minCorners)
            {
                reason = "insufficient-charuco-corners";
            }
            else
            {
                collinear = AreCollinear(charucoCorners);
                if (collinear)
                {
                    reason = "collinear-charuco-corners";
                }
                else
                {
                    var objects = new List<List<double>>(charucoIds.Length);
                    var images = new List<List<double>>(charucoCorners.Length);
                    for (var index = 0; index < charucoCorners.Length; index++)
                    {
                        var objectPoint = boardCorners[charucoIds[index]];
                        objects.Add([objectPoint.X, objectPoint.Y, objectPoint.Z]);
                        images.Add([charucoCorners[index].X, charucoCorners[index].Y]);
                    }
                    view = new CameraCalibrationView(objects, images);
                    quality = CalibrationViewQuality.Evaluate(images, source.Width, source.Height,
                        boardCorners.Length, blurVariance, minBlur, minCoverage);
                    if (rejectWeak && quality.Verdict == CalibrationViewQuality.Verdict.Fail)
                    {
                        reason = quality.Reason;
                        view = null;
                    }
                    else
                    {
                        if (request.Views.Count >= 100)
                            throw new InvalidOperationException("Calibration view budget exceeded (100).");
                        request.Views.Add(view);
                    }
                }
            }
            quality ??= CalibrationViewQuality.Evaluate(charucoCorners.Select(point => new List<double> { point.X, point.Y }).ToList(),
                source.Width, source.Height, boardCorners.Length, blurVariance, minBlur, minCoverage);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BuildResult(view, reason, quality,
                charucoCorners, charucoIds, markerIds, markerCorners, gray, request, blurVariance,
                dictionaryType, squaresX, squaresY, collinear, rejectWeak, cancellationToken));
        }
    }

    private NodeExecutorResult BuildResult(
        CameraCalibrationView? accepted, string? failReason, CalibrationViewQuality.Report quality,
        Point2f[] charucoCorners, int[] charucoIds, int[] markerIds, Point2f[][] markerCorners,
        Mat gray, CameraCalibrationRequest request, double blurVariance, PredefinedDictionaryType dictionaryType,
        int squaresX, int squaresY, bool collinear, bool rejectWeak, CancellationToken cancellationToken)
    {
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(accepted is not null),
            ["count"] = VisionValue.Integer(accepted?.ImagePoints.Count ?? charucoCorners.Length),
            ["charucoCorners"] = VisionValue.String(JsonSerializer.Serialize(charucoCorners.Select(point => new { x = point.X, y = point.Y }))),
            ["charucoIds"] = VisionValue.String(JsonSerializer.Serialize(charucoIds)),
            ["markerCount"] = VisionValue.Integer(markerIds.Length),
            ["calibrationDataJson"] = VisionValue.String(JsonSerializer.Serialize(request)),
            ["blurVariance"] = VisionValue.Double(blurVariance),
            ["coverageX"] = VisionValue.Double(quality.CoverageX),
            ["coverageY"] = VisionValue.Double(quality.CoverageY),
            ["quality"] = VisionValue.String(quality.Verdict.ToString()),
            ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(
                Type,
                ("found", accepted is not null),
                ("count", accepted?.ImagePoints.Count ?? charucoCorners.Length),
                ("markerCount", markerIds.Length),
                ("views", request.Views.Count),
                ("dictionary", dictionaryType.ToString()),
                ("board", $"{squaresX}x{squaresY}"),
                ("blurVariance", blurVariance),
                ("coverageX", quality.CoverageX),
                ("coverageY", quality.CoverageY),
                ("quality", quality.Verdict.ToString()),
                ("collinear", collinear),
                ("reason", failReason)))
        };
        if (accepted is not null) outputs["viewJson"] = VisionValue.String(JsonSerializer.Serialize(accepted));

        var overlay = BuildOverlay(gray, charucoCorners, charucoIds, markerCorners, markerIds);
        try
        {
            var image = VisionImage.Own(overlay);
            overlay = null;
            outputs["overlay"] = VisionValue.Image(image);
            return NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                return new NodeExecutorResult(outputs, new Dictionary<string, object?>
                {
                    ["found"] = accepted is not null,
                    ["count"] = accepted?.ImagePoints.Count ?? charucoCorners.Length,
                    ["markerCount"] = markerIds.Length,
                    ["views"] = request.Views.Count,
                    ["dictionary"] = dictionaryType.ToString(),
                    ["board"] = $"{squaresX}x{squaresY}",
                    ["reason"] = failReason,
                    ["blurVariance"] = blurVariance,
                    ["coverageX"] = quality.CoverageX,
                    ["coverageY"] = quality.CoverageY,
                    ["quality"] = quality.Verdict.ToString(),
                    ["qualityReason"] = quality.Reason,
                    ["guidance"] = quality.Guidance,
                    ["rejectWeakViews"] = rejectWeak,
                    ["algorithmBackend"] = "opencv",
                    ["calibrationBackend"] = "opencv",
                    ["diagnostics"] = outputs["diagnostics"].Require<string>("diagnostics")
                });
            }, cancellationToken);
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    /// <summary>
    /// Managed collinearity gate. CharucoBoard.CheckCharucoCornersCollinear crashed inside
    /// OpenCvSharp5 with a CV_32FC2 Mat in this build, so the node keeps the same safety rule
    /// (all corners must not lie on one line) without calling that native entry point.
    /// </summary>
    private static bool AreCollinear(Point2f[] points)
    {
        if (points.Length < 3) return false;
        var first = points[0];
        var last = points[^1];
        var dx = (double)last.X - first.X;
        var dy = (double)last.Y - first.Y;
        var norm = Math.Sqrt(dx * dx + dy * dy);
        if (norm < 1e-6) return false;
        foreach (var point in points)
        {
            var distance = Math.Abs((point.X - (double)first.X) * dy - (point.Y - (double)first.Y) * dx) / norm;
            if (distance > 0.5) return false;
        }
        return true;
    }

    private static Mat BuildOverlay(Mat gray, Point2f[] charucoCorners, int[] charucoIds, Point2f[][] markerCorners, int[] markerIds)
    {
        var overlay = new Mat();
        try
        {
            JLVisionOperators.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);
            for (var index = 0; index < markerIds.Length && index < markerCorners.Length; index++)
            {
                var points = markerCorners[index]
                    .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                    .ToArray();
                Cv2.Polylines(overlay, new[] { points }, true, Scalar.LimeGreen, 2);
                Cv2.PutText(overlay, $"M{markerIds[index]}", points[0], HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1);
            }
            for (var index = 0; index < charucoCorners.Length; index++)
            {
                var point = new Point((int)Math.Round(charucoCorners[index].X), (int)Math.Round(charucoCorners[index].Y));
                Cv2.Circle(overlay, point, 3, Scalar.Yellow, -1);
                Cv2.PutText(overlay, charucoIds.Length > index ? charucoIds[index].ToString() : string.Empty,
                    new Point(point.X + 4, point.Y - 4), HersheyFonts.HersheySimplex, 0.4, Scalar.Yellow, 1);
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
