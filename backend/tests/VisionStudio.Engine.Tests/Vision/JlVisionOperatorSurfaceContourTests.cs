using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 轮廓节点的真执行验收：每个 XLD 算子都要在一段合成轮廓上跑通并返回可枚举的轮廓列表。
/// </summary>
public sealed class JlVisionOperatorSurfaceContourTests
{
    public static IEnumerable<object[]> Operators()
        => JlContourOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Operators))]
    public async Task ContourOperator_Executes(string key)
    {
        var result = await Run(key,
            """{"operator":"__KEY__","size":30,"count":5,"row":48,"column":48,"phi":0,"length1":20,"length2":12,"min":0,"max":1000000000,"mode":""}"""
                .Replace("__KEY__", key));

        Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
        // 生成器不受输入影响；其余算子至少产出 0 条轮廓（裁剪可能清空），但必须能枚举。
        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.NotNull(contours);
        Assert.Equal(contours.Count, Convert.ToInt32(result.Outputs["count"].Require<long>("count")));
    }

    [Fact]
    public async Task ContourOperator_Smooth_PreservesClosedContour()
    {
        var result = await Run("smoothContours",
            """{"operator":"smoothContours","count":5}""");

        var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.NotEmpty(contours);
        Assert.True(contours[0].Points.Count > 3);
        Assert.True(contours[0].Area > 0);
    }

    [Fact]
    public async Task ContourOperator_MissingSecondContour_IsRejected()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run("differenceClosedContours", """{"operator":"differenceClosedContours"}""", withSecond: false));
        Assert.Contains("requires a contour2 input", error.Message, StringComparison.Ordinal);
    }

    private static async Task<NodeExecutorResult> Run(string key, string json, bool withSecond = true)
    {
        var square = Square();
        var inputs = new Dictionary<string, VisionValue>
        {
            ["contour"] = VisionValue.Contours([square])
        };
        if (withSecond)
        {
            var inner = Square(24, 24, 20);
            inputs["contour2"] = VisionValue.Contours([inner]);
            inputs["region"] = VisionValue.Region(SquareRegion());
        }

        var node = new NodeDefinition("n1", "jlvision.contourOp", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await new JLVisionContourOpNode().ExecuteAsync(
            new NodeExecutionContext(inputs), node, CancellationToken.None);
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

    private static VisionRegion SquareRegion()
    {
        var mask = new byte[128 * 128];
        for (var r = 12; r < 52; r++)
            for (var c = 12; c < 52; c++)
                mask[r * 128 + c] = 255;
        return ImageDifferenceMetrics.RegionFromMask(mask, 128, 128);
    }
}
