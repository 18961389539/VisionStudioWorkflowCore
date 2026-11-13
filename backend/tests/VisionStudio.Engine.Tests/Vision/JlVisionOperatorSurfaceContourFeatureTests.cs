using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 轮廓特征节点的真执行验收：每个 XLD 特征都要在一段闭合轮廓上算出有限数值。
/// </summary>
public sealed class JlVisionOperatorSurfaceContourFeatureTests
{
    public static IEnumerable<object[]> Features()
        => JlContourFeatures.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Features))]
    public async Task ContourFeature_ComputesFiniteValue(string key)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["contour"] = VisionValue.Contours([Square()]),
            ["contour2"] = VisionValue.Contours([Square(20, 20, 20)])
        };
        var node = new NodeDefinition("n1", "jlvision.contourFeature", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"feature":"{{key}}","row":5,"column":5,"row2":40,"column2":45,"mode":""}"""));

        var result = await new JLVisionContourFeatureNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);

        var value = result.Outputs["value"].Require<double>("value");
        Assert.False(double.IsNaN(value), $"{key} returned NaN");
        Assert.Equal(key, Convert.ToString(result.Summary["feature"]));
    }

    [Fact]
    public async Task ContourFeature_AreaCenter_ReportsPointCountAndCentroid()
    {
        var inputs = new Dictionary<string, VisionValue> { ["contour"] = VisionValue.Contours([Square()]) };
        var node = new NodeDefinition("n1", "jlvision.contourFeature", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"feature":"areaCenter"}"""));

        var result = await new JLVisionContourFeatureNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);

        var value = result.Outputs["value"].Require<double>("value");
        // 实测 area_center_points_xld 返回的是点数量（不是面积），5 点闭合轮廓返回 4。
        Assert.Equal(4d, value, 6);
        var values = JsonSerializer.Deserialize<Dictionary<string, double>>(
            Convert.ToString(result.Outputs["values"].Require<string>("values"))!)!;
        Assert.Equal(32d, values["row"], 3);
        Assert.Equal(32d, values["column"], 3);
    }

    [Fact]
    public async Task ContourFeature_MissingSecondContour_IsRejected()
    {
        var inputs = new Dictionary<string, VisionValue> { ["contour"] = VisionValue.Contours([Square()]) };
        var node = new NodeDefinition("n1", "jlvision.contourFeature", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"feature":"distanceCc"}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new JLVisionContourFeatureNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None));
        Assert.Contains("requires a contour2 input", error.Message, StringComparison.Ordinal);
    }

    private static VisionContour Square(int x = 12, int y = 12, int size = 40)
    {
        var points = new List<VisionPoint2D>
        {
            new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size), new(x, y)
        };
        return new VisionContour(0, points, -1, false, size * size, 4 * size, 0.78,
            new VisionRectangle2D(x, y, size, size), new VisionCircle(x + size / 2d, y + size / 2d, size / 2d));
    }
}
