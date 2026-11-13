using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class FrameSetWorkflowTests
{
    [Fact]
    public void VisionDataType_contains_FrameSet()
    {
        Assert.Equal("FrameSet", VisionDataType.FrameSet.ToString());
    }

    [Fact]
    public async Task FrameSetImageNode_selects_requested_camera_without_clone()
    {
        using var top = VisionImage.Own(new Mat(8, 10, MatType.CV_8UC1, Scalar.All(1)));
        using var side = VisionImage.Own(new Mat(6, 12, MatType.CV_8UC1, Scalar.All(2)));
        using var set = new VisionFrameSet(
            "sync-1", "device-ptp", 12.5, true,
            new Dictionary<string, IVisionImage>(StringComparer.OrdinalIgnoreCase) { ["top"] = top, ["side"] = side },
            new Dictionary<string, VisionFrameSetFrameInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["top"] = new("top", 10, DateTimeOffset.UtcNow, 1000, 1, "Mono8"),
                ["side"] = new("side", 11, DateTimeOffset.UtcNow, 1012, 1, "Mono8")
            });
        var node = new NodeDefinition("pick", "frameset.image", null, null, new() { ["cameraId"] = JsonSerializer.SerializeToElement("side") });
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue> { ["frameSet"] = VisionValue.FrameSet(set) });
        var result = await new FrameSetImageNode().ExecuteAsync(context, node, CancellationToken.None);
        Assert.Same(side, result.Outputs["image"].Value);
        Assert.Equal(11L, result.Outputs["sequence"].Value);
    }

    [Fact]
    public void VisionFrameSet_disposes_every_owned_image()
    {
        var a = new TrackingImage();
        var b = new TrackingImage();
        var set = new VisionFrameSet(
            "sync-1", "device-ptp", 3.0, true,
            new Dictionary<string, IVisionImage> { ["a"] = a, ["b"] = b },
            new Dictionary<string, VisionFrameSetFrameInfo>
            {
                ["a"] = new("a", 1, DateTimeOffset.UtcNow, 10, 5, "Mono8"),
                ["b"] = new("b", 1, DateTimeOffset.UtcNow, 13, 5, "Mono8")
            });
        set.Dispose();
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task SynchronizedCaptureNode_emits_FrameSet_and_skew_metadata()
    {
        using var image = VisionImage.Own(new Mat(4, 4, MatType.CV_8UC1));
        using var set = new VisionFrameSet(
            "sync-1", "device-ptp", 8.25, true,
            new Dictionary<string, IVisionImage> { ["a"] = image },
            new Dictionary<string, VisionFrameSetFrameInfo> { ["a"] = new("a", 7, DateTimeOffset.UtcNow, 100, 9, "Mono8") });
        var executor = new SynchronizedCaptureNode(new FakeSyncService(set));
        var node = new NodeDefinition("sync", "camera.syncCapture", null, null, new()
        {
            ["groupId"] = JsonSerializer.SerializeToElement("sync-1"),
            ["scheduled"] = JsonSerializer.SerializeToElement(false)
        });
        var result = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), node, CancellationToken.None);
        Assert.Same(set, result.Outputs["frameSet"].Value);
        Assert.Equal(8.25, result.Outputs["skewUs"].Value);
        Assert.Equal("device-ptp", result.Outputs["timestampBasis"].Value);
    }

    private sealed class FakeSyncService(VisionFrameSet frameSet) : ISynchronizedFrameSetService
    {
        public Task<VisionFrameSet> CaptureAsync(SynchronizedFrameSetCaptureRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(frameSet);
    }

    private sealed class TrackingImage : IVisionImage, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Width => 1;
        public int Height => 1;
        public object NativeImage => this;
        public void Dispose() => Disposed = true;
    }
}
