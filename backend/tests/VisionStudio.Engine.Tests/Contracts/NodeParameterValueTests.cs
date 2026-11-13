using System.Globalization;
using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// "节点值优先、目录默认兜底"的唯一实现（2026-11-10）。
/// 重点守住：数值默认值必须用 invariant 文本参与条件比较，不能被区域设置格式化成 "0,5"。
/// </summary>
public sealed class NodeParameterValueTests
{
    private static NodeCatalogItem Catalog(params ParameterDescriptor[] parameters) => new(
        "test.node", "Test Node", "Test",
        [new PortDescriptor("exec", VisionDataType.Control), new PortDescriptor("image", VisionDataType.Image, Required: false)],
        [new PortDescriptor("next", VisionDataType.Control)],
        parameters,
        Actions: [new NodeActionDeclaration(NodeActionKind.DeviceWrite, NodeActionCompletion.Synchronous, "mode", "0.5")]);

    [Fact]
    public void NodeValue_WinsOverCatalogDefault()
    {
        var catalog = Catalog(new ParameterDescriptor("mode", "Mode", "select", "idle"));
        var node = new NodeDefinition("n", "test.node", null, null, Params("""{"mode":"run"}"""));

        Assert.Equal("run", NodeParameterValue.Resolve(node, catalog, "mode"));
    }

    [Fact]
    public void NumericDefaults_UseInvariantText_AcrossCultureSensitiveConsumers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var catalog = Catalog(new ParameterDescriptor("mode", "Mode", "select", 0.5));
            var node = new NodeDefinition("n", "test.node", null, null, null);
            var conditional = new PortDescriptor("image", VisionDataType.Image, Required: false)
            {
                RequiredWhen = new ParameterVisibility("mode", ["0.5"])
            };

            Assert.Equal("0.5", NodeParameterValue.Resolve(node, catalog, "mode"));
            Assert.True(NodeActionContract.Applies(node, catalog, catalog.Actions![0]));
            Assert.True(conditional.IsRequired(node, catalog));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingDescriptor_AndNonScalarNodeValue_ReturnNull()
    {
        var catalog = Catalog();
        var node = new NodeDefinition("n", "test.node", null, null, Params("""{"mode":{"nested":true}}"""));

        Assert.Null(NodeParameterValue.Resolve(node, catalog, "missing"));
        Assert.Null(NodeParameterValue.Resolve(node, catalog, "mode"));
    }

    private static Dictionary<string, JsonElement> Params(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
