using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using VisionStudio.Engine;
using VisionStudio.Engine.Runtime;
using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Tests;

/// <summary>PNG pixels → native measurement → retained diagnostics → HTTP JSON/image/overlays.</summary>
public sealed class CaliperCircleDiagnosticsApiTests(CaliperDiagnosticsApiFactory factory)
    : IClassFixture<CaliperDiagnosticsApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("frame001.png")]
    [InlineData("frame002.png")]
    [InlineData("frame003.png")]
    [InlineData("frame004.png")]
    public async Task RepositoryPngFramesRunThroughNativeSamplingAndHttpDiagnostics(string filename)
    {
        using var pixels = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "samples", "camera_frames", filename), ImreadModes.Grayscale);
        Assert.False(pixels.Empty());
        using var mask = new Mat();
        Cv2.Threshold(pixels, mask, 180, 255, ThresholdTypes.Binary);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Cv2.MinEnclosingCircle(contours.MaxBy(contour => Cv2.ContourArea(contour))!, out var center, out var radius);
        var run = await Run(pixels, new() { ["centerX"] = center.X, ["centerY"] = center.Y, ["radius"] = radius });
        var manifest = await Manifest(run);
        Assert.True(manifest.GetProperty("found").GetBoolean(), manifest.ToString());
        Assert.InRange(Report(run).GetProperty("outputs").GetProperty("radius").GetProperty("value").GetDouble(), radius - 2, radius + 2);
        var selected = await Profile(run, 36);
        AssertPointMatchesOverlay(run, selected.GetProperty("edgePoint"), "caliper-circle-point-");
        Assert.Equal(pixels.Width, manifest.GetProperty("imageWidth").GetInt32());
        Assert.True(selected.GetProperty("profile").EnumerateArray().Max(x => x.GetDouble())
            > selected.GetProperty("profile").EnumerateArray().Min(x => x.GetDouble()) + 150);
        await Evidence($"repository-{Path.GetFileNameWithoutExtension(filename)}", pixels, run, manifest, selected);
    }

    [Fact]
    public async Task PngProfileCandidatesAndOverlayAgreeWithTheExecutedMeasurement()
    {
        using var pixels = CirclePixels();
        var run = await Run(pixels);
        var manifest = await Manifest(run);
        var spokes = manifest.GetProperty("spokes").EnumerateArray().ToArray();
        Assert.True(manifest.GetProperty("found").GetBoolean(), manifest.ToString());
        Assert.Equal(72, spokes.Length);
        Assert.Equal(Report(run).GetProperty("executionSequence").GetInt64(), manifest.GetProperty("executionSequence").GetInt64());
        Assert.Equal(Report(run).GetProperty("summary").GetProperty("sampleCount").GetInt32(), spokes.Count(x => Text(x, "status") == "used"));
        var selected = await Profile(run, 36); // 0 degrees, right-hand radial caliper.
        var samples = selected.GetProperty("profile").EnumerateArray().ToArray();
        var startRadius = selected.GetProperty("startRadius").GetDouble();
        var spacing = selected.GetProperty("spacing").GetDouble();
        Assert.Equal(31, samples.Length);
        // sampleWidth=1 and this cardinal spoke land exactly on original PNG pixel centers.
        for (var index = 0; index < samples.Length; index++)
            Assert.Equal(pixels.At<byte>(200, 200 + (int)Math.Round(startRadius + index * spacing)), samples[index].GetDouble(), 6);
        Assert.True(samples.First().GetDouble() > samples.Last().GetDouble() + 150);
        Assert.True(selected.GetProperty("candidates").GetArrayLength() > 0);
        var radius = selected.GetProperty("edgeRadius").GetDouble();
        Assert.InRange(radius, 99, 102);
        var candidate = Assert.Single(selected.GetProperty("candidates").EnumerateArray(), x =>
            Math.Abs(startRadius + x.GetProperty("position").GetDouble() - radius) < 1e-7);
        Assert.Equal("gaussian", Text(candidate, "method"));
        Assert.True(candidate.GetProperty("amplitude").GetDouble() < -15);
        Assert.Equal(Math.Abs(candidate.GetProperty("amplitude").GetDouble()), selected.GetProperty("edgeStrength").GetDouble(), 6);
        AssertPointMatchesOverlay(run, selected.GetProperty("edgePoint"), "caliper-circle-point-");
        Assert.InRange(Report(run).GetProperty("outputs").GetProperty("radius").GetProperty("value").GetDouble(), 99, 102);
        var image = await _client.GetByteArrayAsync($"{Base(run)}/image?portName=image");
        using var decoded = Cv2.ImDecode(image, ImreadModes.Grayscale);
        Assert.Equal(pixels.Size(), decoded.Size());
        Assert.InRange(Math.Abs(decoded.At<byte>(200, 200) - pixels.At<byte>(200, 200)), 0, 3);
        Assert.False(run.TryGetProperty("caliperDiagnostics", out _)); // Profiles stay out of run/report JSON.
        Assert.DoesNotContain("\"profile\"", Report(run).GetRawText());
        await Evidence("clean", pixels, run, manifest, selected);
    }

    [Fact]
    public async Task WeakEdgesAndPolarityUseNativeThresholdsRatherThanFabricatedCandidates()
    {
        using var pixels = CirclePixels(50);
        var rejected = await Run(pixels, new() { ["minContrast"] = 100 });
        var rejectedManifest = await Manifest(rejected);
        Assert.False(rejectedManifest.GetProperty("found").GetBoolean());
        var missing = await Profile(rejected, 36);
        Assert.Equal("no-edge-above-threshold", Text(missing, "status"));
        Assert.Empty(missing.GetProperty("candidates").EnumerateArray());
        Assert.True(missing.GetProperty("profile").GetArrayLength() > 0);
        var found = await Run(pixels, new() { ["minContrast"] = 2, ["polarity"] = "falling" });
        Assert.True((await Manifest(found)).GetProperty("found").GetBoolean());
        Assert.All((await Profile(found, 36)).GetProperty("candidates").EnumerateArray(), x => Assert.True(x.GetProperty("amplitude").GetDouble() < 0));
        var wrongPolarity = await Run(pixels, new() { ["minContrast"] = 2, ["polarity"] = "rising" });
        Assert.Equal("no-edge-above-threshold", Text(await Profile(wrongPolarity, 36), "status"));
        await Evidence("weak", pixels, rejected, rejectedManifest, missing);
    }

    [Fact]
    public async Task CompetingOuterEdgeIsSelectedThenClassifiedAsFitOutlierOrRadiusRejection()
    {
        using var pixels = CirclePixels();
        Cv2.Rectangle(pixels, new Rect(305, 198, 7, 5), Scalar.All(220), -1);
        var parameters = new Dictionary<string, object?> { ["searchDirection"] = "firstfromend", ["maxFitOutliers"] = 2, ["fitOutlierThreshold"] = 2 };
        var run = await Run(pixels, parameters);
        var manifest = await Manifest(run);
        var outlier = await Profile(run, 36);
        Assert.True(manifest.GetProperty("found").GetBoolean());
        Assert.Equal("fit-outlier", Text(outlier, "status"));
        Assert.True(outlier.GetProperty("candidates").GetArrayLength() >= 2);
        Assert.Equal(outlier.GetProperty("candidates").EnumerateArray().Max(x => x.GetProperty("position").GetDouble()),
            outlier.GetProperty("edgeRadius").GetDouble() - outlier.GetProperty("startRadius").GetDouble(), 6);
        AssertPointMatchesOverlay(run, outlier.GetProperty("edgePoint"), "caliper-circle-ignored-");
        Assert.Equal(Report(run).GetProperty("summary").GetProperty("ignoredByFit").GetInt32(),
            manifest.GetProperty("spokes").EnumerateArray().Count(x => Text(x, "status") == "fit-outlier"));
        parameters["maxRadiusDeviation"] = 4;
        var gated = await Run(pixels, parameters);
        var radiusRejected = await Profile(gated, 36);
        Assert.Equal("radius-deviation", Text(radiusRejected, "status"));
        AssertPointMatchesOverlay(gated, radiusRejected.GetProperty("edgePoint"), "caliper-circle-radius-rejected-");
        await Evidence("competing-edge", pixels, run, manifest, outlier);
    }

    [Fact]
    public async Task BrushRoiHoleIsJsonNullAndDoesNotCreateAnEdgeAcrossTheGap()
    {
        using var pixels = CirclePixels();
        var run = await Run(pixels, new() { ["roi"] = new { type = "Brush", strokes = new object[] {
            new { mode = "Add", width = 400, points = new[] { new { x = 200, y = 200 } } },
            new { mode = "Erase", width = 13, points = new[] { new { x = 300, y = 200 } } }
        } } });
        var manifest = await Manifest(run);
        var profile = await Profile(run, 36);
        var values = profile.GetProperty("profile").EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, values[15].ValueKind);
        Assert.Equal(220, values[0].GetDouble(), 6);
        Assert.Equal(30, values[^1].GetDouble(), 6);
        Assert.Equal("no-edge-above-threshold", Text(profile, "status"));
        Assert.Empty(profile.GetProperty("candidates").EnumerateArray());
        var metadata = manifest.GetProperty("spokes")[36];
        Assert.Equal(values.Count(x => x.ValueKind == JsonValueKind.Number), metadata.GetProperty("validSampleCount").GetInt32());
        Assert.True(manifest.GetProperty("found").GetBoolean()); // Other spokes still fit the circle.
        await Evidence("roi-hole", pixels, run, manifest, profile);
    }

    [Fact]
    public async Task RigidTransformRotatesAndTranslatesActualProfileAndOverlayCoordinates()
    {
        using var pixels = CirclePixels();
        // Reference center (100,150) → current center (200,200), rotation +90 degrees.
        var run = await Run(pixels, new() { ["centerX"] = 100, ["centerY"] = 150, ["angleStartDeg"] = 0, ["angleEndDeg"] = 180,
            ["caliperCount"] = 25 }, transform: true);
        var manifest = await Manifest(run);
        Assert.Equal(200, manifest.GetProperty("center").GetProperty("x").GetDouble(), 6);
        Assert.Equal(200, manifest.GetProperty("center").GetProperty("y").GetDouble(), 6);
        Assert.Equal(90, manifest.GetProperty("spokes")[0].GetProperty("angleDeg").GetDouble(), 6);
        var profile = await Profile(run, 0);
        Assert.Equal(200, profile.GetProperty("profileStart").GetProperty("x").GetDouble(), 6);
        var values = profile.GetProperty("profile").EnumerateArray().ToArray();
        for (var index = 0; index < values.Length; index++) Assert.Equal(pixels.At<byte>(285 + index, 200), values[index].GetDouble(), 6);
        AssertPointMatchesOverlay(run, profile.GetProperty("edgePoint"), "caliper-circle-point-");
        Assert.True(manifest.GetProperty("found").GetBoolean());
        await Evidence("transform", pixels, run, manifest, profile);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(72)]
    public async Task InvalidSpokeIsRejectedWithoutChangingTheCapture(int index)
    {
        using var pixels = CirclePixels();
        var run = await Run(pixels);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"{Base(run)}/caliper-diagnostics?spokeIndex={index}")).StatusCode);
        Assert.Equal(72, (await Manifest(run)).GetProperty("spokes").GetArrayLength());
    }

    [Fact]
    public async Task RetentionExpiryReturns404ForMetadataProfilesAndImages()
    {
        using var pixels = CirclePixels();
        var run = await Run(pixels);
        var store = factory.Services.GetRequiredService<RunStore>();
        for (var index = 0; index < 33; index++) store.Put(new WorkflowRunResult($"eviction-{Guid.NewGuid():N}", true, 0, false, 0, 0, [], [], null));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base(run)}/caliper-diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base(run)}/caliper-diagnostics?spokeIndex=36")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base(run)}/image")).StatusCode);
    }

    [Fact]
    public async Task NormalRunDoesNotCaptureProfilesWhenInteractiveArtifactsAreDisabled()
    {
        using var pixels = CirclePixels();
        var workflow = Workflow(factory.Save(pixels));
        var runner = factory.Services.GetRequiredService<IVisionWorkflowRunner>();
        var result = await runner.RunAsync(workflow, new VisionRunOptions());
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.CaliperDiagnostics ?? []);
        Assert.DoesNotContain("caliperDiagnosticsAvailable", result.NodeReports.Single(x => x.NodeId == "circle").Summary.Keys);
    }

    private async Task<JsonElement> Run(Mat pixels, Dictionary<string, object?>? patch = null, bool transform = false)
    {
        var response = await _client.PostAsJsonAsync("/api/debug/run", new { workflow = Workflow(factory.Save(pixels), patch, transform),
            options = new { mode = "RunNode", targetNodeId = "circle" } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var run = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.True(Report(run).GetProperty("summary").GetProperty("caliperDiagnosticsAvailable").GetBoolean(), body);
        return run;
    }

    private static WorkflowDefinition Workflow(string path, Dictionary<string, object?>? patch = null, bool transform = false)
    {
        var parameters = new Dictionary<string, object?> { ["centerX"] = 200, ["centerY"] = 200, ["radius"] = 100,
            ["radialBand"] = 15, ["caliperCount"] = 72, ["samplingMode"] = "count", ["sampleWidth"] = 1, ["minContrast"] = 15 };
        foreach (var pair in patch ?? []) parameters[pair.Key] = pair.Value;
        var nodes = new List<NodeDefinition> {
            Node("source", "image.acquire", new() { ["__replayInputPath"] = path }),
            Node("circle", "measure.caliperCircle", parameters)
        };
        var edges = new List<EdgeDefinition> { new("control", "source", "next", "circle", "exec", "control"), new("image", "source", "image", "circle", "image") };
        if (transform)
        {
            nodes.Insert(0, Node("fixture", "coordinate.transformConstant", new() { ["sourceFrame"] = "ReferenceImage", ["targetFrame"] = "image:source:source",
                ["unit"] = "px", ["x"] = 350, ["y"] = 100, ["angleDeg"] = 90 }));
            edges.Insert(0, new("fixture-control", "fixture", "next", "source", "exec", "control"));
            edges.Add(new("fixture-data", "fixture", "transform", "circle", "transform"));
        }
        return new($"caliper-diagnostic-{Guid.NewGuid():N}", "Caliper diagnostic pixel validation", nodes, edges);
    }

    private static NodeDefinition Node(string id, string type, Dictionary<string, object?> parameters)
        => new(id, type, id, null, parameters.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value)));
    private static Mat CirclePixels(byte foreground = 220)
    {
        var pixels = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Circle(pixels, new Point(200, 200), 100, Scalar.All(foreground), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(pixels, pixels, new Size(0, 0), 1.2);
        return pixels;
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static JsonElement Report(JsonElement run) => run.GetProperty("nodeReports").EnumerateArray().Last(x => Text(x, "nodeId") == "circle");
    private static string Base(JsonElement run) => $"/api/runs/{Text(run, "runId")}/nodes/circle";
    private Task<JsonElement> Manifest(JsonElement run) => _client.GetFromJsonAsync<JsonElement>($"{Base(run)}/caliper-diagnostics");
    private Task<JsonElement> Profile(JsonElement run, int index) => _client.GetFromJsonAsync<JsonElement>($"{Base(run)}/caliper-diagnostics?spokeIndex={index}");
    private static void AssertPointMatchesOverlay(JsonElement run, JsonElement point, string prefix)
        => Assert.Contains(run.GetProperty("overlays").EnumerateArray(), overlay => Text(overlay, "id").StartsWith(prefix, StringComparison.Ordinal)
            && Math.Abs(overlay.GetProperty("x").GetDouble() - point.GetProperty("x").GetDouble()) < 1e-7
            && Math.Abs(overlay.GetProperty("y").GetDouble() - point.GetProperty("y").GetDouble()) < 1e-7);

    private static async Task Evidence(string name, Mat pixels, JsonElement run, JsonElement manifest, JsonElement profile)
    {
        var directory = Environment.GetEnvironmentVariable("VISIONSTUDIO_CALIPER_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        Cv2.ImWrite(Path.Combine(directory, $"{name}.png"), pixels);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.json"), JsonSerializer.Serialize(new { run, manifest, profile }, JsonOptions));
    }
}

public sealed class CaliperDiagnosticsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "visionstudio-caliper-diagnostics", Guid.NewGuid().ToString("N"));
    private int _cleanupStarted;
    public CaliperDiagnosticsApiFactory() => Directory.CreateDirectory(_root);
    public string Save(Mat pixels)
    {
        var path = Path.Combine(_root, $"pixels-{Guid.NewGuid():N}.png");
        Assert.True(Cv2.ImWrite(path, pixels));
        return path;
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(_root);
        builder.UseSetting("Security:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Enabled", "false");
        builder.UseSetting("RobotTcpSimulator:Port", "0");
        // 回放读盘是宿主授权能力：测试把临时像素目录显式加入允许根，等价于宿主注入回放路径。
        builder.UseSetting("VisionStudio:ReplayInputRoots", _root);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _cleanupStarted, 1) != 0) { base.Dispose(disposing); return; }
        var logProvider = disposing ? Services.GetRequiredService<RollingFileLoggerProvider>() : null;
        using var pooledConnection = disposing && Directory.Exists(_root)
            ? Services.GetRequiredService<SqliteMetadataDatabase>().OpenConnectionAsync().GetAwaiter().GetResult() : null;
        pooledConnection?.Close();
        base.Dispose(disposing);
        logProvider?.Dispose(); // Registered by instance; the container does not own its file writer.
        if (pooledConnection is not null) SqliteConnection.ClearPool(pooledConnection);
        if (disposing && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
