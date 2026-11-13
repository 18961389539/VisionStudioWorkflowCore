using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

public sealed record HandEyeCalibrationVersionInfo(int Version, string SnapshotHash, string ModelHash, DateTimeOffset CreatedAt, string Note, bool Published);
public sealed record HandEyeCalibrationPublicationEvent(int Version, string Action, DateTimeOffset At);
public sealed record HandEyeCalibrationAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    IReadOnlyList<HandEyeCalibrationVersionInfo> Versions,
    IReadOnlyList<HandEyeCalibrationPublicationEvent> PublicationHistory);
public sealed record HandEyeCalibrationVersionSnapshot(
    string AssetId,
    int Version,
    string SnapshotHash,
    string ModelHash,
    DateTimeOffset CreatedAt,
    string Note,
    HandEyeCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson);
public sealed record CreateHandEyeCalibrationAssetRequest(
    string Id,
    string Name,
    string? Description,
    HandEyeCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);
public sealed record SaveHandEyeCalibrationVersionRequest(
    HandEyeCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);

/// <summary>SQLite-backed immutable planar hand-eye calibration versions with pose-pair evidence.</summary>
public sealed class HandEyeCalibrationAssetStore(SqliteMetadataDatabase db)
    : CalibrationAssetStoreBase<HandEyeCalibrationModel>(db, "hand_eye_calibration")
{
    protected override string Label => "Hand-eye calibration";
    protected override string WizardName => "Hand-Eye Wizard";
    protected override string DefaultCreateNote => "Initial hand-eye calibration";

    public async Task<IReadOnlyList<HandEyeCalibrationAssetDescriptor>> ListAsync(CancellationToken ct)
        => (await ListCoreAsync(ct)).Select(ToDescriptor).ToList();

    public async Task<HandEyeCalibrationAssetDescriptor?> GetAsync(string id, CancellationToken ct)
        => (await GetCoreAsync(id, ct)) is { } data ? ToDescriptor(data) : null;

    public async Task<HandEyeCalibrationAssetDescriptor> CreateAsync(CreateHandEyeCalibrationAssetRequest request, CancellationToken ct)
    {
        var data = await CreateCoreAsync(request.Id, request.Name, request.Description, request.Model,
            request.SourceDocumentJson, request.SummaryJson, request.Note, ct);
        return ToDescriptor(await GetCoreAsync(data.AssetId, ct)
            ?? throw new InvalidDataException($"Created hand-eye calibration asset '{data.AssetId}' could not be reloaded."));
    }

    public async Task<HandEyeCalibrationVersionSnapshot> AddVersionAsync(string id, SaveHandEyeCalibrationVersionRequest request, CancellationToken ct)
        => ToSnapshot(await AddVersionCoreAsync(id, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note, ct));

    public async Task<HandEyeCalibrationVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
        => (await GetVersionCoreAsync(id, version, ct)) is { } data ? ToSnapshot(data) : null;

    public async Task<HandEyeCalibrationVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
        => ToSnapshot(await GetPublishedCoreAsync(id, ct));

    public async Task<HandEyeCalibrationVersionSnapshot> RequireModelMatchAsync(string id, int version, string calibrationJson, CancellationToken ct)
        => ToSnapshot(await RequireModelMatchCoreAsync(id, version, calibrationJson, ct));

    public async Task<HandEyeCalibrationAssetDescriptor> PublishAsync(string id, int version, string action, CancellationToken ct)
        => ToDescriptor(await PublishCoreAsync(id, version, action, ct));

    public static string ComputeModelHash(HandEyeCalibrationModel model) => CalibrationAssetHash.Model(model);

    protected override void ValidateModel(HandEyeCalibrationModel model)
    {
        if (model is null
            || model.Mode is not ("eyeInHand" or "eyeToHand")
            || string.IsNullOrWhiteSpace(model.SourceFrame) || string.IsNullOrWhiteSpace(model.TargetFrame)
            || string.IsNullOrWhiteSpace(model.Unit) || string.IsNullOrWhiteSpace(model.SourceUnit)
            || !double.IsFinite(model.X) || !double.IsFinite(model.Y) || !double.IsFinite(model.ThetaDeg)
            || Math.Abs(model.X) > 1e9 || Math.Abs(model.Y) > 1e9
            || !double.IsFinite(model.AngularWeight) || model.AngularWeight is < 0.001 or > 1e6
            || !double.IsFinite(model.VisionScale) || model.VisionScale is < 1e-6 or > 1e6
            || model.RobustLoss is not ("none" or "tukey"))
            throw new ApiValidationException("Hand-eye calibration model must contain a valid mode, frames, units, finite pose, and in-range solver settings.");
    }

    protected override void ValidateSourceDocument(string sourceDocumentJson, HandEyeCalibrationModel model)
    {
        HandEyeWizardSessionDocument source;
        try
        {
            source = JsonSerializer.Deserialize<HandEyeWizardSessionDocument>(sourceDocumentJson, Json)
                ?? throw new JsonException("empty session document");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"sourceDocumentJson must contain a valid hand-eye wizard session document: {ex.Message}", ex);
        }
        if (!string.Equals(source.FormatVersion, HandEyeWizardSessionDocument.CurrentVersion, StringComparison.Ordinal))
            throw new ApiValidationException($"sourceDocumentJson must use '{HandEyeWizardSessionDocument.CurrentVersion}'.");
        var resolvedTargetFrame = string.IsNullOrWhiteSpace(source.TargetFrame)
            ? (string.Equals(source.Mode, "eyeInHand", StringComparison.OrdinalIgnoreCase) ? "RobotTool" : "RobotBase")
            : source.TargetFrame;
        if (!string.Equals(source.Mode, model.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.SourceFrame, model.SourceFrame, StringComparison.Ordinal)
            || !string.Equals(resolvedTargetFrame, model.TargetFrame, StringComparison.Ordinal)
            || !string.Equals(source.Unit, model.Unit, StringComparison.Ordinal)
            || !string.Equals(source.SourceUnit, model.SourceUnit, StringComparison.Ordinal)
            || source.AngularWeight != model.AngularWeight
            || source.VisionScale != model.VisionScale
            || !string.Equals(source.RobustLoss, model.RobustLoss, StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("The hand-eye wizard session settings must match the calibration model being saved.");
    }

    private HandEyeCalibrationAssetDescriptor ToDescriptor(CalibrationAssetData data)
        => new(data.Id, data.Name, data.Description, data.CreatedAt, data.UpdatedAt, data.LatestVersion, data.PublishedVersion,
            data.Versions.Select(v => new HandEyeCalibrationVersionInfo(v.Version, v.SnapshotHash, v.ModelHash, v.CreatedAt, v.Note, v.Published)).ToList(),
            data.PublicationHistory.Select(h => new HandEyeCalibrationPublicationEvent(h.Version, h.Action, h.At)).ToList());

    private HandEyeCalibrationVersionSnapshot ToSnapshot(CalibrationVersionData data)
        => new(data.AssetId, data.Version, data.SnapshotHash, data.ModelHash, data.CreatedAt, data.Note,
            JsonSerializer.Deserialize<HandEyeCalibrationModel>(data.ModelJson, Json)
                ?? throw new InvalidDataException($"Stored hand-eye model for '{data.AssetId}' v{data.Version} is invalid."),
            data.SourceDocumentJson, data.SummaryJson);
}
