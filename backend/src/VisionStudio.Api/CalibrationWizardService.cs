using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Api;

public sealed record CalibrationWizardCreateRequest(
    string Pattern = "chessboard",
    int Columns = 7,
    int Rows = 6,
    double SpacingMm = 10,
    double MinBlurVariance = 40,
    double MinCoverage = 0.25,
    double MaxGridError = 2,
    double SaddleThreshold = 10,
    int? ImageWidth = null,
    int? ImageHeight = null);

public sealed record CalibrationWizardAddViewRequest(
    string? ViewJson = null,
    string? CalibrationDataJson = null,
    string? Label = null,
    bool? Enable = null,
    double? BlurVariance = null);

public sealed record CalibrationWizardSetEnabledRequest(bool Enabled);

public sealed record CalibrationWizardSolveRequest(double MaxViewRms = 0, double MaxRms = 0, double MaxPoseViewRms = 0, string CameraModel = "pinhole");

public sealed record CalibrationWizardPreviewRequest(string CameraId, bool AddIfAccepted = false, string? Label = null);

public sealed record CalibrationWizardImportRequest(string DocumentJson);
public sealed record CreateCameraIntrinsicAssetFromSessionRequest(string SessionId, string Id, string Name, string? Description = null, string? Note = null);
public sealed record SaveCameraIntrinsicVersionFromSessionRequest(string SessionId, string? Note = null);

public sealed record CalibrationWizardDisableOutliersRequest(int[]? ViewIndexes = null, bool UseSolveOutlier = true);

public sealed record CalibrationHandEyeSolveRequest(
    string SamplesJson,
    string Mode = "eyeInHand",
    double MaxTranslationError = 0,
    double MaxAngleErrorDeg = 0,
    string SourceFrame = "Camera",
    string TargetFrame = "",
    string Unit = "mm",
    string SourceUnit = "mm");

/// <summary>
/// HTTP-facing guided calibration wizards: intrinsics session store (memory + disk),
/// board detect from upload/camera preview, solve, export/import, and planar hand-eye.
/// </summary>
public sealed class CalibrationWizardService
{
    private readonly CalibrationIntrinsicsWizard.SessionStore _store = new();
    private readonly CalibrationDetectBoardNode _detectBoard;
    private readonly CameraIntrinsicCalibrationNode _intrinsics;
    private readonly PlanarHandEyeCalibrationNode _handEye;
    private readonly CameraManager _cameras;
    private readonly CameraIntrinsicAssetStore _intrinsicAssets;
    private readonly string _persistRoot;
    private static readonly JsonSerializerOptions PersistJson = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public CalibrationWizardService(
        CalibrationDetectBoardNode detectBoard,
        CameraIntrinsicCalibrationNode intrinsics,
        PlanarHandEyeCalibrationNode handEye,
        CameraManager cameras,
        CameraIntrinsicAssetStore intrinsicAssets,
        IWebHostEnvironment environment)
    {
        _detectBoard = detectBoard;
        _intrinsics = intrinsics;
        _handEye = handEye;
        _cameras = cameras;
        _intrinsicAssets = intrinsicAssets;
        _persistRoot = Path.Combine(VisionStudioDataRoot.Resolve(environment.ContentRootPath), "calibration-wizard");
        Directory.CreateDirectory(_persistRoot);
        TryHydrateFromDisk();
    }

    public CalibrationIntrinsicsWizard.SessionState Create(CalibrationWizardCreateRequest request)
    {
        var board = new CalibrationIntrinsicsWizard.BoardConfig(
            request.Pattern, request.Columns, request.Rows, request.SpacingMm,
            request.MinBlurVariance, request.MinCoverage, request.MaxGridError, request.SaddleThreshold);
        var session = _store.Create(board);
        CalibrationIntrinsicsWizard.SessionState state;
        if (request.ImageWidth is int w && request.ImageHeight is int h)
            state = session.SetImageSize(w, h);
        else
            state = session.Snapshot();
        Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState Get(string sessionId)
    {
        var session = ResolveSession(sessionId);
        return session.Snapshot();
    }

    public bool Delete(string sessionId)
    {
        var removed = _store.Remove(sessionId);
        var path = SessionPath(sessionId);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
        return removed || File.Exists(path) == false;
    }

    public CalibrationIntrinsicsWizard.SessionState AddViewJson(string sessionId, CalibrationWizardAddViewRequest request)
    {
        var session = ResolveSession(sessionId);
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
            Persist(session);
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
            Persist(session);
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
        var session = ResolveSession(sessionId);
        session.SetImageSize(decoded.Width, decoded.Height);

        var detect = await DetectBoardAsync(session, decoded, ct);
        if (!detect.Found || detect.View is null)
            throw new ApiValidationException($"Board not detected ({detect.Quality.Reason}). {detect.Quality.Guidance}");

        var state = session.AddView(detect.View, detect.Quality, label, enable);
        Persist(session);
        return state;
    }

    public async Task<object> PreviewFromCameraAsync(string sessionId, CalibrationWizardPreviewRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CameraId))
            throw new ApiValidationException("cameraId is required.");
        var session = ResolveSession(sessionId);
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
            Persist(session);
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
        var session = ResolveSession(sessionId);
        var state = session.SetEnabled(index, enabled);
        Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState RemoveView(string sessionId, int index)
    {
        var session = ResolveSession(sessionId);
        var state = session.RemoveView(index);
        Persist(session);
        return state;
    }

    public CalibrationIntrinsicsWizard.SessionState DisableOutliers(string sessionId, CalibrationWizardDisableOutliersRequest request)
    {
        var session = ResolveSession(sessionId);
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
        Persist(session);
        return last ?? session.Snapshot();
    }

    public async Task<object> SolveAsync(string sessionId, CalibrationWizardSolveRequest request, CancellationToken ct)
    {
        var session = ResolveSession(sessionId);
        session.SetCameraModel(request.CameraModel);
        Persist(session);
        var calibRequest = session.BuildRequest();
        var parameters = new Dictionary<string, JsonElement>
        {
            ["calibrationDataJson"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(calibRequest)),
            ["cameraModel"] = JsonSerializer.SerializeToElement(request.CameraModel.Trim().ToLowerInvariant()),
            ["maxViewRms"] = JsonSerializer.SerializeToElement(request.MaxViewRms),
            ["maxRms"] = JsonSerializer.SerializeToElement(request.MaxRms),
            ["maxPoseViewRms"] = JsonSerializer.SerializeToElement(request.MaxPoseViewRms)
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
                ["cameraModel"] = session.Snapshot().CameraModel
            };
            var state = session.StoreSolveResult(model, summary);
            Persist(session);
            return new { session = state, result = summary, hints = PoseHints(session) };
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

    public object Export(string sessionId)
    {
        var session = ResolveSession(sessionId);
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
            fisheyeNote = "Fisheye uses OpenCV's four-coefficient angular model. Telecentric calibration is not supported."
        };
    }

    public async Task<CameraIntrinsicAssetDescriptor> CreateIntrinsicAssetAsync(CreateCameraIntrinsicAssetFromSessionRequest request, CancellationToken ct)
    {
        var solved = RequireSolvedModel(ResolveSession(request.SessionId));
        return await _intrinsicAssets.CreateAsync(new CreateCameraIntrinsicAssetRequest(
            request.Id, request.Name, request.Description, solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    public async Task<CameraIntrinsicVersionSnapshot> AddIntrinsicVersionAsync(string id, SaveCameraIntrinsicVersionFromSessionRequest request, CancellationToken ct)
    {
        var solved = RequireSolvedModel(ResolveSession(request.SessionId));
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
        var session = _store.CreateFromDocument(document);
        Persist(session);
        return session.Snapshot();
    }

    public object Hints(string sessionId) => new { hints = PoseHints(ResolveSession(sessionId)) };

    public async Task<object> SolveHandEyeAsync(CalibrationHandEyeSolveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SamplesJson))
            throw new ApiValidationException("samplesJson is required.");
        var parameters = new Dictionary<string, JsonElement>
        {
            ["samplesJson"] = JsonSerializer.SerializeToElement(request.SamplesJson),
            ["mode"] = JsonSerializer.SerializeToElement(request.Mode),
            ["maxTranslationError"] = JsonSerializer.SerializeToElement(request.MaxTranslationError),
            ["maxAngleErrorDeg"] = JsonSerializer.SerializeToElement(request.MaxAngleErrorDeg),
            ["sourceFrame"] = JsonSerializer.SerializeToElement(request.SourceFrame),
            ["unit"] = JsonSerializer.SerializeToElement(request.Unit),
            ["sourceUnit"] = JsonSerializer.SerializeToElement(request.SourceUnit)
        };
        if (!string.IsNullOrWhiteSpace(request.TargetFrame))
            parameters["targetFrame"] = JsonSerializer.SerializeToElement(request.TargetFrame);

        var node = new NodeDefinition("wizard-handeye", "calibration.handEye2D", "Wizard", null, parameters);
        try
        {
            var result = await _handEye.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
            return new
            {
                pose = result.Outputs.TryGetValue("pose", out var pose) ? pose.Value : null,
                transform = result.Outputs.TryGetValue("transform", out var xf) ? xf.Value : null,
                translationRms = result.Outputs["translationRms"].Require<double>("translationRms"),
                angleRmsDeg = result.Outputs["angleRmsDeg"].Require<double>("angleRmsDeg"),
                maxTranslationError = result.Outputs["maxTranslationError"].Require<double>("maxTranslationError"),
                maxAngleErrorDeg = result.Outputs["maxAngleErrorDeg"].Require<double>("maxAngleErrorDeg"),
                worstSampleIndex = result.Outputs["worstSampleIndex"].Require<long>("worstSampleIndex"),
                sampleResidualsJson = result.Outputs["sampleResidualsJson"].Require<string>("sampleResidualsJson"),
                summary = result.Summary,
                samplesJson = request.SamplesJson,
                mode = request.Mode
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

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

    private CalibrationIntrinsicsWizard.Session ResolveSession(string sessionId)
    {
        if (_store.TryGet(sessionId, out var live) && live is not null)
            return live;
        var path = SessionPath(sessionId);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"Calibration wizard session '{sessionId}' was not found or has expired.");
        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(json, JsonOpts())
                ?? throw new JsonException("empty");
            // Force the on-disk session id.
            document = document with { SessionId = sessionId };
            var restored = _store.CreateFromDocument(document);
            return restored;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            throw new KeyNotFoundException($"Calibration wizard session '{sessionId}' could not be restored from disk.", ex);
        }
    }

    private void Persist(CalibrationIntrinsicsWizard.Session session)
    {
        try
        {
            var document = session.ExportDocument();
            var path = SessionPath(session.SessionId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(document, PersistJson));
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }
        catch
        {
            // Persistence is best-effort; in-memory session remains authoritative for this process.
        }
    }

    private void TryHydrateFromDisk()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_persistRoot, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var document = JsonSerializer.Deserialize<CalibrationIntrinsicsWizard.SessionDocument>(json, JsonOpts());
                    if (document is null) continue;
                    if (_store.TryGet(document.SessionId, out _)) continue;
                    _store.CreateFromDocument(document);
                }
                catch
                {
                    // skip corrupt files
                }
            }
        }
        catch
        {
            // ignore hydrate failures
        }
    }

    private string SessionPath(string sessionId)
    {
        var safe = string.Concat((sessionId ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safe)) throw new ApiValidationException("Invalid session id.");
        return Path.Combine(_persistRoot, safe + ".json");
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static int EstimateWidth(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[0])) + 1);
    private static int EstimateHeight(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[1])) + 1);
}
