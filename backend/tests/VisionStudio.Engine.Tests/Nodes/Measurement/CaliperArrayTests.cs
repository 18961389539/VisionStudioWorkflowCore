using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

public sealed class CaliperArrayTests
{
    [Fact]
    public async Task MeasuresUniformStripAcrossStationsWithAggregateStats()
    {
        const double left = 30.25, right = 63.75;
        using var image = MakeStrip(left, right, height: 96);
        var result = await Run(image, new()
        {
            ["startX"] = 5, ["startY"] = 48, ["endX"] = 120, ["endY"] = 48,
            ["caliperCount"] = 7, ["edgeLength"] = 60, ["minValidCalipers"] = 5,
            ["sampleWidth"] = 1, ["includeProfiles"] = true, ["profileSampleCap"] = 32
        });
        using var _ = result.Outputs["image"].Require<VisionImage>("image");

        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(7L, result.Outputs["caliperCount"].Require<long>("caliperCount"));
        Assert.True(result.Outputs["validCount"].Require<long>("validCount") >= 5);
        Assert.InRange(result.Outputs["widthMean"].Require<double>("widthMean"), right - left - .2, right - left + .2);
        Assert.InRange(result.Outputs["widthMin"].Require<double>("widthMin"), right - left - .35, right - left + .35);
        Assert.InRange(result.Outputs["widthMax"].Require<double>("widthMax"), right - left - .35, right - left + .35);
        Assert.True(result.Outputs["widthStd"].Require<double>("widthStd") < 0.5);
        Assert.True(result.Outputs.ContainsKey("widthCv"));
        Assert.Equal("px", result.Summary["unit"]);

        using var doc = JsonDocument.Parse(result.Outputs["calipersJson"].Require<string>("calipersJson"));
        Assert.Equal(7, doc.RootElement.GetArrayLength());
        var foundStations = 0;
        foreach (var station in doc.RootElement.EnumerateArray())
        {
            if (!station.GetProperty("found").GetBoolean()) continue;
            foundStations++;
            Assert.True(station.TryGetProperty("profile", out var profile));
            Assert.True(profile.GetArrayLength() <= 32);
            Assert.InRange(station.GetProperty("width").GetDouble(), right - left - .35, right - left + .35);
        }
        Assert.True(foundStations >= 5);
        Assert.Contains(result.Overlays ?? [], o => o.Id == "caliper-array-label");
        Assert.Contains(result.Overlays ?? [], o => o.Id.StartsWith("caliper-array-profile-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleValidStation_IsRejectedBySpatialCoverage_AndEveryMissIsExplained()
    {
        // 只在中间一条很窄的水平带里有边缘：9 个站点（沿切线方向分布）里只有中间 1~2 站能看到。
        using var image = MakeHorizontalBand(left: 30, right: 64, bandTop: 44, bandHeight: 8, height: 96);
        var result = await Run(image, new()
        {
            ["startX"] = 5, ["startY"] = 48, ["endX"] = 120, ["endY"] = 48,
            ["caliperCount"] = 9, ["edgeLength"] = 60, ["sampleWidth"] = 1
        });
        DisposeImage(result);

        var valid = result.Outputs["validCount"].Require<long>("validCount");
        // minValidCalipers=1 也救不了它：覆盖率门限才是这里的判据。
        Assert.True(valid <= 3, $"expected a handful of valid stations, got {valid}");
        Assert.False(Found(result), "a single visible station must not produce a passing width");
        Assert.Equal("insufficient-spatial-coverage", Convert.ToString(result.Summary["reason"]));
        // 默认 minValidRatio=0.5：一站有效远低于门限，所以判据是覆盖率而不是 minValidCalipers。
        Assert.True(result.Outputs["validRatio"].Require<double>("validRatio") < 0.5);
        Assert.True(result.Outputs["coverageRatio"].Require<double>("coverageRatio") < 0.5);

        // 每一个缺失站点都必须带索引和原因，不能只给一个总数。
        using var doc = JsonDocument.Parse(result.Outputs["invalidStations"].Require<string>("invalidStations"));
        Assert.Equal(9 - valid, doc.RootElement.GetArrayLength());
        foreach (var station in doc.RootElement.EnumerateArray())
        {
            Assert.True(station.TryGetProperty("Index", out var index));
            Assert.InRange(index.GetInt32(), 0, 8);
            Assert.False(string.IsNullOrWhiteSpace(station.GetProperty("Reason").GetString()));
        }
    }

    [Fact]
    public async Task RejectsArrayWhenTooFewStationsFindPairs()
    {
        // Uniform dark field: no edge pairs anywhere → validCount=0.
        using var mat = new Mat(80, 160, MatType.CV_8UC1, Scalar.All(40));
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(image, new()
        {
            ["startX"] = 10, ["startY"] = 40, ["endX"] = 150, ["endY"] = 40,
            ["caliperCount"] = 9, ["edgeLength"] = 70, ["minValidCalipers"] = 3,
            ["sampleWidth"] = 1
        });
        DisposeImage(result);
        Assert.False(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(0L, result.Outputs["validCount"].Require<long>("validCount"));
        Assert.Equal("insufficient-spatial-coverage", result.Summary["reason"]);
        Assert.Equal(0d, result.Outputs["validRatio"].Require<double>("validRatio"));
        Assert.Equal(0d, result.Outputs["coverageRatio"].Require<double>("coverageRatio"));
        Assert.Equal(0d, result.Outputs["continuousRatio"].Require<double>("continuousRatio"));
        Assert.False(result.Outputs.ContainsKey("widthMean"));
    }

    [Fact]
    public async Task MaxWidthStdGateRejectsNonUniformArray()
    {
        using var mat = new Mat(96, 180, MatType.CV_8UC1, Scalar.All(20));
        // Tapered bright region: width grows along Y so station widths diverge.
        var cols = mat.Cols;
        for (var y = 10; y < 86; y++)
        {
            var width = 20 + (y - 10) / 2;
            var x0 = 60;
            for (var x = x0; x < x0 + width && x < cols; x++)
                mat.Set(y, x, (byte)220);
        }
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(image, new()
        {
            ["startX"] = 20, ["startY"] = 48, ["endX"] = 160, ["endY"] = 48,
            ["caliperCount"] = 7, ["edgeLength"] = 50, ["minValidCalipers"] = 3,
            ["sampleWidth"] = 1, ["maxWidthStd"] = 0.5, ["polarity"] = "bright"
        });
        DisposeImage(result);
        Assert.False(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal("width-std-exceeds-tolerance", result.Summary["reason"]);
        Assert.True(Convert.ToBoolean(result.Summary["stdGateFailed"]));
        Assert.True(result.Outputs.ContainsKey("widthStd"));
        Assert.True(result.Outputs["widthStd"].Require<double>("widthStd") > 0.5);
    }

    [Fact]
    public async Task SingleStationMatchesCaliperWidthSemantics()
    {
        const double left = 40, right = 70;
        using var image = MakeStrip(left, right);
        var result = await Run(image, new()
        {
            ["startX"] = 5, ["startY"] = 32, ["endX"] = 120, ["endY"] = 32,
            ["caliperCount"] = 1, ["minValidCalipers"] = 1, ["sampleWidth"] = 1
        });
        DisposeImage(result);
        Assert.True(Found(result), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(1L, result.Outputs["validCount"].Require<long>("validCount"));
        Assert.InRange(result.Outputs["widthMean"].Require<double>("widthMean"), 29.7, 30.3);
        Assert.Equal(0d, result.Outputs["widthStd"].Require<double>("widthStd"));
    }

    [Fact]
    public async Task ToleranceGateReportsDeviationWithoutChangingFound()
    {
        using var image = MakeStrip(40, 70);
        var baseline = new Dictionary<string, object?>
        {
            ["startX"] = 5, ["startY"] = 32, ["endX"] = 120, ["endY"] = 32,
            ["caliperCount"] = 3, ["minValidCalipers"] = 1, ["sampleWidth"] = 1, ["nominalWidth"] = 33,
            ["minValidRatio"] = 0, ["minCoverageRatio"] = 0, ["minContinuousRatio"] = 0
        };
        var disabled = await Run(image, baseline);
        DisposeImage(disabled);
        Assert.True(Found(disabled), Convert.ToString(disabled.Summary["reason"]));
        Assert.Equal("not-evaluated", disabled.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.False(disabled.Outputs["withinTolerance"].Require<bool>("withinTolerance"));

        var pass = new Dictionary<string, object?>(baseline) { ["lowerTolerance"] = 4, ["upperTolerance"] = 1 };
        var passing = await Run(image, pass);
        DisposeImage(passing);
        Assert.True(Found(passing));
        Assert.True(passing.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("pass", passing.Outputs["evaluationState"].Require<string>("evaluationState"));
        Assert.InRange(passing.Outputs["widthDeviation"].Require<double>("widthDeviation"), -4, 1);

        var fail = new Dictionary<string, object?>(baseline) { ["lowerTolerance"] = 0.01, ["upperTolerance"] = 0.01 };
        var failing = await Run(image, fail);
        DisposeImage(failing);
        // 超差不是“未测到”：found 与 widthMean 仍在，只翻转判定。
        Assert.True(Found(failing));
        Assert.True(failing.Outputs.ContainsKey("widthMean"));
        Assert.False(failing.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Equal("fail", failing.Outputs["evaluationState"].Require<string>("evaluationState"));
    }

    [Fact]
    public async Task CatalogRegistersCaliperArray()
    {
        var item = BuiltInNodeCatalog.Require("measure.caliperArray");
        Assert.Equal("Measurement", item.Category);
        Assert.Contains(item.Outputs, o => o.Name == "calipersJson");
        Assert.Contains(item.Outputs, o => o.Name == "widthMean");
        Assert.Contains(item.Parameters, p => p.Name == "caliperCount");
        Assert.Contains(item.Parameters, p => p.Name == "includeProfiles");
    }

    private static bool Found(NodeExecutorResult result) => result.Outputs["found"].Require<bool>("found");
    private static void DisposeImage(NodeExecutorResult result) => result.Outputs["image"].Require<VisionImage>("image").Dispose();

    /// <summary>只在 [bandTop, bandTop+bandHeight) 行有亮条：用于证明站点空间覆盖不足会被拒绝。</summary>
    private static VisionImage MakeHorizontalBand(double left, double right, int bandTop, int bandHeight, int height)
    {
        var mat = new Mat(height, 128, MatType.CV_8UC1, Scalar.All(20));
        for (var y = bandTop; y < Math.Min(height, bandTop + bandHeight); y++)
            for (var x = (int)left; x <= (int)right; x++)
                mat.Set(y, x, (byte)220);
        return VisionImage.Own(mat);
    }

    private static VisionImage MakeStrip(double left, double right, bool dark = false, int height = 64)
    {
        var mat = new Mat(height, 128, MatType.CV_8UC1);
        var cols = mat.Cols; var rows = mat.Rows;
        for (var x = 0; x < cols; x++)
        {
            var fraction = 1 / (1 + Math.Exp(-(x - left) * 2)) - 1 / (1 + Math.Exp(-(x - right) * 2));
            var value = 20 + 200 * (dark ? 1 - fraction : fraction);
            for (var y = 0; y < rows; y++)
                mat.Set(y, x, (byte)Math.Round(value));
        }
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> Run(VisionImage image, Dictionary<string, object?> parameters)
    {
        var settings = new Dictionary<string, object?>
        {
            ["startX"] = 5, ["startY"] = 32, ["endX"] = 120, ["endY"] = 32,
            ["sampleWidth"] = 1, ["edgeThreshold"] = 12, ["polarity"] = "bright"
        };
        foreach (var (key, value) in parameters) settings[key] = value;
        var node = new NodeDefinition("array", "measure.caliperArray", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(settings)));
        return await new CaliperArrayNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) }),
            node, CancellationToken.None);
    }
}