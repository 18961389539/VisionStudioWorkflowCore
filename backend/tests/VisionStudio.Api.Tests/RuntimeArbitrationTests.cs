using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Production Runtime arbitration: while a locked production job owns an asset, manual I/O and
/// ad-hoc/debug runs that reference that asset are rejected; unrelated workflows stay runnable.
/// </summary>
public sealed class RuntimeArbitrationTests : IClassFixture<VisionStudioApiFactory>
{
    private readonly HttpClient _client;

    public RuntimeArbitrationTests(VisionStudioApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ProductionRuntime_BlocksReferencedAssetsAndRuns_ButAllowsUnrelatedWorkflows()
    {
        var jobId = $"prod-arb-{Guid.NewGuid():N}"[..24];

        var create = await _client.PostAsJsonAsync("/api/jobs", new
        {
            id = jobId,
            name = "Production Arbitration",
            description = (string?)null,
            note = "runtime-arbitration-test",
            workflow = ReferencingWorkflow()
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var publish = await _client.PostAsync($"/api/jobs/{jobId}/versions/1/publish", null);
        Assert.True(publish.StatusCode == HttpStatusCode.OK, $"publish -> {(int)publish.StatusCode}: {await publish.Content.ReadAsStringAsync()}");

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

        var start = await _client.PostAsJsonAsync("/api/production/start", new { jobId });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        try
        {
            var status = await _client.GetFromJsonAsync<JsonElement>("/api/production/status");
            Assert.True(status.GetProperty("productionLocked").GetBoolean());

            var tagWrite = await _client.PutAsJsonAsync("/api/devices/virtual-modbus-1/tags/statusText", new { value = "MANUAL" });
            Assert.Equal(HttpStatusCode.Conflict, tagWrite.StatusCode);

            var robotTarget = await _client.PostAsJsonAsync("/api/robots/virtual-abb-1/target", new
            {
                x = 520.0,
                y = 240.0,
                rDeg = 0.0,
                frame = "RobotBase",
                unit = "mm",
                robot = "ABB",
                guidanceMode = "Test",
                action = "SendTarget",
                waitForInPosition = false,
                timeoutMs = 2000,
                maxRetries = 0,
                retryDelayMs = 0,
                autoAck = true
            });
            Assert.Equal(HttpStatusCode.Conflict, robotTarget.StatusCode);

            var conflictingRun = await _client.PostAsJsonAsync("/api/run", ReferencingWorkflow());
            Assert.Equal(HttpStatusCode.Conflict, conflictingRun.StatusCode);

            var unrelatedRun = await _client.PostAsJsonAsync("/api/run", UnrelatedWorkflow());
            Assert.Equal(HttpStatusCode.OK, unrelatedRun.StatusCode);
        }
        finally
        {
            var stop = await _client.PostAsync("/api/production/stop", null);
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
            var stopped = await _client.GetFromJsonAsync<JsonElement>("/api/production/status");
            Assert.False(stopped.GetProperty("productionLocked").GetBoolean());
        }
    }

    /// <summary>References virtual-modbus-1 and virtual-abb-1 so production locks both assets.</summary>
    private static object ReferencingWorkflow() => new
    {
        id = "prod-arbitration-workflow",
        name = "Production Arbitration Workflow",
        nodes = new object[]
        {
            new
            {
                id = "write", type = "device.writeTag", name = "Write Status",
                parameters = new { deviceId = "virtual-modbus-1", tagId = "statusText", valueType = "String", fallbackValue = "PROD", autoConnect = true }
            },
            new
            {
                id = "pose", type = "robot.currentPose", name = "Read Pose",
                parameters = new { robotId = "virtual-abb-1", autoConnect = true }
            }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "write", sourcePort = "next", targetNodeId = "pose", targetPort = "exec", kind = "control" }
        }
    };

    /// <summary>Touches no device/robot/camera referenced by the locked production manifest.</summary>
    private static object UnrelatedWorkflow() => new
    {
        id = "prod-arbitration-unrelated",
        name = "Unrelated Workflow",
        nodes = new object[]
        {
            new
            {
                id = "source", type = "image.synthetic", name = "Synthetic",
                parameters = new { width = 320, height = 240, centerX = 160, centerY = 120, radius = 45 }
            },
            new
            {
                id = "threshold", type = "image.threshold", name = "Threshold",
                parameters = new { threshold = 100 }
            }
        },
        edges = new object[]
        {
            new { id = "c1", sourceNodeId = "source", sourcePort = "next", targetNodeId = "threshold", targetPort = "exec", kind = "control" },
            new { id = "d1", sourceNodeId = "source", sourcePort = "image", targetNodeId = "threshold", targetPort = "image", kind = "data" }
        }
    };
}
