using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Engine;
using VisionStudio.Engine.Nodes;
using VisionStudio.Api.Infrastructure;

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
    int HeatmapRows = 5);

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
        var used = request.Points.Where(x => x.Enabled).OrderBy(x => x.Index).ToArray();
        if (used.Length < 4) throw new ApiValidationException("At least 4 enabled calibration points are required.");
        var pairs = used.Select(x => new CalibrationPair(x.ImageX, x.ImageY, x.WorldX, x.WorldY)).ToArray();
        var transform = CalibrationMath.SolveHomography(pairs, request.SourceFrame, request.TargetFrame, request.TargetUnit);
        var residuals = Evaluate(transform, used);
        var rmse = Math.Sqrt(residuals.Select(x => x.Error * x.Error).Average());
        var max = residuals.Max(x => x.Error);

        var verificationPoints = (request.VerificationPoints ?? []).Where(x => x.Enabled).OrderBy(x => x.Index).ToArray();
        var verificationResiduals = Evaluate(transform, verificationPoints);
        double? verificationRmse = verificationResiduals.Count == 0 ? null : Math.Sqrt(verificationResiduals.Select(x => x.Error * x.Error).Average());
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
            var predicted = transform.Apply(new VisionPoint2D(point.ImageX, point.ImageY));
            var dx = predicted.X - point.WorldX;
            var dy = predicted.Y - point.WorldY;
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
                Math.Sqrt(dx * dx + dy * dy),
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

/// <summary>SQLite-backed immutable calibration asset versions. Publishing only moves a pointer.</summary>
public sealed class CalibrationAssetStore
{
    private readonly SqliteMetadataDatabase _db;
    private readonly CalibrationWorkspaceService _solver;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public CalibrationAssetStore(SqliteMetadataDatabase db, CalibrationWorkspaceService solver)
    {
        _db = db;
        _solver = solver;
    }

    public CalibrationAssetStore(IWebHostEnvironment env, CalibrationWorkspaceService solver)
        : this(new SqliteMetadataDatabase(env), solver) { }

    public async Task<IReadOnlyList<CalibrationAssetDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var rows = new List<CalibrationAssetRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM calibration_assets ORDER BY updated_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadAssetRow(reader));
        }

        var result = new List<CalibrationAssetDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(connection, row, ct));
        return result;
    }

    public async Task<CalibrationAssetDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadAssetRowAsync(connection, id, null, ct);
        return row is null ? null : await BuildDescriptorAsync(connection, row, ct);
    }

    public async Task<CalibrationAssetDescriptor> CreateAsync(CreateCalibrationAssetRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Calibration asset name is required.");

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = CreateSnapshot(request.Id, 1, request.Workspace, request.Note ?? "Initial calibration", now);

            await ExecuteAsync(connection, transaction,
                "INSERT INTO calibration_assets(id,name,description,created_at,updated_at,latest_version,published_version) VALUES($id,$name,$description,$created,$updated,1,NULL);",
                ct,
                ("$id", request.Id), ("$name", request.Name.Trim()), ("$description", request.Description?.Trim() ?? string.Empty),
                ("$created", Iso(now)), ("$updated", Iso(now)));
            await InsertVersionAsync(connection, transaction, snapshot, ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Calibration asset '{request.Id}' already exists.", ex);
        }
    }

    public async Task<CalibrationVersionSnapshot> AddVersionAsync(string id, SaveCalibrationVersionRequest request, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetRowAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");
        var version = row.LatestVersion + 1;
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(id, version, request.Workspace, request.Note ?? string.Empty, now);
        await InsertVersionAsync(connection, transaction, snapshot, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE calibration_assets SET latest_version=$version, updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<CalibrationVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_hash,created_at,note,workspace_json,result_json FROM calibration_versions WHERE asset_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var workspace = JsonSerializer.Deserialize<CalibrationWorkspaceRequest>(reader.GetString(3), _json)
            ?? throw new InvalidOperationException($"Stored workspace for calibration '{id}' v{version} is invalid.");
        var result = JsonSerializer.Deserialize<CalibrationWorkspaceResult>(reader.GetString(4), _json)
            ?? throw new InvalidOperationException($"Stored result for calibration '{id}' v{version} is invalid.");
        return new CalibrationVersionSnapshot(id, version, reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), workspace, result);
    }

    public async Task<CalibrationVersionSnapshot> GetPublishedAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadAssetRowAsync(connection, id, null, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");
        if (row.PublishedVersion is null) throw new ApiConflictException($"Calibration asset '{id}' has no published version.");
        return await GetVersionAsync(id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published calibration version {row.PublishedVersion} is missing.");
    }

    public async Task<CalibrationAssetDescriptor> PublishAsync(string id, int version, string action, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadAssetRowAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Calibration asset '{id}' does not exist.");

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM calibration_versions WHERE asset_id=$id AND version=$version LIMIT 1;";
            exists.Parameters.AddWithValue("$id", id);
            exists.Parameters.AddWithValue("$version", version);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new ApiNotFoundException($"Calibration version {version} does not exist for '{id}'.");
        }

        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connection, transaction,
            "UPDATE calibration_assets SET published_version=$version, updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$updated", Iso(now)), ("$id", id));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO calibration_publication_history(asset_id,version,action,at) VALUES($id,$version,$action,$at);",
            ct, ("$id", id), ("$version", version), ("$action", action), ("$at", Iso(now)));
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    private CalibrationVersionSnapshot CreateSnapshot(string id, int version, CalibrationWorkspaceRequest workspace, string note, DateTimeOffset now)
    {
        var result = _solver.Solve(workspace);
        var canonical = JsonSerializer.Serialize(new { workspace, result }, _json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new CalibrationVersionSnapshot(id, version, hash, now, note, workspace, result);
    }

    private async Task InsertVersionAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, CalibrationVersionSnapshot snapshot, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT INTO calibration_versions(asset_id,version,snapshot_hash,created_at,note,workspace_json,result_json) VALUES($id,$version,$hash,$created,$note,$workspace,$result);",
            ct,
            ("$id", snapshot.AssetId), ("$version", snapshot.Version), ("$hash", snapshot.SnapshotHash),
            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note),
            ("$workspace", JsonSerializer.Serialize(snapshot.Workspace, _json)),
            ("$result", JsonSerializer.Serialize(snapshot.Result, _json)));
    }

    private async Task<CalibrationAssetDescriptor> BuildDescriptorAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CalibrationAssetRow row, CancellationToken ct)
    {
        var versions = new List<CalibrationVersionInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version,snapshot_hash,created_at,note FROM calibration_versions WHERE asset_id=$id ORDER BY version;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var version = reader.GetInt32(0);
                versions.Add(new CalibrationVersionInfo(version, reader.GetString(1), ParseTime(reader.GetString(2)), reader.GetString(3), row.PublishedVersion == version));
            }
        }

        var history = new List<CalibrationPublicationEvent>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version,action,at FROM calibration_publication_history WHERE asset_id=$id ORDER BY seq;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add(new CalibrationPublicationEvent(reader.GetInt32(0), reader.GetString(1), ParseTime(reader.GetString(2))));
        }

        return new CalibrationAssetDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, versions, history);
    }

    private static async Task<CalibrationAssetRow?> LoadAssetRowAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string id, Microsoft.Data.Sqlite.SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version FROM calibration_assets WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAssetRow(reader) : null;
    }

    private static CalibrationAssetRow ReadAssetRow(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6));

    private static async Task ExecuteAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id) throw new ApiValidationException("Calibration asset id may contain only letters, digits, '-' and '_'.");
    }

    private sealed record CalibrationAssetRow(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion);
}
