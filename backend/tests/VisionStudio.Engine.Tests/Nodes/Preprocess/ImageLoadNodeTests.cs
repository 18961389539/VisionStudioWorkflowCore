using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 离线读图（image.load）测试。
/// 覆盖：文件/目录两种模式、自然排序、sequence 端口、越界策略、灰度/位深、体积上限、路径授权。
/// </summary>
public sealed class ImageLoadNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vs-load-" + Guid.NewGuid().ToString("N"));

    public ImageLoadNodeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    [Fact]
    public async Task FileMode_ReadsSingleImageAndReportsMetadata()
    {
        var path = WriteImage("img10.png", 10, 6);

        var result = await Run(Node(), S(("path", path)));

        using var image = Image(result);
        Assert.Equal(10, image.Width);
        Assert.Equal(6, image.Height);
        Assert.Equal(1L, result.Outputs["frameCount"].Require<long>("frameCount"));
        Assert.Equal(0L, result.Outputs["sequence"].Require<long>("sequence"));
        Assert.Equal("img10.png", result.Outputs["fileName"].Require<string>("fileName"));
        Assert.Equal(8L, result.Outputs["bitDepth"].Require<long>("bitDepth"));
    }

    [Fact]
    public async Task FolderMode_UsesNaturalOrderSoBatchesAreReproducible()
    {
        WriteImage("img1.png", 1, 1);
        WriteImage("img2.png", 2, 2);
        WriteImage("img10.png", 10, 10);

        // 自然排序 = img1, img2, img10；字典序会得到 img1, img10, img2，跑批不可复现。
        var widths = new List<int>();
        foreach (var index in new[] { 0, 1, 2 })
        {
            var result = await Run(Node(("index", index.ToString())), S(("path", _root)));
            using var image = Image(result);
            widths.Add(image.Width);
            Assert.Equal(3L, result.Outputs["frameCount"].Require<long>("frameCount"));
        }
        Assert.Equal([1, 2, 10], widths);
    }

    [Fact]
    public async Task SequencePortOverridesIndexAndSupportsWrap()
    {
        WriteImage("img1.png", 1, 1);
        WriteImage("img2.png", 2, 2);

        // 参数 index=0，端口 sequence=3，wrap 后取 3 % 2 = 1 → 第二张。
        var result = await Run(Node(("index", "0"), ("indexPolicy", "wrap")),
            [("path", VisionValue.String(_root)), ("sequence", VisionValue.Integer(3))]);

        using var image = Image(result);
        Assert.Equal(2, image.Width);
        Assert.Equal(1L, result.Outputs["sequence"].Require<long>("sequence"));
    }

    [Fact]
    public async Task OutOfRangeIndexFailsWithFrameCount()
    {
        WriteImage("a.png", 4, 4);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Node(("index", "3")), S(("path", _root))));
        Assert.Contains("has 1 frames", error.Message);
    }

    [Fact]
    public async Task ColorModeGrayscaleProducesSingleChannel()
    {
        var path = WriteImage("color.png", 8, 8, channels: 3);

        var result = await Run(Node(("colorMode", "gray")), S(("path", path)));
        using var image = Image(result);
        Assert.Equal(1L, result.Outputs["channels"].Require<long>("channels"));
    }

    [Fact]
    public async Task SixteenBitImagesKeepTheirDepth()
    {
        var path = Path.Combine(_root, "deep16.png");
        using (var mat = new Mat(4, 4, MatType.CV_16UC1, Scalar.All(4000)))
            Cv2.ImWrite(path, mat);

        var result = await Run(Node(), S(("path", path)));
        using var image = Image(result);
        Assert.Equal(16L, result.Outputs["bitDepth"].Require<long>("bitDepth"));
    }

    [Fact]
    public async Task OversizeFileIsRejectedBeforeDecoding()
    {
        var path = Path.Combine(_root, "huge.png");
        File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Node(("maxMegabytes", "1")), S(("path", path))));
        Assert.Contains("maxMegabytes=1", error.Message);
    }

    [Fact]
    public async Task UnauthorizedPathIsRejectedWithActionableMessage()
    {
        var outside = Path.Combine(Path.GetTempPath(), "vs-not-authorized-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(outside, [0x89, 0x50]);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Node(), S(("path", outside))));
            Assert.Contains("ReplayInputRoots", error.Message);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task MissingHostResolverRejectsReadsInsteadOfFallingBackToArbitraryPaths()
    {
        var path = WriteImage("img1.png", 1, 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ImageLoadNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue> { ["path"] = VisionValue.String(path) }),
                Node(), CancellationToken.None).AsTask());
        Assert.Contains("host file-read authorization", error.Message);
    }

    [Fact]
    public async Task NonImageExtensionAndUndecodableFileFail()
    {
        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "not an image");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Node(), S(("path", text))));

        var broken = Path.Combine(_root, "broken.png");
        File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 0x00]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(Node(), S(("path", broken))));
    }

    // ===== 夹具 =====

    private string WriteImage(string name, int width, int height, int channels = 1)
    {
        var path = Path.Combine(_root, name);
        var type = channels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC1;
        using var mat = new Mat(height, width, type, Scalar.All(200));
        Cv2.ImWrite(path, mat);
        return path;
    }

    private Task<NodeExecutorResult> Run(NodeDefinition node, (string Port, VisionValue Value)[] inputs)
        => new ImageLoadNode(new AllowRootResolver(_root))
            .ExecuteAsync(new NodeExecutionContext(inputs.ToDictionary(x => x.Port, x => x.Value, StringComparer.OrdinalIgnoreCase)), node, CancellationToken.None)
            .AsTask();

    private static (string Port, VisionValue Value)[] S(params (string Port, string Value)[] inputs)
        => inputs.Select(item => (item.Port, VisionValue.String(item.Value))).ToArray();

    private static VisionImage Image(NodeExecutorResult result) => result.Outputs["image"].Require<VisionImage>("image");

    private static NodeDefinition Node(params (string Key, string Value)[] parameters)
    {
        if (parameters.Length == 0) return new NodeDefinition("n1", "image.load", null, null, null);
        var payload = parameters.ToDictionary(item => item.Key, item => (object)item.Value);
        return new NodeDefinition("n1", "image.load", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }

    /// <summary>只放行指定根目录的授权器；根目录外一律返回 null（拒绝）。</summary>
    private sealed class AllowRootResolver(string root) : IReplayInputResolver
    {
        public string? Resolve(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath)) return null;
            var full = Path.GetFullPath(requestedPath);
            var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return (Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
    }
}
