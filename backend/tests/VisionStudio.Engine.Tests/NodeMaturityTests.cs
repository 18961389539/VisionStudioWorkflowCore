using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStudio.Abstractions;
using VisionStudio.Engine;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 节点成熟度声明（2026-10-29）。这组用例不是"检查字段能不能读"，而是给分级加棘轮：
/// 升降级必须改 <see cref="NodeMaturityCatalog"/> 与本文件的期望表，否则测试失败——
/// 防止"补完测试忘了升级"或"新节点悄悄以 Stable 出厂"。
/// </summary>
public sealed class NodeMaturityTests
{
    /// <summary>当前 Experimental 集合（算法路径没有行为基线）。补了模型级用例就把类型移出这里。</summary>
    private static readonly string[] ExpectedExperimental =
    [
        "inference.instanceSegmentation",
        "inference.obb",
        "inference.pose"
    ];

    /// <summary>当前 Preview 集合（有行为基线但文档写明适用范围）。</summary>
    private static readonly string[] ExpectedPreview =
    [
        "contour.fromMask",
        "device.serialPort",
        "device.tcpRequest",
        "device.udpMessage",
        "image.load",
        "match.feature",
        "measure.gear",
        "measure.profileFilter",
        "measure.roughness",
        "measure.thread",
        "output.database",
        "recognition.barcodeQuality",
        "region.fromMask",
        "tolerance.stackup",
        "value.counter"
    ];

    private static string[] TypesAt(NodeMaturity maturity) => NodeMaturityCatalog.Declarations
        .Where(x => x.Value.Maturity == maturity)
        .Select(x => x.Key)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Declarations_OnlyReferenceExistingCatalogNodes()
    {
        var catalog = BuiltInNodeCatalog.Items.Select(item => item.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = NodeMaturityCatalog.Declarations.Keys
            .Where(type => !catalog.Contains(type))
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        // 节点改名/被合并后，声明表会静默失效（找不到就永远不生效）。这里必须炸出来。
        Assert.Empty(unknown);
    }

    [Fact]
    public void Declarations_AlwaysExplainTheLevel()
    {
        foreach (var (type, declaration) in NodeMaturityCatalog.Declarations)
        {
            Assert.False(string.IsNullOrWhiteSpace(declaration.Reason), $"{type} 缺少分级理由。");
            // 一句话起步：太短的理由等于没写，审计复盘时看不出为什么降级。
            Assert.True(declaration.Reason.Length >= 16, $"{type} 的分级理由过短：{declaration.Reason}");
        }
    }

    [Fact]
    public void Catalog_AppliesDeclaredMaturityAndDefaultsToStable()
    {
        var catalog = BuiltInNodeCatalog.Items.ToDictionary(item => item.Type, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, declaration) in NodeMaturityCatalog.Declarations)
            Assert.Equal(declaration.Maturity, catalog[type].Maturity);

        var undeclared = catalog.Values.Where(item => NodeMaturityCatalog.Find(item.Type) is null).ToArray();
        Assert.NotEmpty(undeclared);
        Assert.All(undeclared, item => Assert.Equal(NodeMaturity.Stable, item.Maturity));
    }

    [Fact]
    public void Levels_MatchTheAuditedRatchet()
    {
        // 期望表按类型名字符串序排列，diff 时一眼能看出谁升级/降级了。
        Assert.Equal(ExpectedExperimental, TypesAt(NodeMaturity.Experimental));
        Assert.Equal(ExpectedPreview, TypesAt(NodeMaturity.Preview));
    }

    [Fact]
    public void CatalogJson_OnlyEmitsNonStableMaturity()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());

        var catalog = BuiltInNodeCatalog.Items.ToDictionary(item => item.Type, StringComparer.OrdinalIgnoreCase);

        // Stable 不写字段：目录 JSON 形状与哈希对绝大多数节点保持不变。
        using var stable = JsonDocument.Parse(JsonSerializer.Serialize(catalog["image.crop"], options));
        Assert.False(stable.RootElement.TryGetProperty("maturity", out _));

        // 非 Stable 写字符串（与 API 全局 JsonStringEnumConverter 一致），前端不需要认数字。
        using var preview = JsonDocument.Parse(JsonSerializer.Serialize(catalog["measure.thread"], options));
        Assert.True(preview.RootElement.TryGetProperty("maturity", out var maturity));
        Assert.Equal("Preview", maturity.GetString());

        using var experimental = JsonDocument.Parse(JsonSerializer.Serialize(catalog["inference.obb"], options));
        Assert.True(experimental.RootElement.TryGetProperty("maturity", out var level));
        Assert.Equal("Experimental", level.GetString());
    }

    [Fact]
    public void CatalogSnapshot_RoundTripsMaturity()
    {
        var document = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
        var restored = VisionCatalogSnapshot.Deserialize(VisionCatalogSnapshot.Serialize(document));

        Assert.NotNull(restored);
        Assert.Equal(document.Hash, VisionCatalogSnapshot.ComputeHash(restored!.Items));
        Assert.Equal(NodeMaturity.Experimental,
            restored.Items.Single(item => item.Type == "inference.obb").Maturity);
        Assert.Equal(NodeMaturity.Stable,
            restored.Items.Single(item => item.Type == "image.crop").Maturity);
    }
}
