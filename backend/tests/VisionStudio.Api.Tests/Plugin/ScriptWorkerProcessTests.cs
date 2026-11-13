using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

/// <summary>
/// Phase 1（2026-11-13）：脚本 Worker 必须能独立完成握手、发现与一次真实脚本执行。
/// 不经过宿主图执行，直接验证命名管道协议与 script.csharp 的进程级闭环。
/// </summary>
public sealed class ScriptWorkerProcessTests
{
    [Fact]
    public async Task Worker_HandshakesDiscoversAndExecutesScript()
    {
        var workerDll = LocateWorkerDll();
        Assert.True(File.Exists(workerDll), $"Script worker assembly was not built: {workerDll}");

        var pipeName = "visionstudio-script-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var process = StartWorker(workerDll, pipeName);
        try
        {
            using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await server.WaitForConnectionAsync(connect.Token);

            var ping = await ExchangeAsync(server, new PluginWorkerRequest { Operation = "ping" });
            Assert.True(ping.Success, ping.Error);

            var discovery = await ExchangeAsync(server, new PluginWorkerRequest { Operation = "discover" });
            Assert.True(discovery.Success, discovery.Error);
            Assert.Equal(VisionPluginWorkerProtocol.Version, discovery.Discovery!.ProtocolVersion);
            Assert.Contains(discovery.Discovery.Tools, tool => tool.Catalog.Type == "script.csharp");

            var parameters = new Dictionary<string, JsonElement>
            {
                ["source"] = JsonSerializer.SerializeToElement("Output.Number = Input.Number(\"a\") * 2;")
            };
            var execute = await ExchangeAsync(server, new PluginWorkerRequest
            {
                Operation = "execute",
                NodeType = "script.csharp",
                Node = new NodeDefinition("n1", "script.csharp", null, null, parameters),
                Inputs = new Dictionary<string, PluginWorkerValue>
                {
                    ["a"] = PluginWorkerValueCodec.Encode(VisionValue.Double(21))
                }
            });
            Assert.True(execute.Success, execute.Error);
            var number = PluginWorkerValueCodec.Decode(execute.Result!.Outputs["number"], null);
            Assert.Equal(42d, number.Require<double>("number"));

            var shutdown = await ExchangeAsync(server, new PluginWorkerRequest { Operation = "shutdown" });
            Assert.True(shutdown.Success, shutdown.Error);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(exit.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<PluginWorkerResponse> ExchangeAsync(NamedPipeServerStream pipe, PluginWorkerRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await PluginWorkerWire.WriteAsync(pipe, request, timeout.Token);
        return await PluginWorkerWire.ReadAsync<PluginWorkerResponse>(pipe, timeout.Token);
    }

    private static Process StartWorker(string workerDll, string pipeName)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false
        };
        start.ArgumentList.Add(workerDll);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--worker-kind");
        start.ArgumentList.Add("script");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the script worker process.");
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
