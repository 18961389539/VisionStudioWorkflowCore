using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 批次 D 回归：手动阈值的极端占比自检（告警 + Otsu 对照值），以及区间分割的 16 位精度与颜色模式。
/// </summary>
public sealed class ThresholdDiagnosticsAndColorRangeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task ManualThreshold_WarnsAndSuggestsOtsuWhenNothingIsSelected()
    {
        // 全图灰度 50，手动阈值 200 → 选中 0%。这类配置以前静默产出一张全黑图。
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(50));
        var result = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":200}"""), CancellationToken.None);

        Assert.Equal(1L, Convert.ToInt64(result.Outputs["warningCount"].Value));
        Assert.Equal(1, Convert.ToInt32(result.Summary["warningCount"]));
        Assert.True(result.Summary.ContainsKey("suggestedThreshold"));
        var warning = Convert.ToString(result.Summary["thresholdWarning"]);
        Assert.Contains("Otsu suggests", warning);
        Assert.Contains("selected 0", warning);
    }

    [Fact]
    public async Task ManualThreshold_StaysQuietForAReasonableSelection()
    {
        using var img = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(200), -1);
        var result = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":100}"""), CancellationToken.None);

        Assert.Equal(0.5d, Convert.ToDouble(result.Outputs["foregroundRatio"].Value), 9);
        Assert.False(result.Summary.ContainsKey("thresholdWarning"));
        Assert.False(result.Summary.ContainsKey("suggestedThreshold"));
    }

    [Fact]
    public async Task RangeSegmentation_KeepsSixteenBitPrecision()
    {
        // 16 位图：左半 1000、右半 3000。区间 1500..4000 只能命中右半，且不做 8 位缩放。
        using var img = new Mat(100, 200, MatType.CV_16UC1, Scalar.All(1000));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(3000), -1);

        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":1500,"maxGray":4000}"""), CancellationToken.None);

        Assert.Equal(100d * 100d, Convert.ToDouble(result.Outputs["area"].Value), 9);
        Assert.Equal("uint2", Convert.ToString(result.Summary["pixelType"]));
        Assert.Equal(65535d, Convert.ToDouble(result.Summary["grayUpperLimit"]));
    }

    [Fact]
    public async Task RangeSegmentation_ColorModeSelectsTheColorBand()
    {
        // 左半纯红 (BGR 0,0,255)、右半纯蓝 (BGR 255,0,0)；只放行 R 通道 0.8..1.0 → 命中左半。
        using var img = new Mat(100, 200, MatType.CV_8UC3, new Scalar(255, 0, 0));
        Cv2.Rectangle(img, new Rect(0, 0, 100, 100), new Scalar(0, 0, 255), -1);

        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"color","space":"rgb","min1":0.8,"max1":1,"min2":0,"max2":1,"min3":0,"max3":1}"""),
            CancellationToken.None);

        Assert.Equal(100d * 100d, Convert.ToDouble(result.Outputs["area"].Value), 9);
        Assert.Equal(1L, Convert.ToInt64(result.Outputs["blobCount"].Value));
        Assert.Equal("color", Convert.ToString(result.Summary["mode"]));
    }
}
