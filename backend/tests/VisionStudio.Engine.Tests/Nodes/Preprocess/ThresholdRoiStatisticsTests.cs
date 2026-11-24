using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// image.threshold 的 ROI 统计口径回归（批次 A 修复点）：
///   · foregroundRatio 曾用 long/long 整除，前景不足一半一律得 0；
///   · 分母曾是整图面积，ROI 内全前景也只报 ROI 占比的整图稀释值；
///   · Otsu/自适应曾在整图上求阈值，ROI 外的高亮会把阈值拉偏，ROI 只影响输出掩膜。
/// </summary>
public sealed class ThresholdRoiStatisticsTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static double Number(NodeExecutorResult result, string port) => Convert.ToDouble(result.Outputs[port].Value);

    private const string LeftHalfRoi = """{"type":"Rectangle","x":0,"y":0,"width":100,"height":100}""";
    private const string CircularRoi = """{"type":"Circle","x":40,"y":40,"radius":20}""";
    private const string PolygonalRoi = """{"type":"Polygon","points":[{"x":20,"y":20},{"x":60,"y":20},{"x":20,"y":60}]}""";

    [Fact]
    public async Task ManualRatio_DividesByRoiPixelsWithoutIntegerTruncation()
    {
        // 左半 255（ROI）、右半 0：ROI 内全是前景。
        using var img = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(0, 0, 100, 100), Scalar.All(255), -1);

        var withRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", $$"""{"mode":"manual","threshold":100,"roi":{{LeftHalfRoi}}}"""), CancellationToken.None);
        var withoutRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"manual","threshold":100}"""), CancellationToken.None);

        // 10000 个前景 / 10000 个 ROI 像素 = 1.0；旧实现 10000/20000 走整除得 0。
        Assert.Equal(1.0, Number(withRoi, "foregroundRatio"), 9);
        Assert.Equal(10000L, Convert.ToInt64(withRoi.Outputs["nonZeroCount"].Value));
        // 无 ROI 时分母是整图：10000/20000 = 0.5（旧实现同样得 0）。
        Assert.Equal(0.5, Number(withoutRoi, "foregroundRatio"), 9);
    }

    [Fact]
    public async Task OtsuThreshold_IsComputedFromRoiPixelsOnly()
    {
        // 左半 0..99 列梯度（ROI）、右半恒 255：全图 Otsu 落在 ramp 与 255 之间（实测 99），
        // 只统计 ROI 时应落在 ramp 中部。ROI 不参与统计时两个值会完全相同。
        using var img = new Mat(100, 200, MatType.CV_8UC1, Scalar.All(255));
        for (var x = 0; x < 100; x++)
        {
            using var column = new Mat(100, 1, MatType.CV_8UC1, Scalar.All(x));
            using var view = new Mat(img, new Rect(x, 0, 1, 100));
            column.CopyTo(view);
        }

        var withRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", $$"""{"mode":"otsu","roi":{{LeftHalfRoi}}}"""), CancellationToken.None);
        var withoutRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"otsu"}"""), CancellationToken.None);

        var roiThreshold = Number(withRoi, "threshold");
        var fullThreshold = Number(withoutRoi, "threshold");
        Assert.InRange(roiThreshold, 40, 60);
        Assert.InRange(fullThreshold, 95, 105);
        Assert.True(roiThreshold < fullThreshold - 30, $"ROI 阈值 {roiThreshold} 应明显低于整图阈值 {fullThreshold}。");
    }

    [Theory]
    [InlineData(CircularRoi, "auto")]
    [InlineData(PolygonalRoi, "12")]
    public async Task OtsuThreshold_IgnoresBrightPixelsOutsideNonRectangularRoi(string roi, string inputBitDepth)
    {
        var definition = Def("image.threshold", $$"""{"mode":"otsu","inputBitDepth":"{{inputBitDepth}}","roi":{{roi}}}""");
        // 12 位输入的 640/1280 经既有灰度映射后也为 40/80。
        var isUInt16 = inputBitDepth == "12";
        var scale = isUInt16 ? 16 : 1;
        using var image = new Mat(80, 80, isUInt16 ? MatType.CV_16UC1 : MatType.CV_8UC1, Scalar.All(40 * scale));
        Cv2.Rectangle(image, new Rect(40, 0, 40, 80), Scalar.All(80 * scale), -1);
        using var mask = definition.GetRoi()!.BuildMask(image.Size());
        using var brightBackground = new Mat(image.Size(), image.Type(), Scalar.All(240 * scale));
        image.CopyTo(brightBackground, mask);

        var original = await RunThreshold(image, definition);
        using var originalBinary = original.Outputs["image"].Require<VisionImage>("image");
        var changed = await RunThreshold(brightBackground, definition);
        using var changedBinary = changed.Outputs["image"].Require<VisionImage>("image");
        using var difference = new Mat();
        Cv2.Absdiff(originalBinary.Mat, changedBinary.Mat, difference);

        Assert.Equal(40d, Number(original, "threshold"));
        Assert.Equal(40d, Number(changed, "threshold"));
        Assert.Equal(0, Cv2.CountNonZero(difference));
        using var outsideMask = new Mat();
        Cv2.Compare(mask, Scalar.All(0), outsideMask, CmpTypes.EQ);
        using var outsideForeground = new Mat();
        Cv2.BitwiseAnd(changedBinary.Mat, outsideMask, outsideForeground);
        Assert.Equal(0, Cv2.CountNonZero(outsideForeground));
        Assert.InRange(Number(changed, "foregroundRatio"), 0.1, 0.9);
    }

    [Fact]
    public async Task ManualThreshold_OtsuSuggestionUsesTheActualRoiMask()
    {
        var definition = Def("image.threshold", $$"""{"mode":"manual","threshold":200,"roi":{{CircularRoi}}}""");
        using var image = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(240));
        using var dark = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(40));
        Cv2.Rectangle(dark, new Rect(40, 0, 40, 80), Scalar.All(80), -1);
        using var mask = definition.GetRoi()!.BuildMask(image.Size());
        dark.CopyTo(image, mask);

        var result = await RunThreshold(image, definition);
        using var output = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, Cv2.CountNonZero(output.Mat));
        Assert.Equal(200d, Number(result, "threshold"));
        Assert.Equal(1d, Number(result, "warningCount"));
        Assert.Equal(40d, Assert.IsType<double>(result.Summary["suggestedThreshold"]));
    }

    [Fact]
    public async Task AdaptiveThreshold_KeepsPixelValuesInsideRoi()
    {
        // 外扩半个窗口是为了让 ROI 内的邻域与整图计算一致：ROI 内像素值必须逐点相同。
        using var img = new Mat(120, 120, MatType.CV_8UC1);
        var rows = img.Rows;
        var cols = img.Cols;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            img.Set(y, x, (byte)((x * 3 + y * 5) % 256));

        var roi = new Rect(20, 20, 60, 60);
        var withRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"adaptive","blockSize":15,"c":3,"roi":{"type":"Rectangle","x":20,"y":20,"width":60,"height":60}}"""),
            CancellationToken.None);
        var withoutRoi = await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.threshold", """{"mode":"adaptive","blockSize":15,"c":3}"""), CancellationToken.None);

        using var masked = withRoi.Outputs["image"].Require<VisionImage>("image");
        using var full = withoutRoi.Outputs["image"].Require<VisionImage>("image");
        using var a = new Mat(masked.Mat, roi).Clone();
        using var b = new Mat(full.Mat, roi).Clone();
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
        // ROI 外必须是 0（掩膜语义不变）。
        using var outside = new Mat(masked.Mat, new Rect(100, 100, 20, 20)).Clone();
        Assert.Equal(0, Cv2.CountNonZero(outside));
    }

    [Fact]
    public async Task RoiCoveringNoPixels_FailsWithActionableMessage()
    {
        using var img = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(128));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.threshold", """{"mode":"otsu","roi":{"type":"Rectangle","x":500,"y":500,"width":10,"height":10}}"""),
                CancellationToken.None));
        Assert.Contains("covers no pixels", error.Message);
    }

    private static async Task<NodeExecutorResult> RunThreshold(Mat image, NodeDefinition definition)
    {
        using var input = VisionImage.Borrow(image);
        return await new ThresholdNode().ExecuteAsync(Ctx(new() { ["image"] = VisionValue.Image(input) }), definition, CancellationToken.None);
    }
}
