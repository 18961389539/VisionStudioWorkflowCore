using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 区域算子节点的真执行验收：表里每个 <see cref="JlRegionOperations"/> 算子都要在一块合成区域上跑通，
/// 并返回一个可枚举的区域对象。
/// </summary>
public sealed class JlVisionOperatorSurfaceRegionTests
{
    public static IEnumerable<object[]> RegionOperators()
        => JlRegionOperations.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(RegionOperators))]
    public async Task RegionOperator_ExecutesOnSyntheticRegion(string key)
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue>
            {
                ["region"] = VisionValue.Region(region),
                ["region2"] = VisionValue.Region(region),
                ["image"] = VisionValue.Image(VisionImage.Own(gray.Clone()))
            };
            var node = new NodeDefinition("n1", "jlvision.regionOp", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    $$"""{"operator":"{{key}}","size":5,"count":1,"row":20,"column":20,"phi":0,"length1":12,"length2":8,"min":0,"max":1000000000,"percent":50,"mode":""}"""));

            var result = await new JLVisionRegionOpNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);

            var output = result.Outputs["region"].Require<VisionRegion>("region");
            Assert.Equal(key, Convert.ToString(result.Summary["operator"]));
            Assert.Equal("jlvision", Convert.ToString(result.Summary["algorithmBackend"]));
            Assert.Equal(64, output.Width);
            Assert.Equal(64, output.Height);
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    [Fact]
    public async Task RegionOperator_MissingSecondRegion_IsRejectedBeforeNativeCall()
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue> { ["region"] = VisionValue.Region(region) };
            var node = new NodeDefinition("n1", "jlvision.regionOp", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"operator":"difference"}"""));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await new JLVisionRegionOpNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None));

            Assert.Contains("requires a region2 input", error.Message, StringComparison.Ordinal);
        }
    }

    private static (VisionRegion Region, Mat Gray) BuildScene()
    {
        var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(10, 10, 24, 18), Scalar.All(255), -1);
        Cv2.Circle(mask, new Point(46, 46), 8, Scalar.All(255), -1);
        var bytes = new byte[64 * 64];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, bytes, 0, bytes.Length);
        mask.Dispose();
        var region = ImageDifferenceMetrics.RegionFromMask(bytes, 64, 64);

        var gray = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(gray, new Rect(10, 10, 24, 18), Scalar.All(210), -1);
        Cv2.Circle(gray, new Point(46, 46), 8, Scalar.All(160), -1);
        return (region, gray);
    }
}
