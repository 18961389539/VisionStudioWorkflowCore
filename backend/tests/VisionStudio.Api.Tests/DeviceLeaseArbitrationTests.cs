using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Unified hardware lease arbitration across production and debug sessions: a paused session keeps its
/// assets leased, a running production blocks new sessions that reference the same assets, and
/// releasing the holder (delete) frees the asset for the next owner.
/// </summary>
public sealed class DeviceLeaseArbitrationTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public DeviceLeaseArbitrationTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task PausedDebugSession_BlocksProductionStart_UntilSessionDeleted()
    {
        var sessionId = await CreateDebugSession(DeviceWorkflow("debug-lease-holder"));
        try
        {
            var jobId = await CreateAndPublishJob("prod-lease-blocked");
            await ConfigureProduction(jobId);

            var blocked = await _client.PostAsJsonAsync("/api/production/start", new { jobId });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            var problem = await blocked.Content.ReadAsStringAsync();
            Assert.Contains("Debug Session", problem);
            Assert.Contains("virtual-modbus-1", problem);
            Assert.Contains("until released", problem);

            var delete = await _client.DeleteAsync($"/api/debug/sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            var start = await _client.PostAsJsonAsync("/api/production/start", new { jobId });
            Assert.True(start.StatusCode == HttpStatusCode.OK, $"start -> {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync()}");
        }
        finally
        {
            await _client.PostAsync("/api/production/stop", null);
            await _client.DeleteAsync($"/api/debug/sessions/{sessionId}");
        }
    }

    [Fact]
    public async Task RunningProduction_BlocksDebugSessionsOnLeasedAssets_ButAllowsOfflineOnes()
    {
        var jobId = await CreateAndPublishJob("prod-lease-owner");
        await ConfigureProduction(jobId);
        var start = await _client.PostAsJsonAsync("/api/production/start", new { jobId });
        Assert.True(start.StatusCode == HttpStatusCode.OK, $"start -> {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync()}");
        try
        {
            var conflicting = await _client.PostAsJsonAsync("/api/debug/sessions", new
            {
                workflow = DeviceWorkflow("debug-lease-conflict"),
                options = new { mode = "Breakpoints", breakpoints = new[] { "write" }, allowSideEffects = true }
            });
            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
            var problem = await conflicting.Content.ReadAsStringAsync();
            Assert.Contains("Production Runtime", problem);
            Assert.Contains("virtual-modbus-1", problem);

            // Offline workflows reference no hardware assets, so they acquire no lease and stay usable.
            var offline = await _client.PostAsJsonAsync("/api/debug/sessions", new
            {
                workflow = OfflineWorkflow(),
                options = new { mode = "Breakpoints", breakpoints = new[] { "threshold" } }
            });
            Assert.True(offline.StatusCode == HttpStatusCode.OK, $"offline -> {(int)offline.StatusCode}: {await offline.Content.ReadAsStringAsync()}");
            using var created = JsonDocument.Parse(await offline.Content.ReadAsStringAsync());
            var offlineSessionId = created.RootElement.GetProperty("sessionId").GetString();
            var delete = await _client.DeleteAsync($"/api/debug/sessions/{offlineSessionId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            await _client.PostAsync("/api/production/stop", null);
        }
    }

    [Fact]
    public async Task PausedDebugSession_BlocksRunsAndManualOperations_OnLeasedAssets_ButAllowsOfflineWork()
    {
        var sessionId = await CreateDebugSession(DeviceWorkflow("debug-lease-guard"));
        try
        {
            var conflictingRun = await _client.PostAsJsonAsync("/api/run", DeviceWorkflow("run-under-lease"));
            Assert.Equal(HttpStatusCode.Conflict, conflictingRun.StatusCode);
            Assert.Contains("Debug Session", await conflictingRun.Content.ReadAsStringAsync());

            // Offline workflows reference no hardware, so runs stay usable under a paused session.
            var offlineRun = await _client.PostAsJsonAsync("/api/run", OfflineWorkflow());
            Assert.True(offlineRun.StatusCode == HttpStatusCode.OK, $"offline run -> {(int)offlineRun.StatusCode}: {await offlineRun.Content.ReadAsStringAsync()}");

            var tagWrite = await _client.PutAsJsonAsync("/api/devices/virtual-modbus-1/tags/statusText", new { value = "MANUAL" });
            Assert.Equal(HttpStatusCode.Conflict, tagWrite.StatusCode);
            Assert.Contains("Debug Session", await tagWrite.Content.ReadAsStringAsync());

            // Dependency mutations are a pure "no holder" check: the paused session also blocks device deletion.
            var deviceDelete = await _client.DeleteAsync("/api/devices/virtual-modbus-1");
            Assert.Equal(HttpStatusCode.Conflict, deviceDelete.StatusCode);
            Assert.Contains("Debug Session", await deviceDelete.Content.ReadAsStringAsync());

            // Camera trigger is a manual hardware operation too: a paused session holding the camera blocks it.
            var cameraSessionId = await CreateDebugSession(CameraWorkflow("debug-lease-camera"), "acquire");
            try
            {
                var cameraTrigger = await _client.PostAsync("/api/cameras/virtual-camera-lease/trigger", null);
                Assert.Equal(HttpStatusCode.Conflict, cameraTrigger.StatusCode);
                Assert.Contains("Debug Session", await cameraTrigger.Content.ReadAsStringAsync());
            }
            finally
            {
                await _client.DeleteAsync($"/api/debug/sessions/{cameraSessionId}");
            }

            var delete = await _client.DeleteAsync($"/api/debug/sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            // After the session is released, the manual request-scoped lease is free again.
            var tagWriteAfter = await _client.PutAsJsonAsync("/api/devices/virtual-modbus-1/tags/statusText", new { value = "MANUAL" });
            Assert.True(tagWriteAfter.StatusCode == HttpStatusCode.OK, $"tag write -> {(int)tagWriteAfter.StatusCode}: {await tagWriteAfter.Content.ReadAsStringAsync()}");
        }
        finally
        {
            await _client.DeleteAsync($"/api/debug/sessions/{sessionId}");
        }
    }

    [Fact]
    public async Task CompletedRun_ReleasesItsLease_AllowingProductionStart()
    {
        var jobId = await CreateAndPublishJob("prod-after-run");
        await ConfigureProduction(jobId);

        var run = await _client.PostAsJsonAsync("/api/run", DeviceWorkflow("run-then-production"));
        Assert.True(run.StatusCode == HttpStatusCode.OK, $"run -> {(int)run.StatusCode}: {await run.Content.ReadAsStringAsync()}");

        try
        {
            // The run-scoped lease must be gone once the handler completed, otherwise production cannot lock the device.
            var start = await _client.PostAsJsonAsync("/api/production/start", new { jobId });
            Assert.True(start.StatusCode == HttpStatusCode.OK, $"start -> {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync()}");
        }
        finally
        {
            await _client.PostAsync("/api/production/stop", null);
        }
    }

    private async Task<string> CreateDebugSession(object workflow, string breakpointNode = "write")
    {
        var create = await _client.PostAsJsonAsync("/api/debug/sessions", new
        {
            workflow,
            options = new { mode = "Breakpoints", breakpoints = new[] { breakpointNode }, allowSideEffects = true }
        });
        Assert.True(create.StatusCode == HttpStatusCode.OK, $"create session -> {(int)create.StatusCode}: {await create.Content.ReadAsStringAsync()}");
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<string> CreateAndPublishJob(string idPrefix)
    {
        var jobId = $"{idPrefix}-{Guid.NewGuid():N}"[..32];
        var create = await _client.PostAsJsonAsync("/api/jobs", new
        {
            id = jobId,
            name = "Lease Arbitration Job",
            description = (string?)null,
            note = "device-lease-arbitration-test",
            workflow = DeviceWorkflow("lease-arbitration-workflow")
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var publish = await _client.PostAsync($"/api/jobs/{jobId}/versions/1/publish", null);
        Assert.True(publish.StatusCode == HttpStatusCode.OK, $"publish -> {(int)publish.StatusCode}: {await publish.Content.ReadAsStringAsync()}");
        return jobId;
    }

    private async Task ConfigureProduction(string jobId)
    {
        var config = await _client.PutAsJsonAsync("/api/production/config", new
        {
            jobId,
            autoStart = false,
            cycleDelayMs = 250,
            maxCycleMs = 15000,
            maxConsecutiveFailures = 3,
            autoRecover = true,
            recoveryDelayMs = 1000,
            stopTimeoutMs = 5000,
            ptpDriftGuardEnabled = false,
            synchronizationHealthGuardEnabled = false
        });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
    }

    /// <summary>References virtual-modbus-1, so the workflow's lease covers that device.</summary>
    private static object DeviceWorkflow(string id) => new
    {
        id,
        name = "Lease Arbitration Device Workflow",
        nodes = new object[]
        {
            new
            {
                id = "write", type = "device.writeTag", name = "Write Status",
                parameters = new { deviceId = "virtual-modbus-1", tagId = "statusText", valueType = "String", fallbackValue = "LEASE", autoConnect = true }
            }
        },
        edges = Array.Empty<object>()
    };

    /// <summary>References virtual-camera-lease, so the workflow's lease covers that camera.</summary>
    private static object CameraWorkflow(string id) => new
    {
        id,
        name = "Lease Arbitration Camera Workflow",
        nodes = new object[]
        {
            new
            {
                id = "acquire", type = "image.acquire", name = "Acquire Camera",
                parameters = new { cameraId = "virtual-camera-lease", autoConnect = true }
            }
        },
        edges = Array.Empty<object>()
    };

    /// <summary>Touches no device/robot/camera, so it requires no hardware lease.</summary>
    private static object OfflineWorkflow() => new
    {
        id = "lease-arbitration-offline",
        name = "Lease Arbitration Offline",
        nodes = new object[]
        {
            new { id = "source", type = "image.synthetic", name = "Synthetic", parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 } },
            new { id = "threshold", type = "image.threshold", name = "Threshold", parameters = new { threshold = 100 } }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
            new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
        }
    };
}