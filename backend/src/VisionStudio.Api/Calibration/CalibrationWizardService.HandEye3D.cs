using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

/// <summary>3D hand-eye wizard domain: solve, session and asset plumbing. Split out of the single
/// 1274-line service so the calibration families no longer read as one file.</summary>
public sealed partial class CalibrationWizardService
{
    public async Task<object> SolveHandEye3DAsync(CalibrationHandEye3DSolveRequest request, CancellationToken ct)
    {
        var result = await SolveHandEye3DCoreAsync(request, ct);
        return new
        {
            transform = result.Outputs["transform"].Value,
            calibrationJson = result.Outputs["calibrationJson"].Require<string>("calibrationJson"),
            translationRms = result.Outputs["translationRms"].Require<double>("translationRms"),
            rotationRmsDeg = result.Outputs["rotationRmsDeg"].Require<double>("rotationRmsDeg"),
            robotTranslationSpan = result.Outputs["robotTranslationSpan"].Require<double>("robotTranslationSpan"),
            maxTranslationError = result.Outputs["maxTranslationError"].Require<double>("maxTranslationError"),
            maxRotationErrorDeg = result.Outputs["maxRotationErrorDeg"].Require<double>("maxRotationErrorDeg"),
            worstSampleIndex = result.Outputs["worstSampleIndex"].Require<long>("worstSampleIndex"),
            worstTranslationSampleIndex = result.Outputs["worstTranslationSampleIndex"].Require<long>("worstTranslationSampleIndex"),
            worstRotationSampleIndex = result.Outputs["worstRotationSampleIndex"].Require<long>("worstRotationSampleIndex"),
            outlierSampleIndex = result.Outputs.TryGetValue("outlierSampleIndex", out var outlier) ? outlier.Require<long>("outlierSampleIndex") : (long?)null,
            usedSampleCount = result.Outputs["usedSampleCount"].Require<long>("usedSampleCount"),
            rejectedSampleIndicesJson = result.Outputs["rejectedSampleIndicesJson"].Require<string>("rejectedSampleIndicesJson"),
            crossCheckTranslationDelta = OptionalOutput(result, "crossCheckTranslationDelta"),
            crossCheckAngleDeltaDeg = OptionalOutput(result, "crossCheckAngleDeltaDeg"),
            sampleResidualsJson = result.Outputs["sampleResidualsJson"].Require<string>("sampleResidualsJson"),
            summary = result.Summary,
            samplesJson = request.SamplesJson,
            mode = request.Mode,
            method = request.Method
        };
    }

    private async Task<NodeExecutorResult> SolveHandEye3DCoreAsync(CalibrationHandEye3DSolveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SamplesJson))
            throw new ApiValidationException("samplesJson is required.");
        var parameters = new Dictionary<string, JsonElement>
        {
            ["samplesJson"] = JsonSerializer.SerializeToElement(request.SamplesJson),
            ["mode"] = JsonSerializer.SerializeToElement(request.Mode),
            ["method"] = JsonSerializer.SerializeToElement(request.Method),
            ["maxTranslationError"] = JsonSerializer.SerializeToElement(request.MaxTranslationError),
            ["maxRotationErrorDeg"] = JsonSerializer.SerializeToElement(request.MaxRotationErrorDeg),
            ["rotationWeight"] = JsonSerializer.SerializeToElement(request.RotationWeight),
            ["sourceFrame"] = JsonSerializer.SerializeToElement(request.SourceFrame),
            ["unit"] = JsonSerializer.SerializeToElement(request.Unit),
            ["outlierRejection"] = JsonSerializer.SerializeToElement(request.OutlierRejection),
            ["crossCheckMethod"] = JsonSerializer.SerializeToElement(request.CrossCheckMethod),
            ["minTranslationSpan"] = JsonSerializer.SerializeToElement(request.MinTranslationSpan)
        };
        if (!string.IsNullOrWhiteSpace(request.TargetFrame))
            parameters["targetFrame"] = JsonSerializer.SerializeToElement(request.TargetFrame);

        var node = new NodeDefinition("wizard-handeye-3d", "calibration.handEye3D", "Wizard", null, parameters);
        try
        {
            return await _handEye3D.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
        }
        catch (OpenCVException ex)
        {
            throw new ApiValidationException($"OpenCV 3D hand-eye solve failed: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

    public HandEye3DWizardSessionDocument SaveHandEye3DSession(HandEye3DWizardSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            throw new ApiValidationException("3D hand-eye session name is required and must be at most 200 characters.");
        ValidateHandEye3DSamplesJson(request.SamplesJson, allowDraft: true);
        ValidateHandEye3DSettings(request.Mode, request.Method, request.RotationWeight, request.OutlierRejection, request.CrossCheckMethod, request.MaxTranslationError, request.MaxRotationErrorDeg, request.MinTranslationSpan);
        if (string.IsNullOrWhiteSpace(request.SourceFrame) || string.IsNullOrWhiteSpace(request.Unit))
            throw new ApiValidationException("sourceFrame and unit are required.");
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? Guid.NewGuid().ToString("N") : request.SessionId.Trim();
        var document = new HandEye3DWizardSessionDocument(
            HandEye3DWizardSessionDocument.CurrentVersion, sessionId, request.Name.Trim(), DateTimeOffset.UtcNow.ToString("O"),
            request.SamplesJson, request.Mode, request.Method, request.RotationWeight, request.OutlierRejection, request.CrossCheckMethod,
            request.MaxTranslationError, request.MaxRotationErrorDeg, request.MinTranslationSpan, request.SourceFrame, request.TargetFrame ?? "",
            request.Unit, request.LastSolveSummaryJson);
        return _handEye3DSessions.Put(document);
    }

    public IReadOnlyList<object> ListHandEye3DSessions()
        => _handEye3DSessions.All()
            .Select(d => (object)new
            {
                sessionId = d.SessionId,
                name = d.Name,
                updatedAt = d.UpdatedAt,
                mode = d.Mode,
                method = d.Method,
                sampleCount = CountHandEye3DSamples(d.SamplesJson),
                hasSolve = !string.IsNullOrWhiteSpace(d.LastSolveSummaryJson)
            }).ToList();

    public HandEye3DWizardSessionDocument GetHandEye3DSession(string sessionId) => _handEye3DSessions.Get(sessionId);

    public bool DeleteHandEye3DSession(string sessionId) => _handEye3DSessions.Delete(sessionId);

    public async Task<HandEye3DCalibrationAssetDescriptor> CreateHandEye3DAssetAsync(CreateHandEye3DAssetFromSessionRequest request, CancellationToken ct)
    {
        if (_handEye3DAssets is null) throw new ApiConflictException("3D hand-eye calibration asset storage is unavailable.");
        var solved = await SolveHandEye3DDocumentAsync(GetHandEye3DSession(request.SessionId), ct);
        return await _handEye3DAssets.CreateAsync(new CreateHandEye3DCalibrationAssetRequest(
            request.Id, request.Name, request.Description, solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    public async Task<HandEye3DCalibrationVersionSnapshot> AddHandEye3DVersionAsync(string id, SaveHandEye3DVersionFromSessionRequest request, CancellationToken ct)
    {
        if (_handEye3DAssets is null) throw new ApiConflictException("3D hand-eye calibration asset storage is unavailable.");
        var solved = await SolveHandEye3DDocumentAsync(GetHandEye3DSession(request.SessionId), ct);
        return await _handEye3DAssets.AddVersionAsync(id, new SaveHandEye3DCalibrationVersionRequest(
            solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    private async Task<(HandEye3DCalibrationModel Model, string DocumentJson, string SummaryJson)> SolveHandEye3DDocumentAsync(HandEye3DWizardSessionDocument document, CancellationToken ct)
    {
        ValidateHandEye3DSamplesJson(document.SamplesJson, allowDraft: false);
        var result = await SolveHandEye3DCoreAsync(new CalibrationHandEye3DSolveRequest(
            document.SamplesJson, document.Mode, document.Method, document.MaxTranslationError, document.MaxRotationErrorDeg,
            document.RotationWeight, document.SourceFrame, document.TargetFrame, document.Unit,
            document.OutlierRejection, document.CrossCheckMethod, document.MinTranslationSpan), ct);
        var summaryJson = JsonSerializer.Serialize(result.Summary, PersistJson);
        var updated = document with { LastSolveSummaryJson = summaryJson };
        _handEye3DSessions.Put(updated);
        var model = JsonSerializer.Deserialize<HandEye3DCalibrationModel>(result.Outputs["calibrationJson"].Require<string>("calibrationJson"), JsonOpts())
            ?? throw new ApiValidationException("3D hand-eye solve returned an empty calibration model.");
        return (model, JsonSerializer.Serialize(updated, PersistJson), summaryJson);
    }

    private static void ValidateHandEye3DSamplesJson(string samplesJson, bool allowDraft)
    {
        if (string.IsNullOrWhiteSpace(samplesJson)) throw new ApiValidationException("samplesJson is required.");
        HandEye3DRequest request;
        try
        {
            request = JsonSerializer.Deserialize<HandEye3DRequest>(samplesJson, JsonOpts()) ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"Invalid samplesJson: {ex.Message}", ex);
        }
        var minimum = allowDraft ? 1 : 6;
        if (request.Samples is null || request.Samples.Count < minimum || request.Samples.Count > 1000)
            throw new ApiValidationException($"samplesJson must contain between {minimum} and 1000 pose pairs.");
        if (request.Samples.Any(s => s is null || s.RobotToBase is not { Length: 16 } || s.TargetToCamera is not { Length: 16 }
            || s.RobotToBase.Any(v => !double.IsFinite(v)) || s.TargetToCamera.Any(v => !double.IsFinite(v))))
            throw new ApiValidationException("Every pose pair must have finite 16-value robotToBase and targetToCamera matrices.");
    }

    private static int CountHandEye3DSamples(string samplesJson)
    {
        try { return JsonSerializer.Deserialize<HandEye3DRequest>(samplesJson, JsonOpts())?.Samples?.Count ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static void ValidateHandEye3DSettings(string mode, string method, double rotationWeight, string outlierRejection, string crossCheckMethod, double maxTranslationError, double maxRotationErrorDeg, double minTranslationSpan)
    {
        // Shared with HandEye3DCalibrationNode; the wizard does not expose screening knobs, so the
        // node defaults are validated here too (they are the values the node will actually use).
        if (HandEyeSettings.ValidateSpatial(mode, method, rotationWeight, outlierRejection, crossCheckMethod,
                maxTranslationError, maxRotationErrorDeg, minTranslationSpan, 20, 0.5) is { } error)
            throw new ApiValidationException(error);
    }
}
