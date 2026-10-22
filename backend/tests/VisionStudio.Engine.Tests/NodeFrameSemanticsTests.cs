using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 图像坐标帧语义必须由目录声明驱动：宿主不再按节点类型字符串猜测。
/// 这些用例锁定内置节点的声明，并证明插件只需声明 <see cref="NodeFrameRule"/> 就能获得同样的
/// 帧跟踪（无需让宿主认识它的类型名）。
/// </summary>
public sealed class NodeFrameSemanticsTests
{
    private static NodeDefinition Node(string type, string? parametersJson = null)
        => new("n1", type, null, null,
            parametersJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson));

    [Theory]
    [InlineData("image.crop", "crop")]
    [InlineData("image.warpAffine", null)]
    [InlineData("image.undistort", "undistorted")]
    [InlineData("image.register", null)]
    [InlineData("roi.transform", null)]
    [InlineData("jlvision.worldPlane", "worldPlane")]
    public void BuiltInSpatialNodes_DeclareFrameRules(string type, string? suffix)
    {
        var item = BuiltInNodeCatalog.Require(type);
        Assert.True(item.DeclaresFrames, $"{type} must declare frame semantics.");

        var rule = NodeFrameSemantics.Resolve(Node(type), item);
        Assert.NotNull(rule);
        Assert.Equal(suffix, rule!.FrameSuffix);
    }

    [Theory]
    [InlineData("match.shape", NodeTransformFramePolicy.TemplateToImage)]
    [InlineData("coordinate.transformCompose", NodeTransformFramePolicy.Compose)]
    [InlineData("coordinate.transformInverse", NodeTransformFramePolicy.Invert)]
    public void BuiltInTransformNodes_DeclareTransformFrames(string type, NodeTransformFramePolicy policy)
    {
        var item = BuiltInNodeCatalog.Require(type);
        Assert.True(item.DeclaresTransformFrames, $"{type} must declare transform frame semantics.");
        Assert.Equal(policy, item.TransformFrames!.Policy);
    }

    [Fact]
    public void ConditionalRules_FollowTheEffectiveOperatorParameter()
    {
        var restoration = BuiltInNodeCatalog.Require("jlvision.restoration");
        var psf = NodeFrameSemantics.Resolve(Node("jlvision.restoration", """{"operator":"psfMotion"}"""), restoration);
        var harmonic = NodeFrameSemantics.Resolve(Node("jlvision.restoration", """{"operator":"harmonic"}"""), restoration);
        Assert.Equal("psf", psf!.FrameSuffix);
        Assert.Equal(NodeImageFramePolicy.PreserveInput, harmonic!.Policy);

        var imageOp = BuiltInNodeCatalog.Require("jlvision.imageOp");
        var spatial = NodeFrameSemantics.Resolve(Node("jlvision.imageOp", """{"operator":"cropPart"}"""), imageOp);
        var abs = NodeFrameSemantics.Resolve(Node("jlvision.imageOp", """{"operator":"abs"}"""), imageOp);
        Assert.Equal(NodeImageFramePolicy.NewSpatialFrame, spatial!.Policy);
        Assert.Equal(NodeImageFramePolicy.PreserveInput, abs!.Policy);
    }

    [Fact]
    public async Task DeclaredPlugin_GetsSpatialFramesWithoutTypeKnowledge()
    {
        var catalog = SpatialCatalog("plugin.spatial",
            [new(NodeImageFramePolicy.NewSpatialFrame, FrameLabel: "plugin spatial", Transform: NodeTransformFramePolicy.Spatial)]);
        using var registry = new VisionNodeRegistry();
        registry.Register(catalog, new IdentityImageExecutor("plugin.spatial"), "test");
        var dispatcher = new VisionNodeDispatcher(registry);

        var (result, source) = await ExecuteAsync(dispatcher, "plugin.spatial", "image:upstream:crop");

        var image = Assert.IsType<VisionImage>(result.Outputs["image"].Value);
        Assert.Equal("image:n1:spatial", image.CoordinateFrame);
        image.Dispose();
        var transform = Assert.IsType<VisionTransform2D>(result.Outputs["transform"].Value);
        Assert.Equal("image:upstream:crop", transform.SourceFrame);
        Assert.Equal("image:n1:spatial", transform.TargetFrame);
        source.Dispose();
    }

    [Fact]
    public async Task UndeclaredPlugin_PreservesTheInputFrame()
    {
        // 未声明帧语义的插件沿用旧默认：输出继承输入帧。宿主不再按类型名猜测。
        var catalog = SpatialCatalog("plugin.image", frames: null);
        using var registry = new VisionNodeRegistry();
        registry.Register(catalog, new IdentityImageExecutor("plugin.image"), "test");
        var dispatcher = new VisionNodeDispatcher(registry);

        var (result, source) = await ExecuteAsync(dispatcher, "plugin.image", "image:upstream:stable");

        var image = Assert.IsType<VisionImage>(result.Outputs["image"].Value);
        Assert.Equal("image:upstream:stable", image.CoordinateFrame);
        image.Dispose();
        source.Dispose();
    }

    [Fact]
    public async Task DeclaredFixedFrame_UsesTheDeclaredSuffix()
    {
        var catalog = SpatialCatalog("plugin.crop",
            [new(NodeImageFramePolicy.NewFixedFrame, FrameSuffix: "crop", FrameLabel: "crop frame", Transform: NodeTransformFramePolicy.Spatial)]);
        using var registry = new VisionNodeRegistry();
        registry.Register(catalog, new IdentityImageExecutor("plugin.crop"), "test");
        var dispatcher = new VisionNodeDispatcher(registry);

        var (result, source) = await ExecuteAsync(dispatcher, "plugin.crop", "image:upstream:source");

        var image = Assert.IsType<VisionImage>(result.Outputs["image"].Value);
        Assert.Equal("image:n1:crop", image.CoordinateFrame);
        image.Dispose();
        source.Dispose();
    }

    private static async Task<(NodeExecutorResult Result, VisionImage Source)> ExecuteAsync(
        VisionNodeDispatcher dispatcher,
        string type,
        string inputFrame)
    {
        var source = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(0)));
        source.SetCoordinateFrame(inputFrame);
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(source) };
        var node = new NodeDefinition("n1", type, null, null, null);
        var result = await dispatcher.ExecuteAsync(node, inputs, "scope", CancellationToken.None);
        return (result, source);
    }

    private static NodeCatalogItem SpatialCatalog(string type, IReadOnlyList<NodeFrameRule>? frames)
        => new(
            type,
            "Plugin Spatial",
            "Test",
            [new("exec", VisionDataType.Control), new("image", VisionDataType.Image)],
            [new("image", VisionDataType.Image), new("transform", VisionDataType.Transform2D)],
            [],
            PluginId: "plugin",
            Frames: frames);

    private sealed class IdentityImageExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(
            NodeExecutionContext context,
            NodeDefinition node,
            CancellationToken cancellationToken)
        {
            var source = context.Require<VisionImage>("image");
            var output = source.CloneOwned();
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["image"] = VisionValue.Image(output),
                    ["transform"] = VisionValue.Transform(new VisionTransform2D(
                        "unknown", "unknown", "px",
                        1, 0, 0,
                        0, 1, 0,
                        0, 0, 1))
                },
                new Dictionary<string, object?>()));
        }
    }
}
