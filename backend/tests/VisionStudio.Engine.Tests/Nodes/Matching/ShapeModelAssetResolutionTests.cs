using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// P0 M2：match.shape 的模型资产解析。重点不是匹配本身，而是**失败语义**：
/// 资产缺失/哈希不符/宿主没提供资产源，必须明确失败，绝不静默退回内联模型。
/// 这几个分支都发生在模板加载与原生建模之前，所以不需要真实模型即可覆盖。
/// </summary>
public sealed class ShapeModelAssetResolutionTests
{
    private sealed class FakeSource : IModelAssetSource
    {
        public Dictionary<(string AssetId, int Version), byte[]> Items { get; } = new();
        public List<(string AssetId, int Version)> Reads { get; } = new();
        public byte[]? TryRead(string assetId, int version)
        {
            Reads.Add((assetId, version));
            return Items.TryGetValue((assetId, version), out var bytes) ? bytes : null;
        }
    }

    private sealed class DisabledSource : IModelAssetSource
    {
        public bool IsEnabled => false;
        public byte[]? TryRead(string assetId, int version) => throw new InvalidOperationException("disabled source must not read bytes");
    }

    private static NodeDefinition Node(string parameters, string type = "match.shape")
        => new("shape-1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters));

    private static async Task<InvalidOperationException> RunExpectingFailureAsync(
        IModelAssetSource? source, string parameters, string nodeType = "match.shape")
    {
        using var image = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(image, new Point(120, 120), 50, Scalar.All(255), -1);
        using var input = VisionImage.Own(image.Clone());
        var context = new NodeExecutionContext(
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(input) },
            workflowScope: null, runId: null, modelAssets: source);
        return await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(nodeType, context, parameters));
    }

    private static Task ExecuteAsync(string nodeType, NodeExecutionContext context, string parameters)
        => nodeType == "match.shape"
            ? new ShapeMatchNode().ExecuteAsync(context, Node(parameters), CancellationToken.None).AsTask()
            : new TemplateMatchNode().ExecuteAsync(context, Node(parameters, "match.template"), CancellationToken.None).AsTask();

    [Fact]
    public async Task AssetReference_WithoutHostSource_FailsInsteadOfFallingBackToInlineModel()
    {
        var error = await RunExpectingFailureAsync(null,
            """{"modelMode":"trained","modelAssetId":"shape-a","modelAssetVersion":1}""");
        Assert.Contains("did not provide a model asset source", error.Message);
        Assert.Contains("shape-a", error.Message);
    }

    [Fact]
    public async Task MissingAsset_FailsAndReportsTheRequestedVersion()
    {
        var source = new FakeSource();
        var error = await RunExpectingFailureAsync(source,
            """{"modelMode":"trained","modelAssetId":"shape-b","modelAssetVersion":3}""");
        Assert.Contains("does not exist or was deleted", error.Message);
        Assert.Contains("shape-b", error.Message);
        Assert.Contains("v3", error.Message);
        Assert.Equal([("shape-b", 3)], source.Reads.ToArray());
    }

    [Fact]
    public async Task VersionZero_IsForwardedAsLatestRequest()
    {
        var source = new FakeSource();
        await RunExpectingFailureAsync(source,
            """{"modelMode":"trained","modelAssetId":"shape-c","modelAssetVersion":0}""");
        // 0 = 取 latest：由资产源解析，节点只负责原样传递。
        Assert.Equal([("shape-c", 0)], source.Reads.ToArray());
    }

    [Fact]
    public async Task PinnedHashMismatch_FailsBeforeAnyMatching()
    {
        var source = new FakeSource();
        source.Items[("shape-d", 1)] = new byte[64];
        var error = await RunExpectingFailureAsync(source,
            """{"modelMode":"trained","modelAssetId":"shape-d","modelAssetVersion":1,"modelAssetSha256":"DEADBEEF"}""");
        Assert.Contains("hash mismatch", error.Message);
        Assert.Contains("DEADBEEF", error.Message);
    }

    [Fact]
    public async Task HashMatch_LetsResolutionProceedToModelDecoding()
    {
        var bytes = new byte[64];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var source = new FakeSource();
        source.Items[("shape-e", 2)] = bytes;

        var error = await RunExpectingFailureAsync(source,
            $$"""{"modelMode":"trained","modelAssetId":"shape-e","modelAssetVersion":2,"modelAssetSha256":"{{sha}}"}""");
        // 逐字节通过资产校验后才会走到反序列化；这里只要求"不是资产解析错误"。
        Assert.DoesNotContain("model asset source", error.Message);
        Assert.DoesNotContain("does not exist or was deleted", error.Message);
        Assert.DoesNotContain("hash mismatch", error.Message);
    }

    [Fact]
    public async Task DisabledSwitch_WithoutInlineModel_FailsWithRollbackHint()
    {
        var error = await RunExpectingFailureAsync(new DisabledSource(),
            """{"modelMode":"trained","modelAssetId":"shape-f","modelAssetVersion":1}""");
        Assert.Contains("modelAssets disabled", error.Message);
        Assert.Contains("no inline trained model", error.Message);
    }

    [Fact]
    public async Task DisabledSwitch_WithInlineModel_UsesInlineInsteadOfReadingTheAsset()
    {
        // 关闭资产库是运维回退路径：有内联模型就继续跑，不访问资产（DisabledSource.TryRead 一旦被调用就抛错）。
        var error = await RunExpectingFailureAsync(new DisabledSource(),
            """{"modelMode":"trained","modelAssetId":"shape-g","modelAssetVersion":1,"trainedModelData":"QUJD"}""");
        Assert.DoesNotContain("disabled", error.Message);
        Assert.DoesNotContain("model asset source", error.Message);
        Assert.DoesNotContain("does not exist or was deleted", error.Message);
    }

    [Fact]
    public async Task TemplateAsset_WithoutHostSource_FailsInsteadOfFallingBackToTemplateFile()
    {
        var error = await RunExpectingFailureAsync(null,
            """{"templateAssetId":"tpl-a","templateAssetVersion":1}""", "match.template");
        Assert.Contains("did not provide an asset source", error.Message);
        Assert.Contains("tpl-a", error.Message);
    }

    [Fact]
    public async Task TemplateAsset_WithUndecodableBytes_FailsAtDecodeNotAtAssetLookup()
    {
        var source = new FakeSource();
        source.Items[("tpl-b", 1)] = [1, 2, 3, 4];
        var error = await RunExpectingFailureAsync(source,
            """{"templateAssetId":"tpl-b","templateAssetVersion":1}""", "match.template");
        Assert.Contains("could not be decoded as an image", error.Message);
        Assert.DoesNotContain("does not exist or was deleted", error.Message);
    }
}
