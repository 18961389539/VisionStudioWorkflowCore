using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 区域特征节点的真执行验收：表里每个特征都要在一块合成区域（含灰度图）上算出有限数值。
/// </summary>
public sealed class JlVisionOperatorSurfaceRegionFeatureTests
{
    public static IEnumerable<object[]> Features()
        => JlRegionFeatures.Keys.Select(key => new object[] { key });

    [Theory]
    [MemberData(nameof(Features))]
    public async Task RegionFeature_ComputesFiniteValue(string key)
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue>
            {
                ["region"] = VisionValue.Region(region),
                ["image"] = VisionValue.Image(VisionImage.Own(gray.Clone()))
            };
            var node = new NodeDefinition("n1", "jlvision.regionFeature", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    $$"""{"feature":"{{key}}","size":5,"count":2,"row":20,"column":20,"min":0,"max":255,"mode":""}"""));

            var result = await new JLVisionRegionFeatureNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);

            var value = result.Outputs["value"].Require<double>("value");
            Assert.False(double.IsNaN(value), $"{key} returned NaN");
            Assert.Equal(key, Convert.ToString(result.Summary["feature"]));
            Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(result.Outputs["values"].Require<string>("values"))));
            foreach (var item in inputs.Values) (item.Value as VisionImage)?.Dispose();
        }
    }

    [Fact]
    public async Task RegionFeature_MissingImage_IsRejectedUpFront()
    {
        var (region, gray) = BuildScene();
        using (gray)
        {
            var inputs = new Dictionary<string, VisionValue> { ["region"] = VisionValue.Region(region) };
            var node = new NodeDefinition("n1", "jlvision.regionFeature", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"feature":"areaCenterGray"}"""));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await new JLVisionRegionFeatureNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None));

            Assert.Contains("requires an image input", error.Message, StringComparison.Ordinal);
        }
    }

    private static (VisionRegion Region, Mat Gray) BuildScene()
    {
        var mask = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new Rect(14, 16, 40, 26), Scalar.All(255), -1);
        Cv2.Circle(mask, new Point(68, 66), 14, Scalar.All(255), -1);
        var bytes = new byte[96 * 96];
        System.Runtime.InteropServices.Marshal.Copy(mask.Data, bytes, 0, bytes.Length);
        mask.Dispose();
        var region = ImageDifferenceMetrics.RegionFromMask(bytes, 96, 96);

        var gray = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(gray, new Rect(14, 16, 40, 26), Scalar.All(200), -1);
        Cv2.Circle(gray, new Point(68, 66), 14, Scalar.All(150), -1);
        Cv2.Line(gray, new Point(0, 90), new Point(95, 60), Scalar.All(90), 2);
        return (region, gray);
    }
}
