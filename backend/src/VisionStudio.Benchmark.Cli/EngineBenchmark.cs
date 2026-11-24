using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Benchmark.Cli;

/// <summary>
/// P2 固定夹具引擎微基准（2026-11-23）。
///
/// 为什么需要它：单元测试用 <c>new Mat(...)</c> 现画输入，只能证明"结果对"，证明不了
/// "还是这个量级"。OpenCvSharp/JLVision 升级或预处理路径改动后，单测可以全绿而产线节拍
/// 已经翻倍。这里用提交进仓库的金样 PNG（与 GoldenWorkflowRegressionTests 共用）跑
/// 固定算法用例，只比较中位耗时，且只在超过容差（默认 25%）时判失败——夜间在固定机器上
/// 跑，避开 PR 噪声。
///
/// 口径：每个用例先热身，再取多轮样本的中位数；基线记录机器名，机器不一致时不比较
/// （跨机器的绝对耗时没有可比性），夜间 runner 第一次用 --update-baseline 钉住基线。
/// </summary>
internal static class EngineBenchmark
{
    private const int ExitSuccess = 0;
    private const int ExitRegression = 10;
    private const int ExitBenchmarkFailed = 11;
    private const int ExitInfrastructure = 12;
    private const double DefaultTolerancePercent = 25;
    private const string MatchingCase = "match.shape.managedChamfer.640x480";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return RunCore(args, stdout, stderr);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ENGINE BENCH ERROR: {ex.Message}");
            return ExitInfrastructure;
        }
    }

    private static int RunCore(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintHelp(stdout);
            return ExitSuccess;
        }

        var root = FindRepositoryRoot();
        var fixtures = Path.GetFullPath(options.Fixtures ?? Path.Combine(root, "backend", "tests", "VisionStudio.Engine.Tests", "Fixtures"));
        var baselinePath = Path.GetFullPath(options.Baseline ?? Path.Combine(root, "ci", "engine-benchmark-baseline.json"));
        var outputPath = Path.GetFullPath(options.Output ?? Path.Combine("artifacts", "engine-benchmark-result.json"));

        using var scratch = LoadFixture(fixtures, "scratch-640x480.png");
        using var edge = LoadFixture(fixtures, "edge-640x480.png");
        var upscaled = Upscale(scratch, 1280, 960);
        try
        {
            var cases = new[]
            {
                new BenchCase(
                    "preprocess.gaussian.640x480", "preprocess", scratch, null,
                    new GaussianBlurNode(), Node("image.blur.gaussian", """{"kernelSize":5}"""),
                    VerifyOutputSize(640, 480)),
                new BenchCase(
                    "preprocess.threshold.640x480", "preprocess", scratch, null,
                    new ThresholdNode(), Node("image.threshold", """{"threshold":60,"invert":true}"""),
                    VerifyThreshold(640, 480)),
                new BenchCase(
                    "preprocess.gaussian.1280x960", "preprocess", upscaled, null,
                    new GaussianBlurNode(), Node("image.blur.gaussian", """{"kernelSize":7}"""),
                    VerifyOutputSize(1280, 960)),
                new BenchCase(
                    MatchingCase, "matching", edge, edge,
                    new ShapeMatchNode(),
                    Node("match.shape", """
                        {
                          "engine": "managedChamfer",
                          "chamferCannyLow": 20,
                          "chamferCannyHigh": 60,
                          "chamferMaxModelPoints": 240,
                          "minScore": 0.6,
                          "maxTargets": 1,
                          "searchAngleStartDeg": 0,
                          "searchAngleEndDeg": 0,
                          "searchScaleMinPercent": 100,
                          "searchScaleMaxPercent": 100,
                          "pyramidLevels": 1
                        }
                        """),
                    VerifyMatching)
            };

            stdout.WriteLine("Engine algorithm benchmark (fixed golden fixtures)");
            stdout.WriteLine($"  Machine:  {options.MachineLabel}");
            stdout.WriteLine($"  Fixtures: {fixtures}");
            stdout.WriteLine($"  Baseline: {baselinePath}");
            stdout.WriteLine($"  Samples:  {options.Samples} (warmup {options.Warmup})");
            stdout.WriteLine();

            var measurements = new List<Measurement>(cases.Length);
            foreach (var benchCase in cases)
            {
                Measurement measurement;
                try
                {
                    measurement = Measure(benchCase, options.Warmup, options.Samples);
                }
                catch (Exception ex) when (ex is not IOException)
                {
                    stderr.WriteLine($"BENCHMARK FAILED: {benchCase.Id}: {ex.Message}");
                    return ExitBenchmarkFailed;
                }
                measurements.Add(measurement);
                stdout.WriteLine(
                    $"  {benchCase.Id,-36} median {measurement.MedianMs,8:0.000} ms   min {measurement.MinMs,8:0.000}   p90 {measurement.P90Ms,8:0.000}");
            }
            stdout.WriteLine();

            var recordedAt = DateTimeOffset.UtcNow;
            if (options.UpdateBaseline)
            {
                var baseline = new BaselineFile(
                    SchemaVersion: 1,
                    Machine: options.MachineLabel,
                    RecordedAt: recordedAt,
                    Samples: options.Samples,
                    TolerancePercent: options.TolerancePercent,
                    Cases: measurements.ToDictionary(
                        m => m.Id,
                        m => new BaselineCase(m.MedianMs, m.MinMs, m.P90Ms),
                        StringComparer.Ordinal));
                WriteJson(baselinePath, baseline);
                var updateRows = measurements.Select(m => Row(m, null, null, true, null)).ToArray();
                WriteEvidence(outputPath, options, measurements, updateRows, "BASELINE_UPDATED", gated: false, machineMismatch: false);
                stdout.WriteLine($"Baseline written to {baselinePath}");
                stdout.WriteLine("Commit this file after the dedicated benchmark machine produced it; timings are only comparable on the same machine.");
                return ExitSuccess;
            }

            if (!File.Exists(baselinePath))
                throw new InvalidOperationException($"Baseline '{baselinePath}' was not found. Run once with --update-baseline on the dedicated benchmark machine.");

            var loaded = JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(baselinePath), Json)
                ?? throw new InvalidOperationException($"Baseline '{baselinePath}' is empty or not valid JSON.");
            if (loaded.SchemaVersion != 1)
                throw new InvalidOperationException($"Unsupported baseline schemaVersion {loaded.SchemaVersion}; expected 1.");

            var machineMismatch = !string.Equals(loaded.Machine, options.MachineLabel, StringComparison.Ordinal);
            if (machineMismatch)
            {
                stdout.WriteLine($"WARNING: baseline was recorded on '{loaded.Machine}' but this machine is '{options.MachineLabel}'.");
                stdout.WriteLine("         Absolute timings are not comparable across machines, so the regression gate is skipped.");
                stdout.WriteLine("         Run once with --update-baseline on this machine to establish its baseline.");
                var skipRows = measurements.Select(m => Row(m, null, null, true, null)).ToArray();
                WriteEvidence(outputPath, options, measurements, skipRows, "MACHINE_MISMATCH", gated: false, machineMismatch: true);
                return ExitSuccess;
            }

            var tolerance = options.TolerancePercent;
            var rows = new List<CaseResult>(measurements.Count);
            var regressions = new List<string>();
            foreach (var measurement in measurements)
            {
                if (!loaded.Cases.TryGetValue(measurement.Id, out var baselineCase) || baselineCase.MedianMs <= 0)
                {
                    rows.Add(Row(measurement, null, null, true, "no baseline entry (new case)"));
                    continue;
                }
                var deltaPercent = (measurement.MedianMs - baselineCase.MedianMs) / baselineCase.MedianMs * 100;
                var passed = deltaPercent <= tolerance;
                if (!passed)
                    regressions.Add(
                        $"{measurement.Id}: median {measurement.MedianMs:0.000} ms vs baseline {baselineCase.MedianMs:0.000} ms (+{deltaPercent:0.0}% > {tolerance:0.#}%)");
                rows.Add(Row(measurement, baselineCase.MedianMs, deltaPercent, passed, null));
            }

            stdout.WriteLine($"Gate tolerance: +{tolerance.ToString("0.#", CultureInfo.InvariantCulture)}% median regression");
            if (regressions.Count > 0)
            {
                foreach (var regression in regressions)
                    stderr.WriteLine($"REGRESSION: {regression}");
                WriteEvidence(outputPath, options, measurements, rows.ToArray(), "REGRESSION", gated: true, machineMismatch: false);
                stderr.WriteLine("Engine performance gate FAILED.");
                return ExitRegression;
            }

            WriteEvidence(outputPath, options, measurements, rows.ToArray(), "PASS", gated: true, machineMismatch: false);
            stdout.WriteLine("Engine performance gate PASSED.");
            return ExitSuccess;
        }
        finally
        {
            upscaled.Dispose();
        }
    }

    private static Measurement Measure(BenchCase benchCase, int warmup, int samples)
    {
        for (var index = 0; index < warmup; index++)
        {
            var result = Execute(benchCase);
            benchCase.Verify(result);
            DisposeOutputs(result);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var timings = new List<double>(samples);
        for (var index = 0; index < samples; index++)
        {
            var result = ExecuteTimed(benchCase, out var elapsedMs);
            benchCase.Verify(result);
            DisposeOutputs(result);
            timings.Add(elapsedMs);
        }

        timings.Sort();
        return new Measurement(benchCase.Id, benchCase.Category, Median(timings), timings[0], Percentile(timings, 0.9));
    }

    private static NodeExecutorResult Execute(BenchCase benchCase) => ExecuteTimed(benchCase, out _);

    private static NodeExecutorResult ExecuteTimed(BenchCase benchCase, out double elapsedMs)
    {
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Borrow(benchCase.Image)) };
        if (benchCase.Template is not null)
            inputs["template"] = VisionValue.Image(VisionImage.Borrow(benchCase.Template));
        var context = new NodeExecutionContext(inputs);
        var watch = Stopwatch.StartNew();
        var result = benchCase.Executor.ExecuteAsync(context, benchCase.Node, CancellationToken.None).GetAwaiter().GetResult();
        watch.Stop();
        elapsedMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }

    private static void DisposeOutputs(NodeExecutorResult result)
    {
        foreach (var value in result.Outputs.Values)
            if (value.Value is VisionImage image) image.Dispose();
    }

    private static double Median(List<double> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static double Percentile(List<double> sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static Action<NodeExecutorResult> VerifyOutputSize(int width, int height) => result =>
    {
        var image = result.Outputs.TryGetValue("image", out var value)
            ? value.Require<IVisionImage>("image")
            : throw new InvalidOperationException("Benchmark node produced no image output.");
        if (image.Width != width || image.Height != height)
            throw new InvalidOperationException($"Output image is {image.Width}x{image.Height}, expected {width}x{height}.");
    };

    private static Action<NodeExecutorResult> VerifyThreshold(int width, int height) => result =>
    {
        VerifyOutputSize(width, height)(result);
        var ratio = result.Outputs.TryGetValue("foregroundRatio", out var value) ? value.Require<double>("foregroundRatio") : 0;
        if (!(ratio > 0) || !(ratio < 1))
            throw new InvalidOperationException($"Threshold foreground ratio {ratio.ToString("0.####", CultureInfo.InvariantCulture)} is degenerate; the fixture no longer exercises the algorithm.");
    };

    private static void VerifyMatching(NodeExecutorResult result)
    {
        if (!result.Outputs.TryGetValue("found", out var foundValue) || !foundValue.Require<bool>("found"))
            throw new InvalidOperationException("Managed chamfer did not find the self-match fixture; timing would be meaningless.");
        var score = result.Outputs.TryGetValue("score", out var scoreValue) ? scoreValue.Require<double>("score") : 0;
        if (score < 0.8)
            throw new InvalidOperationException($"Managed chamfer self-match score {score.ToString("0.000", CultureInfo.InvariantCulture)} fell below the 0.8 sanity floor.");
    }

    private static Mat LoadFixture(string fixtures, string fileName)
    {
        var path = Path.Combine(fixtures, fileName);
        var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidOperationException($"Fixture '{path}' could not be loaded as a grayscale image.");
        }
        return mat;
    }

    private static Mat Upscale(Mat source, int width, int height)
    {
        var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, height), interpolation: InterpolationFlags.Linear);
        return resized;
    }

    private static NodeDefinition Node(string type, string parametersJson)
        => new(
            "bench",
            type,
            null,
            null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson)
                ?? throw new InvalidOperationException($"Benchmark parameters for '{type}' did not parse."));

    private static CaseResult Row(Measurement measurement, double? baselineMedianMs, double? deltaPercent, bool passed, string? note)
        => new(measurement.Id, measurement.Category, measurement.MedianMs, measurement.MinMs, measurement.P90Ms, baselineMedianMs, deltaPercent, passed, note);

    private static void WriteEvidence(
        string path,
        Options options,
        IReadOnlyList<Measurement> measurements,
        IReadOnlyList<CaseResult> cases,
        string gateStatus,
        bool gated,
        bool machineMismatch)
    {
        WriteJson(path, new EvidenceFile(
            SchemaVersion: 1,
            Mode: "engine-microbenchmark",
            Machine: options.MachineLabel,
            EvaluatedAt: DateTimeOffset.UtcNow,
            Samples: options.Samples,
            Warmup: options.Warmup,
            TolerancePercent: options.TolerancePercent,
            Gated: gated,
            MachineMismatch: machineMismatch,
            GateStatus: gateStatus,
            Measurements: measurements,
            Cases: cases));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) && Directory.Exists(Path.Combine(directory.FullName, "docs")))
                return directory.FullName;
        throw new InvalidOperationException("Could not locate the repository root (a directory containing backend/ and docs/).");
    }

    private sealed record BenchCase(
        string Id,
        string Category,
        Mat Image,
        Mat? Template,
        IVisionNodeExecutor Executor,
        NodeDefinition Node,
        Action<NodeExecutorResult> Verify);

    private sealed record Measurement(string Id, string Category, double MedianMs, double MinMs, double P90Ms);

    private sealed record CaseResult(
        string Id,
        string Category,
        double MedianMs,
        double MinMs,
        double P90Ms,
        double? BaselineMedianMs,
        double? DeltaPercent,
        bool Passed,
        string? Note);

    private sealed record BaselineFile(
        int SchemaVersion,
        string Machine,
        DateTimeOffset RecordedAt,
        int Samples,
        double TolerancePercent,
        Dictionary<string, BaselineCase> Cases);

    private sealed record BaselineCase(double MedianMs, double MinMs, double P90Ms);

    private sealed record EvidenceFile(
        int SchemaVersion,
        string Mode,
        string Machine,
        DateTimeOffset EvaluatedAt,
        int Samples,
        int Warmup,
        double TolerancePercent,
        bool Gated,
        bool MachineMismatch,
        string GateStatus,
        IReadOnlyList<Measurement> Measurements,
        IReadOnlyList<CaseResult> Cases);

    private sealed record Options(
        string? Fixtures,
        string? Baseline,
        string? Output,
        bool UpdateBaseline,
        int Samples,
        int Warmup,
        double TolerancePercent,
        string MachineLabel,
        bool ShowHelp)
    {
        public static Options Parse(string[] args)
        {
            string? fixtures = null;
            string? baseline = null;
            string? output = null;
            var updateBaseline = false;
            var samples = 9;
            var warmup = 2;
            var tolerance = DefaultTolerancePercent;
            var machineLabel = Environment.MachineName;
            var showHelp = false;
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                string Value()
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Missing value for {arg}.");
                    return args[++index];
                }

                switch (arg)
                {
                    case "--engine-bench":
                        break;
                    case "--fixtures":
                        fixtures = Value();
                        break;
                    case "--baseline":
                        baseline = Value();
                        break;
                    case "--output":
                        output = Value();
                        break;
                    case "--update-baseline":
                        updateBaseline = true;
                        break;
                    case "--samples":
                        samples = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSamples)
                            ? parsedSamples
                            : throw new ArgumentException("--samples must be an integer.");
                        break;
                    case "--warmup":
                        warmup = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWarmup)
                            ? parsedWarmup
                            : throw new ArgumentException("--warmup must be an integer.");
                        break;
                    case "--tolerance-percent":
                        tolerance = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTolerance)
                            ? parsedTolerance
                            : throw new ArgumentException("--tolerance-percent must be a number.");
                        break;
                    case "--machine-label":
                        machineLabel = Value();
                        break;
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown engine benchmark argument '{arg}'.");
                }
            }

            if (samples is < 3 or > 51) throw new ArgumentException("--samples must be between 3 and 51.");
            if (warmup is < 0 or > 20) throw new ArgumentException("--warmup must be between 0 and 20.");
            if (!double.IsFinite(tolerance) || tolerance is < 1 or > 200)
                throw new ArgumentException("--tolerance-percent must be between 1 and 200.");
            if (string.IsNullOrWhiteSpace(machineLabel))
                throw new ArgumentException("--machine-label must not be empty.");
            return new Options(fixtures, baseline, output, updateBaseline, samples, warmup, tolerance, machineLabel, showHelp);
        }

        public static void PrintHelp(TextWriter output)
        {
            output.WriteLine("Engine algorithm microbenchmark (fixed golden fixtures)");
            output.WriteLine();
            output.WriteLine("Usage:");
            output.WriteLine("  dotnet run --project backend/src/VisionStudio.Benchmark.Cli -- --engine-bench [options]");
            output.WriteLine();
            output.WriteLine("Options:");
            output.WriteLine("  --fixtures DIR            Golden fixture directory (default backend/tests/VisionStudio.Engine.Tests/Fixtures)");
            output.WriteLine("  --baseline FILE           Baseline JSON (default ci/engine-benchmark-baseline.json)");
            output.WriteLine("  --output FILE             Evidence JSON (default artifacts/engine-benchmark-result.json)");
            output.WriteLine("  --update-baseline         Record the current run as the baseline and exit 0");
            output.WriteLine("  --samples N               Timed samples per case (default 9, 3..51)");
            output.WriteLine("  --warmup N                Untimed warmup runs per case (default 2, 0..20)");
            output.WriteLine("  --tolerance-percent N     Allowed median regression (default 25)");
            output.WriteLine("  --machine-label NAME      Machine identity stored in the baseline (default Environment.MachineName)");
            output.WriteLine();
            output.WriteLine("Exit codes: 0=pass (or machine mismatch skip), 10=regression, 11=benchmark sanity failure, 12=infrastructure/config error.");
        }
    }
}