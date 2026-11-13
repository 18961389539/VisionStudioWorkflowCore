using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VisionStudio.Engine.Scripting;
using VisionStudio.Scripting;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Phase 2（2026-11-13）：宿主侧脚本后端必须能在 InProcess 与 WorkerProcess 之间切换，
/// 且同一条脚本在两种后端上的标量/JSON/日志输出一致。
/// </summary>
public sealed class ScriptWorkerSupervisorTests
{
    [Fact]
    public async Task WorkerBackend_ExecutesScriptInWorkerProcess()
    {
        using var supervisor = CreateSupervisor();
        var outcome = await supervisor.RunAsync(
            "Output.Number = Input.Number(\"a\") * 2;",
            ExecutionOptions(5_000),
            new Dictionary<string, object?> { ["a"] = 21d },
            null,
            CancellationToken.None);

        using (outcome)
            Assert.Equal(42d, outcome.Number);
    }

    [Fact]
    public async Task InProcessAndWorker_ProduceTheSameScalarJsonAndLogs()
    {
        const string source = """
            Log("hello");
            Output.Number = Input.Number("a") + 1;
            Output.Text = "ok";
            Output.Boolean = true;
            Output.Json = "{\"x\":1}";
            """;
        var inputs = new Dictionary<string, object?> { ["a"] = 41d };
        var executionOptions = ExecutionOptions(5_000);

        using var inProcess = await new InProcessScriptExecutionBackend()
            .RunAsync(source, executionOptions, inputs, null, CancellationToken.None);
        using var supervisor = CreateSupervisor();
        using var worker = await supervisor.RunAsync(source, executionOptions, inputs, null, CancellationToken.None);

        Assert.Equal(inProcess.Number, worker.Number);
        Assert.Equal(inProcess.Text, worker.Text);
        Assert.Equal(inProcess.Boolean, worker.Boolean);
        Assert.Equal(inProcess.Json, worker.Json);
        Assert.Equal(inProcess.Logs, worker.Logs);
        Assert.Equal(inProcess.SourceHash, worker.SourceHash);
    }

    [Fact]
    public void WorkerMode_MissingAssembly_FailsFast()
    {
        using var environment = new TempWebHostEnvironment();
        var options = Options.Create(new ScriptWorkerOptions
        {
            IsolationMode = ScriptIsolationMode.WorkerProcess,
            WorkerAssemblyPath = Path.Combine(environment.ContentRootPath, "missing-script-worker.dll")
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ScriptWorkerSupervisor(environment, options, NullLogger<ScriptWorkerSupervisor>.Instance));
        Assert.Contains("VisionStudio.Script.Worker.dll", error.Message);
    }

    private static ScriptExecutionOptions ExecutionOptions(int timeoutMs) =>
        new(timeoutMs, 1_000_000, 65_536, 16_777_216);

    private static ScriptWorkerSupervisor CreateSupervisor()
    {
        var environment = new TempWebHostEnvironment();
        var options = Options.Create(new ScriptWorkerOptions
        {
            IsolationMode = ScriptIsolationMode.WorkerProcess,
            WorkerAssemblyPath = LocateWorkerDll(),
            SharedMemoryEnabled = false,
            PoolSize = 1,
            ExecutionTimeoutMs = 30_000,
            JobObjectMemoryLimitMb = 512,
            SharedMemoryRootPath = Path.Combine(environment.ContentRootPath, "script-worker-memory")
        });
        return new ScriptWorkerSupervisor(environment, options, NullLogger<ScriptWorkerSupervisor>.Instance);
    }

    private static string LocateWorkerDll()
    {
        // .../backend/tests/VisionStudio.Api.Tests/bin/<Configuration>/net10.0/
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDir.Parent?.Name ?? "Release";
        var backend = baseDir.Parent?.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("Could not resolve the backend directory from the test output path.");
        return Path.Combine(backend, "src", "VisionStudio.Script.Worker", "bin", configuration, "net10.0", "VisionStudio.Script.Worker.dll");
    }
}
