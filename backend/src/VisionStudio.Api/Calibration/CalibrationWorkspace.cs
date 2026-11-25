using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed record CalibrationWorkspacePoint(
    int Index,
    double ImageX,
    double ImageY,
    double WorldX,
    double WorldY,
    bool Enabled = true,
    string Source = "Manual");

public sealed record CalibrationResidual(
    int Index,
    double ImageX,
    double ImageY,
    double WorldX,
    double WorldY,
    double PredictedX,
    double PredictedY,
    double Dx,
    double Dy,
    double Error,
    string Source);

public sealed record CalibrationHeatCell(int Column, int Row, double X, double Y, double Error);

public sealed record CalibrationWorkspaceRequest(
    string SourceFrame,
    string TargetFrame,
    string TargetUnit,
    IReadOnlyList<CalibrationWorkspacePoint> Points,
    IReadOnlyList<CalibrationWorkspacePoint>? VerificationPoints = null,
    int HeatmapColumns = 5,
    int HeatmapRows = 5,
    double MaxRmse = 0,
    double MaxPointError = 0);

public sealed record CalibrationWorkspaceResult(
    VisionTransform2D Transform,
    double Rmse,
    double MaxError,
    IReadOnlyList<CalibrationResidual> Residuals,
    double? VerificationRmse,
    double? VerificationMaxError,
    IReadOnlyList<CalibrationResidual> VerificationResiduals,
    IReadOnlyList<CalibrationHeatCell> Heatmap,
    int UsedPointCount,
    int VerificationPointCount);

public sealed record CalibrationGridCaptureRequest(
    string ProviderId = "virtual",
    int Columns = 3,
    int Rows = 3,
    double OriginX = 0,
    double OriginY = 0,
    double StepX = 22,
    double StepY = 14);

public sealed record CalibrationProviderDescriptor(string Id, string Name, string Description, bool Simulated);

public interface ICalibrationPointProvider
{
    string Id { get; }
    CalibrationProviderDescriptor Descriptor { get; }
    ValueTask<IReadOnlyList<CalibrationWorkspacePoint>> CaptureGridAsync(CalibrationGridCaptureRequest request, CancellationToken ct);
}

/// <summary>
/// Deterministic simulated 2D calibration source. It represents the contract that a future
/// ABB + camera sampler will implement: move to known world XY, observe image XY, return pairs.
/// </summary>
public sealed class VirtualCalibrationPointProvider : ICalibrationPointProvider
{
    public string Id => "virtual";
    public CalibrationProviderDescriptor Descriptor => new(
        Id,
        "Virtual 9-Point Provider",
        "Simulates robot/world targets and camera image observations with small deterministic image noise.",
        true);

    public ValueTask<IReadOnlyList<CalibrationWorkspacePoint>> CaptureGridAsync(CalibrationGridCaptureRequest request, CancellationToken ct)
    {
        if (request.Columns < 2 || request.Rows < 2) throw new ApiValidationException("Calibration grid requires at least 2x2 points.");
        var result = new List<CalibrationWorkspacePoint>();
        var index = 1;
        for (var row = 0; row < request.Rows; row++)
        {
            for (var col = 0; col < request.Columns; col++)
            {
                ct.ThrowIfCancellationRequested();
                var wx = request.OriginX + col * request.StepX;
                var wy = request.OriginY + row * request.StepY;
                // Synthetic inverse camera mapping used only by the MVP provider.
                var idealX = 100.0 + wx * 10.0;
                var idealY = 100.0 + wy * 10.0;
                var noiseX = Math.Sin(index * 1.71) * 0.18;
                var noiseY = Math.Cos(index * 1.13) * 0.16;
                result.Add(new CalibrationWorkspacePoint(index++, idealX + noiseX, idealY + noiseY, wx, wy, true, "VirtualAuto"));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<CalibrationWorkspacePoint>>(result);
    }
}

public sealed class CalibrationWorkspaceService
{
    public CalibrationWorkspaceResult Solve(CalibrationWorkspaceRequest request)
    {
        if (request.Points.Count > 10000) throw new ApiValidationException("At most 10000 calibration points are allowed.");
        if ((request.VerificationPoints?.Count ?? 0) > 10000) throw new ApiValidationException("At most 10000 verification points are allowed.");
        var sourceFrame = CoordinateValidation.RequireFrame(request.SourceFrame, "sourceFrame");
        var targetFrame = CoordinateValidation.RequireFrame(request.TargetFrame, "targetFrame");
        var targetUnit = CoordinateValidation.RequireUnit(request.TargetUnit, "targetUnit", "mm", "cm", "m", "px");
        var used = request.Points.Where(x => x.Enabled).OrderBy(x => x.Index).ToArray();
        if (used.Length < 4) throw new ApiValidationException("At least 4 enabled calibration points are required.");
        var pairs = used.Select(x => new CalibrationPair(x.ImageX, x.ImageY, x.WorldX, x.WorldY)).ToArray();
        var transform = CalibrationMath.SolveHomography(pairs, sourceFrame, targetFrame, targetUnit);
        var residuals = Evaluate(transform, used);
        var rmse = CalibrationMath.RootMeanSquare(residuals.Select(x => x.Error));
        var max = residuals.Max(x => x.Error);
        if (!double.IsFinite(request.MaxRmse) || request.MaxRmse < 0 || !double.IsFinite(request.MaxPointError) || request.MaxPointError < 0)
            throw new ApiValidationException("Calibration quality limits must be finite and >= 0 (0 disables each gate).");
        if (request.MaxRmse > 0 && rmse > request.MaxRmse)
            throw new ApiValidationException($"Calibration RMSE {rmse:F6} {targetUnit} exceeds maxRmse {request.MaxRmse:F6} {targetUnit}.");
        if (request.MaxPointError > 0 && max > request.MaxPointError)
        {
            var worst = residuals.MaxBy(point => point.Error)!;
            throw new ApiValidationException($"Calibration point {worst.Index} error {max:F6} {targetUnit} exceeds maxPointError {request.MaxPointError:F6} {targetUnit}.");
        }

        var verificationPoints = (request.VerificationPoints ?? []).Where(x => x.Enabled).OrderBy(x => x.Index).ToArray();
        var verificationResiduals = Evaluate(transform, verificationPoints);
        double? verificationRmse = verificationResiduals.Count == 0 ? null : CalibrationMath.RootMeanSquare(verificationResiduals.Select(x => x.Error));
        double? verificationMax = verificationResiduals.Count == 0 ? null : verificationResiduals.Max(x => x.Error);

        var heatmapSource = verificationResiduals.Count > 0 ? verificationResiduals : residuals;
        var heatmap = BuildHeatmap(heatmapSource, Math.Clamp(request.HeatmapColumns, 2, 20), Math.Clamp(request.HeatmapRows, 2, 20));
        return new CalibrationWorkspaceResult(transform, rmse, max, residuals, verificationRmse, verificationMax, verificationResiduals, heatmap, used.Length, verificationPoints.Length);
    }

    private static IReadOnlyList<CalibrationResidual> Evaluate(VisionTransform2D transform, IReadOnlyList<CalibrationWorkspacePoint> points)
    {
        var result = new List<CalibrationResidual>(points.Count);
        foreach (var point in points)
        {
            if (!double.IsFinite(point.ImageX) || !double.IsFinite(point.ImageY) || !double.IsFinite(point.WorldX) || !double.IsFinite(point.WorldY))
                throw new ApiValidationException($"Calibration point {point.Index} must contain only finite coordinates.");
            var predicted = transform.Apply(new VisionPoint2D(point.ImageX, point.ImageY));
            var dx = predicted.X - point.WorldX;
            var dy = predicted.Y - point.WorldY;
            if (!double.IsFinite(dx) || !double.IsFinite(dy)) throw new ApiValidationException($"Calibration point {point.Index} produced a non-finite residual.");
            result.Add(new CalibrationResidual(
                point.Index,
                point.ImageX,
                point.ImageY,
                point.WorldX,
                point.WorldY,
                predicted.X,
                predicted.Y,
                dx,
                dy,
                CalibrationMath.Distance(dx, dy),
                point.Source));
        }
        return result;
    }

    private static IReadOnlyList<CalibrationHeatCell> BuildHeatmap(IReadOnlyList<CalibrationResidual> residuals, int columns, int rows)
    {
        if (residuals.Count == 0) return [];
        var minX = residuals.Min(x => x.WorldX); var maxX = residuals.Max(x => x.WorldX);
        var minY = residuals.Min(x => x.WorldY); var maxY = residuals.Max(x => x.WorldY);
        if (Math.Abs(maxX - minX) < 1e-9) maxX = minX + 1;
        if (Math.Abs(maxY - minY) < 1e-9) maxY = minY + 1;

        var cells = new List<CalibrationHeatCell>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            var y = minY + (maxY - minY) * row / (rows - 1.0);
            for (var col = 0; col < columns; col++)
            {
                var x = minX + (maxX - minX) * col / (columns - 1.0);
                var weighted = 0.0; var weightSum = 0.0;
                foreach (var residual in residuals)
                {
                    var dx = x - residual.WorldX; var dy = y - residual.WorldY;
                    var d2 = dx * dx + dy * dy;
                    var weight = 1.0 / Math.Max(d2, 1e-8);
                    weighted += weight * residual.Error;
                    weightSum += weight;
                }
                cells.Add(new CalibrationHeatCell(col, row, x, y, weighted / weightSum));
            }
        }
        return cells;
    }
}

public sealed record CalibrationVersionInfo(int Version, string SnapshotHash, DateTimeOffset CreatedAt, string Note, bool Published);
public sealed record CalibrationPublicationEvent(int Version, string Action, DateTimeOffset At);
public sealed record CalibrationAssetDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    IReadOnlyList<CalibrationVersionInfo> Versions,
    IReadOnlyList<CalibrationPublicationEvent> PublicationHistory);
public sealed record CalibrationVersionSnapshot(
    string AssetId,
    int Version,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    string Note,
    CalibrationWorkspaceRequest Workspace,
    CalibrationWorkspaceResult Result);
public sealed record CreateCalibrationAssetRequest(string Id, string Name, string? Description, CalibrationWorkspaceRequest Workspace, string? Note = null);
public sealed record SaveCalibrationVersionRequest(CalibrationWorkspaceRequest Workspace, string? Note = null);

/// <summary>SQLite-backed immutable calibration asset versions (calibration_versions). Publishing only moves a pointer.</summary>
public sealed class CalibrationAssetStore
{
    private readonly IVisionStudioDbFactory _dbFactory;
    private readonly CalibrationWorkspaceService _solver;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public CalibrationAssetStore(IVisionStudioDbFactory dbFactory, CalibrationWorkspaceService solver)
    {
        _dbFactory = dbFactory;
        _solver = solver;
    }

    public CalibrationAssetStore(SqliteMetadataDatabase db, CalibrationWorkspaceService solver)
        : this(new VisionStudioDbFactory(db), solver) { }

    public CalibrationAssetStore(IWebHostEnvironment env, CalibrationWorkspaceService solver)
        : this(new SqliteMetadataDatabase(env), solver) { }

    public async Task<IReadOnlyList<CalibrationAssetDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.CalibrationAssets.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var result = new List<CalibrationAssetDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(db, row, ct));
        return result;
    }

    public async Task<CalibrationAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CalibrationAssets.FirstOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? null : await BuildDescriptorAsync(db, row, ct);
    }

    public async Task<CalibrationAssetDescriptor> CreateAsync(CreateCalibrationAssetRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Calibration asset name is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = CreateSnapshot(request.Id, 1, request.Workspace, request.Note ?? "Initial calibration", now);

            db.CalibrationAssets.Add(new CalibrationAssetEntity
            {
                Id = request.Id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
                LatestVersion = 1,
                PublishedVersion = null
            });
            db.CalibrationVersions.Add(ToVersionEntity(snapshot));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqliteEx)
        {
            throw new ApiConflictException($"Calibration asset '{request.Id}' already exists.", sqliteEx);
        }
    }

    public async Task<CalibrationVersionSnapshot> AddVersionAsync(string id, SaveCalibrationVersionRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.CalibrationAssets.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");
        var version = row.LatestVersion + 1;
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(id, version, request.Workspace, request.Note ?? string.Empty, now);
        db.CalibrationVersions.Add(ToVersionEntity(snapshot));
        row.LatestVersion = version;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<CalibrationVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ReadVersionAsync(db, id, version, ct);
    }

    private async Task<CalibrationVersionSnapshot?> ReadVersionAsync(VisionStudioDbContext db, string id, int version, CancellationToken ct)
    {
        var entity = await db.CalibrationVersions.FirstOrDefaultAsync(x => x.AssetId == id && x.Version == version, ct);
        if (entity is null) return null;
        var workspace = JsonSerializer.Deserialize<CalibrationWorkspaceRequest>(entity.WorkspaceJson, _json)
            ?? throw new InvalidOperationException($"Stored workspace for calibration '{id}' v{version} is invalid.");
        var result = JsonSerializer.Deserialize<CalibrationWorkspaceResult>(entity.ResultJson, _json)
            ?? throw new InvalidOperationException($"Stored result for calibration '{id}' v{version} is invalid.");
        var actualHash = ComputeSnapshotHash(workspace, result);
        if (!string.Equals(entity.SnapshotHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Calibration '{id}' v{version} failed snapshot integrity validation: stored hash {entity.SnapshotHash}, computed hash {actualHash}.");
        return new CalibrationVersionSnapshot(id, version, entity.SnapshotHash, entity.CreatedAt, entity.Note, workspace, result);
    }

    public async Task<CalibrationVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CalibrationAssets.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"Calibration asset '{id}' has no published version.");
        return await ReadVersionAsync(db, id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published calibration version {row.PublishedVersion} is missing.");
    }

    public async Task<CalibrationAssetDescriptor> PublishAsync(string id, int version, string action, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await _dbFactory.BeginImmediateTransactionAsync(db, ct);
        var row = await db.CalibrationAssets.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");

        if (await ReadVersionAsync(db, id, version, ct) is null)
            throw new ApiNotFoundException($"Calibration version {version} does not exist for '{id}'.");

        var now = DateTimeOffset.UtcNow;
        row.PublishedVersion = version;
        row.UpdatedAt = now;
        db.CalibrationPublicationHistory.Add(new CalibrationPublicationHistoryEntity
        {
            AssetId = id,
            Version = version,
            Action = action,
            At = now
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    private CalibrationVersionSnapshot CreateSnapshot(string id, int version, CalibrationWorkspaceRequest workspace, string note, DateTimeOffset now)
    {
        var result = _solver.Solve(workspace);
        var hash = ComputeSnapshotHash(workspace, result);
        return new CalibrationVersionSnapshot(id, version, hash, now, note, workspace, result);
    }

    private string ComputeSnapshotHash(CalibrationWorkspaceRequest workspace, CalibrationWorkspaceResult result)
    {
        var canonical = JsonSerializer.Serialize(new { workspace, result }, _json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private CalibrationVersionEntity ToVersionEntity(CalibrationVersionSnapshot snapshot) => new()
    {
        AssetId = snapshot.AssetId,
        Version = snapshot.Version,
        SnapshotHash = snapshot.SnapshotHash,
        CreatedAt = snapshot.CreatedAt,
        Note = snapshot.Note,
        WorkspaceJson = JsonSerializer.Serialize(snapshot.Workspace, _json),
        ResultJson = JsonSerializer.Serialize(snapshot.Result, _json)
    };

    private async Task<CalibrationAssetDescriptor> BuildDescriptorAsync(VisionStudioDbContext db, CalibrationAssetEntity row, CancellationToken ct)
    {
        var versions = await db.CalibrationVersions
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Version)
            .Select(x => new CalibrationVersionInfo(x.Version, x.SnapshotHash, x.CreatedAt, x.Note, row.PublishedVersion == x.Version))
            .ToListAsync(ct);

        var history = await db.CalibrationPublicationHistory
            .Where(x => x.AssetId == row.Id)
            .OrderBy(x => x.Seq)
            .Select(x => new CalibrationPublicationEvent(x.Version, x.Action, x.At))
            .ToListAsync(ct);

        return new CalibrationAssetDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id) throw new ApiValidationException("Calibration asset id may contain only letters, digits, '-' and '_'.");
    }
}
