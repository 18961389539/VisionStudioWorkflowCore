using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

/// <summary>Hand-eye wizard domain: solve, session and asset plumbing. Split out of the single
/// 1274-line service so the calibration families no longer read as one file.</summary>
public sealed partial class CalibrationWizardService
{
    public async Task<object> SolveHandEyeAsync(CalibrationHandEyeSolveRequest request, CancellationToken ct)
    {
        var result = await SolveHandEyeCoreAsync(request, ct);
        return new
        {
            pose = result.Outputs.TryGetValue("pose", out var pose) ? pose.Value : null,
            transform = result.Outputs.TryGetValue("transform", out var xf) ? xf.Value : null,
            calibrationJson = result.Outputs["calibrationJson"].Require<string>("calibrationJson"),
            translationRms = result.Outputs["translationRms"].Require<double>("translationRms"),
            angleRmsDeg = result.Outputs["angleRmsDeg"].Require<double>("angleRmsDeg"),
            maxTranslationError = result.Outputs["maxTranslationError"].Require<double>("maxTranslationError"),
            maxAngleErrorDeg = result.Outputs["maxAngleErrorDeg"].Require<double>("maxAngleErrorDeg"),
            worstSampleIndex = result.Outputs["worstSampleIndex"].Require<long>("worstSampleIndex"),
            outlierSampleIndex = result.Outputs.TryGetValue("outlierSampleIndex", out var outlier) ? outlier.Require<long>("outlierSampleIndex") : (long?)null,
            converged = result.Outputs["converged"].Require<bool>("converged"),
            iterations = result.Outputs["iterations"].Require<long>("iterations"),
            conditionNumber = OptionalOutput(result, "conditionNumber"),
            stdDevX = result.Outputs["stdDevX"].Require<double>("stdDevX"),
            stdDevY = result.Outputs["stdDevY"].Require<double>("stdDevY"),
            angleStdDevDeg = result.Outputs["angleStdDevDeg"].Require<double>("angleStdDevDeg"),
            crossCheckTranslationDelta = OptionalOutput(result, "crossCheckTranslationDelta"),
            crossCheckAngleDeltaDeg = OptionalOutput(result, "crossCheckAngleDeltaDeg"),
            unitWarning = result.Summary.TryGetValue("unitWarning", out var warning) ? warning : null,
            sampleResidualsJson = result.Outputs["sampleResidualsJson"].Require<string>("sampleResidualsJson"),
            summary = result.Summary,
            samplesJson = request.SamplesJson,
            mode = request.Mode
        };
    }

    private async Task<NodeExecutorResult> SolveHandEyeCoreAsync(CalibrationHandEyeSolveRequest request, CancellationToken ct)
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
            ["sourceUnit"] = JsonSerializer.SerializeToElement(request.SourceUnit),
            ["angularWeight"] = JsonSerializer.SerializeToElement(request.AngularWeight),
            ["visionScale"] = JsonSerializer.SerializeToElement(request.VisionScale),
            ["robustLoss"] = JsonSerializer.SerializeToElement(request.RobustLoss)
        };
        if (!string.IsNullOrWhiteSpace(request.TargetFrame))
            parameters["targetFrame"] = JsonSerializer.SerializeToElement(request.TargetFrame);

        var node = new NodeDefinition("wizard-handeye", "calibration.handEye2D", "Wizard", null, parameters);
        try
        {
            return await _handEye.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiValidationException(ex.Message, ex);
        }
    }

    private static double? OptionalOutput(NodeExecutorResult result, string key)
        => result.Outputs.TryGetValue(key, out var value) ? value.Require<double>(key) : null;

    public HandEyeWizardSessionDocument SaveHandEyeSession(HandEyeWizardSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            throw new ApiValidationException("Hand-eye session name is required and must be at most 200 characters.");
        ValidateHandEyeSamplesJson(request.SamplesJson, allowDraft: true);
        ValidateHandEyeSettings(request.Mode, request.AngularWeight, request.VisionScale, request.RobustLoss, request.MaxTranslationError, request.MaxAngleErrorDeg);
        if (string.IsNullOrWhiteSpace(request.SourceFrame) || string.IsNullOrWhiteSpace(request.Unit) || string.IsNullOrWhiteSpace(request.SourceUnit))
            throw new ApiValidationException("sourceFrame, unit, and sourceUnit are required.");
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? Guid.NewGuid().ToString("N") : request.SessionId.Trim();
        var document = new HandEyeWizardSessionDocument(
            HandEyeWizardSessionDocument.CurrentVersion, sessionId, request.Name.Trim(), DateTimeOffset.UtcNow.ToString("O"),
            request.SamplesJson, request.Mode, request.AngularWeight, request.VisionScale, request.RobustLoss,
            request.MaxTranslationError, request.MaxAngleErrorDeg, request.SourceFrame, request.TargetFrame ?? "",
            request.Unit, request.SourceUnit, request.LastSolveSummaryJson);
        return _handEyeSessions.Put(document);
    }

    public IReadOnlyList<object> ListHandEyeSessions()
        => _handEyeSessions.All()
            .Select(d => (object)new
            {
                sessionId = d.SessionId,
                name = d.Name,
                updatedAt = d.UpdatedAt,
                mode = d.Mode,
                sampleCount = CountHandEyeSamples(d.SamplesJson),
                hasSolve = !string.IsNullOrWhiteSpace(d.LastSolveSummaryJson)
            }).ToList();

    public HandEyeWizardSessionDocument GetHandEyeSession(string sessionId) => _handEyeSessions.Get(sessionId);

    public bool DeleteHandEyeSession(string sessionId) => _handEyeSessions.Delete(sessionId);

    public async Task<HandEyeCalibrationAssetDescriptor> CreateHandEyeAssetAsync(CreateHandEyeAssetFromSessionRequest request, CancellationToken ct)
    {
        if (_handEyeAssets is null) throw new ApiConflictException("Hand-eye calibration asset storage is unavailable.");
        var solved = await SolveHandEyeDocumentAsync(GetHandEyeSession(request.SessionId), ct);
        return await _handEyeAssets.CreateAsync(new CreateHandEyeCalibrationAssetRequest(
            request.Id, request.Name, request.Description, solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    public async Task<HandEyeCalibrationVersionSnapshot> AddHandEyeVersionAsync(string id, SaveHandEyeVersionFromSessionRequest request, CancellationToken ct)
    {
        if (_handEyeAssets is null) throw new ApiConflictException("Hand-eye calibration asset storage is unavailable.");
        var solved = await SolveHandEyeDocumentAsync(GetHandEyeSession(request.SessionId), ct);
        return await _handEyeAssets.AddVersionAsync(id, new SaveHandEyeCalibrationVersionRequest(
            solved.Model, solved.DocumentJson, solved.SummaryJson, request.Note), ct);
    }

    private async Task<(HandEyeCalibrationModel Model, string DocumentJson, string SummaryJson)> SolveHandEyeDocumentAsync(HandEyeWizardSessionDocument document, CancellationToken ct)
    {
        ValidateHandEyeSamplesJson(document.SamplesJson, allowDraft: false);
        var result = await SolveHandEyeCoreAsync(new CalibrationHandEyeSolveRequest(
            document.SamplesJson, document.Mode, document.MaxTranslationError, document.MaxAngleErrorDeg,
            document.SourceFrame, document.TargetFrame, document.Unit, document.SourceUnit,
            document.AngularWeight, document.VisionScale, document.RobustLoss), ct);
        var summaryJson = JsonSerializer.Serialize(result.Summary, PersistJson);
        var updated = document with { LastSolveSummaryJson = summaryJson };
        _handEyeSessions.Put(updated);
        var model = JsonSerializer.Deserialize<HandEyeCalibrationModel>(result.Outputs["calibrationJson"].Require<string>("calibrationJson"), JsonOpts())
            ?? throw new ApiValidationException("Hand-eye solve returned an empty calibration model.");
        return (model, JsonSerializer.Serialize(updated, PersistJson), summaryJson);
    }

    private static void ValidateHandEyeSamplesJson(string samplesJson, bool allowDraft)
    {
        if (string.IsNullOrWhiteSpace(samplesJson)) throw new ApiValidationException("samplesJson is required.");
        HandEyeRequest request;
        try
        {
            request = JsonSerializer.Deserialize<HandEyeRequest>(samplesJson, JsonOpts()) ?? throw new JsonException("empty payload");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"Invalid samplesJson: {ex.Message}", ex);
        }
        var minimum = allowDraft ? 1 : 4;
        if (request.Samples is null || request.Samples.Count < minimum || request.Samples.Count > 1000)
            throw new ApiValidationException($"samplesJson must contain between {minimum} and 1000 pose pairs.");
        if (request.Samples.Any(s => s is null || !double.IsFinite(s.Robot.X) || !double.IsFinite(s.Robot.Y) || !double.IsFinite(s.Robot.ThetaDeg)
            || !double.IsFinite(s.Vision.X) || !double.IsFinite(s.Vision.Y) || !double.IsFinite(s.Vision.ThetaDeg)))
            throw new ApiValidationException("Every pose pair must have finite robot and vision coordinates.");
    }

    private static int CountHandEyeSamples(string samplesJson)
    {
        try { return JsonSerializer.Deserialize<HandEyeRequest>(samplesJson, JsonOpts())?.Samples?.Count ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static void ValidateHandEyeSettings(string mode, double angularWeight, double visionScale, string robustLoss, double maxTranslationError, double maxAngleErrorDeg)
    {
        // Shared with PlanarHandEyeCalibrationNode; this layer only changes the exception type.
        if (HandEyeSettings.ValidatePlanar(mode, angularWeight, visionScale, robustLoss, maxTranslationError, maxAngleErrorDeg, 0) is { } error)
            throw new ApiValidationException(error);
    }
}
