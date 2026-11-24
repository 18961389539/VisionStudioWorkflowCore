using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

public sealed class RecipeRolloutService(
    IOptions<ProductionFleetOptions> options, JobStore jobs, IHttpClientFactory clients, SqliteMetadataDatabase database)
{
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
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT b.id,b.job_id,b.job_name,b.source_version,b.workflow_hash,b.created_at,
       s.station_id,s.station_name,s.outcome,s.local_version,s.detail
FROM (SELECT * FROM recipe_rollout_batches ORDER BY created_at DESC,id DESC LIMIT 30) b
JOIN recipe_rollout_stations s ON s.batch_id=b.id
ORDER BY b.created_at DESC,b.id DESC,s.station_id;
""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<RecipeRolloutBatch>();
        List<RecipeRolloutStation> stationRows = [];
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            if (rows.Count == 0 || rows[^1].Id != id)
            {
                stationRows = [];
                rows.Add(new RecipeRolloutBatch(id, reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), stationRows));
            }
            stationRows.Add(ReadStation(reader));
        }
        return rows;
    }

    public async Task<IReadOnlyList<StationRecipeRollout>> LatestByStationAsync(CancellationToken ct)
    {
        var stationIds = options.Value.Stations.Select(station => station.Id).ToArray();
        if (stationIds.Length == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT b.id,b.job_id,b.job_name,b.source_version,b.workflow_hash,b.created_at,
       s.station_id,s.station_name,s.outcome,s.local_version,s.detail
FROM json_each($stations) configured
JOIN recipe_rollout_latest_station latest ON latest.station_id=configured.value
JOIN recipe_rollout_stations s ON s.batch_id=latest.batch_id AND s.station_id=latest.station_id
JOIN recipe_rollout_batches b ON b.id=latest.batch_id
ORDER BY s.station_id;
""";
        command.Parameters.AddWithValue("$stations", JsonSerializer.Serialize(stationIds));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<StationRecipeRollout>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StationRecipeRollout(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), ReadStation(reader)));
        }
        return rows;
    }

    private static RecipeRolloutStation ReadStation(SqliteDataReader reader)
        => new(reader.GetString(6), reader.GetString(7), Enum.Parse<RecipeDeliveryOutcome>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10));

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
        await using var connection = await database.OpenConnectionAsync(ct, strongDurability: true);
        await using var tx = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO recipe_rollout_batches(id,job_id,job_name,source_version,workflow_hash,created_at)
VALUES($id,$job,$name,$version,$hash,$at);
""";
            command.Parameters.AddWithValue("$id", batch.Id);
            command.Parameters.AddWithValue("$job", batch.JobId);
            command.Parameters.AddWithValue("$name", batch.JobName);
            command.Parameters.AddWithValue("$version", batch.SourceVersion);
            command.Parameters.AddWithValue("$hash", batch.WorkflowHash);
            command.Parameters.AddWithValue("$at", batch.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct);
        }
        foreach (var station in batch.Stations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
INSERT INTO recipe_rollout_stations(batch_id,station_id,station_name,outcome) VALUES($batch,$station,$name,$outcome);
INSERT INTO recipe_rollout_latest_station(station_id,batch_id) VALUES($station,$batch)
ON CONFLICT(station_id) DO UPDATE SET batch_id=excluded.batch_id;
""";
            command.Parameters.AddWithValue("$batch", batch.Id);
            command.Parameters.AddWithValue("$station", station.StationId);
            command.Parameters.AddWithValue("$name", station.StationName);
            command.Parameters.AddWithValue("$outcome", station.Outcome.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task SaveStationResultAsync(string id, RecipeRolloutStation station)
    {
        await using var connection = await database.OpenConnectionAsync(strongDurability: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
UPDATE recipe_rollout_stations SET outcome=$outcome,local_version=$version,detail=$detail
WHERE batch_id=$batch AND station_id=$station;
""";
        command.Parameters.AddWithValue("$batch", id);
        command.Parameters.AddWithValue("$station", station.StationId);
        command.Parameters.AddWithValue("$outcome", station.Outcome.ToString());
        command.Parameters.AddWithValue("$version", (object?)station.LocalVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)station.Detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
