using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Api.Storage.EfCore;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api;

public sealed class RecipeRolloutService(
    IOptions<ProductionFleetOptions> options, JobStore jobs, IHttpClientFactory clients, IVisionStudioDbFactory dbFactory)
{
    public RecipeRolloutService(IOptions<ProductionFleetOptions> options, JobStore jobs, IHttpClientFactory clients, SqliteMetadataDatabase database)
        : this(options, jobs, clients, new VisionStudioDbFactory(database)) { }

    internal const string DeliveryPath = "api/production/recipe-deliveries";
    public const int MaxStations = 8;
    public const int MaxPackageBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    public async Task<RecipeRolloutBatch> SendAsync(string id, RecipeRolloutRequest request, CancellationToken ct)
    {
        if (request.StationIds is not { Count: > 0 and <= MaxStations } ||
            request.StationIds.Any(string.IsNullOrWhiteSpace) ||
            request.StationIds.Distinct(StringComparer.Ordinal).Count() != request.StationIds.Count)
            throw new ApiValidationException($"Select 1 to {MaxStations} distinct stations.");
        var stations = request.StationIds.Select(stationId => options.Value.Stations.SingleOrDefault(candidate =>
            candidate.Id == stationId && candidate.DeliveryToken is not null)
            ?? throw new ApiValidationException($"Recipe delivery is not configured for station '{stationId}'.")).ToArray();
        if (!await _dispatchGate.WaitAsync(0, ct))
            throw new ApiConflictException("A recipe delivery batch is in progress. Wait for its result before sending another.");

        try
        {
            var payload = await jobs.GetPublishedDeliveryAsync(request, ct);
            var packages = stations.Select(station => JsonSerializer.SerializeToUtf8Bytes(payload with { StationId = station.Id }, WireJson)).ToArray();
            if (packages.Any(package => package.Length > MaxPackageBytes))
                throw new ApiValidationException($"A recipe delivery must be at most {MaxPackageBytes / (1024 * 1024)} MiB.");

            var batch = new RecipeRolloutBatch(id, payload.JobId, payload.JobName, payload.SourceVersion, payload.WorkflowHash,
                DateTimeOffset.UtcNow, stations.Select(station => new RecipeRolloutStation(station.Id, station.Name, RecipeDeliveryOutcome.Unconfirmed)).ToArray());
            await SaveBatchAsync(batch, ct);

            // The request is explicit and the batch has been journalled. A browser disconnect does not interrupt its bounded attempts.
            var receipts = await Task.WhenAll(stations.Select(async (station, index) =>
            {
                var result = await SendStationAsync(station, payload, packages[index]);
                await SaveStationResultAsync(batch.Id, result);
                return result;
            }));
            return batch with { Stations = receipts };
        }
        finally { _dispatchGate.Release(); }
    }

    public async Task<IReadOnlyList<RecipeRolloutBatch>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var batches = await db.RecipeRolloutBatches
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(30)
            .ToListAsync(ct);
        if (batches.Count == 0) return [];
        var batchIds = batches.Select(x => x.Id).ToList();
        var stations = await db.RecipeRolloutStations
            .Where(x => batchIds.Contains(x.BatchId))
            .OrderBy(x => x.StationId)
            .ToListAsync(ct);
        var byBatch = stations.GroupBy(x => x.BatchId).ToDictionary(g => g.Key, g => g.Select(ToStation).ToList());
        return batches.Select(b => new RecipeRolloutBatch(
            b.Id, b.JobId, b.JobName, b.SourceVersion, b.WorkflowHash, b.CreatedAt,
            byBatch.TryGetValue(b.Id, out var list) ? list : [])).ToList();
    }

    public async Task<IReadOnlyList<StationRecipeRollout>> LatestByStationAsync(CancellationToken ct)
    {
        var stationIds = options.Value.Stations.Select(station => station.Id).ToArray();
        if (stationIds.Length == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await (
            from latest in db.RecipeRolloutLatestStations
            where stationIds.Contains(latest.StationId)
            join s in db.RecipeRolloutStations on new { latest.BatchId, latest.StationId } equals new { s.BatchId, s.StationId }
            join b in db.RecipeRolloutBatches on latest.BatchId equals b.Id
            orderby s.StationId
            select new { Batch = b, Station = s })
            .ToListAsync(ct);

        return rows.Select(x => new StationRecipeRollout(
            x.Batch.Id, x.Batch.JobId, x.Batch.JobName, x.Batch.SourceVersion, x.Batch.WorkflowHash, x.Batch.CreatedAt,
            ToStation(x.Station))).ToList();
    }

    private static RecipeRolloutStation ToStation(RecipeRolloutStationEntity s)
        => new(s.StationId, s.StationName, Enum.Parse<RecipeDeliveryOutcome>(s.Outcome), s.LocalVersion, s.Detail);

    private async Task<RecipeRolloutStation> SendStationAsync(ProductionStationOptions station, StationRecipeDelivery payload, byte[] body)
    {
        var result = new RecipeRolloutStation(station.Id, station.Name, RecipeDeliveryOutcome.Unconfirmed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = clients.CreateClient(ProductionFleet.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ProductionFleet.BaseUri(station), DeliveryPath))
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", station.DeliveryToken);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var rejected = code is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout;
                return result with { Outcome = rejected ? RecipeDeliveryOutcome.Rejected : RecipeDeliveryOutcome.Unconfirmed,
                    Detail = $"HTTP {code}" };
            }
            var receipt = await response.Content.ReadFromJsonAsync<RecipeDeliveryReceipt>(WireJson, timeout.Token);
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.Id) || receipt.StationId != station.Id || receipt.JobId != payload.JobId ||
                receipt.SourceVersion != payload.SourceVersion || receipt.LocalVersion < 1 ||
                !string.Equals(receipt.WorkflowHash, payload.WorkflowHash, StringComparison.OrdinalIgnoreCase))
                return result with { Detail = "receipt_mismatch" };
            return result with { Outcome = RecipeDeliveryOutcome.Received, LocalVersion = receipt.LocalVersion };
        }
        catch (OperationCanceledException) { return result with { Detail = "timeout" }; }
        catch (HttpRequestException) { return result with { Detail = "connection_error" }; }
        catch (JsonException) { return result with { Detail = "invalid_response" }; }
        catch (NotSupportedException) { return result with { Detail = "invalid_response" }; }
    }

    private async Task SaveBatchAsync(RecipeRolloutBatch batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct, strongDurability: true);
        await using var tx = await dbFactory.BeginImmediateTransactionAsync(db, ct);
        db.RecipeRolloutBatches.Add(new RecipeRolloutBatchEntity
        {
            Id = batch.Id,
            JobId = batch.JobId,
            JobName = batch.JobName,
            SourceVersion = batch.SourceVersion,
            WorkflowHash = batch.WorkflowHash,
            CreatedAt = batch.CreatedAt
        });
        await db.SaveChangesAsync(ct);

        var stationIds = batch.Stations.Select(s => s.StationId).ToList();
        var existingLatest = await db.RecipeRolloutLatestStations
            .AsTracking()
            .Where(x => stationIds.Contains(x.StationId))
            .ToDictionaryAsync(x => x.StationId, ct);

        foreach (var station in batch.Stations)
        {
            db.RecipeRolloutStations.Add(new RecipeRolloutStationEntity
            {
                BatchId = batch.Id,
                StationId = station.StationId,
                StationName = station.StationName,
                Outcome = station.Outcome.ToString()
            });
            if (existingLatest.TryGetValue(station.StationId, out var latest))
            {
                latest.BatchId = batch.Id;
            }
            else
            {
                db.RecipeRolloutLatestStations.Add(new RecipeRolloutLatestStationEntity
                {
                    StationId = station.StationId,
                    BatchId = batch.Id
                });
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task SaveStationResultAsync(string id, RecipeRolloutStation station)
    {
        await using var db = await dbFactory.CreateDbContextAsync(strongDurability: true);
        var outcome = station.Outcome.ToString();
        await db.RecipeRolloutStations
            .Where(x => x.BatchId == id && x.StationId == station.StationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Outcome, outcome)
                .SetProperty(x => x.LocalVersion, station.LocalVersion)
                .SetProperty(x => x.Detail, station.Detail));
    }
}
