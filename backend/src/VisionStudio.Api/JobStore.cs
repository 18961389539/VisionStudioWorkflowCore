using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed record JobVersionInfo(
    int Version,
    string WorkflowHash,
    DateTimeOffset CreatedAt,
    string Note,
    bool Published,
    JobVersionValidationInfo? Validation = null,
    int BindingCount = 0);

public sealed record PublicationEvent(
    int Version,
    string Action,
    DateTimeOffset At,
    string? DependencyManifestHash = null);

public sealed record JobDescriptor(
    string Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LatestVersion,
    int? PublishedVersion,
    string? PublishedDependencyManifestHash,
    IReadOnlyList<JobVersionInfo> Versions,
    IReadOnlyList<PublicationEvent> PublicationHistory,
    string? ProductId = null,
    string? RecipeCode = null);

public sealed record JobVersionSnapshot(
    string JobId,
    int Version,
    string WorkflowHash,
    DateTimeOffset CreatedAt,
    string Note,
    WorkflowDefinition Workflow,
    string? BaseWorkflowHash = null,
    WorkflowDefinition? BaseWorkflow = null,
    IReadOnlyDictionary<string, string>? ParameterBindings = null,
    IReadOnlyDictionary<string, JsonElement>? ParameterSnapshot = null);


public sealed record PublishedJobSnapshot(
    JobVersionSnapshot VersionSnapshot,
    RuntimeDependencyManifest DependencyManifest)
{
    public string JobId => VersionSnapshot.JobId;
    public int Version => VersionSnapshot.Version;
    public string WorkflowHash => VersionSnapshot.WorkflowHash;
    public WorkflowDefinition Workflow => VersionSnapshot.Workflow;
}

public sealed record CreateJobRequest(
    string Id,
    string Name,
    string? Description,
    WorkflowDefinition Workflow,
    string? Note = null,
    string? ProductId = null,
    string? RecipeCode = null,
    Dictionary<string, string>? ParameterBindings = null);

public sealed record SaveJobVersionRequest(
    WorkflowDefinition Workflow,
    string? Note = null,
    Dictionary<string, string>? ParameterBindings = null);

/// <summary>
/// SQLite-backed immutable Job/Recipe version store.
/// Version snapshots are transactional rows and are never overwritten.
/// Publish/Rollback only changes the published pointer and appends history.
/// </summary>
public sealed class JobStore
{
    private readonly SqliteMetadataDatabase _db;
    private readonly RecipeParameterStore _parameters;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobStore(SqliteMetadataDatabase db, RecipeParameterStore parameters)
    {
        _db = db;
        _parameters = parameters;
    }
    public JobStore(SqliteMetadataDatabase db) : this(db, new RecipeParameterStore(db)) { }
    public JobStore(IWebHostEnvironment env) : this(new SqliteMetadataDatabase(env)) { }

    public async Task<IReadOnlyList<JobDescriptor>> ListAsync(CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var rows = new List<JobRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version,published_dependency_manifest_hash,product_id,recipe_code FROM jobs ORDER BY updated_at DESC;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadJobRow(reader));
        }

        var result = new List<JobDescriptor>(rows.Count);
        foreach (var row in rows) result.Add(await BuildDescriptorAsync(connection, row, ct));
        return result;
    }

    public async Task<JobDescriptor?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadJobRowAsync(connection, id, null, ct);
        return row is null ? null : await BuildDescriptorAsync(connection, row, ct);
    }

    public async Task<JobDescriptor> CreateAsync(CreateJobRequest request, CancellationToken ct)
    {
        ValidateId(request.Id);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ApiValidationException("Job name is required.");

        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = await CreateSnapshotAsync(request.Id, 1, request.Workflow, request.Note ?? "Initial version", request.ProductId, request.ParameterBindings, now, ct);

            await ExecuteAsync(connection, transaction,
                "INSERT INTO jobs(id,name,description,created_at,updated_at,latest_version,published_version,product_id,recipe_code) VALUES($id,$name,$description,$created,$updated,1,NULL,$product,$recipe);",
                ct,
                ("$id", request.Id), ("$name", request.Name.Trim()), ("$description", request.Description?.Trim() ?? string.Empty),
                ("$created", Iso(now)), ("$updated", Iso(now)),
                ("$product", NullIfWhiteSpace(request.ProductId)), ("$recipe", NullIfWhiteSpace(request.RecipeCode)));

            await InsertVersionAsync(connection, transaction, snapshot, ct);
            await transaction.CommitAsync(ct);
            return (await GetAsync(request.Id, ct))!;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ApiConflictException($"Job '{request.Id}' already exists.", ex);
        }
    }

    public async Task<JobVersionSnapshot> AddVersionAsync(string id, SaveJobVersionRequest request, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var row = await LoadJobRowAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        var next = row.LatestVersion + 1;
        var now = DateTimeOffset.UtcNow;
        var snapshot = await CreateSnapshotAsync(id, next, request.Workflow, request.Note ?? string.Empty, row.ProductId, request.ParameterBindings, now, ct);

        await InsertVersionAsync(connection, transaction, snapshot, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE jobs SET latest_version=$version, updated_at=$updated WHERE id=$id;",
            ct, ("$version", next), ("$updated", Iso(now)), ("$id", id));
        await transaction.CommitAsync(ct);
        return snapshot;
    }

    public async Task<JobVersionSnapshot?> GetVersionAsync(string id, int version, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT workflow_hash,created_at,note,workflow_json,base_workflow_hash,base_workflow_json,parameter_bindings_json,parameter_snapshot_json FROM job_versions WHERE job_id=$id AND version=$version;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(3), _json)
            ?? throw new InvalidOperationException($"Stored workflow for job '{id}' v{version} is invalid.");
        var baseWorkflow = reader.IsDBNull(5) ? workflow : JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(5), _json) ?? workflow;
        var bindings = reader.IsDBNull(6) ? new Dictionary<string,string>() : JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(6), _json) ?? new();
        var parameterSnapshot = reader.IsDBNull(7) ? new Dictionary<string,JsonElement>() : JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(reader.GetString(7), _json) ?? new();
        return new JobVersionSnapshot(id, version, reader.GetString(0), ParseTime(reader.GetString(1)), reader.GetString(2), workflow,
            reader.IsDBNull(4) ? WorkflowFingerprint.Compute(baseWorkflow) : reader.GetString(4), baseWorkflow, bindings, parameterSnapshot);
    }

    public async Task<JobVersionSnapshot> GetPublishedVersionAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadJobRowAsync(connection, id, null, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        if (row.PublishedVersion is null)
            throw new ApiConflictException($"Job '{id}' has no published production version.");
        return await GetVersionAsync(id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published version {row.PublishedVersion} is missing for job '{id}'.");
    }

    public async Task<PublishedJobSnapshot> GetPublishedSnapshotAsync(string id, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        var row = await LoadJobRowAsync(connection, id, null, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");
        if (row.PublishedVersion is null)
            throw new ApiConflictException($"Job '{id}' has no published production version.");
        if (string.IsNullOrWhiteSpace(row.PublishedDependencyManifestHash))
            throw new ApiConflictException($"Job '{id}' was published before RuntimeDependencyManifest was introduced. Re-publish version {row.PublishedVersion} before production use.");

        var version = await GetVersionAsync(id, row.PublishedVersion.Value, ct)
            ?? throw new InvalidDataException($"Published version {row.PublishedVersion} is missing for job '{id}'.");
        var manifest = await LoadManifestAsync(connection, row.PublishedDependencyManifestHash, ct)
            ?? throw new InvalidDataException($"Dependency manifest '{row.PublishedDependencyManifestHash}' is missing for published job '{id}'.");
        return new PublishedJobSnapshot(version, manifest);
    }

    public async Task<JobDescriptor> PublishAsync(string id, int version, string action, RuntimeDependencyManifest dependencyManifest, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var row = await LoadJobRowAsync(connection, id, transaction, ct)
            ?? throw new ApiNotFoundException($"Job '{id}' does not exist.");

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM job_versions WHERE job_id=$id AND version=$version LIMIT 1;";
            exists.Parameters.AddWithValue("$id", id);
            exists.Parameters.AddWithValue("$version", version);
            if (await exists.ExecuteScalarAsync(ct) is null)
                throw new ApiNotFoundException($"Version {version} does not exist for job '{id}'.");
        }

        if (!string.IsNullOrWhiteSpace(row.ProductId))
        {
            await using var validation = connection.CreateCommand();
            validation.Transaction = transaction;
            validation.CommandText = "SELECT accepted,reason FROM job_version_validations WHERE job_id=$id AND version=$version;";
            validation.Parameters.AddWithValue("$id", id);
            validation.Parameters.AddWithValue("$version", version);
            await using var validationReader = await validation.ExecuteReaderAsync(ct);
            if (!await validationReader.ReadAsync(ct))
                throw new ApiConflictException($"Recipe '{id}' V{version} must be linked to an accepted Dataset Validation run before publish.");
            if (validationReader.GetInt32(0) == 0)
                throw new ApiConflictException($"Recipe '{id}' V{version} failed its linked validation policy: {validationReader.GetString(1)}");
        }

        var now = DateTimeOffset.UtcNow;
        await StoreManifestAsync(connection, transaction, dependencyManifest, ct);
        await ExecuteAsync(connection, transaction,
            "UPDATE jobs SET published_version=$version, published_dependency_manifest_hash=$manifest, updated_at=$updated WHERE id=$id;",
            ct, ("$version", version), ("$manifest", dependencyManifest.ManifestHash), ("$updated", Iso(now)), ("$id", id));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO job_publication_history(job_id,version,action,at,dependency_manifest_hash) VALUES($id,$version,$action,$at,$manifest);",
            ct, ("$id", id), ("$version", version), ("$action", action), ("$at", Iso(now)), ("$manifest", dependencyManifest.ManifestHash));
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    private async Task<JobDescriptor> BuildDescriptorAsync(SqliteConnection connection, JobRow row, CancellationToken ct)
    {
        var versions = new List<JobVersionInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT v.version,v.workflow_hash,v.created_at,v.note,
       jv.validation_run_id,jv.dataset_id,jv.accepted,jv.linked_at,jv.reason,jv.summary_json,jv.policy_json,
       COALESCE(v.parameter_bindings_json,'{}')
FROM job_versions v LEFT JOIN job_version_validations jv ON jv.job_id=v.job_id AND jv.version=v.version
WHERE v.job_id=$id ORDER BY v.version;
""";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var version = reader.GetInt32(0);
                JobVersionValidationInfo? validation = null;
                if (!reader.IsDBNull(4))
                {
                    var summary = JsonSerializer.Deserialize<ValidationRunSummary>(reader.GetString(9), _json);
                    var policy = JsonSerializer.Deserialize<ValidationAcceptancePolicy>(reader.GetString(10), _json);
                    validation = new JobVersionValidationInfo(reader.GetString(4), reader.GetString(5), reader.GetInt32(6) != 0, ParseTime(reader.GetString(7)), reader.GetString(8), summary, policy);
                }
                var bindingCount = (JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(11), _json) ?? new()).Count;
                versions.Add(new JobVersionInfo(version, reader.GetString(1), ParseTime(reader.GetString(2)), reader.GetString(3), row.PublishedVersion == version, validation, bindingCount));
            }
        }

        var history = new List<PublicationEvent>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version,action,at,dependency_manifest_hash FROM job_publication_history WHERE job_id=$id ORDER BY seq;";
            command.Parameters.AddWithValue("$id", row.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add(new PublicationEvent(reader.GetInt32(0), reader.GetString(1), ParseTime(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return new JobDescriptor(row.Id, row.Name, row.Description, row.CreatedAt, row.UpdatedAt, row.LatestVersion, row.PublishedVersion, row.PublishedDependencyManifestHash, versions, history, row.ProductId, row.RecipeCode);
    }

    private async Task<JobVersionSnapshot> CreateSnapshotAsync(
        string jobId, int version, WorkflowDefinition baseWorkflow, string note, string? productId,
        IReadOnlyDictionary<string,string>? bindings, DateTimeOffset now, CancellationToken ct)
    {
        var resolved = await _parameters.ResolveForCreateAsync(jobId, productId, baseWorkflow, bindings, ct);
        return new JobVersionSnapshot(jobId, version, resolved.EffectiveWorkflowHash, now, note, resolved.EffectiveWorkflow,
            resolved.BaseWorkflowHash, resolved.BaseWorkflow, resolved.Bindings, resolved.Snapshot);
    }

    private async Task InsertVersionAsync(SqliteConnection connection, SqliteTransaction transaction, JobVersionSnapshot snapshot, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            """
INSERT INTO job_versions(job_id,version,workflow_hash,created_at,note,workflow_json,base_workflow_hash,base_workflow_json,parameter_bindings_json,parameter_snapshot_json)
VALUES($job,$version,$hash,$created,$note,$workflow,$baseHash,$baseWorkflow,$bindings,$snapshot);
""",
            ct,
            ("$job", snapshot.JobId), ("$version", snapshot.Version), ("$hash", snapshot.WorkflowHash),
            ("$created", Iso(snapshot.CreatedAt)), ("$note", snapshot.Note), ("$workflow", JsonSerializer.Serialize(snapshot.Workflow, _json)),
            ("$baseHash", snapshot.BaseWorkflowHash ?? snapshot.WorkflowHash),
            ("$baseWorkflow", JsonSerializer.Serialize(snapshot.BaseWorkflow ?? snapshot.Workflow, _json)),
            ("$bindings", JsonSerializer.Serialize(snapshot.ParameterBindings ?? new Dictionary<string,string>(), _json)),
            ("$snapshot", JsonSerializer.Serialize(snapshot.ParameterSnapshot ?? new Dictionary<string,JsonElement>(), _json)));
    }

    private async Task StoreManifestAsync(SqliteConnection connection, SqliteTransaction transaction, RuntimeDependencyManifest manifest, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO runtime_dependency_manifests(manifest_hash,schema_version,captured_at,manifest_json) VALUES($hash,$schema,$captured,$json);",
            ct, ("$hash", manifest.ManifestHash), ("$schema", manifest.SchemaVersion), ("$captured", Iso(manifest.CapturedAt)), ("$json", JsonSerializer.Serialize(manifest, _json)));
    }

    private async Task<RuntimeDependencyManifest?> LoadManifestAsync(SqliteConnection connection, string hash, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json FROM runtime_dependency_manifests WHERE manifest_hash=$hash;";
        command.Parameters.AddWithValue("$hash", hash);
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<RuntimeDependencyManifest>(json, _json);
    }

    private static async Task<JobRow?> LoadJobRowAsync(SqliteConnection connection, string id, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,description,created_at,updated_at,latest_version,published_version,published_dependency_manifest_hash,product_id,recipe_code FROM jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJobRow(reader) : null;
    }

    private static JobRow ReadJobRow(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Sanitize(id) != id)
            throw new ApiValidationException("Job id may contain only letters, digits, '-' and '_'.");
    }

    private static string Sanitize(string id) => string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record JobRow(string Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int LatestVersion, int? PublishedVersion, string? PublishedDependencyManifestHash, string? ProductId, string? RecipeCode);
}
