using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Media;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;
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
    double Accuracy, double? FalseOkRate, double? FalseNgRate,
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

public sealed class DatasetValidationStore(IVisionStudioDbFactory dbFactory, TraceabilityStore traces, MediaLibraryService media)
{
    public DatasetValidationStore(SqliteMetadataDatabase db, TraceabilityStore traces, MediaLibraryService media)
        : this(new VisionStudioDbFactory(db), traces, media) { }

    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ValidationDatasetSummary>> ListDatasetsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var datasets = await db.ValidationDatasets.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
        var counts = await db.ValidationDatasetItems
            .GroupBy(x => x.DatasetId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return datasets.Select(d => new ValidationDatasetSummary(
            d.Id, d.Name, d.Description, d.TopologyHash,
            counts.TryGetValue(d.Id, out var c) ? c : 0,
            d.CreatedAt, d.UpdatedAt)).ToList();
    }

    public async Task<ValidationDatasetDetail> CreateDatasetAsync(CreateValidationDatasetRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 120) throw new ApiValidationException("Dataset name must be 1-120 characters.");
        var id = $"dataset-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ValidationDatasets.Add(new ValidationDatasetEntity
        {
            Id = id,
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            TopologyHash = null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return await GetDatasetAsync(id, ct);
    }

    public async Task<ValidationDatasetDetail> GetDatasetAsync(string datasetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var d = await db.ValidationDatasets.FirstOrDefaultAsync(x => x.Id == datasetId, ct)
            ?? throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");
        var raw = await db.ValidationDatasetItems
            .Where(x => x.DatasetId == datasetId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.ItemId)
            .ToListAsync(ct);
        var dataset = new ValidationDatasetSummary(d.Id, d.Name, d.Description, d.TopologyHash, raw.Count, d.CreatedAt, d.UpdatedAt);

        var items = new List<ValidationDatasetItem>(raw.Count);
        foreach (var row in raw)
        {
            if (row.SourceKind == "TRACE")
            {
                var trace = await traces.GetAsync(row.SourceRef, ct);
                items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.SourceKind, row.SourceRef, row.SourceRef, row.ExpectedDisposition, row.Note, row.SortOrder, row.AddedAt, trace?.Disposition ?? "MISSING", trace?.WorkflowName ?? "Missing trace", trace?.HasReplayInput ?? false));
            }
            else
            {
                try
                {
                    var path = media.ResolveItemPath(row.SourceRef);
                    items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.SourceKind, row.SourceRef, null, row.ExpectedDisposition, row.Note, row.SortOrder, row.AddedAt, row.ExpectedDisposition, Path.GetFileName(path), true));
                }
                catch
                {
                    items.Add(new ValidationDatasetItem(row.ItemId, datasetId, row.SourceKind, row.SourceRef, null, row.ExpectedDisposition, row.Note, row.SortOrder, row.AddedAt, "MISSING", row.SourceRef, false));
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);

        var existingRefs = (await db.ValidationDatasetItems
            .Where(x => x.DatasetId == datasetId)
            .Select(x => new { x.SourceKind, x.SourceRef })
            .ToListAsync(ct))
            .Select(x => (x.SourceKind, x.SourceRef))
            .ToHashSet();

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

            if (existingRefs.Add((kind, sourceRef)))
            {
                db.ValidationDatasetItems.Add(new ValidationDatasetItemEntity
                {
                    DatasetId = datasetId,
                    ItemId = $"item-{Guid.NewGuid():N}",
                    SourceKind = kind,
                    SourceRef = sourceRef,
                    ExpectedDisposition = expected,
                    Note = input.Note?.Trim(),
                    SortOrder = nextSort++,
                    AddedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var now = DateTimeOffset.UtcNow;
        var affected = await db.ValidationDatasets
            .Where(x => x.Id == datasetId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.TopologyHash, topology)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (affected == 0) throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetDatasetAsync(datasetId, ct);
    }

    public async Task EnsureTopologyAsync(string datasetId, string topologyHash, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.ValidationDatasets
            .Where(x => x.Id == datasetId && (x.TopologyHash == null || x.TopologyHash == topologyHash))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.TopologyHash, x => x.TopologyHash ?? topologyHash)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (affected == 0) throw new ApiConflictException("Candidate workflow topology does not match the dataset topology.");
    }

    public async Task<ValidationDatasetDetail> UpdateItemAsync(string datasetId, string itemId, UpdateValidationDatasetItemRequest request, CancellationToken ct)
    {
        var expected = NormalizeExpected(request.ExpectedDisposition);
        var note = request.Note?.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.ValidationDatasetItems
            .Where(x => x.DatasetId == datasetId && x.ItemId == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ExpectedDisposition, expected)
                .SetProperty(x => x.Note, note), ct);
        if (affected == 0) throw new ApiNotFoundException($"Dataset item '{itemId}' was not found.");
        await TouchAsync(db, datasetId, ct);
        return await GetDatasetAsync(datasetId, ct);
    }

    public async Task DeleteItemAsync(string datasetId, string itemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.ValidationDatasetItems
            .Where(x => x.DatasetId == datasetId && x.ItemId == itemId)
            .ExecuteDeleteAsync(ct);
        if (affected == 0) throw new ApiNotFoundException($"Dataset item '{itemId}' was not found.");
        await TouchAsync(db, datasetId, ct);
    }

    public async Task DeleteDatasetAsync(string datasetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ValidationRuns.AnyAsync(x => x.DatasetId == datasetId && x.Status == "Running", ct))
            throw new ApiConflictException("Cannot delete a dataset while validation is running.");
        if (await db.JobVersionValidations.AnyAsync(x => x.DatasetId == datasetId, ct))
            throw new ApiConflictException("Cannot delete a dataset referenced by Product/Recipe validation evidence.");
        if (await db.InvestigationCaseVerifications.AnyAsync(x => x.DatasetId == datasetId, ct))
            throw new ApiConflictException("Cannot delete a dataset referenced by Investigation verification evidence.");
        var affected = await db.ValidationDatasets.Where(x => x.Id == datasetId).ExecuteDeleteAsync(ct);
        if (affected == 0) throw new ApiNotFoundException($"Dataset '{datasetId}' was not found.");
    }

    public async Task<ValidationRunRecord> CreateRunAsync(string datasetId, string sourceWorkflowRunId, WorkflowDefinition candidate, int requestedCount, CancellationToken ct)
    {
        var runId = $"validation-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var hash = WorkflowFingerprint.Compute(candidate);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ValidationRuns.Add(new ValidationRunEntity
        {
            RunId = runId,
            DatasetId = datasetId,
            Status = "Running",
            SourceWorkflowRunId = sourceWorkflowRunId,
            WorkflowHash = hash,
            CandidateWorkflowJson = JsonSerializer.Serialize(candidate, _json),
            RequestedCount = requestedCount,
            CompletedCount = 0,
            StartedAt = now
        });
        await db.SaveChangesAsync(ct);
        return new ValidationRunRecord(runId, datasetId, "Running", sourceWorkflowRunId, hash, requestedCount, 0, now, null, false, null, null);
    }

    public async Task AddResultAsync(ValidationResultRecord result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        db.ValidationResults.Add(new ValidationResultEntity
        {
            RunId = result.RunId,
            ItemId = result.ItemId,
            SourceKind = result.SourceKind,
            SourceRef = result.SourceRef,
            ExpectedDisposition = result.ExpectedDisposition,
            ActualDisposition = result.ActualDisposition,
            Classification = result.Classification,
            ReplayRunId = result.ReplayRunId,
            Success = result.Success,
            DurationMs = result.DurationMs,
            FailedNodesJson = JsonSerializer.Serialize(result.FailedNodeIds, _json),
            Error = result.Error,
            CompletedAt = result.CompletedAt
        });
        await db.SaveChangesAsync(ct);
        await db.ValidationRuns
            .Where(x => x.RunId == result.RunId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedCount, x => x.CompletedCount + 1), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CompleteRunAsync(string runId, string status, string? error, CancellationToken ct)
    {
        var results = await GetResultsAsync(runId, ct);
        var summary = BuildSummary(results);
        var summaryJson = JsonSerializer.Serialize(summary, _json);
        var completedAt = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ValidationRuns
            .Where(x => x.RunId == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedAt, completedAt)
                .SetProperty(x => x.Error, error)
                .SetProperty(x => x.SummaryJson, summaryJson), ct);
    }

    public async Task RequestCancelAsync(string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.ValidationRuns
            .Where(x => x.RunId == runId && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelRequested, true), ct);
        if (affected == 0) throw new ApiConflictException("Validation run is not running or does not exist.");
    }

    public async Task<IReadOnlyList<ValidationRunRecord>> ListRunsAsync(string datasetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ValidationRuns
            .Where(x => x.DatasetId == datasetId)
            .OrderByDescending(x => x.StartedAt)
            .Take(100)
            .ToListAsync(ct);
        return rows.Select(ToRunRecord).ToList();
    }

    public async Task<WorkflowDefinition> GetRunWorkflowAsync(string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = await db.ValidationRuns
            .Where(x => x.RunId == runId)
            .Select(x => x.CandidateWorkflowJson)
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiNotFoundException($"Validation run '{runId}' was not found.");
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, _json)
            ?? throw new ApiConflictException($"Validation run '{runId}' has an invalid candidate workflow snapshot.");
    }

    public async Task<ValidationRunRecord> GetRunAsync(string runId, bool includeResults, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ValidationRuns.FirstOrDefaultAsync(x => x.RunId == runId, ct)
            ?? throw new ApiNotFoundException($"Validation run '{runId}' was not found.");
        var run = ToRunRecord(entity);
        return includeResults ? run with { Results = await GetResultsAsync(runId, ct) } : run;
    }

    public async Task<IReadOnlyList<ValidationResultRecord>> GetResultsAsync(string runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ValidationResults
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.CompletedAt)
            .ThenBy(x => x.ItemId)
            .ToListAsync(ct);
        return rows.Select(r => new ValidationResultRecord(
            r.RunId, r.ItemId, r.SourceKind, r.SourceRef, r.ExpectedDisposition, r.ActualDisposition, r.Classification,
            r.ReplayRunId, r.Success, r.DurationMs,
            JsonSerializer.Deserialize<string[]>(r.FailedNodesJson, _json) ?? [],
            r.Error, r.CompletedAt)).ToList();
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
            expectedNg == 0 ? null : falseOk / (double)expectedNg,
            expectedOk == 0 ? null : falseNg / (double)expectedOk,
            Percentile(durations, .50), Percentile(durations, .95), Percentile(durations, .99), durations.Length == 0 ? 0 : durations[^1]);
    }

    public static string TopologyHash(WorkflowDefinition workflow)
    {
        var canonical = string.Join("\n", workflow.Nodes.Select(x => $"N|{x.Id}|{x.Type}").OrderBy(x => x, StringComparer.Ordinal)) + "\n" +
                        string.Join("\n", workflow.Edges.Select(x => $"E|{x.SourceNodeId}|{x.SourcePort}|{x.TargetNodeId}|{x.TargetPort}|{x.Kind}").OrderBy(x => x, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private ValidationRunRecord ToRunRecord(ValidationRunEntity e)
    {
        ValidationRunSummary? summary = string.IsNullOrWhiteSpace(e.SummaryJson) ? null : JsonSerializer.Deserialize<ValidationRunSummary>(e.SummaryJson, _json);
        return new ValidationRunRecord(e.RunId, e.DatasetId, e.Status, e.SourceWorkflowRunId, e.WorkflowHash, e.RequestedCount, e.CompletedCount, e.StartedAt, e.CompletedAt, e.CancelRequested, e.Error, summary);
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

    private static Task TouchAsync(VisionStudioDbContext db, string datasetId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return db.ValidationDatasets
            .Where(x => x.Id == datasetId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now), ct);
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
