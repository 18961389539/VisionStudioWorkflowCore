using System.Text.Json;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

public sealed record HandEye3DCalibrationVersionInfo(int Version, string SnapshotHash, string ModelHash, DateTimeOffset CreatedAt, string Note, bool Published);
public sealed record HandEye3DCalibrationPublicationEvent(int Version, string Action, DateTimeOffset At);
public sealed record HandEye3DCalibrationAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    IReadOnlyList<HandEye3DCalibrationVersionInfo> Versions,
    IReadOnlyList<HandEye3DCalibrationPublicationEvent> PublicationHistory);
public sealed record HandEye3DCalibrationVersionSnapshot(
    string AssetId,
    int Version,
    string SnapshotHash,
    string ModelHash,
    DateTimeOffset CreatedAt,
    string Note,
    HandEye3DCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson);
public sealed record CreateHandEye3DCalibrationAssetRequest(
    string Id,
    string Name,
    string? Description,
    HandEye3DCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);
public sealed record SaveHandEye3DCalibrationVersionRequest(
    HandEye3DCalibrationModel Model,
    string SourceDocumentJson,
    string SummaryJson,
    string? Note = null);

/// <summary>SQLite-backed immutable SE(3) hand-eye calibration versions with pose-pair evidence.</summary>
public sealed class HandEye3DCalibrationAssetStore(SqliteMetadataDatabase db)
    : CalibrationAssetStoreBase<HandEye3DCalibrationModel>(db, "hand_eye3d_calibration")
{
    protected override string Label => "3D hand-eye calibration";
    protected override string WizardName => "3D Hand-Eye Wizard";
    protected override string DefaultCreateNote => "Initial 3D hand-eye calibration";

    public async Task<IReadOnlyList<HandEye3DCalibrationAssetDescriptor>> ListAsync(CancellationToken ct)
        => (await ListCoreAsync(ct)).Select(ToDescriptor).ToList();

    public async Task<HandEye3DCalibrationAssetDescriptor?> GetAsync(string id, CancellationToken ct)
        => (await GetCoreAsync(id, ct)) is { } data ? ToDescriptor(data) : null;

    public async Task<HandEye3DCalibrationAssetDescriptor> CreateAsync(CreateHandEye3DCalibrationAssetRequest request, CancellationToken ct)
    {
        var data = await CreateCoreAsync(request.Id, request.Name, request.Description, request.Model,
            request.SourceDocumentJson, request.SummaryJson, request.Note, ct);
        return ToDescriptor(await GetCoreAsync(data.AssetId, ct)
            ?? throw new InvalidDataException($"Created 3D hand-eye calibration asset '{data.AssetId}' could not be reloaded."));
    }

    public async Task<HandEye3DCalibrationVersionSnapshot> AddVersionAsync(string id, SaveHandEye3DCalibrationVersionRequest request, CancellationToken ct)
        => ToSnapshot(await AddVersionCoreAsync(id, request.Model, request.SourceDocumentJson, request.SummaryJson, request.Note, ct));

    public async Task<HandEye3DCalibrationVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
        => (await GetVersionCoreAsync(id, version, ct)) is { } data ? ToSnapshot(data) : null;

    public async Task<HandEye3DCalibrationVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
        => ToSnapshot(await GetPublishedCoreAsync(id, ct));

    public async Task<HandEye3DCalibrationVersionSnapshot> RequireModelMatchAsync(string id, int version, string calibrationJson, CancellationToken ct)
        => ToSnapshot(await RequireModelMatchCoreAsync(id, version, calibrationJson, ct));

    public async Task<HandEye3DCalibrationAssetDescriptor> PublishAsync(string id, int version, string action, CancellationToken ct)
        => ToDescriptor(await PublishCoreAsync(id, version, action, ct));

    public static string ComputeModelHash(HandEye3DCalibrationModel model) => CalibrationAssetHash.Model(model);

    protected override void ValidateModel(HandEye3DCalibrationModel model)
    {
        if (model is null
            || model.Mode is not ("eyeInHand" or "eyeToHand")
            || model.Method is not ("TSAI" or "PARK" or "HORAUD" or "ANDREFF" or "DANIILIDIS")
            || string.IsNullOrWhiteSpace(model.SourceFrame) || string.IsNullOrWhiteSpace(model.TargetFrame)
            || string.IsNullOrWhiteSpace(model.Unit)
            || model.OutlierRejection is not ("none" or "iterative")
            || model.Matrix is not { Length: 16 } || model.Matrix.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e9))
            throw new ApiValidationException("3D hand-eye calibration model must contain a valid mode, method, frames, unit, outlier rejection, and a finite 4x4 matrix.");
        var m = model.Matrix;
        if (Math.Abs(m[12]) > 1e-6 || Math.Abs(m[13]) > 1e-6 || Math.Abs(m[14]) > 1e-6 || Math.Abs(m[15] - 1) > 1e-6)
            throw new ApiValidationException("3D hand-eye calibration matrix must have homogeneous last row [0,0,0,1].");
        var error = 0d;
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
        {
            var dot = 0d;
            for (var k = 0; k < 3; k++) dot += m[k * 4 + r] * m[k * 4 + c];
            error = Math.Max(error, Math.Abs(dot - (r == c ? 1 : 0)));
        }
        var determinant = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
        if (error > 1e-3 || Math.Abs(determinant - 1) > 1e-3)
            throw new ApiValidationException("3D hand-eye calibration matrix rotation must be orthonormal with determinant +1.");
    }

    protected override void ValidateSourceDocument(string sourceDocumentJson, HandEye3DCalibrationModel model)
    {
        HandEye3DWizardSessionDocument source;
        try
        {
            source = JsonSerializer.Deserialize<HandEye3DWizardSessionDocument>(sourceDocumentJson, Json)
                ?? throw new JsonException("empty session document");
        }
        catch (JsonException ex)
        {
            throw new ApiValidationException($"sourceDocumentJson must contain a valid 3D hand-eye wizard session document: {ex.Message}", ex);
        }
        if (!string.Equals(source.FormatVersion, HandEye3DWizardSessionDocument.CurrentVersion, StringComparison.Ordinal))
            throw new ApiValidationException($"sourceDocumentJson must use '{HandEye3DWizardSessionDocument.CurrentVersion}'.");
        var resolvedTargetFrame = string.IsNullOrWhiteSpace(source.TargetFrame)
            ? (string.Equals(source.Mode, "eyeInHand", StringComparison.OrdinalIgnoreCase) ? "RobotTool" : "RobotBase")
            : source.TargetFrame;
        if (!string.Equals(source.Mode, model.Mode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Method, model.Method, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.SourceFrame, model.SourceFrame, StringComparison.Ordinal)
            || !string.Equals(resolvedTargetFrame, model.TargetFrame, StringComparison.Ordinal)
            || !string.Equals(source.Unit, model.Unit, StringComparison.Ordinal)
            || !string.Equals(source.OutlierRejection, model.OutlierRejection, StringComparison.OrdinalIgnoreCase))
            throw new ApiValidationException("The 3D hand-eye wizard session settings must match the calibration model being saved.");
    }

    private HandEye3DCalibrationAssetDescriptor ToDescriptor(CalibrationAssetData data)
        => new(data.Id, data.Name, data.Description, data.CreatedAt, data.UpdatedAt, data.LatestVersion, data.PublishedVersion,
            data.Versions.Select(v => new HandEye3DCalibrationVersionInfo(v.Version, v.SnapshotHash, v.ModelHash, v.CreatedAt, v.Note, v.Published)).ToList(),
            data.PublicationHistory.Select(h => new HandEye3DCalibrationPublicationEvent(h.Version, h.Action, h.At)).ToList());

    private HandEye3DCalibrationVersionSnapshot ToSnapshot(CalibrationVersionData data)
        => new(data.AssetId, data.Version, data.SnapshotHash, data.ModelHash, data.CreatedAt, data.Note,
            JsonSerializer.Deserialize<HandEye3DCalibrationModel>(data.ModelJson, Json)
                ?? throw new InvalidDataException($"Stored 3D hand-eye model for '{data.AssetId}' v{data.Version} is invalid."),
            data.SourceDocumentJson, data.SummaryJson);
}
