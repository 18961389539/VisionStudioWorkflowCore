using System.Text.Json;
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

public sealed record CalibrationWizardSolveRequest(double MaxViewRms = 0, double MaxRms = 0);

/// <summary>
/// HTTP-facing guided intrinsics wizard: session store + board detect from uploaded
/// images + solve via the same CameraIntrinsicCalibrationNode used in workflows.
/// </summary>
public sealed class CalibrationWizardService
{
    private readonly CalibrationIntrinsicsWizard.SessionStore _store = new();
    private readonly CalibrationDetectBoardNode _detectBoard;
    private readonly CameraIntrinsicCalibrationNode _intrinsics;

    public CalibrationWizardService(CalibrationDetectBoardNode detectBoard, CameraIntrinsicCalibrationNode intrinsics)
    {
        _detectBoard = detectBoard;
        _intrinsics = intrinsics;
    }

    public CalibrationIntrinsicsWizard.SessionState Create(CalibrationWizardCreateRequest request)
    {
        var board = new CalibrationIntrinsicsWizard.BoardConfig(
            request.Pattern, request.Columns, request.Rows, request.SpacingMm,
            request.MinBlurVariance, request.MinCoverage, request.MaxGridError, request.SaddleThreshold);
        var session = _store.Create(board);
        if (request.ImageWidth is int w && request.ImageHeight is int h)
            return session.SetImageSize(w, h);
        return session.Snapshot();
    }

    public CalibrationIntrinsicsWizard.SessionState Get(string sessionId) => _store.Get(sessionId).Snapshot();

    public bool Delete(string sessionId) => _store.Remove(sessionId);

    public CalibrationIntrinsicsWizard.SessionState AddViewJson(string sessionId, CalibrationWizardAddViewRequest request)
    {
        var session = _store.Get(sessionId);
        if (!string.IsNullOrWhiteSpace(request.ViewJson))
        {
            CameraCalibrationView view;
            try { view = JsonSerializer.Deserialize<CameraCalibrationView>(request.ViewJson, JsonOpts()) ?? throw new JsonException("empty"); }
            catch (JsonException ex) { throw new ApiValidationException($"Invalid viewJson: {ex.Message}", ex); }
            var blur = request.BlurVariance ?? 0;
            var quality = CalibrationViewQuality.Evaluate(
                view.ImagePoints,
                session.Snapshot().ImageWidth ?? EstimateWidth(view),
                session.Snapshot().ImageHeight ?? EstimateHeight(view),
                session.Board.Rows * session.Board.Columns,
                blur,
                session.Board.MinBlurVariance,
                session.Board.MinCoverage);
            return session.AddView(view, quality, request.Label, request.Enable);
        }

        if (!string.IsNullOrWhiteSpace(request.CalibrationDataJson))
        {
            CameraCalibrationRequest data;
            try { data = JsonSerializer.Deserialize<CameraCalibrationRequest>(request.CalibrationDataJson, JsonOpts()) ?? throw new JsonException("empty"); }
            catch (JsonException ex) { throw new ApiValidationException($"Invalid calibrationDataJson: {ex.Message}", ex); }
            if (data.Views is null || data.Views.Count == 0) throw new ApiValidationException("calibrationDataJson contains no views.");
            session.SetImageSize(data.ImageWidth, data.ImageHeight);
            CalibrationIntrinsicsWizard.SessionState? last = null;
            foreach (var (view, i) in data.Views.Select((v, i) => (v, i)))
            {
                var quality = CalibrationViewQuality.Evaluate(
                    view.ImagePoints, data.ImageWidth, data.ImageHeight,
                    session.Board.Rows * session.Board.Columns,
                    request.BlurVariance ?? 120,
                    session.Board.MinBlurVariance, session.Board.MinCoverage);
                last = session.AddView(view, quality, request.Label is null ? $"import-{i}" : $"{request.Label}-{i}", request.Enable);
            }
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
        var session = _store.Get(sessionId);
        session.SetImageSize(decoded.Width, decoded.Height);

        var board = session.Board;
        var parameters = new Dictionary<string, JsonElement>
        {
            ["pattern"] = JsonSerializer.SerializeToElement(board.Pattern),
            ["columns"] = JsonSerializer.SerializeToElement(board.Columns),
            ["rows"] = JsonSerializer.SerializeToElement(board.Rows),
            ["spacingMm"] = JsonSerializer.SerializeToElement(board.SpacingMm),
            ["maxGridError"] = JsonSerializer.SerializeToElement(board.MaxGridError),
            ["saddleThreshold"] = JsonSerializer.SerializeToElement(board.SaddleThreshold),
            ["minBlurVariance"] = JsonSerializer.SerializeToElement(0), // wizard applies its own gate after detect
            ["minCoverage"] = JsonSerializer.SerializeToElement(0),
            ["rejectWeakViews"] = JsonSerializer.SerializeToElement(false)
        };
        var node = new NodeDefinition("wizard-detect", "calibration.detectBoard", "Wizard", null, parameters);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(decoded.Clone())) };
        var result = await _detectBoard.ExecuteAsync(new NodeExecutionContext(inputs), node, ct);
        var found = result.Outputs["found"].Require<bool>("found");
        var blur = result.Outputs.TryGetValue("blurVariance", out var blurVal) ? blurVal.Require<double>("blurVariance") : 0d;
        if (!found)
        {
            var quality = CalibrationViewQuality.Evaluate(
                null, decoded.Width, decoded.Height, board.Rows * board.Columns, blur,
                board.MinBlurVariance, board.MinCoverage);
            var reason = result.Summary.TryGetValue("reason", out var r) && r is not null ? r.ToString() : quality.Reason;
            throw new ApiValidationException($"Board not detected ({reason}). {quality.Guidance}");
        }

        var viewJson = result.Outputs["viewJson"].Require<string>("viewJson");
        var view = JsonSerializer.Deserialize<CameraCalibrationView>(viewJson, JsonOpts())
            ?? throw new ApiValidationException("detectBoard returned an empty view.");
        // detectBoard already appended into its local request; we only take the single view.
        var report = CalibrationViewQuality.Evaluate(
            view.ImagePoints, decoded.Width, decoded.Height, board.Rows * board.Columns, blur,
            board.MinBlurVariance, board.MinCoverage);
        return session.AddView(view, report, label, enable);
    }

    public CalibrationIntrinsicsWizard.SessionState SetEnabled(string sessionId, int index, bool enabled) =>
        _store.Get(sessionId).SetEnabled(index, enabled);

    public CalibrationIntrinsicsWizard.SessionState RemoveView(string sessionId, int index) =>
        _store.Get(sessionId).RemoveView(index);

    public async Task<object> SolveAsync(string sessionId, CalibrationWizardSolveRequest request, CancellationToken ct)
    {
        var session = _store.Get(sessionId);
        var calibRequest = session.BuildRequest();
        var parameters = new Dictionary<string, JsonElement>
        {
            ["calibrationDataJson"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(calibRequest)),
            ["maxViewRms"] = JsonSerializer.SerializeToElement(request.MaxViewRms),
            ["maxRms"] = JsonSerializer.SerializeToElement(request.MaxRms)
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
                ["nodeSummary"] = result.Summary,
                ["calibrationJson"] = json,
                ["enabledViews"] = calibRequest.Views.Count
            };
            var state = session.StoreSolveResult(model, summary);
            return new { session = state, result = summary };
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

    public object Export(string sessionId)
    {
        var session = _store.Get(sessionId);
        var state = session.Snapshot();
        return new
        {
            session = state,
            calibrationDataJson = state.EnabledCount >= 1
                ? JsonSerializer.Serialize(new CameraCalibrationRequest(
                    state.ImageWidth ?? 0, state.ImageHeight ?? 0,
                    state.Views.Where(v => v.Enabled).Select(v => v.View).ToList()))
                : null,
            calibrationJson = state.Model is null ? null : JsonSerializer.Serialize(state.Model)
        };
    }

    private static JsonSerializerOptions JsonOpts() => new() { PropertyNameCaseInsensitive = true };
    private static int EstimateWidth(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[0])) + 1);
    private static int EstimateHeight(CameraCalibrationView view) => Math.Max(16, (int)Math.Ceiling(view.ImagePoints.Max(p => p[1])) + 1);
}
