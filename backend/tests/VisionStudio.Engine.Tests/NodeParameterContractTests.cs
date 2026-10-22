using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 目录参数契约：执行器真正读取的每个参数都必须在目录里声明。
/// 这些用例锁定 P0 修复点，防止"目录里没有、运行时却在读"再次出现。
/// </summary>
public sealed class NodeParameterContractTests
{
    private static NodeCatalogItem Item(string type) => BuiltInNodeCatalog.Require(type);

    private static ParameterDescriptor Parameter(string type, string name) =>
        Item(type).Parameters.SingleOrDefault(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new Xunit.Sdk.XunitException($"Node '{type}' does not declare parameter '{name}'.");

    [Theory]
    // 画布 ROI 参数：所有 SupportsRoi 节点统一以隐藏参数形式声明。
    [InlineData("image.crop", "roi")]
    [InlineData("roi.transform", "roi")]
    [InlineData("image.threshold", "roi")]
    [InlineData("measure.blobs", "roi")]
    [InlineData("feature.line", "roi")]
    // 运行时读 ROI 但未声明能力位的节点（能力位待补，契约先如实暴露）。
    [InlineData("measure.blob", "roi")]
    [InlineData("region.fromRoi", "roi")]
    [InlineData("recognition.ocr", "roi")]
    [InlineData("recognition.barcode", "roi")]
    // 匹配节点的多个 ROI 参数
    [InlineData("match.shape", "searchRoi")]
    [InlineData("match.shape", "templateRoi")]
    [InlineData("match.shape", "clutterRoi")]
    [InlineData("match.template", "searchRoi")]
    [InlineData("match.template", "templateRoi")]
    [InlineData("match.template", "ignoreRoi")]
    public void LocalRoiParameters_AreDeclaredAsInternal(string type, string parameter)
    {
        var descriptor = Parameter(type, parameter);
        Assert.True(descriptor.Internal, $"{type}.{parameter} must be declared Internal (canvas-authored asset, not a form field).");
        Assert.Equal("roi", descriptor.Type);
        Assert.Null(descriptor.DefaultValue);
    }

    [Fact]
    public void ImageCrop_LegacyRectangleFields_AreDeclaredInternalWithoutDefaults()
    {
        foreach (var name in new[] { "x", "y", "width", "height" })
        {
            var descriptor = Parameter("image.crop", name);
            Assert.True(descriptor.Internal);
            // 没有默认值：新节点不应把 0 写进 width/height 覆盖"整幅图"语义。
            Assert.Null(descriptor.DefaultValue);
        }
    }

    [Fact]
    public void RoiTransform_ReferenceCaptureAssets_AreDeclaredInternal()
    {
        foreach (var name in new[] { "captureReference", "referenceImage", "referenceWidth", "referenceHeight", "debugImagePreviewOnly" })
            Assert.True(Parameter("roi.transform", name).Internal, $"roi.transform.{name} must be declared Internal.");
    }

    [Fact]
    public void AcquireImage_ReplayParameters_AreDeclaredInternal()
    {
        foreach (var name in new[] { "__replayInputPath", "__replaySequence", "__replayTimestamp", "__replayCameraId" })
        {
            var descriptor = Parameter("image.acquire", name);
            Assert.True(descriptor.Internal);
            Assert.Null(descriptor.DefaultValue);
        }
        Assert.True(Parameter("image.acquire", "autoOpen").Internal);
    }

    [Fact]
    public void InternalParameters_SurviveCatalogSerialization()
    {
        var document = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
        var roundTripped = VisionCatalogSnapshot.Deserialize(VisionCatalogSnapshot.Serialize(document));
        Assert.NotNull(roundTripped);

        var acquire = roundTripped!.Items.Single(item => item.Type == "image.acquire");
        var replay = acquire.Parameters.Single(parameter => parameter.Name == "__replayInputPath");
        Assert.True(replay.Internal);
        Assert.Equal(VisionCatalogSnapshot.ComputeHash(BuiltInNodeCatalog.Items), VisionCatalogSnapshot.ComputeHash(roundTripped.Items));
    }

    [Fact]
    public void OptionalNumericParameters_SurviveCatalogSerialization()
    {
        var document = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
        var roundTripped = VisionCatalogSnapshot.Deserialize(VisionCatalogSnapshot.Serialize(document))!;
        var optional = roundTripped.Items.SelectMany(item => item.Parameters.Where(parameter => parameter.Optional)
            .Select(parameter => $"{item.Type}.{parameter.Name}")).Order().ToArray();

        Assert.Equal(new[] { "image.perspective.height", "image.perspective.width", "image.resize.height", "image.resize.width", "jlvision.measure1d.column", "jlvision.measure1d.row" }, optional);
    }

    [Fact]
    public void JoinNode_DoesNotExposeADiagnosticOnlyPolicy()
    {
        var join = BuiltInNodeCatalog.Items.Single(item => item.Type == "flow.join");
        Assert.DoesNotContain(join.Parameters, parameter => parameter.Name == "requireAll");
    }

    [Fact]
    public void InternalParameters_AreNeverRenderedAsUserDefaults()
    {
        // 前端 makeDefaultParameters/configurationParameters 按 Internal 过滤；未声明 Internal 的
        // 以 "__" 开头的键会泄漏到配置面板，这里作为兜底断言。
        foreach (var item in BuiltInNodeCatalog.Items)
        {
            foreach (var parameter in item.Parameters.Where(parameter => parameter.Name.StartsWith("__", StringComparison.Ordinal)))
                Assert.True(parameter.Internal, $"{item.Type}.{parameter.Name} uses the host-injected '__' prefix and must be Internal.");
        }
    }

    /// <summary>
    /// match.shape 与 match.template 六个同名参数的默认值不同（其中两个角色也不同）。
    /// 目录说明必须写明差异，测试锁定这张表：改默认值就必须同步改说明与本用例。
    /// </summary>
    [Theory]
    [InlineData("angleRangeDeg", 180, 0)]
    [InlineData("angleStepDeg", 1, 5)]
    [InlineData("minScore", 0.6, 0.5)]
    [InlineData("maxTargets", 5, 1)]
    [InlineData("minCenterDistance", 20, 0)]
    [InlineData("scaleStepPercent", 1, 10)]
    public void MatchDefaultDivergence_IsPinnedAndDocumented(string parameter, double shapeDefault, double templateDefault)
    {
        Assert.Equal(shapeDefault, Convert.ToDouble(Parameter("match.shape", parameter).DefaultValue));
        Assert.Equal(templateDefault, Convert.ToDouble(Parameter("match.template", parameter).DefaultValue));

        var description = Parameter("match.shape", parameter).Description ?? string.Empty;
        Assert.Contains("Template Match", description, StringComparison.Ordinal);
    }
}
