using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

public sealed partial class CalibrationWizardService
{
    private readonly IntrinsicsWizardSessionRepository _intrinsicsSessions;
    private readonly CalibrationDetectBoardNode _detectBoard;
    private readonly CameraIntrinsicCalibrationNode _intrinsics;
    private readonly PlanarHandEyeCalibrationNode _handEye;
    private readonly HandEye3DCalibrationNode _handEye3D;
    private readonly DualCameraExtrinsicsNode _dualCamera;
    private readonly CameraManager _cameras;
    private readonly CameraIntrinsicAssetStore _intrinsicAssets;
    private readonly HandEyeCalibrationAssetStore? _handEyeAssets;
    private readonly HandEye3DCalibrationAssetStore? _handEye3DAssets;
    private readonly WizardSessionStore<HandEyeWizardSessionDocument> _handEyeSessions;
    private readonly WizardSessionStore<HandEye3DWizardSessionDocument> _handEye3DSessions;
    private static readonly JsonSerializerOptions PersistJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [ActivatorUtilitiesConstructor]
    public CalibrationWizardService(
        CalibrationWizardSolvers solvers,
        CameraManager cameras,
        CalibrationWizardAssetStores stores,
        IWebHostEnvironment environment,
        ILogger<CalibrationWizardService>? logger = null,
        IConfiguration? configuration = null)
    {
        _detectBoard = solvers.DetectBoard;
        _intrinsics = solvers.Intrinsics;
        _handEye = solvers.HandEye;
        _handEye3D = solvers.HandEye3D;
        _dualCamera = solvers.DualCamera;
        _cameras = cameras;
        _intrinsicAssets = stores.Intrinsics;
        _handEyeAssets = stores.HandEye;
        _handEye3DAssets = stores.HandEye3D;
        var persistRoot = Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath, configuration), "calibration-wizard");
        _intrinsicsSessions = new IntrinsicsWizardSessionRepository(persistRoot, JsonOpts(), PersistJson, logger);
        _handEyeSessions = new WizardSessionStore<HandEyeWizardSessionDocument>(
            Path.Combine(persistRoot, "handeye"), "Hand-eye wizard", HandEyeWizardSessionDocument.CurrentVersion,
            document => document.SessionId, document => document.FormatVersion, document => document.UpdatedAt,
            (document, id) => document with { SessionId = id }, JsonOpts(), PersistJson, Warn(logger));
        _handEye3DSessions = new WizardSessionStore<HandEye3DWizardSessionDocument>(
            Path.Combine(persistRoot, "handeye3d"), "3D hand-eye wizard", HandEye3DWizardSessionDocument.CurrentVersion,
            document => document.SessionId, document => document.FormatVersion, document => document.UpdatedAt,
            (document, id) => document with { SessionId = id }, JsonOpts(), PersistJson, Warn(logger));
    }

    private static Action<string>? Warn(ILogger<CalibrationWizardService>? logger)
        => logger is null ? null : text => logger.LogWarning("{WizardSessionIssue}", text);

    public CalibrationIntrinsicsWizard.SessionState Create(CalibrationWizardCreateRequest request)
    {
        var board = new CalibrationIntrinsicsWizard.BoardConfig(
            request.Pattern, request.Columns, request.Rows, request.SpacingMm,
            request.MinBlurVariance, request.MinCoverage, request.MaxGridError, request.SaddleThreshold);
        var session = _intrinsicsSessions.Create(board);
        var state = request.ImageWidth is int w && request.ImageHeight is int h
            ? session.SetImageSize(w, h)
            : session.Snapshot();
        _intrinsicsSessions.Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState Get(string sessionId)
        => _intrinsicsSessions.Resolve(sessionId).Snapshot();

    public bool Delete(string sessionId)
        => _intrinsicsSessions.Delete(sessionId);

    public CalibrationIntrinsicsWizard.SessionState AddViewJson(string sessionId, CalibrationWizardAddViewRequest request)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        if (!string.IsNullOrWhiteSpace(request.ViewJson))
        {
            CameraCalibrationView view;
            try { view = JsonSerializer.Deserialize<CameraCalibrationView>(request.ViewJson, JsonOpts()) ?? throw new JsonException("empty"); }
            catch (JsonException ex) { throw new ApiValidationException($"Invalid viewJson: {ex.Message}", ex); }
            var blur = request.BlurVariance ?? 0;
            var snap = session.Snapshot();
            var quality = CalibrationViewQuality.Evaluate(
                view.ImagePoints,
                snap.ImageWidth ?? EstimateWidth(view),
                snap.ImageHeight ?? EstimateHeight(view),
                snap.Board.Rows * snap.Board.Columns,
                blur,
                snap.Board.MinBlurVariance,
                snap.Board.MinCoverage);
            var state = session.AddView(view, quality, request.Label, request.Enable);
            _intrinsicsSessions.Persist(session);
            return state;
        }

        if (!string.IsNullOrWhiteSpace(request.CalibrationDataJson))
        {
            CameraCalibrationRequest data;
            try { data = JsonSerializer.Deserialize<CameraCalibrationRequest>(request.CalibrationDataJson, JsonOpts()) ?? throw new JsonException("empty"); }
            catch (JsonException ex) { throw new ApiValidationException($"Invalid calibrationDataJson: {ex.Message}", ex); }
            if (data.Views is null || data.Views.Count == 0) throw new ApiValidationException("calibrationDataJson contains no views.");
            session.SetImageSize(data.ImageWidth, data.ImageHeight);
            CalibrationIntrinsicsWizard.SessionState? last = null;
            var boardCfg = session.Board;
            foreach (var (view, i) in data.Views.Select((v, i) => (v, i)))
            {
                var quality = CalibrationViewQuality.Evaluate(
                    view.ImagePoints, data.ImageWidth, data.ImageHeight,
                    boardCfg.Rows * boardCfg.Columns,
                    request.BlurVariance ?? 120,
                    boardCfg.MinBlurVariance, boardCfg.MinCoverage);
                last = session.AddView(view, quality, request.Label is null ? $"import-{i}" : $"{request.Label}-{i}", request.Enable);
            }
            _intrinsicsSessions.Persist(session);
            return last!;
        }

        throw new ApiValidationException("Provide viewJson or calibrationDataJson.");
    }

    public async Task<CalibrationIntrinsicsWizard.SessionState> AddViewFromImageAsync(
        string sessionId, byte[] imageBytes, string? label, bool? enable, CancellationToken ct)
    {
        if (imageBytes.Length is < 32 or > 40_000_000) throw new ApiValidationException("Image payload must be between 32 bytes and 40 MB.");
        using var decoded = Cv2.ImDecode(imageBytes, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new ApiValidationException("Could not decode the uploaded image.");
        var session = _intrinsicsSessions.Resolve(sessionId);
        session.SetImageSize(decoded.Width, decoded.Height);

        var detect = await DetectBoardAsync(session, decoded, ct);
        if (!detect.Found || detect.View is null)
            throw new ApiValidationException($"Board not detected ({detect.Quality.Reason}). {detect.Quality.Guidance}");

        var state = session.AddView(detect.View, detect.Quality, label, enable);
        _intrinsicsSessions.Persist(session);
        return state;
    }

    public async Task<object> PreviewFromCameraAsync(string sessionId, CalibrationWizardPreviewRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CameraId))
            throw new ApiValidationException("cameraId is required.");
        var session = _intrinsicsSessions.Resolve(sessionId);
        using var lease = await _cameras.AcquireAsync(
            request.CameraId.Trim(),
            CameraFrameMode.Latest,
            TimeSpan.FromSeconds(3),
            autoStart: true,
            triggerBeforeGrab: false,
            cancellationToken: ct);
        using var mat = lease.Image.Clone();
        session.SetImageSize(mat.Width, mat.Height);
        var detect = await DetectBoardAsync(session, mat, ct);

        using var overlay = mat.Channels() == 1
            ? new Mat()
            : mat.Clone();
        if (mat.Channels() == 1) Cv2.CvtColor(mat, overlay, ColorConversionCodes.GRAY2BGR);
        if (detect.View?.ImagePoints is { Count: > 0 } pts)
        {
            for (var i = 0; i < pts.Count; i++)
            {
                var p = new Point((int)Math.Round(pts[i][0]), (int)Math.Round(pts[i][1]));
                Cv2.Circle(overlay, p, 4, new Scalar(0, 220, 0), -1, LineTypes.AntiAlias);
                if (i % Math.Max(1, pts.Count / 12) == 0)
                    Cv2.PutText(overlay, i.ToString(), p + new Point(5, -5), HersheyFonts.HersheySimplex, 0.4, new Scalar(40, 180, 255), 1, LineTypes.AntiAlias);
            }
            var color = detect.Quality.Verdict switch
            {
                CalibrationViewQuality.Verdict.Pass => new Scalar(40, 200, 40),
                CalibrationViewQuality.Verdict.Warn => new Scalar(0, 180, 255),
                _ => new Scalar(40, 40, 220)
            };
            Cv2.PutText(overlay, $"{detect.Quality.Verdict}: {detect.Quality.Reason}", new Point(12, 28),
                HersheyFonts.HersheySimplex, 0.8, color, 2, LineTypes.AntiAlias);
        }
        else
        {
            Cv2.PutText(overlay, $"Fail: {detect.Quality.Reason}", new Point(12, 28),
                HersheyFonts.HersheySimplex, 0.8, new Scalar(40, 40, 220), 2, LineTypes.AntiAlias);
        }

        Cv2.ImEncode(".jpg", overlay, out var jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)]);
        CalibrationIntrinsicsWizard.SessionState? added = null;
        if (request.AddIfAccepted && detect.Found && detect.View is not null
            && detect.Quality.Verdict != CalibrationViewQuality.Verdict.Fail)
        {
            added = session.AddView(detect.View, detect.Quality, request.Label ?? $"camera-{request.CameraId}", enable: true);
            _intrinsicsSessions.Persist(session);
        }

        var hints = PoseHints(session);
        return new
        {
            found = detect.Found,
            quality = detect.Quality,
            guidance = detect.Quality.Guidance,
            imageWidth = mat.Width,
            imageHeight = mat.Height,
            previewJpegBase64 = Convert.ToBase64String(jpeg),
            viewJson = detect.View is null ? null : JsonSerializer.Serialize(detect.View),
            session = added ?? session.Snapshot(),
            hints
        };
    }

    public CalibrationIntrinsicsWizard.SessionState SetEnabled(string sessionId, int index, bool enabled)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        var state = session.SetEnabled(index, enabled);
        _intrinsicsSessions.Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState RemoveView(string sessionId, int index)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        var state = session.RemoveView(index);
        _intrinsicsSessions.Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState DisableOutliers(string sessionId, CalibrationWizardDisableOutliersRequest request)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        var snap = session.Snapshot();
        var indexes = new HashSet<int>();
        if (request.ViewIndexes is { Length: > 0 })
        {
            foreach (var i in request.ViewIndexes) indexes.Add(i);
        }
        else if (request.UseSolveOutlier && snap.LastSolveSummary is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(snap.LastSolveSummary);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("nodeSummary", out var ns)
                    && ns.ValueKind == JsonValueKind.Object
                    && ns.TryGetProperty("outlierViewIndex", out var oi)
                    && oi.ValueKind is JsonValueKind.Number)
                {
                    indexes.Add(oi.GetInt32());
                }
                if (doc.RootElement.TryGetProperty("worstViewIndex", out var wi)
                    && wi.ValueKind is JsonValueKind.Number)
                {
                    indexes.Add(wi.GetInt32());
                }
            }
            catch (JsonException)
            {
                // ignore malformed summary
            }
        }

        if (indexes.Count == 0)
            throw new ApiValidationException("No outlier view indexes to disable. Solve first or pass viewIndexes.");

        // Map solve view order (enabled-only) onto session view indexes when needed.
        var enabledOrder = snap.Views.Where(v => v.Enabled).Select(v => v.Index).ToList();
        foreach (var idx in indexes.ToArray())
        {
            if (snap.Views.Any(v => v.Index == idx)) continue;
            if (idx >= 0 && idx < enabledOrder.Count)
                indexes.Add(enabledOrder[idx]);
        }

        CalibrationIntrinsicsWizard.SessionState? last = null;
        foreach (var idx in indexes)
        {
            if (snap.Views.Any(v => v.Index == idx))
                last = session.SetEnabled(idx, false);
        }
        _intrinsicsSessions.Persist(session);
        return last ?? session.Snapshot();
    }

    public async Task<object> SolveAsync(string sessionId, CalibrationWizardSolveRequest request, CancellationToken ct)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        session.SetCameraModel(request.CameraModel);
        session.SetTelecentricParameters(request.TelecentricMagnification, request.TelecentricPixelSizeUm);
        _intrinsicsSessions.Persist(session);
        var calibRequest = session.BuildRequest();
        var parameters = new Dictionary<string, JsonElement>
        {
            ["calibrationDataJson"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(calibRequest)),
            ["cameraModel"] = JsonSerializer.SerializeToElement(request.CameraModel.Trim().ToLowerInvariant()),
            ["maxViewRms"] = JsonSerializer.SerializeToElement(request.MaxViewRms),
            ["maxRms"] = JsonSerializer.SerializeToElement(request.MaxRms),
            ["maxPoseViewRms"] = JsonSerializer.SerializeToElement(request.MaxPoseViewRms),
            ["telecentricMagnification"] = JsonSerializer.SerializeToElement(request.TelecentricMagnification),
            ["telecentricPixelSizeUm"] = JsonSerializer.SerializeToElement(request.TelecentricPixelSizeUm)
        };
        var node = new NodeDefinition("wizard-intrinsics", "calibration.cameraIntrinsics", "Wizard", null, parameters);
        try
        {
            var result = await _intrinsics.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
            var json = result.Outputs["calibrationJson"].Require<string>("calibrationJson");
            var model = JsonSerializer.Deserialize<CameraCalibrationModel>(json, JsonOpts())
                ?? throw new InvalidOperationException("Intrinsic solve returned an empty model.");
            var summary = new Dictionary<string, object?>
            {
                ["rms"] = result.Outputs["rms"].Require<double>("rms"),
                ["viewStats"] = result.Outputs.TryGetValue("viewStats", out var vs) ? vs.Require<string>("viewStats") : null,
                ["worstViewRms"] = result.Outputs.TryGetValue("worstViewRms", out var wr) ? wr.Require<double>("worstViewRms") : null,
                ["worstViewIndex"] = result.Outputs.TryGetValue("worstViewIndex", out var wi) ? wi.Require<long>("worstViewIndex") : null,
                ["worstPoseViewRms"] = result.Outputs.TryGetValue("worstPoseViewRms", out var wpr) ? wpr.Require<double>("worstPoseViewRms") : null,
                ["worstPoseViewIndex"] = result.Outputs.TryGetValue("worstPoseViewIndex", out var wpi) ? wpi.Require<long>("worstPoseViewIndex") : null,
                ["outlierViewIndex"] = result.Outputs.TryGetValue("outlierViewIndex", out var oi) ? oi.Require<long>("outlierViewIndex") : null,
                ["nodeSummary"] = result.Summary,
                ["calibrationJson"] = json,
                ["enabledViews"] = calibRequest.Views.Count,
                ["cameraModel"] = session.Snapshot().CameraModel,
                ["telecentricMagnification"] = session.Snapshot().TelecentricMagnification,
                ["telecentricPixelSizeUm"] = session.Snapshot().TelecentricPixelSizeUm
            };
            var state = session.StoreSolveResult(model, summary);
            _intrinsicsSessions.Persist(session);
            return new { session = state, result = summary, hints = PoseHints(session) };
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

    public object Export(string sessionId)
    {
        var session = _intrinsicsSessions.Resolve(sessionId);
        var state = session.Snapshot();
        var document = session.ExportDocument();
        return new
        {
            session = state,
            document,
            documentJson = JsonSerializer.Serialize(document, PersistJson),
            calibrationDataJson = state.EnabledCount >= 1
                ? JsonSerializer.Serialize(new CameraCalibrationRequest(
                    state.ImageWidth ?? 0, state.ImageHeight ?? 0,
                    state.Views.Where(v => v.Enabled).Select(v => v.View).ToList()))
                : null,
            calibrationJson = state.Model is null ? null : JsonSerializer.Serialize(state.Model),
            cameraModel = state.CameraModel,
            hints = PoseHints(session),
            fisheyeSupported = true,
            fisheyeNote = "Fisheye uses OpenCV's four-coefficient angular model.",
            telecentricSupported = true,
            telecentricNote = "Object-side area-scan telecentric calibration uses the JLVision native polynomial model. Seed magnification and fixed sensor pixel pitch from the lens/camera datasheets."
        };
    }

    public async Task<CameraIntrinsicAssetDescriptor> CreateIntrinsicAssetAsync(CreateCameraIntrinsicAssetFromSessionRequest request, CancellationToken ct)
    {
        var solved = RequireSolvedModel(_intrinsicsSessions.Resolve(request.SessionId));
        return await _intrinsicAssets.CreateAsync(new CreateCameraIntrinsicAssetRequest(
            request.Id, request.Name, request.Description, solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    public async Task<CameraIntrinsicVersionSnapshot> AddIntrinsicVersionAsync(string id, SaveCameraIntrinsicVersionFromSessionRequest request, CancellationToken ct)
    {
        var solved = RequireSolvedModel(_intrinsicsSessions.Resolve(request.SessionId));
        return await _intrinsicAssets.AddVersionAsync(id, new SaveCameraIntrinsicVersionRequest(
            solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    private static (CameraCalibrationModel Model, string DocumentJson, string SummaryJson) RequireSolvedModel(CalibrationIntrinsicsWizard.Session session)
    {
        var state = session.Snapshot();
        if (state.Model is null) throw new ApiConflictException("Solve camera intrinsics before saving a versioned calibration asset.");
        var document = session.ExportDocument();
        if (document.Model is null) throw new InvalidOperationException("Solved session model was not included in the wizard source document.");
        return (state.Model, JsonSerializer.Serialize(document, PersistJson), JsonSerializer.Serialize(state.LastSolveSummary ?? new { }, PersistJson));
    }

    public CalibrationIntrinsicsWizard.SessionState ImportDocument(CalibrationWizardImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentJson))
            throw new ApiValidationException("documentJson is required.");
        CalibrationIntrinsicsWizard.SessionDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(request.DocumentJson, JsonOpts())
                ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"Invalid wizard documentJson: {ex.Message}", ex);
        }
        var session = _intrinsicsSessions.CreateFromDocument(document);
        _intrinsicsSessions.Persist(session);
        return session.Snapshot();
    }

    public object Hints(string sessionId) => new { hints = PoseHints(_intrinsicsSessions.Resolve(sessionId)) };

    private async Task<(bool Found, CameraCalibrationView? View, CalibrationViewQuality.Report Quality)> DetectBoardAsync(
        CalibrationIntrinsicsWizard.Session session, Mat image, CancellationToken ct)
    {
        var board = session.Board;
        var parameters = new Dictionary<string, JsonElement>
        {
            ["pattern"] = JsonSerializer.SerializeToElement(board.Pattern),
            ["columns"] = JsonSerializer.SerializeToElement(board.Columns),
            ["rows"] = JsonSerializer.SerializeToElement(board.Rows),
            ["spacingMm"] = JsonSerializer.SerializeToElement(board.SpacingMm),
            ["maxGridError"] = JsonSerializer.SerializeToElement(board.MaxGridError),
            ["saddleThreshold"] = JsonSerializer.SerializeToElement(board.SaddleThreshold),
            ["minBlurVariance"] = JsonSerializer.SerializeToElement(0),
            ["minCoverage"] = JsonSerializer.SerializeToElement(0),
            ["rejectWeakViews"] = JsonSerializer.SerializeToElement(false)
        };
        var node = new NodeDefinition("wizard-detect", "calibration.detectBoard", "Wizard", null, parameters);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        var result = await _detectBoard.ExecuteAsync(new NodeExecutionContext(inputs), node, ct);
        var found = result.Outputs["found"].Require<bool>("found");
        var blur = result.Outputs.TryGetValue("blurVariance", out var blurVal) ? blurVal.Require<double>("blurVariance") : 0d;
        if (!found)
        {
            var quality = CalibrationViewQuality.Evaluate(
                null, image.Width, image.Height, board.Rows * board.Columns, blur,
                board.MinBlurVariance, board.MinCoverage);
            var reason = result.Summary.TryGetValue("reason", out var r) && r is not null ? r.ToString() : quality.Reason;
            return (false, null, quality with { Reason = reason ?? quality.Reason });
        }

        var viewJson = result.Outputs["viewJson"].Require<string>("viewJson");
        var view = JsonSerializer.Deserialize<CameraCalibrationView>(viewJson, JsonOpts())
            ?? throw new ApiValidationException("detectBoard returned an empty view.");
        var report = CalibrationViewQuality.Evaluate(
            view.ImagePoints, image.Width, image.Height, board.Rows * board.Columns, blur,
            board.MinBlurVariance, board.MinCoverage);
        return (true, view, report);
    }

    private static IReadOnlyList<CalibrationPoseHints.Hint> PoseHints(CalibrationIntrinsicsWizard.Session session)
    {
        var snap = session.Snapshot();
        if (snap.ImageWidth is not int w || snap.ImageHeight is not int h)
            return [new CalibrationPoseHints.Hint("need-size", "Capture or set image size before pose coverage hints are available.", "setup")];
        var views = snap.Views.Select(v => ((IReadOnlyList<IReadOnlyList<double>>)v.View.ImagePoints, v.Enabled)).ToList();
        return CalibrationPoseHints.Suggest(views, w, h);
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static int EstimateWidth(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[0])) + 1);
    private static int EstimateHeight(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[1])) + 1);

    public async Task<object> SolveDualCameraAsync(CalibrationDualCameraSolveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PairsJson))
            throw new ApiValidationException("pairsJson is required.");
        var parameters = new Dictionary<string, JsonElement>
        {
            ["pairsJson"] = JsonSerializer.SerializeToElement(request.PairsJson),
            ["inputMode"] = JsonSerializer.SerializeToElement(request.InputMode),
            ["method"] = JsonSerializer.SerializeToElement(request.Method),
            ["maxTranslationError"] = JsonSerializer.SerializeToElement(request.MaxTranslationError),
            ["maxRotationErrorDeg"] = JsonSerializer.SerializeToElement(request.MaxRotationErrorDeg),
            ["rotationWeight"] = JsonSerializer.SerializeToElement(request.RotationWeight),
            ["sourceFrame"] = JsonSerializer.SerializeToElement(request.SourceFrame),
            ["targetFrame"] = JsonSerializer.SerializeToElement(request.TargetFrame),
            ["unit"] = JsonSerializer.SerializeToElement(request.Unit),
            ["imageWidth"] = JsonSerializer.SerializeToElement(request.ImageWidth),
            ["imageHeight"] = JsonSerializer.SerializeToElement(request.ImageHeight)
        };
        var node = new NodeDefinition("wizard-dual-camera", "calibration.dualCameraExtrinsics", "Wizard", null, parameters);
        try
        {
            var result = await _dualCamera.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
            return new
            {
                transform = result.Outputs["transform"].Value,
                matrixJson = result.Outputs["matrixJson"].Require<string>("matrixJson"),
                translationRms = result.Outputs["translationRms"].Require<double>("translationRms"),
                rotationRmsDeg = result.Outputs["rotationRmsDeg"].Require<double>("rotationRmsDeg"),
                baseline = result.Outputs["baseline"].Require<double>("baseline"),
                maxTranslationError = result.Outputs["maxTranslationError"].Require<double>("maxTranslationError"),
                maxRotationErrorDeg = result.Outputs["maxRotationErrorDeg"].Require<double>("maxRotationErrorDeg"),
                worstSampleIndex = result.Outputs["worstSampleIndex"].Require<long>("worstSampleIndex"),
                worstTranslationSampleIndex = result.Outputs["worstTranslationSampleIndex"].Require<long>("worstTranslationSampleIndex"),
                worstRotationSampleIndex = result.Outputs["worstRotationSampleIndex"].Require<long>("worstRotationSampleIndex"),
                pairCount = result.Outputs["pairCount"].Require<long>("pairCount"),
                sampleResidualsJson = result.Outputs["sampleResidualsJson"].Require<string>("sampleResidualsJson"),
                summary = result.Summary,
                pairsJson = request.PairsJson,
                inputMode = request.InputMode,
                method = request.Method
            };
        }
        catch (OpenCVException ex)
        {
            throw new ApiValidationException($"OpenCV dual-camera solve failed: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }
}
