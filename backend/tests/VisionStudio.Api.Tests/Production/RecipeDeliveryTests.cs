using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VisionStudio.Api;
using VisionStudio.Api.Infrastructure;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class RecipeDeliveryTests
{
    private const string Deliveries = "/api/production/recipe-deliveries";
    private const string StationId = "inspection-a";
    private static readonly WorkflowDefinition Workflow = new("distributed-flow", "Inspection",
        [new NodeDefinition("source", "image.synthetic", "Synthetic", null, null)], []);

    [Fact]
    public async Task RuntimeStationStagesIdempotentlyAndRequiresSeparateLocalPublication()
    {
        using var original = new SecureVisionStudioApiFactory();
        using var factory = original.WithWebHostBuilder(builder => builder
            .UseSetting("ProductionFleet:StationId", StationId)
            .UseSetting("VisionStudio:RuntimeOnly", "true"));
        using var administrator = factory.CreateClient();
        await SignInAsync(administrator);
        using var readToken = factory.CreateClient();
        readToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(administrator, "stations:read"));
        using var deliveryToken = factory.CreateClient();
        deliveryToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(administrator, "stations:deliver"));

        var delivery = new StationRecipeDelivery(StationId, "remote-recipe", "Inspection A", 4,
            WorkflowFingerprint.Compute(Workflow), false, Workflow);
        Assert.Equal(HttpStatusCode.Forbidden, (await readToken.PostAsJsonAsync(Deliveries, delivery)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deliveryToken.GetAsync(Deliveries)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deliveryToken.GetAsync("/api/production/stations/local")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await deliveryToken.PostAsJsonAsync(Deliveries,
            delivery with { StationId = "wrong-station" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await deliveryToken.PostAsJsonAsync(Deliveries,
            delivery with { WorkflowHash = new string('0', 64) })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await deliveryToken.PostAsJsonAsync(Deliveries,
            delivery with { Workflow = Workflow with { SchemaVersion = WorkflowDocumentSchema.CurrentVersion + 1 } })).StatusCode);

        var received = await Task.WhenAll(ReceiveAsync(deliveryToken, delivery), ReceiveAsync(deliveryToken, delivery));
        var receipt = received[0];
        Assert.Equal(receipt.Id, received[1].Id);
        Assert.Equal(1, receipt.LocalVersion);
        Assert.Null(receipt.PublishedVersion);
        var stationJobs = factory.Services.GetRequiredService<JobStore>();
        Assert.Null((await stationJobs.GetAsync(delivery.JobId, default))!.PublishedVersion);
        Assert.Single((await administrator.GetFromJsonAsync<RecipeDeliveryReceipt[]>(Deliveries))!);

        var route = $"{Deliveries}/{receipt.Id}/publish";
        var expected = new PublishRecipeDeliveryRequest(receipt.JobUpdatedAt);
        Assert.Equal(HttpStatusCode.Forbidden, (await deliveryToken.PostAsJsonAsync(route, expected)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.GetAsync("/api/production/operator-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await administrator.PostAsJsonAsync(route, expected)).StatusCode);
        Assert.Equal(1, (await stationJobs.GetAsync(delivery.JobId, default))!.PublishedVersion);

        var validated = await ReceiveAsync(deliveryToken, delivery with { SourceVersion = 5, RequiresValidation = true });
        Assert.Equal(2, validated.LocalVersion);
        Assert.True(validated.RequiresValidation);
        Assert.Equal(1, validated.PublishedVersion);
        using var blocked = await administrator.PostAsJsonAsync($"{Deliveries}/{validated.Id}/publish",
            new PublishRecipeDeliveryRequest(validated.JobUpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("accepted Dataset Validation", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(1, (await stationJobs.GetAsync(delivery.JobId, default))!.PublishedVersion);
    }

    [Fact]
    public async Task BatchPersistsPerStationReceiptsAndRejectsSupersededSourceBeforeSending()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var operatorClient = factory.CreateFreshClient();
        await SignInAsync(operatorClient);
        const string jobId = "source-recipe";
        var jobStore = factory.Services.GetRequiredService<JobStore>();
        var created = await jobStore.CreateAsync(new CreateJobRequest(jobId, "Inspection", null, Workflow), default);
        (await operatorClient.PostAsync($"/api/jobs/{jobId}/versions/1/publish", null)).EnsureSuccessStatusCode();
        var options = Options.Create(new ProductionFleetOptions
        {
            Stations = [
                new() { Id = StationId, Name = "Inspection A", BaseUrl = "https://a.example/vision/", Token = "vst_read", DeliveryToken = "vst_write_a" },
                new() { Id = "inspection-b", Name = "Inspection B", BaseUrl = "https://b.example/", Token = "vst_read_b", DeliveryToken = "vst_write_b" }]
        });
        var sends = 0;
        using var handler = new StubHandler(async (message, ct) =>
        {
            Interlocked.Increment(ref sends);
            var payload = await message.Content!.ReadFromJsonAsync<StationRecipeDelivery>(cancellationToken: ct);
            Assert.NotNull(payload);
            Assert.Equal(created.Versions[0].WorkflowHash, payload.WorkflowHash);
            if (payload.StationId != StationId) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Assert.Equal("https://a.example/vision/api/production/recipe-deliveries", message.RequestUri!.AbsoluteUri);
            Assert.Equal("vst_write_a", message.Headers.Authorization?.Parameter);
            var receipt = new RecipeDeliveryReceipt("received", StationId, jobId, "Inspection", 1, payload.WorkflowHash,
                3, false, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) };
        });
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        var service = new RecipeRolloutService(options, jobStore, new StubClientFactory(handler), db);
        var request = new RecipeRolloutRequest(jobId, 1, created.Versions[0].WorkflowHash, [StationId, "inspection-b"]);
        var result = await service.SendAsync("first-batch", request, default);
        Assert.Collection(result.Stations,
            first => { Assert.Equal(RecipeDeliveryOutcome.Received, first.Outcome); Assert.Equal(3, first.LocalVersion); },
            second => { Assert.Equal(RecipeDeliveryOutcome.Unconfirmed, second.Outcome); Assert.Null(second.LocalVersion); });
        Assert.Equal(result.Stations, Assert.Single(await new RecipeRolloutService(options, jobStore, new StubClientFactory(handler), db).ListAsync(default)).Stations);

        await service.SendAsync("retry-batch", request with { StationIds = ["inspection-b"] }, default);
        Assert.Collection(await service.LatestByStationAsync(default),
            first => { Assert.Equal("first-batch", first.BatchId); Assert.Equal(3, first.Delivery.LocalVersion); },
            second => { Assert.Equal("retry-batch", second.BatchId); Assert.Equal(RecipeDeliveryOutcome.Unconfirmed, second.Delivery.Outcome); });

        await jobStore.AddVersionAsync(jobId, new SaveJobVersionRequest(Workflow, "Another version"), default);
        (await operatorClient.PostAsync($"/api/jobs/{jobId}/versions/2/publish", null)).EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<ApiConflictException>(() => service.SendAsync("stale-batch", request, default));
        Assert.Equal(3, sends);
        Assert.Equal(2, (await service.ListAsync(default)).Count);
    }

    [Fact]
    public async Task MigrationBackfillsLatestForAStationOutsideTheRecentBatchWindow()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var _ = factory.CreateFreshClient();
        var database = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
DROP TABLE recipe_rollout_latest_station;
DROP INDEX ix_run_traces_operator_ng_preview;
DELETE FROM schema_migration_history WHERE version=37;
UPDATE schema_info SET version=36;

WITH RECURSIVE batches(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM batches WHERE i<32)
INSERT INTO recipe_rollout_batches(id,job_id,job_name,source_version,workflow_hash,created_at)
SELECT printf('batch-%02d', i),'recipe','Recipe',i,'workflow-hash',
       strftime('%Y-%m-%dT%H:%M:%S+00:00', '2026-11-23 08:00:00', '+' || i || ' seconds')
FROM batches;

INSERT INTO recipe_rollout_stations(batch_id,station_id,station_name,outcome,local_version)
SELECT id,CASE WHEN id='batch-01' THEN 'inspection-a' ELSE 'inspection-b' END,
       'Inspection','Received',source_version
FROM recipe_rollout_batches;
""";
            await command.ExecuteNonQueryAsync();
            var upgraded = await new SqliteSchemaMigrationRunner().MigrateAsync(connection);
            Assert.Equal(database.CurrentSchemaVersion, upgraded.CurrentVersion);
        }

        var options = Options.Create(new ProductionFleetOptions
        {
            Stations = [new() { Id = StationId }, new() { Id = "inspection-b" }, new() { Id = "inspection-c" }]
        });
        var service = new RecipeRolloutService(options, factory.Services.GetRequiredService<JobStore>(),
            factory.Services.GetRequiredService<IHttpClientFactory>(), database);
        Assert.DoesNotContain(await service.ListAsync(default), batch => batch.Id == "batch-01");
        var latest = await service.LatestByStationAsync(default);
        Assert.Collection(latest,
            first => { Assert.Equal(StationId, first.Delivery.StationId); Assert.Equal("batch-01", first.BatchId); },
            second => { Assert.Equal("inspection-b", second.Delivery.StationId); Assert.Equal("batch-32", second.BatchId); });
    }

    [Fact]
    public async Task OperatorPreviewSelectsOnlySavedImagesFromTheCurrentProductionSession()
    {
        using var factory = new SecureVisionStudioApiFactory();
        using var _ = factory.CreateFreshClient();
        var traces = factory.Services.GetRequiredService<TraceabilityStore>();
        var db = factory.Services.GetRequiredService<SqliteMetadataDatabase>();
        var session = DateTimeOffset.UtcNow.AddMinutes(-2);
        await traces.BeginAsync("old", session.AddSeconds(-1), Workflow, new RunTraceContext("ProductionRuntime", "recipe-a", 2), default);
        await traces.BeginAsync("new", session.AddSeconds(1), Workflow, new RunTraceContext("ProductionRuntime", "recipe-a", 2), default);
        await traces.BeginAsync("manual", session.AddSeconds(2), Workflow, new RunTraceContext("PublishedJob", "recipe-a", 2), default);
        await traces.BeginAsync("different-version", session.AddSeconds(3), Workflow, new RunTraceContext("ProductionRuntime", "recipe-a", 1), default);
        await traces.BeginAsync("no-image", session.AddSeconds(4), Workflow, new RunTraceContext("ProductionRuntime", "recipe-a", 2), default);
        await traces.BeginAsync("latest-ok", session.AddSeconds(5), Workflow, new RunTraceContext("ProductionRuntime", "recipe-a", 2), default);
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
UPDATE run_traces
SET has_preview = CASE WHEN run_id='no-image' THEN 0 ELSE 1 END,
    disposition = CASE WHEN run_id='latest-ok' THEN 'OK' WHEN run_id='new' THEN 'ng' ELSE 'NG' END;
""";
            await command.ExecuteNonQueryAsync();
        }
        var preview = await traces.GetOperatorPreviewAsync("recipe-a", 2, session, default);
        Assert.Equal("latest-ok", preview?.RunId);
        var ngPreview = await traces.GetOperatorPreviewAsync("recipe-a", 2, session, default, ngOnly: true);
        Assert.Equal("new", ngPreview?.RunId);
        Assert.Equal("NG", ngPreview?.Disposition);
        Assert.Null(await traces.GetOperatorPreviewAsync("recipe-a", 2, session.AddSeconds(2), default, ngOnly: true));
    }

    private static async Task<RecipeDeliveryReceipt> ReceiveAsync(HttpClient client, StationRecipeDelivery delivery)
    {
        using var result = await client.PostAsJsonAsync(Deliveries, delivery);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        return (await result.Content.ReadFromJsonAsync<RecipeDeliveryReceipt>())!;
    }

    private static async Task<string> TokenAsync(HttpClient client, string scope)
    {
        using var result = await client.PostAsJsonAsync("/api/security/service-tokens", new { name = scope.Replace(':', '-'), scopes = new[] { scope } });
        result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var account = new { username = "admin", displayName = "Administrator", password = "AdminPass!234" };
        (await client.PostAsJsonAsync("/api/auth/bootstrap", account)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/auth/login", account)).EnsureSuccessStatusCode();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
