using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionStudio.Abstractions;
using VisionStudio.Api.Workers;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Scripting;
using VisionStudio.Scripting;

namespace VisionStudio.Api;

/// <summary>Where script.csharp executes. InProcess preserves the historical behavior.</summary>
public enum ScriptIsolationMode
{
    InProcess = 0,
    WorkerProcess = 1
}

public sealed class ScriptWorkerOptions
{
    public ScriptIsolationMode IsolationMode { get; set; } = ScriptIsolationMode.InProcess;
    public string? WorkerAssemblyPath { get; set; }
    public int StartupTimeoutMs { get; set; } = 10_000;
    public int ExecutionTimeoutMs { get; set; } = 15_000;
    public int MaxWorkingSetMb { get; set; } = 1024;
    public int MaxRestartsPerMinute { get; set; } = 5;
    public int PoolSize { get; set; } = 1;
    public bool SharedMemoryEnabled { get; set; } = true;
    public int SharedMemoryThresholdBytes { get; set; } = 1_048_576;
    public string SharedMemoryRootPath { get; set; } = "data/script-workers/shared-memory";
    public int SharedMemoryStaleMinutes { get; set; } = 30;
    public int JobObjectMemoryLimitMb { get; set; } = 1024;
    public int JobObjectCpuRatePercent { get; set; }
}

/// <summary>
/// Host-side pool for <c>VisionStudio.Script.Worker</c>. It reuses the plugin worker's process pool,
/// pipe protocol, shared-memory codec and restart circuit; only the launch path is script-specific.
/// </summary>
public sealed class ScriptWorkerSupervisor : IScriptExecutionBackend, IDisposable
{
    private readonly ScriptWorkerOptions _options;
    private readonly WorkerPool _pool;

    public ScriptWorkerSupervisor(
        IWebHostEnvironment environment,
        IOptions<ScriptWorkerOptions> options,
        ILogger<ScriptWorkerSupervisor> logger)
    {
        _options = options.Value;
        var workerAssembly = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.WorkerAssemblyPath)
            ? Path.Combine(AppContext.BaseDirectory, "VisionStudio.Script.Worker.dll")
            : Path.IsPathRooted(_options.WorkerAssemblyPath)
                ? _options.WorkerAssemblyPath
                : Path.Combine(environment.ContentRootPath, _options.WorkerAssemblyPath));
        var sharedRoot = Path.GetFullPath(Path.IsPathRooted(_options.SharedMemoryRootPath)
            ? _options.SharedMemoryRootPath
            : Path.Combine(environment.ContentRootPath, _options.SharedMemoryRootPath));
        var threshold = _options.SharedMemoryEnabled ? _options.SharedMemoryThresholdBytes : 0;
        if (threshold < 0) throw new InvalidOperationException("Script worker shared-memory threshold cannot be negative.");
        if (_options.IsolationMode == ScriptIsolationMode.WorkerProcess && !File.Exists(workerAssembly))
        {
            throw new InvalidOperationException(
                $"ScriptWorkers:IsolationMode=WorkerProcess but the worker assembly was not found at '{workerAssembly}'. " +
                "Publish VisionStudio.Script.Worker.dll beside the API host or configure ScriptWorkers:WorkerAssemblyPath.");
        }

        var poolOptions = new PluginWorkerOptions
        {
            StartupTimeoutMs = _options.StartupTimeoutMs,
            ExecutionTimeoutMs = _options.ExecutionTimeoutMs,
            MaxWorkingSetMb = _options.MaxWorkingSetMb,
            MaxRestartsPerMinute = _options.MaxRestartsPerMinute,
            DefaultPoolSize = Math.Max(1, _options.PoolSize),
            MaxPoolSize = Math.Max(1, _options.PoolSize),
            SharedMemoryEnabled = _options.SharedMemoryEnabled,
            SharedMemoryThresholdBytes = _options.SharedMemoryThresholdBytes,
            SharedMemoryRootPath = sharedRoot,
            SharedMemoryStaleMinutes = _options.SharedMemoryStaleMinutes,
            JobObjectMemoryLimitMb = _options.JobObjectMemoryLimitMb,
            JobObjectCpuRatePercent = _options.JobObjectCpuRatePercent
        };
        _pool = new WorkerPool(
            "builtin.script",
            workerAssembly,
            workerAssembly,
            Math.Max(1, _options.PoolSize),
            threshold,
            sharedRoot,
            poolOptions,
            logger);
    }

    public PluginWorkerStatus Status => _pool.Status;

    public Task RestartAsync(CancellationToken cancellationToken = default) => _pool.RestartAsync(cancellationToken);

    public async ValueTask<ScriptExecutionOutcome> RunAsync(
        string source,
        ScriptExecutionOptions options,
        IReadOnlyDictionary<string, object?> inputs,
        OpenCvSharp.Mat? image,
        CancellationToken cancellationToken)
    {
        var encodedInputs = new Dictionary<string, PluginWorkerValue>(StringComparer.OrdinalIgnoreCase);
        var node = new NodeDefinition("script-worker", "script.csharp", null, null, new Dictionary<string, JsonElement>
        {
            ["source"] = JsonSerializer.SerializeToElement(source),
            ["timeoutMs"] = JsonSerializer.SerializeToElement(options.TimeoutMs),
            ["maxTicks"] = JsonSerializer.SerializeToElement(options.MaxTicks),
            ["maxOutputBytes"] = JsonSerializer.SerializeToElement(options.MaxOutputBytes),
            ["maxImagePixels"] = JsonSerializer.SerializeToElement(options.MaxImagePixels)
        });

        try
        {
            if (image is not null)
            {
                using var owned = VisionImage.Own(image);
                encodedInputs["image"] = PluginWorkerValueCodec.Encode(VisionValue.Image(owned), _pool.SharedMemory);
            }
            foreach (var (name, value) in inputs)
            {
                if (value is not null)
                    encodedInputs[name] = PluginWorkerValueCodec.Encode(ToWireValue(name, value), _pool.SharedMemory);
            }

            // The runner enforces the script's own budget; this IPC timeout is only a safety net for
            // compile/scheduling overhead. The pool kills the worker if the net ever fires.
            var ipcTimeoutMs = Math.Max(_options.ExecutionTimeoutMs, options.TimeoutMs + 3_000);
            var exchange = await _pool.SendAsync(new PluginWorkerRequest
            {
                Operation = "execute",
                NodeType = "script.csharp",
                Node = node,
                Inputs = encodedInputs
            }, startup: false, cancellationToken, ipcTimeoutMs);

            var result = exchange.Response.Result ?? throw new InvalidOperationException("Script worker returned no execution result.");
            VisionImage? outputImage = null;
            try
            {
                var decoded = result.Outputs.ToDictionary(
                    pair => pair.Key,
                    pair => PluginWorkerValueCodec.Decode(pair.Value, _pool.SharedMemory),
                    StringComparer.OrdinalIgnoreCase);
                if (decoded.TryGetValue("image", out var imageValue))
                {
                    outputImage = imageValue.Require<IVisionImage>("image") as VisionImage
                        ?? throw new InvalidOperationException("Script worker returned a non-owned image handle.");
                }

                var summary = PluginWorkerValueCodec.DecodeSummary(result.Summary);
                return new ScriptExecutionOutcome(
                    decoded.TryGetValue("result", out var resultValue) ? resultValue.Value : null,
                    outputImage,
                    decoded.TryGetValue("number", out var number) ? number.Require<double>("number") : null,
                    decoded.TryGetValue("text", out var text) ? text.Require<string>("text") : null,
                    decoded.TryGetValue("boolean", out var boolean) ? boolean.Require<bool>("boolean") : null,
                    decoded.TryGetValue("json", out var json) ? json.Require<string>("json") : null,
                    Strings(summary, "logs"),
                    Long(summary, "ticks"),
                    Number(summary, "compileMs", exchange.Response.Timing?.InputDecodeMs ?? 0),
                    Number(summary, "executeMs", exchange.Response.Timing?.PluginExecuteMs ?? 0),
                    Text(summary, "sourceHash"));
            }
            catch
            {
                outputImage?.Dispose();
                throw;
            }
            finally
            {
                if (_pool.SharedMemory is not null)
                    foreach (var value in result.Outputs.Values) _pool.SharedMemory.Delete(value);
            }
        }
        finally
        {
            if (_pool.SharedMemory is not null)
                foreach (var value in encodedInputs.Values) _pool.SharedMemory.Delete(value);
        }
    }

    public void Dispose() => _pool.Dispose();

    private static VisionValue ToWireValue(string name, object value) => value switch
    {
        double number => VisionValue.Double(number),
        long integer => VisionValue.Integer(integer),
        bool boolean => VisionValue.Boolean(boolean),
        string text => VisionValue.String(text),
        double[] numbers => VisionValue.NumberList(numbers),
        string[] strings => VisionValue.StringList(strings),
        ScriptPoint[] points => VisionValue.PointList(points.Select(point => new VisionPoint2D(point.X, point.Y)).ToArray()),
        IReadOnlyList<VisionPoint2D> points => VisionValue.PointList(points),
        _ => throw new InvalidOperationException(
            $"script.csharp input '{name}' has unsupported type {value.GetType().FullName} for worker transport.")
    };

    private static string Text(IReadOnlyDictionary<string, object?> summary, string key) =>
        summary.TryGetValue(key, out var raw) && raw is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static double Number(IReadOnlyDictionary<string, object?> summary, string key, double fallback) =>
        summary.TryGetValue(key, out var raw) && raw is JsonElement element
            ? element.ValueKind == JsonValueKind.Number ? element.GetDouble()
                : element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsed) ? parsed
                : fallback
            : fallback;

    private static long Long(IReadOnlyDictionary<string, object?> summary, string key) =>
        summary.TryGetValue(key, out var raw) && raw is JsonElement element
            ? element.ValueKind == JsonValueKind.Number ? element.GetInt64()
                : element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var parsed) ? parsed
                : 0
            : 0;

    private static IReadOnlyList<string> Strings(IReadOnlyDictionary<string, object?> summary, string key) =>
        summary.TryGetValue(key, out var raw) && raw is JsonElement element && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
            : [];
}
