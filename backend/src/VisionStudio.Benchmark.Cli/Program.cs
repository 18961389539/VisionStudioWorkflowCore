using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const int ExitSuccess = 0;
const int ExitRegressionGateFailed = 10;
const int ExitBenchmarkFailed = 11;
const int ExitInfrastructureError = 12;
const int ExitTimedOut = 13;

try
{
    var options = CliOptions.Parse(args);
    if (options.ShowHelp)
    {
        CliOptions.PrintHelp();
        return ExitSuccess;
    }

    if (string.IsNullOrWhiteSpace(options.SpecPath))
        throw new CliException("--spec is required.");
    if (!File.Exists(options.SpecPath))
        throw new CliException($"CI spec '{options.SpecPath}' was not found.");

    var specText = await File.ReadAllTextAsync(options.SpecPath);
    var specSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specText))).ToLowerInvariant();
    using var specDoc = JsonDocument.Parse(specText);
    var root = specDoc.RootElement;
    var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaEl) ? schemaEl.GetInt32() : 0;
    if (schemaVersion != 1) throw new CliException($"Unsupported CI spec schemaVersion {schemaVersion}. Expected 1.");
    if (!root.TryGetProperty("benchmark", out var benchmark) || benchmark.ValueKind != JsonValueKind.Object)
        throw new CliException("CI spec does not contain a benchmark object.");

    var specName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "plugin-performance-gate" : "plugin-performance-gate";
    var requireRegressionPass = true;
    var failOnBenchmarkFailure = true;
    if (root.TryGetProperty("gate", out var gate) && gate.ValueKind == JsonValueKind.Object)
    {
        if (gate.TryGetProperty("requireRegressionPass", out var requireEl) && requireEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            requireRegressionPass = requireEl.GetBoolean();
        if (gate.TryGetProperty("failOnBenchmarkFailure", out var failEl) && failEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            failOnBenchmarkFailure = failEl.GetBoolean();
    }

    var cookies = new CookieContainer();
    using var handler = new HttpClientHandler { CookieContainer = cookies };
    using var client = new HttpClient(handler) { BaseAddress = new Uri(EnsureTrailingSlash(options.Server)) };
    client.Timeout = TimeSpan.FromSeconds(Math.Max(15, options.HttpTimeoutSeconds));

    var token = options.Token ?? Environment.GetEnvironmentVariable("VISIONSTUDIO_BENCH_TOKEN");
    var username = options.Username ?? Environment.GetEnvironmentVariable("VISIONSTUDIO_BENCH_USERNAME");
    var password = options.Password ?? Environment.GetEnvironmentVariable("VISIONSTUDIO_BENCH_PASSWORD");
    if (!string.IsNullOrWhiteSpace(token))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    else
    {
        var authStatus = await GetJsonAsync(client, "api/auth/status");
        var securityEnabled = authStatus.RootElement.TryGetProperty("enabled", out var enabledEl) && enabledEl.GetBoolean();
        if (securityEnabled)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                throw new CliException("VisionStudio security is enabled. Set --token or --username/--password (or VISIONSTUDIO_BENCH_TOKEN / VISIONSTUDIO_BENCH_USERNAME / VISIONSTUDIO_BENCH_PASSWORD).");
            using var loginContent = JsonContent(new { username, password });
            using var loginResponse = await client.PostAsync("api/auth/login", loginContent);
            await EnsureSuccessAsync(loginResponse, "login");
        }
    }

    Console.WriteLine($"VisionStudio plugin performance gate: {specName}");
    Console.WriteLine($"Server: {client.BaseAddress}");
    Console.WriteLine($"CI spec SHA-256: {specSha256}");
    Console.WriteLine("Submitting benchmark...");

    using var startContent = new StringContent(benchmark.GetRawText(), Encoding.UTF8, "application/json");
    using var startResponse = await client.PostAsync("api/plugin-benchmarks", startContent);
    await EnsureSuccessAsync(startResponse, "start benchmark");
    var startJson = await startResponse.Content.ReadAsStringAsync();
    using var startDoc = JsonDocument.Parse(startJson);
    var runId = startDoc.RootElement.GetProperty("runId").GetString() ?? throw new CliException("Benchmark API did not return runId.");
    Console.WriteLine($"Run: {runId}");

    var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, options.TimeoutSeconds));
    string finalJson = startJson;
    string status = startDoc.RootElement.GetProperty("status").GetString() ?? "Running";
    while (!IsTerminal(status))
    {
        if (DateTimeOffset.UtcNow >= deadline)
        {
            await BestEffortCancelAsync(client, runId);
            var timeoutSummary = $"Benchmark {runId} exceeded timeout of {options.TimeoutSeconds}s and cancellation was requested.";
            Console.Error.WriteLine(timeoutSummary);
            await WriteTextAsync(options.OutputPath, JsonSerializer.Serialize(new { schemaVersion = 1, specName, specSha256, runId, evaluatedAt = DateTimeOffset.UtcNow, passed = false, exitCode = ExitTimedOut, status = "TimedOut", error = timeoutSummary }, CiJson.Options));
            await WriteJUnitAsync(options.JunitPath, specName, false, timeoutSummary, null);
            await WriteSummaryAsync(options.SummaryPath, specName, runId, "TIMED_OUT", null, timeoutSummary);
            return ExitTimedOut;
        }
        await Task.Delay(Math.Max(100, options.PollMs));
        using var response = await client.GetAsync($"api/plugin-benchmarks/{Uri.EscapeDataString(runId)}");
        await EnsureSuccessAsync(response, "poll benchmark");
        finalJson = await response.Content.ReadAsStringAsync();
        using var pollDoc = JsonDocument.Parse(finalJson);
        status = pollDoc.RootElement.GetProperty("status").GetString() ?? "Unknown";
        Console.Write($"\rStatus: {status,-12}");
    }
    Console.WriteLine();

    using var finalDoc = JsonDocument.Parse(finalJson);
    var error = finalDoc.RootElement.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String ? errorEl.GetString() : null;
    string? gateStatus = null;
    string gateReason = string.Empty;
    if (finalDoc.RootElement.TryGetProperty("regressionGate", out var regression) && regression.ValueKind == JsonValueKind.Object)
    {
        gateStatus = regression.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (regression.TryGetProperty("reasons", out var reasonsEl) && reasonsEl.ValueKind == JsonValueKind.Array)
            gateReason = string.Join(" ", reasonsEl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
    }

    var benchmarkOk = status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    var regressionOk = !requireRegressionPass || string.Equals(gateStatus, "PASS", StringComparison.OrdinalIgnoreCase);
    var passed = benchmarkOk && regressionOk;
    var failureMessage = !benchmarkOk
        ? $"Benchmark status is {status}. {error}".Trim()
        : !regressionOk
            ? $"Regression gate is {gateStatus ?? "missing"}. {gateReason}".Trim()
            : string.Empty;
    var exitCode = !benchmarkOk && failOnBenchmarkFailure ? ExitBenchmarkFailed : benchmarkOk && !regressionOk ? ExitRegressionGateFailed : ExitSuccess;
    var evidence = new
    {
        schemaVersion = 1,
        specName,
        specSha256,
        server = client.BaseAddress?.ToString(),
        runId,
        evaluatedAt = DateTimeOffset.UtcNow,
        passed,
        exitCode,
        benchmark = finalDoc.RootElement.Clone()
    };
    await WriteTextAsync(options.OutputPath, JsonSerializer.Serialize(evidence, CiJson.Options));

    await WriteJUnitAsync(options.JunitPath, specName, passed, failureMessage, finalDoc.RootElement);
    await WriteSummaryAsync(options.SummaryPath, specName, runId, gateStatus ?? status, finalDoc.RootElement, failureMessage);

    PrintFinal(finalDoc.RootElement, gateStatus);
    return exitCode;
}
catch (CliException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return ExitInfrastructureError;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"HTTP ERROR: {ex.Message}");
    return ExitInfrastructureError;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return ExitInfrastructureError;
}

static bool IsTerminal(string status) => status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || status.Equals("Failed", StringComparison.OrdinalIgnoreCase) || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
static StringContent JsonContent<T>(T value) => new(JsonSerializer.Serialize(value, CiJson.Options), Encoding.UTF8, "application/json");

static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
{
    using var response = await client.GetAsync(path);
    await EnsureSuccessAsync(response, $"GET {path}");
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode) return;
    var body = await response.Content.ReadAsStringAsync();
    string detail = body;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("detail", out var el)) detail = el.GetString() ?? body;
    }
    catch { }
    throw new CliException($"Failed to {operation}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
}

static async Task BestEffortCancelAsync(HttpClient client, string runId)
{
    try { using var _ = await client.PostAsync($"api/plugin-benchmarks/{Uri.EscapeDataString(runId)}/cancel", content: null); }
    catch { }
}

static async Task WriteTextAsync(string? path, string text)
{
    if (string.IsNullOrWhiteSpace(path)) return;
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, text + Environment.NewLine);
}

static async Task WriteJUnitAsync(string? path, string name, bool passed, string failureMessage, JsonElement? result)
{
    if (string.IsNullOrWhiteSpace(path)) return;
    var testcase = new XElement("testcase", new XAttribute("classname", "VisionStudio.PluginPerformance"), new XAttribute("name", name));
    if (!passed) testcase.Add(new XElement("failure", new XAttribute("message", failureMessage), failureMessage));
    if (result is not null) testcase.Add(new XElement("system-out", JsonSerializer.Serialize(result.Value, CiJson.Options)));
    var suite = new XElement("testsuite", new XAttribute("name", "VisionStudio Plugin Performance Gate"), new XAttribute("tests", 1), new XAttribute("failures", passed ? 0 : 1), testcase);
    var document = new XDocument(new XElement("testsuites", suite));
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, document.ToString());
}

static async Task WriteSummaryAsync(string? configuredPath, string name, string runId, string status, JsonElement? result, string failureMessage)
{
    var path = configuredPath ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrWhiteSpace(path)) return;
    var lines = new List<string>
    {
        $"## VisionStudio Plugin Performance Gate — {name}",
        "",
        $"- Run: `{runId}`",
        $"- Result: **{status}**"
    };
    if (result is not null)
    {
        var root = result.Value;
        if (root.TryGetProperty("pluginId", out var plugin)) lines.Add($"- Plugin: `{plugin.GetString()}` `{GetString(root, "pluginVersion")}`");
        if (root.TryGetProperty("recommendation", out var rec) && rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("recommendedPoolSize", out var pool))
            lines.Add($"- Recommended pool: **{pool.GetInt32()}**");
        if (root.TryGetProperty("regressionGate", out var gate) && gate.ValueKind == JsonValueKind.Object)
        {
            lines.Add("");
            lines.Add("| Metric | Result |");
            lines.Add("| --- | ---: |");
            AddMetric(lines, gate, "p95DeltaPercent", "p95 delta", "%");
            AddMetric(lines, gate, "p99DeltaPercent", "p99 delta", "%");
            AddMetric(lines, gate, "throughputRatio", "Throughput ratio", "x");
            AddMetric(lines, gate, "workingSetDeltaPercent", "Working set delta", "%");
        }
    }
    if (!string.IsNullOrWhiteSpace(failureMessage)) { lines.Add(""); lines.Add($"> {failureMessage}"); }
    lines.Add("");
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.AppendAllLinesAsync(full, lines);
}

static void AddMetric(List<string> lines, JsonElement gate, string property, string label, string suffix)
{
    if (!gate.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number) return;
    lines.Add($"| {label} | {el.GetDouble():0.####}{suffix} |");
}

static string GetString(JsonElement root, string property) => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty;

static void PrintFinal(JsonElement root, string? gateStatus)
{
    Console.WriteLine($"Plugin: {GetString(root, "pluginId")} {GetString(root, "pluginVersion")}");
    if (root.TryGetProperty("recommendation", out var recommendation) && recommendation.ValueKind == JsonValueKind.Object && recommendation.TryGetProperty("recommendedPoolSize", out var pool))
        Console.WriteLine($"Recommended pool: {pool.GetInt32()}");
    Console.WriteLine($"Regression gate: {gateStatus ?? "n/a"}");
    if (root.TryGetProperty("regressionGate", out var gate) && gate.ValueKind == JsonValueKind.Object && gate.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
        foreach (var reason in reasons.EnumerateArray()) Console.WriteLine($"  - {reason.GetString()}");
}

sealed class CliException(string message) : Exception(message);

sealed record CliOptions(
    string Server,
    string? SpecPath,
    string? Token,
    string? Username,
    string? Password,
    string? OutputPath,
    string? JunitPath,
    string? SummaryPath,
    int PollMs,
    int TimeoutSeconds,
    int HttpTimeoutSeconds,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help") { help = true; continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new CliException($"Unexpected argument '{arg}'.");
            var key = arg[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new CliException($"Missing value for --{key}.");
            map[key] = args[++i];
        }
        string? Get(string key) => map.TryGetValue(key, out var value) ? value : null;
        int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        return new CliOptions(
            Get("server") ?? "http://127.0.0.1:5080",
            Get("spec"), Get("token"), Get("username"), Get("password"),
            Get("output") ?? "artifacts/plugin-benchmark-result.json",
            Get("junit") ?? "artifacts/plugin-benchmark-junit.xml", Get("summary"),
            GetInt("poll-ms", 1000), GetInt("timeout-seconds", 3600), GetInt("http-timeout-seconds", 60), help);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("VisionStudio.Benchmark.Cli — V0.59 headless plugin performance gate");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project backend/src/VisionStudio.Benchmark.Cli -- --spec ci-spec.json [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --server URL                 VisionStudio Host (default http://127.0.0.1:5080)");
        Console.WriteLine("  --spec FILE                  Exported V0.59 benchmark CI spec (required)");
        Console.WriteLine("  --token TOKEN                Bearer token; env VISIONSTUDIO_BENCH_TOKEN is also supported");
        Console.WriteLine("  --username USER              Login username (or VISIONSTUDIO_BENCH_USERNAME)");
        Console.WriteLine("  --password PASSWORD          Login password (or VISIONSTUDIO_BENCH_PASSWORD)");
        Console.WriteLine("  --output FILE                Machine-readable final benchmark JSON");
        Console.WriteLine("  --junit FILE                 Optional JUnit XML result");
        Console.WriteLine("  --summary FILE               Optional Markdown summary; GITHUB_STEP_SUMMARY is auto-detected");
        Console.WriteLine("  --poll-ms N                  Poll interval (default 1000)");
        Console.WriteLine("  --timeout-seconds N          Overall benchmark timeout (default 3600)");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0=pass, 10=regression gate fail, 11=benchmark failed/cancelled, 12=infrastructure/config error, 13=timeout.");
    }
}

static class CiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
