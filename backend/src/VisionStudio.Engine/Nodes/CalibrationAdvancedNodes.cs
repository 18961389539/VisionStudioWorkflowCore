using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed record CameraCalibrationView(List<List<double>> ObjectPoints, List<List<double>> ImagePoints);
public sealed record CameraCalibrationRequest(int ImageWidth, int ImageHeight, List<CameraCalibrationView> Views);
public sealed record CameraCalibrationModel(int ImageWidth, int ImageHeight, double[][] CameraMatrix, double[] DistCoeffs, double Rms, string? NativeCameraType = null, double[]? NativeCameraParameters = null, string? LensModel = null);

/// <summary>Intrinsic pinhole, fisheye, or object-side telecentric calibration from corresponding object/image points.</summary>
public sealed class CameraIntrinsicCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.cameraIntrinsics";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var json = context.TryGetValue("calibrationDataJson", out var connectedViews)
            ? connectedViews.Require<string>("calibrationDataJson")
            : node.GetString("calibrationDataJson", "{}");
        CameraCalibrationRequest request;
        try { request = JsonSerializer.Deserialize<CameraCalibrationRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid calibrationDataJson: {ex.Message}", ex); }
        var cameraModel = node.GetString("cameraModel", "pinhole").Trim().ToLowerInvariant();
        if (cameraModel is not ("pinhole" or "fisheye" or "telecentric")) throw new InvalidOperationException("cameraModel must be pinhole, fisheye, or telecentric.");
        var telecentricMagnification = node.GetDouble("telecentricMagnification", 0.2);
        var telecentricPixelSizeUm = node.GetDouble("telecentricPixelSizeUm", 3.45);
        if (cameraModel == "telecentric" && (!double.IsFinite(telecentricMagnification) || telecentricMagnification is < 0.001 or > 10))
            throw new InvalidOperationException("telecentricMagnification must be finite and between 0.001 and 10.");
        if (cameraModel == "telecentric" && (!double.IsFinite(telecentricPixelSizeUm) || telecentricPixelSizeUm is < 0.1 or > 100))
            throw new InvalidOperationException("telecentricPixelSizeUm must be finite and between 0.1 and 100.");
        if (request.ImageWidth is < 16 or > 32768 || request.ImageHeight is < 16 or > 32768) throw new InvalidOperationException("ImageWidth and ImageHeight must each be between 16 and 32768 pixels.");
        if (request.Views is null || request.Views.Count < 3) throw new InvalidOperationException("Intrinsic calibration requires at least three views; use varied board orientations and positions.");
        if (request.Views.Count > 500) throw new InvalidOperationException("Intrinsic calibration accepts at most 500 views.");
        var objects = new List<IEnumerable<Point3f>>(); var images = new List<IEnumerable<Point2f>>();
        var totalPointCount = 0;
        foreach (var (view, index) in request.Views.Select((v, i) => (v, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view is null || view.ObjectPoints is null || view.ImagePoints is null || view.ObjectPoints.Count < 6 || view.ObjectPoints.Count != view.ImagePoints.Count)
                throw new InvalidOperationException($"View {index} must have matching object/image point arrays with at least six points.");
            if (view.ObjectPoints.Count > 5000) throw new InvalidOperationException($"View {index} exceeds the 5000-point limit.");
            totalPointCount = checked(totalPointCount + view.ObjectPoints.Count);
            if (totalPointCount > 100000) throw new InvalidOperationException("Intrinsic calibration accepts at most 100000 total correspondence points.");
            if (view.ObjectPoints.Any(p => p is null || p.Count != 3 || p.Any(v => !double.IsFinite(v) || !float.IsFinite((float)(v * 0.001)))) || view.ImagePoints.Any(p => p is null || p.Count != 2 || p.Any(v => !double.IsFinite(v))))
                throw new InvalidOperationException($"View {index} points must be finite [X,Y,Z] and [u,v] arrays.");
            var ip = view.ImagePoints.Select(p => new Point2f((float)p[0], (float)p[1])).ToArray();
            if (ip.Any(p => p.X < 0 || p.Y < 0 || p.X >= request.ImageWidth || p.Y >= request.ImageHeight)) throw new InvalidOperationException($"View {index} contains image points outside the declared image size.");
            var mx=ip.Average(p=>(double)p.X); var my=ip.Average(p=>(double)p.Y);
            var xx=ip.Sum(p=>Math.Pow(p.X-mx,2)); var yy=ip.Sum(p=>Math.Pow(p.Y-my,2)); var xy=ip.Sum(p=>(p.X-mx)*(p.Y-my));
            if (xx*yy-xy*xy < request.ImageWidth * request.ImageHeight * 1e-5) throw new InvalidOperationException($"View {index} image points are degenerate or nearly collinear.");
            objects.Add(view.ObjectPoints.Select(p => new Point3f((float)p[0], (float)p[1], (float)p[2])).ToArray());
            images.Add(ip);
        }
        // Per-view quality reporting before trusting the model: point coverage plus,
        // for planar boards, a homography reprojection error per view. A single bad
        // view (mis-detected corners) is the most common cause of a poor calibration,
        // and a global RMS alone hides it.
        var viewStats = request.Views.Select((view, index) =>
        {
            var stat = CalibrationViewAnalysis.Analyze(view.ObjectPoints, view.ImagePoints, request.ImageWidth, request.ImageHeight);
            return new { index, pointCount = stat.PointCount, coverageX = Math.Round(stat.CoverageX, 4), coverageY = Math.Round(stat.CoverageY, 4), reprojectionRms = stat.ReprojectionRms, reprojectionMax = stat.ReprojectionMax };
        }).ToArray();
        var maxViewRms = node.GetDouble("maxViewRms", 0);
        if (!double.IsFinite(maxViewRms) || maxViewRms is < 0 or > 100) throw new InvalidOperationException("maxViewRms must be finite and between 0 and 100 (0 disables the per-view gate).");
        if (maxViewRms > 0 && (cameraModel is "pinhole" or "telecentric"))
        {
            var bad = viewStats.FirstOrDefault(v => v.reprojectionRms is not null && v.reprojectionRms > maxViewRms);
            if (bad is not null) throw new InvalidOperationException($"Calibration view {bad.index} has planar reprojection RMS {bad.reprojectionRms:0.###} px above maxViewRms {maxViewRms:0.###}; check that view's detected points.");
        }
        CameraCalibrationModel model;
        FisheyeCalibrationResult? fisheyeResult = null;
        try
        {
            if (cameraModel == "fisheye")
            {
                fisheyeResult = JLVisionCalibration.CalibrateFisheye(request, cancellationToken);
                model = fisheyeResult.Model;
            }
            else model = JLVisionCalibration.Calibrate(request, cancellationToken,
                cameraModel == "telecentric" ? "area_scan_telecentric_polynomial" : "area_scan_polynomial",
                telecentricMagnification, telecentricPixelSizeUm);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"{(cameraModel == "fisheye" ? "OpenCV fisheye" : "JLVision")} intrinsic calibration failed: {ex.Message}", ex); }
        var rms = model.Rms;
        var maxRms = node.GetDouble("maxRms", 0);
        if (!double.IsFinite(maxRms) || maxRms is < 0 or > 1000) throw new InvalidOperationException("maxRms must be finite and between 0 and 1000 (0 disables the model gate).");
        if (maxRms > 0 && rms > maxRms) throw new InvalidOperationException($"Intrinsic calibration RMS {rms:0.###} exceeds maxRms {maxRms:0.###}; collect better-distributed views or remove the worst view (see viewStats).");
        var resultJson = JsonSerializer.Serialize(model);
        var nativePolynomialDistortionActive = (model.NativeCameraType is "area_scan_polynomial" or "area_scan_telecentric_polynomial")
            && model.NativeCameraParameters is { Length: 12 }
            && model.NativeCameraParameters.Skip(1).Take(5).Any(value => Math.Abs(value) > 1e-12);
        var telecentricModelActive = model.NativeCameraType == "area_scan_telecentric_polynomial";
        // Fuller per-view reprojection through the fitted camera matrix itself:
        // recover each planar view's pose from K^-1 H and reproject (homography alone
        // absorbs pose error, so this is the stricter, model-based number).
        var fx = model.CameraMatrix[0][0]; var fy = model.CameraMatrix[1][1];
        var cx = model.CameraMatrix[0][2]; var cy = model.CameraMatrix[1][2];
        var fullViewStats = request.Views.Select((view, index) =>
        {
            var pose = fisheyeResult is null && !nativePolynomialDistortionActive && !telecentricModelActive
                ? CalibrationViewAnalysis.AnalyzeWithCamera(view.ObjectPoints, view.ImagePoints, request.ImageWidth, request.ImageHeight, fx, fy, cx, cy, model.DistCoeffs)
                : null;
            var homography = viewStats[index];
            var fittedRms = fisheyeResult is not null ? fisheyeResult.ViewErrors[index].Rms : pose?.ReprojectionRms == homography.reprojectionRms ? null : pose?.ReprojectionRms;
            var fittedMax = fisheyeResult is not null ? fisheyeResult.ViewErrors[index].Max : pose?.ReprojectionRms == homography.reprojectionRms ? null : pose?.ReprojectionMax;
            return new
            {
                homography.index, homography.pointCount, homography.coverageX, homography.coverageY,
                homographyRms = homography.reprojectionRms, homographyMax = homography.reprojectionMax,
                poseReprojectionRms = fittedRms,
                poseReprojectionMax = fittedMax
            };
        }).ToArray();
        var worstView = cameraModel == "fisheye"
            ? fullViewStats.Where(v => v.poseReprojectionRms is not null).OrderByDescending(v => v.poseReprojectionRms!.Value).FirstOrDefault()
            : fullViewStats.Where(v => v.homographyRms is not null).OrderByDescending(v => v.homographyRms!.Value).FirstOrDefault();
        var worstViewRms = worstView is null ? 0 : cameraModel == "fisheye" ? worstView.poseReprojectionRms!.Value : worstView.homographyRms!.Value;
        var worstViewIndex = worstView is null ? (int?)null : worstView.index;
        var worstPoseViewRms = fullViewStats.Where(v => v.poseReprojectionRms is not null).Select(v => v.poseReprojectionRms!.Value).DefaultIfEmpty(0).Max();
        var worstPoseViewIndex = fullViewStats.Where(v => v.poseReprojectionRms is not null).OrderByDescending(v => v.poseReprojectionRms!.Value).Select(v => (int?)v.index).FirstOrDefault();
        var poseRmsValues = fullViewStats.Where(v => v.poseReprojectionRms is not null).Select(v => v.poseReprojectionRms!.Value).OrderBy(v => v).ToArray();
        var medianPoseRms = poseRmsValues.Length == 0 ? 0d : poseRmsValues.Length % 2 == 1 ? poseRmsValues[poseRmsValues.Length / 2] : (poseRmsValues[poseRmsValues.Length / 2 - 1] + poseRmsValues[poseRmsValues.Length / 2]) / 2d;
        var fisheyeModelActive = model.LensModel == "opencv_fisheye";
        var maxPoseViewRms = node.GetDouble("maxPoseViewRms", 0);
        if (!double.IsFinite(maxPoseViewRms) || maxPoseViewRms is < 0 or > 100) throw new InvalidOperationException("maxPoseViewRms must be finite and between 0 and 100 (0 disables the fitted-model per-view gate).");
        if (maxPoseViewRms > 0 && (nativePolynomialDistortionActive || telecentricModelActive))
            throw new InvalidOperationException("maxPoseViewRms cannot accurately score the selected native camera model. Use maxViewRms and maxRms or disable this gate.");
        if (maxPoseViewRms > 0)
        {
            var bad = fullViewStats.FirstOrDefault(v => v.poseReprojectionRms is not null && v.poseReprojectionRms > maxPoseViewRms);
            if (bad is not null) throw new InvalidOperationException($"Calibration view {bad.index} has fitted-model reprojection RMS {bad.poseReprojectionRms:0.###} px above maxPoseViewRms {maxPoseViewRms:0.###}; check or disable that view.");
        }
        if (cameraModel == "fisheye" && maxViewRms > 0)
        {
            var bad = fullViewStats.FirstOrDefault(v => v.poseReprojectionRms is not null && v.poseReprojectionRms > maxViewRms);
            if (bad is not null) throw new InvalidOperationException($"Calibration view {bad.index} has fitted fisheye reprojection RMS {bad.poseReprojectionRms:0.###} px above maxViewRms {maxViewRms:0.###}; check or disable that view.");
        }
        // Outlier view: pose RMS more than 3x the median (and above 0.5 px) - the view
        // to inspect/remove first, as mature calibrators flag suspect views.
        var outlierViewIndex = !nativePolynomialDistortionActive && !telecentricModelActive && medianPoseRms > 0 && worstPoseViewRms > Math.Max(0.5, 3 * medianPoseRms) ? worstPoseViewIndex : null;
        var outputs = new Dictionary<string, VisionValue>
        {
            ["calibrationJson"] = VisionValue.String(resultJson), ["rms"] = VisionValue.Double(rms),
            ["viewStats"] = VisionValue.String(JsonSerializer.Serialize(fullViewStats)),
            ["meanCoverageX"] = VisionValue.Double(viewStats.Average(v => v.coverageX)),
            ["meanCoverageY"] = VisionValue.Double(viewStats.Average(v => v.coverageY)),
            ["nativePolynomialDistortionActive"] = VisionValue.Boolean(nativePolynomialDistortionActive),
            ["fisheyeModelActive"] = VisionValue.Boolean(fisheyeModelActive),
            ["telecentricModelActive"] = VisionValue.Boolean(telecentricModelActive)
        };
        if (fullViewStats.Any(v => v.homographyRms is not null || v.poseReprojectionRms is not null)) outputs["worstViewRms"] = VisionValue.Double(worstViewRms);
        if (worstViewIndex is not null) outputs["worstViewIndex"] = VisionValue.Integer(worstViewIndex.Value);
        if (worstPoseViewRms > 0) outputs["worstPoseViewRms"] = VisionValue.Double(worstPoseViewRms);
        if (worstPoseViewIndex is not null) outputs["worstPoseViewIndex"] = VisionValue.Integer(worstPoseViewIndex.Value);
        if (outlierViewIndex is not null) outputs["outlierViewIndex"] = VisionValue.Integer(outlierViewIndex.Value);
        var diag = CoordinateValidation.Diagnostics(Type,
            ("views", request.Views.Count), ("rms", rms), ("cameraModel", cameraModel),
            ("worstViewRms", worstViewRms), ("worstPoseViewRms", worstPoseViewRms),
            ("medianPoseRms", medianPoseRms), ("outlierViewIndex", outlierViewIndex),
            ("meanCoverageX", viewStats.Average(v => v.coverageX)), ("meanCoverageY", viewStats.Average(v => v.coverageY)),
            ("nativePolynomialDistortionActive", nativePolynomialDistortionActive),
            ("fisheyeModelActive", fisheyeModelActive), ("telecentricModelActive", telecentricModelActive));
        outputs["diagnostics"] = VisionValue.String(diag);
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["diagnostics"] = diag, ["views"] = request.Views.Count, ["imageSize"] = $"{request.ImageWidth}x{request.ImageHeight}", ["rms"] = rms, ["cameraModel"] = cameraModel, ["telecentricMagnification"] = cameraModel == "telecentric" ? telecentricMagnification : null, ["telecentricPixelSizeUm"] = cameraModel == "telecentric" ? telecentricPixelSizeUm : null, ["maxViewRms"] = maxViewRms, ["maxPoseViewRms"] = maxPoseViewRms, ["maxRms"] = maxRms, ["worstViewRms"] = worstViewRms, ["worstPoseViewRms"] = worstPoseViewRms, ["worstPoseViewIndex"] = worstPoseViewIndex, ["medianPoseRms"] = medianPoseRms, ["outlierViewIndex"] = outlierViewIndex, ["nativePolynomialDistortionActive"] = nativePolynomialDistortionActive, ["fisheyeModelActive"] = fisheyeModelActive, ["telecentricModelActive"] = telecentricModelActive, ["poseRmsModel"] = telecentricModelActive ? "not available for telecentric projection model" : nativePolynomialDistortionActive ? "not available for native polynomial distortion" : fisheyeModelActive ? "OpenCV fisheye fitted model" : "pinhole", ["meanCoverageX"] = viewStats.Average(v => v.coverageX), ["meanCoverageY"] = viewStats.Average(v => v.coverageY), ["model"] = model.LensModel ?? model.NativeCameraType ?? "brown_conrady", ["algorithmBackend"] = fisheyeModelActive ? "opencv" : "jlvision", ["calibrationBackend"] = fisheyeModelActive ? "opencv" : "jlvision" }));
    }
}

public sealed class ImageUndistortNode : IVisionNodeExecutor
{
    public string Type => "image.undistort";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var intrinsicAssetId = node.GetString("intrinsicsAssetId", "").Trim();
        var intrinsicAssetVersion = node.GetInt("intrinsicsAssetVersion", 0);
        var fisheyeBalance = node.GetDouble("fisheyeBalance", 0);
        if (intrinsicAssetVersion < 0 || (intrinsicAssetId.Length > 0) != (intrinsicAssetVersion > 0))
            throw new InvalidOperationException("intrinsicsAssetId and a positive intrinsicsAssetVersion must be provided together, or both left unset.");
        if (!double.IsFinite(fisheyeBalance) || fisheyeBalance is < 0 or > 1)
            throw new InvalidOperationException("fisheyeBalance must be finite and between 0 and 1.");
        var source = context.RequireImage("image").Mat;
        CameraCalibrationModel model;
        var calibrationJson = context.TryGetValue("calibrationJson", out var connectedCalibration)
            ? connectedCalibration.Require<string>("calibrationJson")
            : node.GetString("calibrationJson", "{}");
        try { model = JsonSerializer.Deserialize<CameraCalibrationModel>(calibrationJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid camera calibration JSON: {ex.Message}", ex); }
        if (model.ImageWidth < 1 || model.ImageHeight < 1 || model.CameraMatrix is not { Length: 3 } || model.CameraMatrix.Any(r => r is not { Length: 3 } || r.Any(x => !double.IsFinite(x))) || model.DistCoeffs is null || model.DistCoeffs.Length is not (4 or 5 or 8 or 12 or 14) || model.DistCoeffs.Any(x => !double.IsFinite(x)) || model.CameraMatrix[0][0] <= 0 || model.CameraMatrix[1][1] <= 0 || Math.Abs(model.CameraMatrix[2][0]) > 1e-12 || Math.Abs(model.CameraMatrix[2][1]) > 1e-12 || Math.Abs(model.CameraMatrix[2][2]-1) > 1e-9 || !HasValidLensModel(model))
            throw new InvalidOperationException("Calibration JSON must contain a finite 3x3 cameraMatrix, positive focal lengths, and 4/5/8/12/14 finite distCoeffs.");
        if (fisheyeBalance != 0 && model.LensModel != "opencv_fisheye")
            throw new InvalidOperationException("fisheyeBalance is only available for OpenCV fisheye calibration models.");
        Mat output;
        try { output = JLVisionCalibration.Undistort(source, model, cancellationToken, fisheyeBalance); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"{(model.LensModel == "opencv_fisheye" ? "OpenCV fisheye" : "JLVision")} undistortion failed: {ex.Message}", ex); }
        JLVisionOperators.MinMax(output, out double undistMin, out double undistMax);
        var undistMean = JLVisionOperators.Mean(output);
        var undistDiag = CoordinateValidation.Diagnostics(Type, ("lensModel", model.LensModel ?? model.NativeCameraType ?? "brown_conrady"), ("fisheyeBalance", fisheyeBalance),
            ("width", output.Width), ("height", output.Height), ("channels", output.Channels()), ("meanIntensity", undistMean), ("minIntensity", undistMin), ("maxIntensity", undistMax));
        return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)), ["meanIntensity"] = VisionValue.Double(undistMean), ["minIntensity"] = VisionValue.Double(undistMin), ["maxIntensity"] = VisionValue.Double(undistMax), ["diagnostics"] = VisionValue.String(undistDiag) }, new Dictionary<string, object?> { ["lensModel"] = model.LensModel ?? model.NativeCameraType ?? "brown_conrady", ["fisheyeBalance"] = fisheyeBalance, ["sourceSize"] = $"{source.Width}x{source.Height}", ["calibrationSize"] = $"{model.ImageWidth}x{model.ImageHeight}", ["scaledIntrinsics"] = source.Width != model.ImageWidth || source.Height != model.ImageHeight, ["intrinsicsAssetId"] = intrinsicAssetId.Length == 0 ? null : intrinsicAssetId, ["intrinsicsAssetVersion"] = intrinsicAssetVersion == 0 ? null : intrinsicAssetVersion, ["calibrationBackend"] = model.LensModel == "opencv_fisheye" ? "opencv" : "jlvision", ["algorithmBackend"] = model.LensModel == "opencv_fisheye" ? "opencv" : "jlvision" }));
    }

    private static bool HasValidLensModel(CameraCalibrationModel model)
    {
        if (model.NativeCameraType is not null && (model.NativeCameraType is not ("area_scan_polynomial" or "area_scan_telecentric_polynomial") || model.NativeCameraParameters is not { Length: 12 })) return false;
        if (model.NativeCameraType is null && model.NativeCameraParameters is not null) return false;
        return model.LensModel switch
        {
            "opencv_fisheye" => model.DistCoeffs.Length == 4 && model.NativeCameraType is null && model.NativeCameraParameters is null,
            "area_scan_polynomial" => model.NativeCameraType == "area_scan_polynomial" && model.NativeCameraParameters is { Length: 12 },
            "area_scan_telecentric_polynomial" => model.NativeCameraType == "area_scan_telecentric_polynomial" && model.NativeCameraParameters is { Length: 12 },
            "brown_conrady" => model.NativeCameraType is null,
            null => true,
            _ => false
        };
    }
}

public sealed record HandEyeSample(VisionPose2D Robot, VisionPose2D Vision);
public sealed record HandEyeRequest(List<HandEyeSample> Samples);
public sealed record HandEyeCalibrationModel(
    string Mode, string SourceFrame, string TargetFrame, string Unit, string SourceUnit,
    double X, double Y, double ThetaDeg,
    double AngularWeight, double VisionScale, string RobustLoss);

/// <summary>Planar SE(2) hand-eye calibration only. Uses closure minimization; no 3D rigid transform is produced.</summary>
public sealed class PlanarHandEyeCalibrationNode : IVisionNodeExecutor
{
    public string Type => "calibration.handEye2D";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        var json = context.TryGetValue("samplesJson", out var connectedSamples)
            ? connectedSamples.Require<string>("samplesJson")
            : node.GetString("samplesJson", "{}");
        HandEyeRequest request;
        try { request = JsonSerializer.Deserialize<HandEyeRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty payload"); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid samplesJson: {ex.Message}", ex); }
        var samples = request.Samples;
        if (samples is null || samples.Count < 4) throw new InvalidOperationException("2D hand-eye calibration requires at least four robot/vision pose pairs.");
        if (samples.Count > 1000) throw new InvalidOperationException("2D hand-eye calibration accepts at most 1000 pose pairs.");
        if (samples.Any(s => s is null || !Finite(s.Robot) || !Finite(s.Vision))) throw new InvalidOperationException("Robot and vision poses must be finite, each sample must be non-null, and X/Y coordinates must be within +/-1e9.");
        // Reject exact duplicate robot poses — they add no observability and inflate false confidence.
        for (var i = 0; i < samples.Count; i++)
        for (var j = i + 1; j < samples.Count; j++)
        {
            if (Math.Abs(samples[i].Robot.X - samples[j].Robot.X) < 1e-9
                && Math.Abs(samples[i].Robot.Y - samples[j].Robot.Y) < 1e-9
                && Math.Abs(Normalize(samples[i].Robot.ThetaDeg) - Normalize(samples[j].Robot.ThetaDeg)) < 1e-6)
                throw new InvalidOperationException($"Hand-eye samples {i} and {j} share the same robot pose; teach distinct robot poses.");
        }
        var mode = node.GetString("mode", "eyeInHand").Trim();
        var visionScale = node.GetDouble("visionScale", 1);
        var robustLoss = node.GetString("robustLoss", "none").Trim().ToLowerInvariant();
        var angularWeight = node.GetDouble("angularWeight", 50);
        var maxTranslation = node.GetDouble("maxTranslationError", 0);
        var maxAngle = node.GetDouble("maxAngleErrorDeg", 0);
        var maxConditionNumber = node.GetDouble("maxConditionNumber", 0);
        // Ranges come from the shared rule set so the wizard API and this node cannot drift apart.
        if (HandEyeSettings.ValidatePlanar(mode, angularWeight, visionScale, robustLoss, maxTranslation, maxAngle, maxConditionNumber) is { } settingsError)
            throw new InvalidOperationException(settingsError);
        var handEyeAssetId = node.GetString("handEyeAssetId", "").Trim();
        var handEyeAssetVersion = node.GetInt("handEyeAssetVersion", 0);
        if (handEyeAssetVersion < 0 || (handEyeAssetId.Length > 0) != (handEyeAssetVersion > 0))
            throw new InvalidOperationException("handEyeAssetId and a positive handEyeAssetVersion must be provided together, or both left unset.");
        var work = visionScale == 1 ? samples : samples.Select(s => new HandEyeSample(s.Robot, new VisionPose2D(s.Vision.X * visionScale, s.Vision.Y * visionScale, s.Vision.ThetaDeg))).ToList();
        var robotRotationCoverage = AngularCoverage(work.Select(s => s.Robot.ThetaDeg));
        var visionRotationCoverage = AngularCoverage(work.Select(s => s.Vision.ThetaDeg));
        var robotTranslationSpan = TranslationSpan(work.Select(s => s.Robot));
        var visionTranslationSpan = TranslationSpan(work.Select(s => s.Vision));
        if (robotRotationCoverage < 8 || visionRotationCoverage < 8 || robotTranslationSpan < 1e-3 || visionTranslationSpan < 1e-3)
            throw new InvalidOperationException("Hand-eye poses are degenerate: robot and vision samples must each span at least 8 degrees of rotation and 0.001 coordinate units of translation.");
        var x = new double[3];
        // Damped finite-difference Gauss-Newton over the unknown SE(2) hand-eye transform.
        // Tukey mode reweights per-sample blocks each iteration (IRLS); weights stay frozen
        // while the Jacobian is probed so the step remains a genuine Gauss-Newton step.
        var damping = 1e-3;
        var sqrtWeights = new double[work.Count];
        Array.Fill(sqrtWeights, 1.0);
        var converged = false;
        var iterationsUsed = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterationsUsed = iteration + 1;
            var raw = Residuals(work, x, mode, angularWeight, robustLoss == "tukey" ? sqrtWeights : null);
            if (robustLoss == "tukey")
            {
                UpdateRobustWeights(raw, sqrtWeights);
                // Recompute with the updated weights so the base residual and the finite-difference
                // probes share the same weighted closure center.
                raw = Residuals(work, x, mode, angularWeight, sqrtWeights);
            }
            var residual = robustLoss == "tukey" ? Weighted(raw, sqrtWeights) : raw;
            var jac = new double[residual.Length, 3];
            for (var j = 0; j < 3; j++)
            {
                var perturbed = (double[])x.Clone(); perturbed[j] += j == 2 ? 1e-5 : 1e-4;
                var deltaRaw = Residuals(work, perturbed, mode, angularWeight, robustLoss == "tukey" ? sqrtWeights : null);
                var delta = robustLoss == "tukey" ? Weighted(deltaRaw, sqrtWeights) : deltaRaw;
                for (var i = 0; i < residual.Length; i++) jac[i,j] = (delta[i]-residual[i]) / (j == 2 ? 1e-5 : 1e-4);
            }
            var h = new double[3,3]; var g = new double[3];
            for (var i = 0; i < residual.Length; i++) for (var r = 0; r < 3; r++) { g[r] -= jac[i,r]*residual[i]; for (var c = 0; c < 3; c++) h[r,c] += jac[i,r]*jac[i,c]; }
            var trace = h[0,0]+h[1,1]+h[2,2];
            if (!double.IsFinite(trace) || trace < 1e-10) throw new InvalidOperationException("Hand-eye solve is degenerate; robot/vision motions do not constrain a unique 2D transform.");
            EnsureRank(h);
            var hd = (double[,])h.Clone(); for (var d=0;d<3;d++) hd[d,d] += damping * Math.Max(1, h[d,d]);
            double[] step;
            try { step = Solve(hd,g); } catch (InvalidOperationException) { throw new InvalidOperationException("Hand-eye solve is singular; collect more diverse robot and vision poses."); }
            var candidate = new[] { x[0]+step[0], x[1]+step[1], x[2]+step[2] };
            var candidateRaw = Residuals(work, candidate, mode, angularWeight, robustLoss == "tukey" ? sqrtWeights : null);
            var candidateResidual = robustLoss == "tukey" ? Weighted(candidateRaw, sqrtWeights) : candidateRaw;
            if (Norm(candidateResidual) < Norm(residual)) { x = candidate; damping = Math.Max(1e-9,damping*0.3); if (Math.Sqrt(step.Sum(v=>v*v)) < 1e-7) { converged = true; break; } }
            else damping = Math.Min(1e9,damping*10);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var observability = CheckObservability(work,x,mode,angularWeight);
        var errors = Residuals(work,x,mode,angularWeight); var translationRms = Math.Sqrt(Enumerable.Range(0,work.Count).Average(i=>errors[3*i]*errors[3*i]+errors[3*i+1]*errors[3*i+1])); var angleRms = Math.Sqrt(Enumerable.Range(0,work.Count).Average(i=>errors[3*i+2]*errors[3*i+2]))/angularWeight;
        if (x.Any(value => !double.IsFinite(value)) || !double.IsFinite(translationRms) || !double.IsFinite(angleRms) || translationRms > 1e6)
            throw new InvalidOperationException("Hand-eye solve did not converge to a finite solution.");
        // Parameter uncertainty from sigma^2 * (J^T J)^-1 plus the conditioning of J^T J,
        // so operators can judge how trustworthy the solved transform is.
        var conditionNumber = ScaleInvariantCondition(observability);
        if (maxConditionNumber > 0 && !(conditionNumber < maxConditionNumber))
            throw new InvalidOperationException($"Hand-eye observability is poor: scale-invariant condition number {conditionNumber:0.###e+0} is not below maxConditionNumber {maxConditionNumber:0.###e+0}; move the robot through a wider, better-conditioned pose set.");
        var stdDevX = 0d; var stdDevY = 0d; var angleStdDevDeg = 0d;
        var dof = errors.Length - 3;
        // Tukey runs reweight the residual vector; using the unweighted vector here would let the very
        // outliers the robust loss rejected inflate sigma^2 (and therefore every reported std dev).
        var sigmaErrors = errors;
        if (robustLoss == "tukey")
        {
            var finalWeights = new double[work.Count];
            UpdateRobustWeights(errors, finalWeights);
            sigmaErrors = Weighted(errors, finalWeights);
        }
        var sigma2 = dof > 0 ? sigmaErrors.Sum(v => v * v) / dof : 0;
        var det = observability[0,0]*(observability[1,1]*observability[2,2]-observability[1,2]*observability[2,1])
            - observability[0,1]*(observability[1,0]*observability[2,2]-observability[1,2]*observability[2,0])
            + observability[0,2]*(observability[1,0]*observability[2,1]-observability[1,1]*observability[2,0]);
        if (sigma2 > 0 && double.IsFinite(det) && Math.Abs(det) > 1e-30)
        {
            stdDevX = Math.Sqrt(Math.Max(0, (observability[1,1]*observability[2,2]-observability[1,2]*observability[2,1]) / det * sigma2));
            stdDevY = Math.Sqrt(Math.Max(0, (observability[0,0]*observability[2,2]-observability[0,2]*observability[2,0]) / det * sigma2));
            angleStdDevDeg = Math.Sqrt(Math.Max(0, (observability[0,0]*observability[1,1]-observability[0,1]*observability[1,0]) / det * sigma2)) * 180 / Math.PI;
        }
        // Per-sample residual report (translation hypot + angle degrees) so operators can
        // drop a bad pose pair the way mature hand-eye tools highlight worst samples.
        var sampleResiduals = Enumerable.Range(0, samples.Count).Select(i => new
        {
            index = i,
            translationError = Math.Sqrt(errors[3 * i] * errors[3 * i] + errors[3 * i + 1] * errors[3 * i + 1]),
            angleErrorDeg = Math.Abs(errors[3 * i + 2] / angularWeight) * 180 / Math.PI,
            robot = new { x = samples[i].Robot.X, y = samples[i].Robot.Y, thetaDeg = samples[i].Robot.ThetaDeg },
            vision = new { x = samples[i].Vision.X, y = samples[i].Vision.Y, thetaDeg = samples[i].Vision.ThetaDeg }
        }).ToArray();
        var maxTranslationError = sampleResiduals.Max(s => s.translationError);
        var maxAngleErrorDeg = sampleResiduals.Max(s => s.angleErrorDeg);
        var worstTranslationSampleIndex = sampleResiduals.OrderByDescending(s => s.translationError).Select(s => s.index).First();
        var worstAngleSampleIndex = sampleResiduals.OrderByDescending(s => s.angleErrorDeg).Select(s => s.index).First();
        var worstSampleIndex = sampleResiduals.OrderByDescending(s => Hypot(s.translationError, s.angleErrorDeg * Math.PI / 180 * angularWeight)).Select(s => s.index).First();
        // Outlier sample: combined residual more than 3x the median, above a span-relative
        // floor so numerically exact data is never flagged. Report-only, like outlierViewIndex.
        var combined = sampleResiduals.Select(s => Hypot(s.translationError, s.angleErrorDeg * Math.PI / 180 * angularWeight)).ToArray();
        var medianCombined = Median(combined);
        var maxCombined = combined.Max();
        var outlierFloor = 1e-6 * Math.Max(1, Math.Max(robotTranslationSpan, visionTranslationSpan));
        var outlierSampleIndex = medianCombined > 0 && maxCombined > 3 * medianCombined && maxCombined > outlierFloor ? worstSampleIndex : (int?)null;
        var crossCheck = AlgebraicCrossCheck(work, mode, x);
        if (maxTranslation > 0 && maxTranslationError > maxTranslation)
            throw new InvalidOperationException($"Hand-eye sample {worstTranslationSampleIndex} translation residual {maxTranslationError:0.####} exceeds maxTranslationError {maxTranslation:0.####}; remove or re-teach that pose pair.");
        if (maxAngle > 0 && maxAngleErrorDeg > maxAngle)
            throw new InvalidOperationException($"Hand-eye sample {worstAngleSampleIndex} angle residual {maxAngleErrorDeg:0.####} deg exceeds maxAngleErrorDeg {maxAngle:0.####}; remove or re-teach that pose pair.");
        var theta=Normalize(x[2]*180/Math.PI); var pose = new VisionPose2D(x[0],x[1],theta);
        var radians=theta*Math.PI/180; var sourceFrame=CoordinateValidation.RequireFrame(node.GetString("sourceFrame","Camera"),"sourceFrame");
        var defaultTargetFrame=mode.Equals("eyeInHand",StringComparison.OrdinalIgnoreCase)?"RobotTool":"RobotBase";
        var targetFrame=node.GetString("targetFrame",defaultTargetFrame); if(string.IsNullOrWhiteSpace(targetFrame))targetFrame=defaultTargetFrame;
        targetFrame=CoordinateValidation.RequireFrame(targetFrame,"targetFrame");
        var unit=CoordinateValidation.RequireUnit(node.GetString("unit","mm"),"unit");
        var sourceUnit=CoordinateValidation.RequireUnit(node.GetString("sourceUnit","mm"),"sourceUnit");
        var unitWarning = !unit.Equals(sourceUnit, StringComparison.OrdinalIgnoreCase) && visionScale == 1
            ? $"sourceUnit '{sourceUnit}' differs from unit '{unit}' but visionScale is 1, so vision coordinates are used as-is; set visionScale (target units per vision source unit) or pre-scale the samples."
            : null;
        var transform=new VisionTransform2D(sourceFrame,targetFrame,unit,Math.Cos(radians),-Math.Sin(radians),x[0],Math.Sin(radians),Math.Cos(radians),x[1],0,0,1,sourceUnit);
        var canonicalMode = mode.Equals("eyeInHand", StringComparison.OrdinalIgnoreCase) ? "eyeInHand" : "eyeToHand";
        var calibrationModel = new HandEyeCalibrationModel(canonicalMode, sourceFrame, targetFrame, unit, sourceUnit, x[0], x[1], theta, angularWeight, visionScale, robustLoss);
        var outputs = new Dictionary<string, VisionValue>
        {
            ["pose"] = VisionValue.Pose(pose), ["transform"] = VisionValue.Transform(transform),
            ["calibrationJson"] = VisionValue.String(JsonSerializer.Serialize(calibrationModel)),
            ["translationRms"] = VisionValue.Double(translationRms), ["angleRmsDeg"] = VisionValue.Double(angleRms*180/Math.PI),
            ["maxTranslationError"] = VisionValue.Double(maxTranslationError), ["maxAngleErrorDeg"] = VisionValue.Double(maxAngleErrorDeg),
            ["worstSampleIndex"] = VisionValue.Integer(worstSampleIndex),
            ["worstTranslationSampleIndex"] = VisionValue.Integer(worstTranslationSampleIndex),
            ["worstAngleSampleIndex"] = VisionValue.Integer(worstAngleSampleIndex),
            ["robotRotationCoverageDeg"] = VisionValue.Double(robotRotationCoverage),
            ["visionRotationCoverageDeg"] = VisionValue.Double(visionRotationCoverage),
            ["robotTranslationSpan"] = VisionValue.Double(robotTranslationSpan),
            ["visionTranslationSpan"] = VisionValue.Double(visionTranslationSpan),
            ["sampleResidualsJson"] = VisionValue.String(JsonSerializer.Serialize(sampleResiduals)),
            ["converged"] = VisionValue.Boolean(converged),
            ["iterations"] = VisionValue.Integer(iterationsUsed),
            ["stdDevX"] = VisionValue.Double(stdDevX),
            ["stdDevY"] = VisionValue.Double(stdDevY),
            ["angleStdDevDeg"] = VisionValue.Double(angleStdDevDeg),
            ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(Type,
                ("mode", mode), ("samples", samples.Count), ("translationRms", translationRms),
                ("angleRmsDeg", angleRms * 180 / Math.PI), ("maxTranslationError", maxTranslationError),
                ("maxAngleErrorDeg", maxAngleErrorDeg), ("worstSampleIndex", worstSampleIndex),
                ("outlierSampleIndex", outlierSampleIndex),
                ("robotRotationCoverageDeg", robotRotationCoverage), ("visionRotationCoverageDeg", visionRotationCoverage),
                ("robustLoss", robustLoss), ("visionScale", visionScale),
                ("converged", converged), ("iterations", iterationsUsed),
                ("conditionNumber", double.IsFinite(conditionNumber) ? conditionNumber : null),
                ("stdDevX", stdDevX), ("stdDevY", stdDevY), ("angleStdDevDeg", angleStdDevDeg),
                ("crossCheckTranslationDelta", crossCheck?.TranslationDelta), ("crossCheckAngleDeltaDeg", crossCheck?.AngleDeltaDeg),
                ("unitWarning", unitWarning),
                ("sourceFrame", sourceFrame), ("targetFrame", targetFrame), ("unit", unit)))
        };
        if (double.IsFinite(conditionNumber)) outputs["conditionNumber"] = VisionValue.Double(conditionNumber);
        if (outlierSampleIndex is not null) outputs["outlierSampleIndex"] = VisionValue.Integer(outlierSampleIndex.Value);
        if (crossCheck is not null)
        {
            outputs["crossCheckTranslationDelta"] = VisionValue.Double(crossCheck.Value.TranslationDelta);
            outputs["crossCheckAngleDeltaDeg"] = VisionValue.Double(crossCheck.Value.AngleDeltaDeg);
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["mode"] = mode, ["samples"] = samples.Count, ["transform2D"] = "SE(2)", ["sourceFrame"] = sourceFrame, ["targetFrame"] = targetFrame, ["unit"] = unit,
            ["handEyeAssetId"] = handEyeAssetId.Length == 0 ? null : handEyeAssetId,
            ["handEyeAssetVersion"] = handEyeAssetVersion == 0 ? null : handEyeAssetVersion,
            ["translationRms"] = translationRms, ["angleRmsDeg"] = angleRms*180/Math.PI,
            ["calibrationBackend"] = "managed", ["algorithmBackend"] = "managed",
            ["maxTranslationError"] = maxTranslationError, ["maxAngleErrorDeg"] = maxAngleErrorDeg, ["worstSampleIndex"] = worstSampleIndex,
            ["worstTranslationSampleIndex"] = worstTranslationSampleIndex, ["worstAngleSampleIndex"] = worstAngleSampleIndex,
            ["outlierSampleIndex"] = outlierSampleIndex,
            ["robotRotationCoverageDeg"] = robotRotationCoverage, ["visionRotationCoverageDeg"] = visionRotationCoverage,
            ["robotTranslationSpan"] = robotTranslationSpan, ["visionTranslationSpan"] = visionTranslationSpan, ["angularWeight"] = angularWeight,
            ["maxConditionNumber"] = maxConditionNumber,
            ["robustLoss"] = robustLoss, ["visionScale"] = visionScale,
            ["converged"] = converged, ["iterations"] = iterationsUsed,
            ["conditionNumber"] = double.IsFinite(conditionNumber) ? conditionNumber : null,
            ["stdDevX"] = stdDevX, ["stdDevY"] = stdDevY, ["angleStdDevDeg"] = angleStdDevDeg,
            ["crossCheckTranslationDelta"] = crossCheck?.TranslationDelta, ["crossCheckAngleDeltaDeg"] = crossCheck?.AngleDeltaDeg,
            ["unitWarning"] = unitWarning,
            // A differing sourceUnit with visionScale=1 is a silent 1000x-class error; surface it as a warning
            // (not a failure) so failOnWarning can gate it per workflow.
            ["warningCount"] = unitWarning is null ? 0 : 1,
            ["scope"] = "planar only; no 3D hand-eye"
        }));
    }
    private static bool Finite(VisionPose2D p) => double.IsFinite(p.X)&&double.IsFinite(p.Y)&&Math.Abs(p.X)<=1e9&&Math.Abs(p.Y)<=1e9&&double.IsFinite(p.ThetaDeg);
    private static double[] Residuals(List<HandEyeSample> s, double[] x, string mode, double angularWeight, double[]? sqrtWeights = null)
    {
        var result = new double[s.Count*3]; var closures = s.Select(v => mode.Equals("eyeInHand",StringComparison.OrdinalIgnoreCase) ? Mul(Mul(v.Robot,x),v.Vision) : Mul(Mul(Inv(v.Robot),x),v.Vision)).ToArray();
        double meanX; double meanY; double meanAngle;
        if (sqrtWeights is null)
        {
            meanAngle = Math.Atan2(closures.Average(v=>Math.Sin(v.Theta)),closures.Average(v=>Math.Cos(v.Theta)));
            meanX = closures.Average(v => v.X); meanY = closures.Average(v => v.Y);
        }
        else
        {
            // Weighted closure center: a rejected pair must not drag the estimated target pose.
            double wsum = 0, sx = 0, sy = 0, ssin = 0, scos = 0;
            for (var i = 0; i < closures.Length; i++)
            {
                var w = sqrtWeights[i] * sqrtWeights[i];
                wsum += w; sx += w * closures[i].X; sy += w * closures[i].Y;
                ssin += w * Math.Sin(closures[i].Theta); scos += w * Math.Cos(closures[i].Theta);
            }
            if (wsum < 1e-12)
            {
                meanAngle = Math.Atan2(closures.Average(v=>Math.Sin(v.Theta)),closures.Average(v=>Math.Cos(v.Theta)));
                meanX = closures.Average(v => v.X); meanY = closures.Average(v => v.Y);
            }
            else { meanX = sx / wsum; meanY = sy / wsum; meanAngle = Math.Atan2(ssin / wsum, scos / wsum); }
        }
        // Angular closure residual is measured against the circular mean of the closures. Because every
        // closure angle shifts by exactly X.theta, the mean shifts with it: the whole angular block is
        // invariant in X and has a zero Jacobian column. angularWeight therefore only sets the unit of
        // the reported angle residuals (and of the gates / combined worst-sample ranking); it cannot
        // bias the solved transform. Do not "fix" this by dropping the mean - the mean-centring is what
        // makes the closure formulation identifiable.
        for(var i=0;i<s.Count;i++){result[i*3]=closures[i].X-meanX;result[i*3+1]=closures[i].Y-meanY;result[i*3+2]=Wrap(closures[i].Theta-meanAngle)*angularWeight;}
        return result;
    }
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,(double X,double Y,double Theta) b){var c=Math.Cos(a.Theta);var s=Math.Sin(a.Theta);return(a.X+c*b.X-s*b.Y,a.Y+s*b.X+c*b.Y,a.Theta+b.Theta);}
    private static (double X,double Y,double Theta) Inv(VisionPose2D p)=>Inv(ToTuple(p));
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,VisionPose2D b)=>Mul(a,(b.X,b.Y,b.ThetaDeg*Math.PI/180));
    private static (double X,double Y,double Theta) Mul(VisionPose2D a,double[] b)=>Mul((a.X,a.Y,a.ThetaDeg*Math.PI/180),(b[0],b[1],b[2]));
    private static (double X,double Y,double Theta) Mul((double X,double Y,double Theta) a,double[] b)=>Mul(a,(b[0],b[1],b[2]));
    private static double Wrap(double x)=>Math.Atan2(Math.Sin(x),Math.Cos(x));
    private static double Normalize(double deg){var r=deg%360;if(r>180)r-=360;if(r<=-180)r+=360;return r;}
    private static double Norm(double[] v)=>Math.Sqrt(v.Sum(x=>x*x));
    private static double[,] CheckObservability(List<HandEyeSample> samples,double[] x,string mode,double angularWeight)
    {
        var residual=Residuals(samples,x,mode,angularWeight);var jac=new double[residual.Length,3];
        for(var j=0;j<3;j++){var p=(double[])x.Clone();var step=j==2?1e-5:1e-4;p[j]+=step;var shifted=Residuals(samples,p,mode,angularWeight);for(var i=0;i<residual.Length;i++)jac[i,j]=(shifted[i]-residual[i])/step;}
        var h=new double[3,3];for(var i=0;i<residual.Length;i++)for(var r=0;r<3;r++)for(var c=0;c<3;c++)h[r,c]+=jac[i,r]*jac[i,c];EnsureRank(h);
        return h;
    }
    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone(); Array.Sort(sorted);
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
    private static void UpdateRobustWeights(double[] residual, double[] sqrtWeights)
    {
        // Tukey biweight with k = 3x median block magnitude (~4.685 sigma for 3-DOF Gaussian
        // residuals). Unlike MAD-scaled Huber, the median scale does not collapse when one gross
        // outlier inflates every inlier residual, so bad pairs are fully rejected (weight 0).
        var n = sqrtWeights.Length;
        var magnitude = new double[n];
        for (var i = 0; i < n; i++) magnitude[i] = Math.Sqrt(residual[3*i]*residual[3*i] + residual[3*i+1]*residual[3*i+1] + residual[3*i+2]*residual[3*i+2]);
        var cutoff = Math.Max(3 * Median(magnitude), 1e-12);
        for (var i = 0; i < n; i++)
        {
            var u = magnitude[i] / cutoff;
            sqrtWeights[i] = u >= 1 ? 0 : 1 - u * u;
        }
    }
    private static double[] Weighted(double[] residual, double[] sqrtWeights)
    {
        var result = new double[residual.Length];
        for (var i = 0; i < sqrtWeights.Length; i++)
        {
            result[3*i] = residual[3*i] * sqrtWeights[i];
            result[3*i+1] = residual[3*i+1] * sqrtWeights[i];
            result[3*i+2] = residual[3*i+2] * sqrtWeights[i];
        }
        return result;
    }
    /// <summary>Condition number of the column-normalised Jacobian (i.e. sqrt of cond(J^T J) after
    /// removing the per-column unit scale). cond(J^T J) is the squared conditioning of J and is also
    /// unit-dependent, so reporting it raw would overstate badness by an arbitrary factor.</summary>
    private static double ScaleInvariantCondition(double[,] h)
    {
        var s0 = Math.Sqrt(Math.Max(0, h[0, 0])); var s1 = Math.Sqrt(Math.Max(0, h[1, 1])); var s2 = Math.Sqrt(Math.Max(0, h[2, 2]));
        if (s0 <= 0 || s1 <= 0 || s2 <= 0) return double.PositiveInfinity;
        var scaled = new double[3, 3];
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
            scaled[r, c] = h[r, c] / (r switch { 0 => s0, 1 => s1, _ => s2 }) / (c switch { 0 => s0, 1 => s1, _ => s2 });
        return Math.Sqrt(ConditionNumber(scaled));
    }
    private static double ConditionNumber(double[,] h)
    {
        double a = h[0,0], b = h[1,1], c = h[2,2], d = h[0,1], e = h[0,2], f = h[1,2];
        var offDiagonal = d*d + e*e + f*f;
        if (offDiagonal < 1e-30)
        {
            var lo = Math.Min(a, Math.Min(b, c)); var hi = Math.Max(a, Math.Max(b, c));
            return lo <= 0 ? double.PositiveInfinity : hi / lo;
        }
        var q = (a + b + c) / 3;
        var p = Math.Sqrt(((a-q)*(a-q) + (b-q)*(b-q) + (c-q)*(c-q) + 2*offDiagonal) / 6);
        var b00 = (a-q)/p; var b11 = (b-q)/p; var b22 = (c-q)/p; var b01 = d/p; var b02 = e/p; var b12 = f/p;
        var detB = b00*(b11*b22 - b12*b12) - b01*(b01*b22 - b12*b02) + b02*(b01*b12 - b11*b02);
        var phi = Math.Acos(Math.Clamp(detB / 2, -1, 1)) / 3;
        var eig1 = q + 2*p*Math.Cos(phi);
        var eig3 = q + 2*p*Math.Cos(phi + 2*Math.PI/3);
        var eig2 = 3*q - eig1 - eig3;
        var max = Math.Max(eig1, Math.Max(eig2, eig3));
        var min = Math.Min(eig1, Math.Min(eig2, eig3));
        return min <= 1e-30 ? double.PositiveInfinity : max / min;
    }
    private static (double X, double Y, double Theta) ToTuple(VisionPose2D p) => (p.X, p.Y, p.ThetaDeg * Math.PI / 180);
    private static (double X, double Y, double Theta) Inv((double X, double Y, double Theta) p)
        => (-Math.Cos(p.Theta)*p.X - Math.Sin(p.Theta)*p.Y, Math.Sin(p.Theta)*p.X - Math.Cos(p.Theta)*p.Y, -p.Theta);
    /// <summary>Independent algebraic cross-check via relative motions A·X = X·B: 1-D search over
    /// the hand-eye rotation with a linear translation solve per motion pair. Report-only.</summary>
    private static (double TranslationDelta, double AngleDeltaDeg)? AlgebraicCrossCheck(List<HandEyeSample> s, string mode, double[] x)
    {
        var eyeInHand = mode.Equals("eyeInHand", StringComparison.OrdinalIgnoreCase);
        var usable = new List<(double ThetaA, double Tx, double Ty, double Bx, double By)>();
        void AddPair(int i, int j)
        {
            var ri = ToTuple(s[i].Robot); var rj = ToTuple(s[j].Robot);
            var a = eyeInHand ? Mul(Inv(rj), ri) : Mul(rj, Inv(ri));
            var b = Mul(ToTuple(s[j].Vision), Inv(ToTuple(s[i].Vision)));
            if (Math.Abs(Wrap(a.Theta)) > 0.5 * Math.PI / 180) usable.Add((a.Theta, a.X, a.Y, b.X, b.Y));
        }
        for (var i = 0; i + 1 < s.Count && usable.Count < 128; i++) AddPair(i, i + 1);
        if (s.Count > 2 && usable.Count < 128) AddPair(0, s.Count - 1);
        if (usable.Count < 2) return null;
        double Spread(double theta, out double meanX, out double meanY)
        {
            var c = Math.Cos(theta); var sn = Math.Sin(theta);
            double sx = 0, sy = 0, sxx = 0, syy = 0;
            foreach (var p in usable)
            {
                var ca = Math.Cos(p.ThetaA); var sa = Math.Sin(p.ThetaA);
                var det = (ca - 1) * (ca - 1) + sa * sa;
                var rx = c * p.Bx - sn * p.By - p.Tx;
                var ry = sn * p.Bx + c * p.By - p.Ty;
                var ex = ((ca - 1) * rx + sa * ry) / det;
                var ey = (-sa * rx + (ca - 1) * ry) / det;
                sx += ex; sy += ey; sxx += ex * ex; syy += ey * ey;
            }
            var n = usable.Count;
            meanX = sx / n; meanY = sy / n;
            return Math.Max(0, sxx / n - meanX * meanX) + Math.Max(0, syy / n - meanY * meanY);
        }
        var bestTheta = 0d; var bestSpread = double.PositiveInfinity;
        for (var k = 0; k < 720; k++)
        {
            var theta = -Math.PI + k * (2 * Math.PI / 720);
            var spread = Spread(theta, out _, out _);
            if (spread < bestSpread) { bestSpread = spread; bestTheta = theta; }
        }
        var lo = bestTheta - Math.PI / 360; var hi = bestTheta + Math.PI / 360;
        for (var k = 0; k < 60; k++)
        {
            var m1 = lo + (hi - lo) / 3; var m2 = hi - (hi - lo) / 3;
            if (Spread(m1, out _, out _) < Spread(m2, out _, out _)) hi = m2; else lo = m1;
        }
        Spread((lo + hi) / 2, out var crossX, out var crossY);
        return (Hypot(crossX - x[0], crossY - x[1]), Math.Abs(Wrap((lo + hi) / 2 - x[2])) * 180 / Math.PI);
    }
    private static double AngularCoverage(IEnumerable<double> anglesDeg)
    {
        var sorted = anglesDeg.Select(value => { var angle = value % 360d; return angle < 0 ? angle + 360d : angle; }).OrderBy(value => value).ToArray();
        if (sorted.Length < 2) return 0;
        var largestGap = sorted[0] + 360d - sorted[^1];
        for (var i = 1; i < sorted.Length; i++) largestGap = Math.Max(largestGap, sorted[i] - sorted[i - 1]);
        return 360d - largestGap;
    }
    private static double TranslationSpan(IEnumerable<VisionPose2D> poses)
    {
        var points = poses.ToArray();
        return Math.Sqrt(Math.Pow(points.Max(p => p.X) - points.Min(p => p.X), 2) + Math.Pow(points.Max(p => p.Y) - points.Min(p => p.Y), 2));
    }
    private static double Hypot(double x, double y) { var scale = Math.Max(Math.Abs(x), Math.Abs(y)); return scale == 0 ? 0 : scale * Math.Sqrt(Math.Pow(x / scale, 2) + Math.Pow(y / scale, 2)); }
    private static void EnsureRank(double[,] h){var a=(double[,])h.Clone();var n=3;var scale=Math.Max(a[0,0],Math.Max(a[1,1],a[2,2]));if(scale<=0)throw new InvalidOperationException("Hand-eye solve is rank deficient.");for(var k=0;k<n;k++){var p=k;for(var r=k+1;r<n;r++)if(Math.Abs(a[r,k])>Math.Abs(a[p,k]))p=r;if(Math.Abs(a[p,k])<scale*1e-10)throw new InvalidOperationException("Hand-eye solve is non-unique: the 2D robot/vision motion does not sufficiently constrain translation and rotation.");for(var c=k;c<n;c++)(a[k,c],a[p,c])=(a[p,c],a[k,c]);var d=a[k,k];for(var c=k;c<n;c++)a[k,c]/=d;for(var r=k+1;r<n;r++){var f=a[r,k];for(var c=k;c<n;c++)a[r,c]-=f*a[k,c];}}}
    private static double[] Solve(double[,] a,double[] b){var n=b.Length;for(var k=0;k<n;k++){var p=k;for(var r=k+1;r<n;r++)if(Math.Abs(a[r,k])>Math.Abs(a[p,k]))p=r;if(Math.Abs(a[p,k])<1e-14)throw new InvalidOperationException();for(var c=k;c<n;c++)(a[k,c],a[p,c])=(a[p,c],a[k,c]);(b[k],b[p])=(b[p],b[k]);var d=a[k,k];for(var c=k;c<n;c++)a[k,c]/=d;b[k]/=d;for(var r=0;r<n;r++)if(r!=k){var f=a[r,k];for(var c=k;c<n;c++)a[r,c]-=f*a[k,c];b[r]-=f*b[k];}}return b;}
}
