using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace VisionStudio.Api.Tests;

public sealed class ApiSmokeTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public ApiSmokeTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsCurrentArchitectureVersion()
    {
        var response = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(payload);
        Assert.Contains("v0.63", payload!["mvp"].ToString()!.ToLowerInvariant());

        // F10：日志可靠性计数（丢弃/写入失败/刷盘失败/排空超时）经 health 暴露，不再静默。
        var health = await _client.GetFromJsonAsync<JsonElement>("/api/health");
        var logging = health.GetProperty("logging");
        Assert.True(logging.GetProperty("droppedEntries").GetInt64() >= 0);
        Assert.True(logging.TryGetProperty("writeFailures", out _));
        Assert.True(logging.TryGetProperty("flushFailures", out _));
        Assert.True(logging.TryGetProperty("drainTimeouts", out _));

        // 相机厂商 SDK 的静默失败台账同样经 health 暴露（2026-11-13）。
        Assert.Equal(JsonValueKind.Array, health.GetProperty("cameraSdk").ValueKind);

        // 脚本 Worker 状态（Phase 2）：匿名 health 只暴露状态位，不暴露进程号/程序集路径。
        Assert.Equal(JsonValueKind.Object, health.GetProperty("scriptWorker").ValueKind);
    }

    [Fact]
    public async Task DeviceActionResolutionEndpoint_IsAdministratorAudited()
    {
        var response = await _client.PostAsJsonAsync("/api/production/device-actions/resolve", new
        {
            runId = "legacy", manifestHash = "no-pending-state", deviceIds = Array.Empty<string>(), robotIds = Array.Empty<string>(),
            reason = "checked", evidence = "controller readback", deviceIdentityEvidence = "serial ABC"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // route is active and refuses a nonmatching safety record.

        var audit = await _client.GetFromJsonAsync<JsonElement>("/api/audit?action=production.device-actions.resolve");
        var item = audit.EnumerateArray().First(x => x.GetProperty("path").GetString() == "/api/production/device-actions/resolve");
        Assert.Equal("production.device-actions.resolve", item.GetProperty("action").GetString());
        Assert.Equal("Security Disabled", item.GetProperty("username").GetString()); // authenticated system principal in this test host.
        Assert.False(item.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Catalog_AndVirtualDevice_AreExposedAfterHostedBootstrap()
    {
        var catalogResponse = await _client.GetAsync("/api/catalog");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        Assert.True(catalogResponse.Headers.Contains("ETag"));
        Assert.True(catalogResponse.Headers.Contains("X-VisionStudio-Catalog-Hash"));
        Assert.True(catalogResponse.Headers.Contains("X-VisionStudio-Catalog-Schema"));
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        Assert.NotNull(catalog);
        Assert.True(catalog!.Count >= 39);

        var meta = await _client.GetFromJsonAsync<JsonElement>("/api/catalog/meta");
        Assert.Equal(1, meta.GetProperty("schemaVersion").GetInt32());
        Assert.True(meta.GetProperty("nodeCount").GetInt32() >= 39);
        Assert.Equal(64, meta.GetProperty("hash").GetString()!.Length);

        var devicesJson = await _client.GetStringAsync("/api/devices");
        Assert.Contains("virtual-modbus-1", devicesJson.ToLowerInvariant());
    }


    [Fact]
    public async Task WorkflowEngineDispatchesDurableCountersWithStableIdentity()
    {
        async Task<JsonElement> RunAsync(string eventId)
        {
            var workflow = new
            {
                id = "durable-counter-smoke",
                name = "Durable counter smoke",
                nodes = new object[]
                {
                    new { id = "event", type = "value.string", parameters = new { template = eventId } },
                    new { id = "counter", type = "value.counter", parameters = new { durability = "durable" } }
                },
                edges = new object[]
                {
                    new { id = "control", sourceNodeId = "event", sourcePort = "next", targetNodeId = "counter", targetPort = "exec", kind = "control" },
                    new { id = "event-id", sourceNodeId = "event", sourcePort = "textValue", targetNodeId = "counter", targetPort = "eventId", kind = "data" }
                }
            };
            using var response = await _client.PostAsJsonAsync("/api/run", workflow);
            var payload = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, payload);
            using var json = JsonDocument.Parse(payload);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean(), payload);
            return json.RootElement.GetProperty("nodeReports").EnumerateArray()
                .Single(report => report.GetProperty("nodeId").GetString() == "counter").GetProperty("summary").Clone();
        }

        Assert.Equal(1, (await RunAsync("inspection-1")).GetProperty("count").GetInt64());
        Assert.True((await RunAsync("inspection-1")).GetProperty("reused").GetBoolean());
        Assert.Equal(2, (await RunAsync("inspection-2")).GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task OfflineReplay_ReexecutesSoftwareWorkflow_AndReturnsNodeSnapshots()
    {
        var workflow = new
        {
            id = "replay-smoke",
            name = "Replay Smoke",
            nodes = new object[]
            {
                new { id = "source", type = "image.synthetic", name = "Synthetic", position = new { x = 0, y = 0 }, parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
                new { id = "threshold", type = "image.threshold", name = "Threshold", position = new { x = 300, y = 0 }, parameters = new { threshold = 100 } }
            },
            edges = new object[]
            {
                new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
                new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
            }
        };

        var run = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        var runId = runDoc.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var context = await _client.GetFromJsonAsync<JsonElement>($"/api/traces/{runId}/replay/context");
        Assert.True(context.GetProperty("replayable").GetBoolean());

        var replay = await _client.PostAsJsonAsync($"/api/traces/{runId}/replay", new
        {
            workflow = (object?)null,
            options = new { mode = "RunNode", targetNodeId = "threshold", breakpoints = Array.Empty<string>() }
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDoc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        var reports = replayDoc.RootElement.GetProperty("nodeReports");
        Assert.True(reports.GetArrayLength() >= 2);
        var threshold = reports.EnumerateArray().Single(x => x.GetProperty("nodeId").GetString() == "threshold");
        Assert.True(threshold.GetProperty("inputs").TryGetProperty("image", out _));
        Assert.True(threshold.GetProperty("outputs").TryGetProperty("image", out _));
    }

    [Fact]
    public async Task DatasetValidation_DatasetCrudSurface_IsExposed()
    {
        var create = await _client.PostAsJsonAsync("/api/validation/datasets", new { name = "Smoke Dataset", description = "V0.48" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var datasetId = created.RootElement.GetProperty("dataset").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(datasetId));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/validation/datasets");
        Assert.True(list.GetArrayLength() >= 1);
        var detail = await _client.GetAsync($"/api/validation/datasets/{datasetId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task ParameterTuning_TransientPreview_AndCandidateWorkflow_AreExposed()
    {
        var workflow = new
        {
            id = "tuning-smoke",
            name = "Tuning Smoke",
            nodes = new object[]
            {
                new { id = "source", type = "image.synthetic", name = "Synthetic", position = new { x = 0, y = 0 }, parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
                new { id = "threshold", type = "image.threshold", name = "Threshold", position = new { x = 300, y = 0 }, parameters = new { threshold = 100 } }
            },
            edges = new object[]
            {
                new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
                new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
            }
        };
        var runResponse = await _client.PostAsJsonAsync("/api/run", workflow);
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        using var runDoc = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
        var sourceRunId = runDoc.RootElement.GetProperty("runId").GetString()!;

        var create = await _client.PostAsJsonAsync("/api/validation/datasets", new { name = "Tuning Smoke Dataset", description = "V0.49" });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var datasetId = createDoc.RootElement.GetProperty("dataset").GetProperty("id").GetString()!;
        var add = await _client.PostAsJsonAsync($"/api/validation/datasets/{datasetId}/items", new
        {
            items = new[] { new { sourceKind = "TRACE", sourceRef = sourceRunId, expectedDisposition = "OK" } }
        });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        using var addDoc = JsonDocument.Parse(await add.Content.ReadAsStringAsync());
        var itemId = addDoc.RootElement.GetProperty("items")[0].GetProperty("itemId").GetString()!;

        var preview = await _client.PostAsJsonAsync("/api/tuning/preview", new
        {
            datasetId, itemId, workflow, targetNodeId = "threshold", fullWorkflow = false
        });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewDoc = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("threshold", previewDoc.RootElement.GetProperty("selectedNodeReport").GetProperty("nodeId").GetString());

        var validation = await _client.PostAsJsonAsync($"/api/validation/datasets/{datasetId}/runs", new
        {
            sourceWorkflowRunId = "parameter-tuning-smoke", workflow
        });
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        using var validationDoc = JsonDocument.Parse(await validation.Content.ReadAsStringAsync());
        var validationRunId = validationDoc.RootElement.GetProperty("runId").GetString()!;
        var candidate = await _client.GetFromJsonAsync<JsonElement>($"/api/validation/runs/{validationRunId}/workflow");
        Assert.Equal("tuning-smoke", candidate.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Production_Status_Config_AndAlarms_AreExposed()
    {
        var status = await _client.GetAsync("/api/production/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var config = await _client.GetAsync("/api/production/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var alarms = await _client.GetAsync("/api/alarms");
        Assert.Equal(HttpStatusCode.OK, alarms.StatusCode);
    }

    [Fact]
    public async Task MissingDevice_ReturnsProblemDetails404WithCorrelationId()
    {
        var response = await _client.GetAsync("/api/devices/not-registered");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task InvalidCompile_ReturnsValidationProblemDetails400()
    {
        var response = await _client.PostAsJsonAsync("/api/compile", new
        {
            id = "invalid",
            name = "Invalid",
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", document.RootElement.GetProperty("code").GetString());
    }
}

public sealed class VisionStudioApiFactory : IsolatedApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
    }

}

public sealed class RuntimeOnlyHostTests
{
    [Fact]
    public async Task RuntimeOnlyHost_BlocksDesignerMutation_ButAllowsProductionStatus()
    {
        using var factory = new RuntimeOnlyApiFactory();
        using var client = factory.CreateClient();

        var hostMode = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/host-mode");
        Assert.NotNull(hostMode);
        Assert.Contains("true", hostMode!["runtimeOnly"].ToString()!.ToLowerInvariant());

        var blocked = await client.PostAsJsonAsync("/api/run", new { });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("application/problem+json", blocked.Content.Headers.ContentType?.MediaType);

        var blockedConfig = await client.PutAsJsonAsync("/api/production/config", new
        {
            jobId = "x", autoStart = false, cycleDelayMs = 25, maxCycleMs = 15000,
            maxConsecutiveFailures = 3, autoRecover = true, recoveryDelayMs = 1000, stopTimeoutMs = 5000
        });
        Assert.Equal(HttpStatusCode.Forbidden, blockedConfig.StatusCode);

        var allowed = await client.GetAsync("/api/production/status");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var blockedRuntimeConfig = await client.PutAsJsonAsync("/api/production/runtimes/line-1/config", new
        {
            jobId = "x", autoStart = false, cycleDelayMs = 25, maxCycleMs = 15000,
            maxConsecutiveFailures = 3, autoRecover = true, recoveryDelayMs = 1000, stopTimeoutMs = 5000
        });
        Assert.Equal(HttpStatusCode.Forbidden, blockedRuntimeConfig.StatusCode);

        var blockedRuntimeDelete = await client.DeleteAsync("/api/production/runtimes/line-1");
        Assert.Equal(HttpStatusCode.Forbidden, blockedRuntimeDelete.StatusCode);

        // 多工位与本机并行产线启停/触发/恢复属于运行期生产操作，RuntimeOnly 模式应放行（不得返回 403 runtime_only）。
        var allowedStationControl = await client.PostAsJsonAsync("/api/production/stations/control", new
        {
            stationIds = Array.Empty<string>(),
            action = "Start"
        });
        Assert.NotEqual(HttpStatusCode.Forbidden, allowedStationControl.StatusCode);

        var allowedRuntimeStart = await client.PostAsJsonAsync("/api/production/runtimes/line-1/start", new { jobId = "missing-job" });
        Assert.NotEqual(HttpStatusCode.Forbidden, allowedRuntimeStart.StatusCode);

        var allowedStartAll = await client.PostAsync("/api/production/runtimes/start-all", content: null);
        Assert.Equal(HttpStatusCode.OK, allowedStartAll.StatusCode);
    }
}

public sealed class ProductionConfigApiTests
{
    [Fact]
    public async Task SaveRequiresRevisionAndRejectsAnotherClientsStaleWrite()
    {
        using var factory = new VisionStudioApiFactory();
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        using var before = await first.GetAsync("/api/production/config");
        var revision = before.Headers.ETag!.ToString();

        using var missing = await first.PutAsJsonAsync("/api/production/config", new ProductionRuntimeConfig());
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var saved = await first.PutProductionConfigAsync(new ProductionRuntimeConfig(CycleDelayMs: 120), revision);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.NotEqual(revision, saved.Headers.ETag!.ToString());
        using var stale = await second.PutProductionConfigAsync(new ProductionRuntimeConfig(RecoveryDelayMs: 500), revision);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await second.GetFromJsonAsync<JsonElement>("/api/production/config");
        Assert.Equal(120, current.GetProperty("cycleDelayMs").GetInt32());
    }

    [Fact]
    public void TestFactoriesKeepIndependentDataRootsOutsideTheSourceTree()
    {
        using var first = new VisionStudioApiFactory();
        using var second = new RuntimeOnlyApiFactory();
        var firstRoot = first.Services.GetRequiredService<SqliteMetadataDatabase>().DataRoot;
        var secondRoot = second.Services.GetRequiredService<SqliteMetadataDatabase>().DataRoot;
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.StartsWith(Path.GetTempPath(), firstRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetTempPath(), secondRoot, StringComparison.OrdinalIgnoreCase);
        var sourceRoot = first.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        Assert.NotEqual(Path.Combine(sourceRoot, "data"), firstRoot);
    }

    [Fact]
    public void HostRejectsConcurrentOwnershipOfTheSameDataRoot_AndReleasesItOnDisposal()
    {
        using var directory = new TempWebHostEnvironment();
        var root = directory.ContentRootPath;
        using (var first = Host(root))
        {
            using var firstClient = first.CreateClient();
            using var duplicate = Host(Path.Combine(root, "."));
            var error = Assert.ThrowsAny<Exception>(() => duplicate.CreateClient());
            Assert.Contains("exclusive ownership of VisionStudio data root", error.ToString());
        }

        using var restarted = Host(root);
        using var client = restarted.CreateClient();

        static WebApplicationFactory<Program> Host(string root) =>
            new IsolatedApiFactory(root).WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Security:Enabled", "false");
                builder.UseSetting("RobotTcpSimulator:Enabled", "false");
            });
    }
}

public sealed class RuntimeOnlyApiFactory : IsolatedApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("VisionStudio:RuntimeOnly", "true");
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
    }

}
