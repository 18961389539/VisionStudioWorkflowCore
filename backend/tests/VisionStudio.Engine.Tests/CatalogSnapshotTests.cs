namespace VisionStudio.Engine.Tests;

public sealed class CatalogSnapshotTests
{
    [Fact]
    public void CatalogHash_IsIndependentOfRegistrationOrder()
    {
        var forward = VisionCatalogSnapshot.ComputeHash(BuiltInNodeCatalog.Items);
        var reverse = VisionCatalogSnapshot.ComputeHash(BuiltInNodeCatalog.Items.Reverse());
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void CatalogHash_ChangesWhenExecutableUiContractChanges()
    {
        var original = BuiltInNodeCatalog.Items.ToArray();
        var modified = original
            .Select(x => x.Type == "image.threshold" ? x with { Description = x.Description + " changed" } : x)
            .ToArray();

        Assert.NotEqual(VisionCatalogSnapshot.ComputeHash(original), VisionCatalogSnapshot.ComputeHash(modified));
    }

    [Fact]
    public void GeneratedDocument_RoundTripsAndPreservesSemanticHash()
    {
        var expected = VisionCatalogSnapshot.Create(BuiltInNodeCatalog.Items);
        var json = VisionCatalogSnapshot.Serialize(expected);
        var actual = VisionCatalogSnapshot.Deserialize(json);

        Assert.NotNull(actual);
        Assert.Equal(VisionCatalogSnapshot.SchemaVersion, actual!.SchemaVersion);
        Assert.Equal(expected.Hash, actual.Hash);
        Assert.Equal(expected.Hash, VisionCatalogSnapshot.ComputeHash(actual.Items));
        Assert.Equal(BuiltInNodeCatalog.Items.Count, actual.Items.Count);
    }
    [Fact]
    public void CatalogHash_IgnoresJsonObjectPropertyOrderInsidePluginDefaults()
    {
        var template = BuiltInNodeCatalog.Require("image.synthetic");
        var a = template with
        {
            Parameters = [new ParameterDescriptor("config", "Config", "json", new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 })]
        };
        var b = template with
        {
            Parameters = [new ParameterDescriptor("config", "Config", "json", new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 })]
        };

        Assert.Equal(VisionCatalogSnapshot.ComputeHash([a]), VisionCatalogSnapshot.ComputeHash([b]));
    }

}
