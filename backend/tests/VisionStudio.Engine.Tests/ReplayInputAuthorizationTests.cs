using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// P0：<c>__replayInputPath</c> 是宿主注入能力，不是工作流文档能力。
/// 没有宿主授权时必须拒绝执行，避免外部导入的流程 JSON 借 image.acquire 读取任意本地文件。
/// </summary>
public sealed class ReplayInputAuthorizationTests
{
    private sealed class FixedResolver(string? allowed) : IReplayInputResolver
    {
        public int Calls { get; private set; }
        public string? Resolve(string requestedPath)
        {
            Calls++;
            return allowed;
        }
    }

    private static NodeDefinition Definition(string path) => new(
        "source", "image.acquire", "source", null,
        new Dictionary<string, JsonElement> { ["__replayInputPath"] = JsonSerializer.SerializeToElement(path) });

    [Fact]
    public async Task AcquireImage_WithoutHostResolver_RejectsReplayPath()
    {
        await using var manager = new CameraManager();
        var node = new AcquireImageNode(manager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), Definition("C:\\Windows\\win.ini"), default).AsTask());

        Assert.Contains("not authorized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcquireImage_WithDenyingResolver_RejectsReplayPath()
    {
        await using var manager = new CameraManager();
        var resolver = new FixedResolver(null);
        var node = new AcquireImageNode(manager, resolver);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), Definition("C:\\Windows\\win.ini"), default).AsTask());

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task AcquireImage_WithAuthorizedPath_ReturnsReplayFrame()
    {
        var directory = Path.Combine(Path.GetTempPath(), "visionstudio-replay-auth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "frame.png");
        try
        {
            using (var pixels = new Mat(24, 32, MatType.CV_8UC1, Scalar.All(90)))
                Assert.True(Cv2.ImWrite(path, pixels));

            await using var manager = new CameraManager();
            var node = new AcquireImageNode(manager, new FixedResolver(path));

            var result = await node.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()), Definition(path), default);

            Assert.Equal(32, Convert.ToInt32(result.Outputs["width"].Value));
            Assert.Equal(24, Convert.ToInt32(result.Outputs["height"].Value));
            var image = Assert.IsType<VisionImage>(result.Outputs["image"].Value);
            Assert.Equal(32, image.Width);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
