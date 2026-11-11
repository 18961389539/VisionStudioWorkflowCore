using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 批次 C 回归：阈值/边界可由端口驱动、image.threshold 前景赋值可配置、
/// region.fromRange 的 ROI 限制与反选（补集不越过 ROI/域），以及 count 与 blobCount 的差异。
/// </summary>
public sealed class ThresholdRangeInputTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static double Number(NodeExecutorResult result, string port) => Convert.ToDouble(result.Outputs[port].Value);
    private static long Integer(NodeExecutorResult result, string port) => Convert.ToInt64(result.Outputs[port].Value);

    private static Mat TwoBlocks()
    {
        var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(50));
        Cv2.Rectangle(img, new Rect(4, 4, 10, 10), Scalar.All(200), -1);
        Cv2.Rectangle(img, new Rect(30, 30, 10, 10), Scalar.All(200), -1);
        return img;
    }

    [Fact]
    public async Task ManualThreshold_ForegroundValueIsConfigurable()
    {
        // 前景赋值 1 时输出 0/1 掩膜；占比按非零像素统计，不受赋值大小影响。
        using var img = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(200), -1);

        var result = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":100,"maxValue":1}"""), CancellationToken.None);

        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0.5d, Number(result, "foregroundRatio"), 9);
        using var foreground = new Mat(output.Mat, new Rect(100, 0, 100, 100));
        JLVisionOperators.MinMax(foreground, out var foregroundMin, out var foregroundMax);
        Assert.Equal(1d, foregroundMin);
        Assert.Equal(1d, foregroundMax);
        JLVisionOperators.MinMax(output.Mat, out _, out var wholeMax);
        Assert.Equal(1d, wholeMax);
        Assert.Equal(1d, Convert.ToDouble(result.Summary["maxValue"]));
    }

    [Fact]
    public async Task ManualThreshold_ThresholdValuePortOverridesParameter()
    {
        // 参数写 10（左半 0 也会通过），端口给 150 → 只有右半 200 通过，说明端口优先。
        using var img = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(100, 0, 100, 100), Scalar.All(200), -1);

        var result = await new ThresholdNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["thresholdValue"] = VisionValue.Double(150) }),
            Def("image.threshold", """{"mode":"manual","threshold":10}"""), CancellationToken.None);

        Assert.Equal(0.5d, Number(result, "foregroundRatio"), 9);
        Assert.Equal(150d, Number(result, "threshold"), 9);
    }

    [Fact]
    public async Task AutoModes_RejectThresholdValuePortInsteadOfIgnoringIt()
    {
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(128));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ThresholdNode().ExecuteAsync(
                Ctx(new() { ["image"] = Img(img), ["thresholdValue"] = VisionValue.Double(120) }),
                Def("image.threshold", """{"mode":"otsu"}"""), CancellationToken.None));
        Assert.Contains("only used by mode=manual", error.Message);
    }

    [Fact]
    public async Task RangeSegmentation_InvertReturnsComplementWithBlobCount()
    {
        using var img = TwoBlocks();
        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":150,"maxGray":255,"invert":true}"""), CancellationToken.None);

        // 64×64 画布去掉两个 10×10 面片，背景连成一片。
        Assert.Equal(64d * 64d - 200d, Number(result, "area"), 9);
        Assert.Equal(1L, Integer(result, "blobCount"));
        Assert.True((bool)result.Outputs["found"].Value!);
    }

    [Fact]
    public async Task RangeSegmentation_RoiLimitsSelectionToTheRoi()
    {
        using var img = TwoBlocks();
        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":150,"maxGray":255,"roi":{"type":"Rectangle","x":0,"y":0,"width":20,"height":20}}"""),
            CancellationToken.None);

        // 只有 ROI 内的第一块被保留。
        Assert.Equal(100d, Number(result, "area"), 9);
        Assert.Equal(1L, Integer(result, "blobCount"));
        Assert.Equal(1L, Integer(result, "count"));
        Assert.Contains("\"hasRoi\":true", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task RangeSegmentation_InvertStaysInsideTheRoi()
    {
        using var img = TwoBlocks();
        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":150,"maxGray":255,"invert":true,"roi":{"type":"Rectangle","x":0,"y":0,"width":20,"height":20}}"""),
            CancellationToken.None);

        // ROI 400 像素减去块 100 像素，而不是整幅画布的 3896。
        Assert.Equal(300d, Number(result, "area"), 9);
        Assert.Equal(1L, Integer(result, "blobCount"));
    }

    [Fact]
    public async Task RangeSegmentation_BoundsFromPortsOverrideParameters()
    {
        using var img = TwoBlocks();
        var result = await new RegionFromRangeNode().ExecuteAsync(
            Ctx(new()
            {
                ["image"] = Img(img),
                ["minGray"] = VisionValue.Double(150),
                ["maxGray"] = VisionValue.Double(255)
            }),
            Def("region.fromRange", """{"mode":"gray","minGray":0,"maxGray":50}"""), CancellationToken.None);

        // 参数区间 0..50 只会选中背景；端口给的 150..255 选中两块面片。
        Assert.Equal(200d, Number(result, "area"), 9);
        Assert.Equal(2L, Integer(result, "blobCount"));
    }
    [Fact]
    public async Task RangeSegmentation_ReportsBlobCountNextToObjectCount()
    {
        // 两块互不相连的面片：count 是区域对象数（阈值区域不拆分，恒为 1），blobCount 才是目标个数。
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(50));
        Cv2.Rectangle(img, new Rect(4, 4, 10, 10), Scalar.All(200), -1);
        Cv2.Rectangle(img, new Rect(30, 30, 10, 10), Scalar.All(200), -1);

        var result = await new RegionFromRangeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("region.fromRange", """{"mode":"gray","minGray":150,"maxGray":255}"""), CancellationToken.None);

        Assert.Equal(1L, Convert.ToInt64(result.Outputs["count"].Value));
        Assert.Equal(2L, Convert.ToInt64(result.Outputs["blobCount"].Value));
        Assert.Equal(200d, Convert.ToDouble(result.Outputs["area"].Value), 9);
        Assert.Contains("\"blobCount\":2", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }
}
