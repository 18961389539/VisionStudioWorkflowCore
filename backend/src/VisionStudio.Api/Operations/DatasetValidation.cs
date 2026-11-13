using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Engine;

namespace VisionStudio.Api;

public sealed record ValidationDatasetSummary(string Id, string Name, string Description, string? TopologyHash, int ItemCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ValidationDatasetItem(
    string ItemId,
    string DatasetId,
    string SourceKind,
    string SourceRef,
    string? SourceRunId,
    string ExpectedDisposition,
    string? Note,
    int SortOrder,
    DateTimeOffset AddedAt,
    string SourceDisposition,
    string DisplayName,
    bool ReplayReady);
public sealed record ValidationDatasetDetail(ValidationDatasetSummary Dataset, IReadOnlyList<ValidationDatasetItem> Items);
public sealed record CreateValidationDatasetRequest(string Name, string? Description = null);
public sealed record ValidationDatasetItemInput(string SourceKind, string SourceRef, string ExpectedDisposition, string? Note = null);
public sealed record AddValidationDatasetItemsRequest(IReadOnlyList<ValidationDatasetItemInput> Items);
public sealed record UpdateValidationDatasetItemRequest(string ExpectedDisposition, string? Note = null);
public sealed record ValidationRunRequest(string? SourceWorkflowRunId = null, WorkflowDefinition? Workflow = null, int? MaxItems = null);

public sealed record ValidationRunSummary(
    int Total, int Completed, int TrueOk, int TrueNg, int FalseOk, int FalseNg, int Errors,
    double Accuracy, double FalseOkRate, double FalseNgRate,
    double P50DurationMs, double P95DurationMs, double P99DurationMs, double MaxDurationMs);

public sealed record ValidationResultRecord(
    string RunId,
    string ItemId,
    string SourceKind,
    string SourceRef,
    string ExpectedDisposition,
    string ActualDisposition,
    string Classification,
    string? ReplayRunId,
    bool Success,
    double DurationMs,
    IReadOnlyList<string> FailedNodeIds,
    string? Error,
    DateTimeOffset CompletedAt);

public sealed record ValidationRunRecord(
    string RunId,
    string DatasetId,
    string Status,
    string SourceWorkflowRunId,
    string WorkflowHash,
    int RequestedCount,
    int CompletedCount,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool CancelRequested,
    string? Error,
    ValidationRunSummary? Summary,
    IReadOnlyList<ValidationResultRecord>? Results = null);

public sealed class DatasetValidationStore(SqliteMetadataDatabase db, TraceabilityStore traces, MediaLibraryService media)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ValidationDatasetSummary>> ListDatasetsAsync(CancellationToken ct)
    {
        var result = new List<ValidationDatasetSummary>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT d.id,d.name,d.description,d.topology_hash,d.created_at,d.updated_at,COUNT(i.item_id)
FROM validation_datasets d LEFT JOIN validation_dataset_items i ON i.dataset_id=d.id
GROUP BY d.id,d.name,d.description,d.topology_hash,d.created_at,d.updated_at ORDER BY d.updated_at DESC;
""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ValidationDatasetSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), DbString(reader, 3), Convert.ToInt32(reader.GetInt64(6)), DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5))));
        return result;
    }

    public async Task<ValidationDatasetDetail> CreateDatasetAsync(CreateValidationDatasetRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120) throw new ApiValidationException("Dataset name must be 1-120 characters.");
        var id = $"dataset-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO validation_datasets(id,name,description,topology_hash,created_at,updated_at) VALUES($id,$name,$description,NULL,$created,$updated);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", request.Description?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$created", Iso(now));
        command.Parameters.AddWithValue("$updated", Iso(now));
        await command.ExecuteNonQueryAsync(ct);
        return await GetDatasetAsync(id, ct);
    }

    public async Task<ValidationDatasetDetail> GetDatasetAsync(string datasetId, CancellationToken ct)
    {
        ValidationDatasetSummary? dataset = null;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
SELECT d.id,d.name,d.description,d.topology_hash,d.created_at,d.updated_at,COUNT(i.item_id)
FROM validation_datasets d LEFT JOIN validation_dataset_items i ON i.dataset_id=d.id
WHERE d.id=$id GROUP BY d.id,d.name,d.description,d.topology_hash,d.created_at,d.updated_at;
""";
            command.Parameters.AddWithValue("$id", datasetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                dataset = new ValidationDatasetSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), DbString(reader, 3), Convert.ToInt32(reader.GetInt64(6)), DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)));
        }
        if (dataset is null) throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");

        var raw = new List<(string ItemId, string Kind, string Ref, string Expected, string? Note, int Sort, DateTimeOffset Added)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT item_id,source_kind,source_ref,expected_disposition,note,sort_order,added_at FROM validation_dataset_items WHERE dataset_id=$id ORDER BY sort_order,item_id;";
            command.Parameters.AddWithValue("$id", datasetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) raw.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DbString(reader, 4), reader.GetInt32(5), DateTimeOffset.Parse(reader.GetString(6))));
        }

        var items = new List<ValidationDatasetItem>(raw.Count);
        foreach (var row in raw)
        {
            if (row.Kind == "TRACE")
            {
                var trace = await traces.GetAsync(row.Ref, ct);
                items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.Kind, row.Ref, row.Ref, row.Expected, row.Note, row.Sort, row.Added, trace?.Disposition ?? "MISSING", trace?.WorkflowName ?? "Missing trace", trace?.HasReplayInput ?? false));
            }
            else
            {
                try
                {
                    var path = media.ResolveItemPath(row.Ref);
                    items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.Kind, row.Ref, null, row.Expected, row.Note, row.Sort, row.Added, row.Expected, Path.GetFileName(path), true));
                }
                catch
                {
                    items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.Kind, row.Ref, null, row.Expected, row.Note, row.Sort, row.Added, "MISSING", row.Ref, false));
                }
            }
        }
        return new ValidationDatasetDetail(dataset, items);
    }

    public async Task<ValidationDatasetDetail> AddItemsAsync(string datasetId, AddValidationDatasetItemsRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0) throw new ApiValidationException("At least one dataset item is required.");
        var detail = await GetDatasetAsync(datasetId, ct);
        var topology = detail.Dataset.TopologyHash;
        var nextSort = detail.Items.Count == 0 ? 0 : detail.Items.Max(x => x.SortOrder) + 1;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);

        foreach (var input in request.Items)
        {
            var kind = NormalizeKind(input.SourceKind);
            var expected = NormalizeExpected(input.ExpectedDisposition);
            var sourceRef = (input.SourceRef ?? string.Empty).Trim();
            if (sourceRef.Length == 0) throw new ApiValidationException("Dataset sourceRef is required.");
            if (kind == "TRACE")
            {
                var trace = await traces.GetAsync(sourceRef, ct) ?? throw new ApiNotFoundException($"Trace '{sourceRef}' was not found.");
                if (!trace.HasReplayInput) throw new ApiConflictException($"Trace '{sourceRef}' has no persisted replay input.");
                var workflow = await traces.GetWorkflowSnapshotAsync(sourceRef, ct) ?? throw new ApiConflictException($"Trace '{sourceRef}' has no workflow snapshot.");
                if (workflow.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase))) throw new ApiConflictException("Synchronized FrameSet traces are not supported by V0.48 batch validation.");
                var itemTopology = TopologyHash(workflow);
                if (topology is null) topology = itemTopology;
                if (!string.Equals(topology, itemTopology, StringComparison.OrdinalIgnoreCase)) throw new ApiConflictException($"Trace '{sourceRef}' has a different workflow topology from this dataset.");
            }
            else
            {
                _ = media.ResolveItemPath(sourceRef); // validates Media Library ownership and supported image type
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
INSERT OR IGNORE INTO validation_dataset_items(dataset_id,item_id,source_kind,source_ref,expected_disposition,note,sort_order,added_at)
VALUES($dataset,$item,$kind,$ref,$expected,$note,$sort,$added);
""";
            insert.Parameters.AddWithValue("$dataset", datasetId);
            insert.Parameters.AddWithValue("$item", $"item-{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$ref", sourceRef);
            insert.Parameters.AddWithValue("$expected", expected);
            insert.Parameters.AddWithValue("$note", (object?)input.Note?.Trim() ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sort", nextSort++);
            insert.Parameters.AddWithValue("$added", Iso(DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE validation_datasets SET topology_hash=$topology,updated_at=$updated WHERE id=$id;";
            update.Parameters.AddWithValue("$topology", (object?)topology ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$id", datasetId);
            if (await update.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");
        }
        await transaction.CommitAsync(ct);
        return await GetDatasetAsync(datasetId, ct);
    }

    public async Task EnsureTopologyAsync(string datasetId, string topologyHash, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE validation_datasets SET topology_hash=COALESCE(topology_hash,$topology),updated_at=$updated WHERE id=$id AND (topology_hash IS NULL OR topology_hash=$topology);";
        command.Parameters.AddWithValue("$topology", topologyHash);
        command.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", datasetId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiConflictException("Candidate workflow topology does not match the dataset topology.");
    }

    public async Task<ValidationDatasetDetail> UpdateItemAsync(string datasetId, string itemId, UpdateValidationDatasetItemRequest request, CancellationToken ct)
    {
        var expected = NormalizeExpected(request.ExpectedDisposition);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE validation_dataset_items SET expected_disposition=$expected,note=$note WHERE dataset_id=$dataset AND item_id=$item;";
        command.Parameters.AddWithValue("$expected", expected);
        command.Parameters.AddWithValue("$note", (object?)request.Note?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$dataset", datasetId);
        command.Parameters.AddWithValue("$item", itemId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Dataset item '{itemId}' was not found.");
        await TouchAsync(connection, datasetId, ct);
        return await GetDatasetAsync(datasetId, ct);
    }

    public async Task DeleteItemAsync(string datasetId, string itemId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM validation_dataset_items WHERE dataset_id=$dataset AND item_id=$item;";
        command.Parameters.AddWithValue("$dataset", datasetId);
        command.Parameters.AddWithValue("$item", itemId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Dataset item '{itemId}' was not found.");
        await TouchAsync(connection, datasetId, ct);
    }

    public async Task DeleteDatasetAsync(string datasetId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM validation_runs WHERE dataset_id=$id AND status='Running';";
            check.Parameters.AddWithValue("$id", datasetId);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0) throw new ApiConflictException("Cannot delete a dataset while validation is running.");
        }
        await using (var linked = connection.CreateCommand())
        {
            linked.CommandText = "SELECT COUNT(*) FROM job_version_validations WHERE dataset_id=$id;";
            linked.Parameters.AddWithValue("$id", datasetId);
            if (Convert.ToInt32(await linked.ExecuteScalarAsync(ct)) > 0)
                throw new ApiConflictException("Cannot delete a dataset referenced by Product/Recipe validation evidence.");
        }
        await using (var investigation = connection.CreateCommand())
        {
            investigation.CommandText = "SELECT COUNT(*) FROM investigation_case_verifications WHERE dataset_id=$id;";
            investigation.Parameters.AddWithValue("$id", datasetId);
            if (Convert.ToInt32(await investigation.ExecuteScalarAsync(ct)) > 0)
                throw new ApiConflictException("Cannot delete a dataset referenced by Investigation verification evidence.");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM validation_datasets WHERE id=$id;";
        command.Parameters.AddWithValue("$id", datasetId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");
    }

    public async Task<ValidationRunRecord> CreateRunAsync(string datasetId, string sourceWorkflowRunId, WorkflowDefinition candidate, int requestedCount, CancellationToken ct)
    {
        var runId = $"validation-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var hash = WorkflowFingerprint.Compute(candidate);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO validation_runs(run_id,dataset_id,status,source_workflow_run_id,workflow_hash,candidate_workflow_json,requested_count,completed_count,started_at,completed_at,cancel_requested,error,summary_json)
VALUES($run,$dataset,'Running',$source,$hash,$workflow,$requested,0,$started,NULL,0,NULL,NULL);
""";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$dataset", datasetId);
        command.Parameters.AddWithValue("$source", sourceWorkflowRunId);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$workflow", JsonSerializer.Serialize(candidate, _json));
        command.Parameters.AddWithValue("$requested", requestedCount);
        command.Parameters.AddWithValue("$started", Iso(now));
        await command.ExecuteNonQueryAsync(ct);
        return new ValidationRunRecord(runId, datasetId, "Running", sourceWorkflowRunId, hash, requestedCount, 0, now, null, false, null, null);
    }

    public async Task AddResultAsync(ValidationResultRecord result, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
INSERT INTO validation_results(run_id,item_id,source_kind,source_ref,expected_disposition,actual_disposition,classification,replay_run_id,success,duration_ms,failed_nodes_json,error,completed_at)
VALUES($run,$item,$kind,$ref,$expected,$actual,$classification,$replay,$success,$duration,$nodes,$error,$completed);
""";
            command.Parameters.AddWithValue("$run", result.RunId);
            command.Parameters.AddWithValue("$item", result.ItemId);
            command.Parameters.AddWithValue("$kind", result.SourceKind);
            command.Parameters.AddWithValue("$ref", result.SourceRef);
            command.Parameters.AddWithValue("$expected", result.ExpectedDisposition);
            command.Parameters.AddWithValue("$actual", result.ActualDisposition);
            command.Parameters.AddWithValue("$classification", result.Classification);
            command.Parameters.AddWithValue("$replay", (object?)result.ReplayRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
            command.Parameters.AddWithValue("$duration", result.DurationMs);
            command.Parameters.AddWithValue("$nodes", JsonSerializer.Serialize(result.FailedNodeIds, _json));
            command.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$completed", Iso(result.CompletedAt));
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE validation_runs SET completed_count=completed_count+1 WHERE run_id=$run;";
            update.Parameters.AddWithValue("$run", result.RunId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task CompleteRunAsync(string runId, string status, string? error, CancellationToken ct)
    {
        var results = await GetResultsAsync(runId, ct);
        var summary = BuildSummary(results);
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE validation_runs SET status=$status,completed_at=$completed,error=$error,summary_json=$summary WHERE run_id=$run;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completed", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary, _json));
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RequestCancelAsync(string runId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE validation_runs SET cancel_requested=1 WHERE run_id=$run AND status='Running';";
        command.Parameters.AddWithValue("$run", runId);
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new ApiConflictException("Validation run is not running or does not exist.");
    }

    public async Task<IReadOnlyList<ValidationRunRecord>> ListRunsAsync(string datasetId, CancellationToken ct)
    {
        var result = new List<ValidationRunRecord>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,dataset_id,status,source_workflow_run_id,workflow_hash,requested_count,completed_count,started_at,completed_at,cancel_requested,error,summary_json FROM validation_runs WHERE dataset_id=$dataset ORDER BY started_at DESC LIMIT 100;";
        command.Parameters.AddWithValue("$dataset", datasetId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadRun(reader));
        return result;
    }

    public async Task<WorkflowDefinition> GetRunWorkflowAsync(string runId, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT candidate_workflow_json FROM validation_runs WHERE run_id=$run;";
        command.Parameters.AddWithValue("$run", runId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        if (json is null) throw new ApiNotFoundException($"Validation run '{runId}' was not found.");
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, _json)
            ?? throw new ApiConflictException($"Validation run '{runId}' has an invalid candidate workflow snapshot.");
    }

    public async Task<ValidationRunRecord> GetRunAsync(string runId, bool includeResults, CancellationToken ct)
    {
        ValidationRunRecord? run = null;
        await using var connection = await db.OpenConnectionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,dataset_id,status,source_workflow_run_id,workflow_hash,requested_count,completed_count,started_at,completed_at,cancel_requested,error,summary_json FROM validation_runs WHERE run_id=$run;";
            command.Parameters.AddWithValue("$run", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) run = ReadRun(reader);
        }
        if (run is null) throw new ApiNotFoundException($"Validation run '{runId}' was not found.");
        return includeResults ? run with { Results = await GetResultsAsync(runId, ct) } : run;
    }

    public async Task<IReadOnlyList<ValidationResultRecord>> GetResultsAsync(string runId, CancellationToken ct)
    {
        var result = new List<ValidationResultRecord>();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,item_id,source_kind,source_ref,expected_disposition,actual_disposition,classification,replay_run_id,success,duration_ms,failed_nodes_json,error,completed_at FROM validation_results WHERE run_id=$run ORDER BY completed_at,item_id;";
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ValidationResultRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), DbString(reader, 7), reader.GetInt32(8) != 0, reader.GetDouble(9), JsonSerializer.Deserialize<string[]>(reader.GetString(10), _json) ?? [], DbString(reader, 11), DateTimeOffset.Parse(reader.GetString(12))));
        return result;
    }

    public static ValidationRunSummary BuildSummary(IReadOnlyList<ValidationResultRecord> results)
    {
        var completed = results.Count;
        var trueOk = results.Count(x => x.Classification == "TRUE_OK");
        var trueNg = results.Count(x => x.Classification == "TRUE_NG");
        var falseOk = results.Count(x => x.Classification == "FALSE_OK");
        var falseNg = results.Count(x => x.Classification == "FALSE_NG");
        var errors = results.Count(x => x.Classification == "ERROR");
        var classified = completed - errors;
        var expectedNg = results.Count(x => x.ExpectedDisposition == "NG" && x.Classification != "ERROR");
        var expectedOk = results.Count(x => x.ExpectedDisposition == "OK" && x.Classification != "ERROR");
        var durations = results.Select(x => x.DurationMs).Where(x => x >= 0).OrderBy(x => x).ToArray();
        return new ValidationRunSummary(
            completed, completed, trueOk, trueNg, falseOk, falseNg, errors,
            classified == 0 ? 0 : (trueOk + trueNg) / (double)classified,
            expectedNg == 0 ? 0 : falseOk / (double)expectedNg,
            expectedOk == 0 ? 0 : falseNg / (double)expectedOk,
            Percentile(durations, .50), Percentile(durations, .95), Percentile(durations, .99), durations.Length == 0 ? 0 : durations[^1]);
    }

    public static string TopologyHash(WorkflowDefinition workflow)
    {
        var canonical = string.Join("\n", workflow.Nodes.Select(x => $"N|{x.Id}|{x.Type}").OrderBy(x => x, StringComparer.Ordinal)) + "\n" +
                        string.Join("\n", workflow.Edges.Select(x => $"E|{x.SourceNodeId}|{x.SourcePort}|{x.TargetNodeId}|{x.TargetPort}|{x.Kind}").OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private ValidationRunRecord ReadRun(SqliteDataReader reader)
    {
        ValidationRunSummary? summary = reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<ValidationRunSummary>(reader.GetString(11), _json);
        return new ValidationRunRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetInt32(9) != 0, DbString(reader, 10), summary);
    }

    private static string NormalizeExpected(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "OK" or "NG" ? normalized : throw new ApiValidationException("Expected disposition must be OK or NG.");
    }
    private static string NormalizeKind(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "TRACE" or "MEDIA" ? normalized : throw new ApiValidationException("Dataset sourceKind must be TRACE or MEDIA.");
    }
    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        var index = (values.Length - 1) * p;
        var lo = (int)Math.Floor(index); var hi = (int)Math.Ceiling(index);
        return lo == hi ? values[lo] : values[lo] + (values[hi] - values[lo]) * (index - lo);
    }
    private static string? DbString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static async Task TouchAsync(SqliteConnection connection, string datasetId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE validation_datasets SET updated_at=$updated WHERE id=$id;";
        command.Parameters.AddWithValue("$updated", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", datasetId);
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class DatasetValidationService(DatasetValidationStore store, TraceabilityStore traces, MediaLibraryService media, OfflineReplayService replay, IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ValidationRunRecord> StartAsync(string datasetId, ValidationRunRequest request, CancellationToken ct)
    {
        var dataset = await store.GetDatasetAsync(datasetId, ct);
        if (dataset.Items.Count == 0) throw new ApiConflictException("Dataset has no items.");
        WorkflowDefinition candidate;
        var sourceWorkflowRunId = string.IsNullOrWhiteSpace(request.SourceWorkflowRunId) ? "designer-current" : request.SourceWorkflowRunId.Trim();
        if (request.Workflow is not null)
        {
            candidate = request.Workflow;
        }
        else
        {
            candidate = await traces.GetWorkflowSnapshotAsync(sourceWorkflowRunId, ct)
                ?? throw new ApiNotFoundException($"Workflow source trace '{sourceWorkflowRunId}' was not found. Supply an explicit candidate Workflow for a Media-only dataset.");
        }
        if (candidate.Nodes.Any(x => x.Type.Equals("camera.syncCapture", StringComparison.OrdinalIgnoreCase))) throw new ApiConflictException("V0.48 does not batch-validate synchronized FrameSet workflows yet.");
        if (dataset.Items.Any(x => x.SourceKind == "MEDIA") && candidate.Nodes.Count(x => x.Type.Equals("image.acquire", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ApiConflictException("A Dataset containing Media Library images requires exactly one image.acquire node in the candidate workflow.");
        var topology = DatasetValidationStore.TopologyHash(candidate);
        await store.EnsureTopologyAsync(datasetId, topology, ct);
        dataset = await store.GetDatasetAsync(datasetId, ct);
        var max = Math.Clamp(request.MaxItems ?? dataset.Items.Count, 1, dataset.Items.Count);
        var run = await store.CreateRunAsync(datasetId, sourceWorkflowRunId, candidate, max, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        if (!_active.TryAdd(run.RunId, cts)) throw new InvalidOperationException("Validation runner ID collision.");
        _ = Task.Run(() => ExecuteAsync(run.RunId, dataset, candidate, max, cts.Token));
        return run;
    }

    public async Task CancelAsync(string runId, CancellationToken ct)
    {
        await store.RequestCancelAsync(runId, ct);
        if (_active.TryGetValue(runId, out var cts)) cts.Cancel();
    }

    private async Task ExecuteAsync(string runId, ValidationDatasetDetail dataset, WorkflowDefinition candidate, int max, CancellationToken ct)
    {
        string? terminalError = null;
        var status = "Completed";
        try
        {
            foreach (var item in dataset.Items.Take(max))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    OfflineReplayResult result;
                    if (item.SourceKind == "TRACE")
                    {
                        result = await replay.ExecuteAsync(item.SourceRef, new OfflineReplayRequest(candidate, new VisionRunOptions(DebugRunMode.Full)), $"Validation:{runId}", ct, persistReplayInput: false);
                    }
                    else
                    {
                        var path = media.ResolveItemPath(item.SourceRef);
                        result = await replay.ExecuteImageAsync(item.SourceRef, path, candidate, $"Validation:{runId}", ct);
                    }
                    var actual = result.Success ? NormalizeActual(result.QualityDisposition ?? "OK") : "ERROR";
                    var failedNodes = result.NodeReports.Where(x => !x.Success).Select(x => x.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    await store.AddResultAsync(new ValidationResultRecord(runId, item.ItemId, item.SourceKind, item.SourceRef, item.ExpectedDisposition, actual, Classify(item.ExpectedDisposition, actual), result.RunId, result.Success, result.TotalDurationMs, failedNodes, result.Error, DateTimeOffset.UtcNow), CancellationToken.None);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    await store.AddResultAsync(new ValidationResultRecord(runId, item.ItemId, item.SourceKind, item.SourceRef, item.ExpectedDisposition, "ERROR", "ERROR", null, false, 0, [], ex.Message, DateTimeOffset.UtcNow), CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) { status = "Cancelled"; }
        catch (Exception ex) { status = "Failed"; terminalError = ex.Message; }
        finally
        {
            await store.CompleteRunAsync(runId, status, terminalError, CancellationToken.None);
            if (_active.TryRemove(runId, out var cts)) cts.Dispose();
        }
    }

    private static string NormalizeActual(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized == "OK") return "OK";
        if (normalized is "NG" or "REVIEW") return "NG";
        return "ERROR";
    }
    private static string Classify(string expected, string actual) => (expected, actual) switch
    {
        (_, "ERROR") => "ERROR",
        ("OK", "OK") => "TRUE_OK",
        ("NG", "NG") => "TRUE_NG",
        ("NG", "OK") => "FALSE_OK",
        ("OK", "NG") => "FALSE_NG",
        _ => "ERROR"
    };
}
