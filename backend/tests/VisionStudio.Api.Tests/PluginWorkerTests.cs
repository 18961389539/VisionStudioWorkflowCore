using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Api.Tests;

public sealed class PluginWorkerTests
{
    [Fact]
    public void Worker_codec_round_trips_scalar_values()
    {
        var encoded = PluginWorkerValueCodec.Encode(VisionValue.Double(12.5));
        var decoded = PluginWorkerValueCodec.Decode(encoded);
        Assert.Equal(VisionDataType.Double, decoded.Type);
        Assert.Equal(12.5, decoded.Require<double>("value"));
    }

    [Fact]
    public void Sdk_surface_advertises_worker_process_isolation()
    {
        using var registry = new VisionNodeRegistry();
        using var manager = new PluginManager(registry, NullLogger<PluginManager>.Instance);
        Assert.Contains(nameof(VisionPluginIsolationMode.WorkerProcess), manager.SdkInfo.SupportedIsolationModes);
        Assert.Contains("separate process", manager.SdkInfo.IsolationBoundary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_manifest_accepts_string_worker_isolation_mode()
    {
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>("""{"schemaVersion":2,"id":"sample","name":"Sample","version":"1.0.0","entryAssembly":"sample.dll","isolationMode":"WorkerProcess"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal(VisionPluginIsolationMode.WorkerProcess, manifest!.IsolationMode);
    }

    [Fact]
    public void Worker_protocol_v3_advertises_shared_memory_transport_and_timings()
    {
        Assert.Equal(3, VisionPluginWorkerProtocol.Version);
        Assert.Equal("FileBackedSharedMemory", PluginWorkerSharedMemoryStore.TransportName);
    }

    [Fact]
    public void Plugin_manifest_accepts_pool_and_shared_memory_threshold()
    {
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>("""{"schemaVersion":2,"id":"sample","name":"Sample","version":"1.0.0","entryAssembly":"sample.dll","isolationMode":"WorkerProcess","workerPoolSize":3,"workerSharedMemoryThresholdBytes":524288}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.WorkerPoolSize);
        Assert.Equal(524288, manifest.WorkerSharedMemoryThresholdBytes);
    }

    [Fact]
    public void Worker_options_default_to_bounded_pool_and_one_megabyte_shared_memory_threshold()
    {
        var options = new PluginWorkerOptions();
        Assert.Equal(1, options.DefaultPoolSize);
        Assert.Equal(4, options.MaxPoolSize);
        Assert.True(options.SharedMemoryEnabled);
        Assert.Equal(1_048_576, options.SharedMemoryThresholdBytes);
    }

    [Fact]
    public void Shared_image_descriptor_is_relative_and_protocol_typed()
    {
        var descriptor = new PluginWorkerSharedImage("abc.vsmem", 100, 200, "8UC1", 20_000);
        var value = new PluginWorkerValue(VisionDataType.Image, SharedImage: descriptor);
        Assert.Null(value.ImagePng);
        Assert.Equal(PluginWorkerImageTransport.FileBackedSharedMemory, value.SharedImage!.Transport);
        Assert.Equal("abc.vsmem", value.SharedImage.FileName);
    }
    [Fact]
    public void Shared_memory_image_codec_round_trips_raw_pixels_without_png_payload()
    {
        var root = Path.Combine(Path.GetTempPath(), "visionstudio-worker-shm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PluginWorkerSharedMemoryStore(root, thresholdBytes: 1);
            using var mat = new Mat(16, 32, MatType.CV_8UC1, Scalar.All(37));
            using var image = VisionStudio.Engine.Camera.VisionImage.Own(mat.Clone());
            var encoded = PluginWorkerValueCodec.Encode(VisionValue.Image(image), store);
            Assert.NotNull(encoded.SharedImage);
            Assert.Null(encoded.ImagePng);
            var decoded = PluginWorkerValueCodec.Decode(encoded, store);
            using var decodedImage = decoded.Require<IVisionImage>("decoded") as IDisposable;
            var decodedMat = (Mat)decoded.Require<IVisionImage>("decoded").NativeImage;
            Assert.Equal(16, decodedMat.Rows);
            Assert.Equal(32, decodedMat.Cols);
            Assert.Equal(37, decodedMat.At<byte>(0, 0));
            Assert.False(File.Exists(Path.Combine(root, encoded.SharedImage!.FileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Performance_profiler_separates_stages_and_recommends_more_workers_under_queue_pressure()
    {
        var profiler = new PluginWorkerPerformanceProfiler(64, 10, 4, 0.70, 0.15);
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 32; i++)
        {
            profiler.Record(new PluginWorkerPerformanceSample(
                start.AddMilliseconds(i * 20),
                "sample.math.offset",
                0,
                true,
                0.2,
                15,
                22,
                0.3,
                18,
                0.4,
                0.2,
                38));
        }

        var profile = profiler.Snapshot("sample.math", 1);
        Assert.True(profile.RecommendationReady);
        Assert.True(profile.QueueWait.P95Ms >= 15);
        Assert.True(profile.PluginExecute.P95Ms >= 18);
        Assert.True(profile.RecommendedPoolSize >= 2);
        Assert.Contains("Increase", profile.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Performance_profiler_reset_clears_rolling_window()
    {
        var profiler = new PluginWorkerPerformanceProfiler(64, 5, 4, 0.70, 0.15);
        profiler.Record(new PluginWorkerPerformanceSample(DateTimeOffset.UtcNow, "tool", 0, true, 1, 2, 8, 1, 4, 1, 1, 12));
        Assert.Equal(1, profiler.Snapshot("sample", 1).SampleCount);
        profiler.Reset();
        Assert.Equal(0, profiler.Snapshot("sample", 1).SampleCount);
    }

    [Fact]
    public void Worker_timing_contract_carries_decode_execute_and_encode_segments()
    {
        var timing = new PluginWorkerTiming(1.25, 8.5, 2.75);
        var response = new PluginWorkerResponse { RequestId = "r", Success = true, Timing = timing };
        Assert.Equal(8.5, response.Timing!.PluginExecuteMs);
    }

}
