using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 图像预处理补全批（node-gap-analysis.md 第九节）的行为覆盖。
///
/// 用例都用**可计算的合成图**（水平渐变、同心环、阶跃噪声、常量通道），
/// 断言落在解析值附近：CLAHE 必须真的提升暗侧局部对比度、极坐标展开的亮环必须落在预期行、
/// 白平衡必须把三个通道的均值拉到同一水平、位深映射必须精确命中。
/// </summary>
public sealed class PreprocessGapNodeTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    /// <summary>左暗右亮的水平渐变（0..255）。</summary>
    private static Mat Gradient(int width = 96, int height = 32)
    {
        var mat = new Mat(height, width, MatType.CV_8UC1);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                mat.Set(y, x, (byte)(255 * x / (width - 1)));
        return mat;
    }

    /// <summary>同心环：r∈[inner,outer] 为 255，其余 0。</summary>
    private static Mat Ring(double cx, double cy, double inner, double outer, int size = 120)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (r >= inner && r <= outer) mat.Set(y, x, (byte)255);
            }
        }
        return mat;
    }

    /// <summary>左 50 右 200 的阶跃图，叠加可复现的均匀噪声。</summary>
    private static Mat NoisyStep(int width = 64, int height = 64, int noise = 20)
    {
        var mat = new Mat(height, width, MatType.CV_8UC1);
        var random = new Random(1234);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var baseValue = x < width / 2 ? 50 : 200;
                var value = Math.Clamp(baseValue + random.Next(-noise, noise + 1), 0, 255);
                mat.Set(y, x, (byte)value);
            }
        }
        return mat;
    }

    private static double RegionStdDev(Mat mat, int fromX, int toX)
    {
        using var roi = new Mat(mat, new Rect(fromX, 0, toX - fromX, mat.Height));
        Cv2.MeanStdDev(roi, out _, out var stddev);
        return stddev.Val0;
    }

    // ── CLAHE ───────────────────────────────────────────────────────
    [Fact]
    public async Task Clahe_RaisesLocalContrast_InTheDarkHalf()
    {
        using var source = Gradient();
        var before = RegionStdDev(source, 0, 24);

        using var vision = VisionImage.Own(source.Clone());
        var result = await new ClaheNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.clahe", """{"clipLimit":4,"tilesX":8,"tilesY":8,"colorMode":"luminance"}"""), CancellationToken.None);

        var image = result.Outputs["image"].Require<VisionImage>("image");
        var after = RegionStdDev(image.Mat, 0, 24);
        Assert.True(after > before, $"CLAHE must raise local contrast in the dark half (before {before:0.##}, after {after:0.##})");
        Assert.Equal(1L, result.Outputs["channels"].Require<long>("channels"));

        // 灰阶输出：彩色输入 + colorMode=gray 必须真的塌成单通道
        using var color = new Mat(32, 32, MatType.CV_8UC3, Scalar.All(90));
        using var colorVision = VisionImage.Own(color.Clone());
        var gray = await new ClaheNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(colorVision) }),
            Def("image.clahe", """{"colorMode":"gray"}"""), CancellationToken.None);
        Assert.Equal(1L, gray.Outputs["channels"].Require<long>("channels"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ClaheNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(new Mat(8, 8, MatType.CV_16UC1, Scalar.All(1000)))) }),
                Def("image.clahe"), CancellationToken.None));
    }

    // ── 极坐标展开 ──────────────────────────────────────────────────
    [Fact]
    public async Task PolarUnwrap_PutsTheRingOnTheExpectedRows()
    {
        using var source = Ring(60, 60, 20, 30);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new PolarUnwrapNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.polarUnwrap", """{"centerX":60,"centerY":60,"innerRadius":10,"outerRadius":50,"radialSamples":40,"angularSamples":36}"""),
            CancellationToken.None);

        Assert.Equal(36L, result.Outputs["width"].Require<long>("width"));
        Assert.Equal(40L, result.Outputs["height"].Require<long>("height"));
        var strip = result.Outputs["image"].Require<VisionImage>("image").Mat;
        Assert.Equal(40, strip.Rows);

        // 行 y ↔ 半径 10 + (y + 0.5)；r∈[20,30] → y∈[9.5,19.5]
        var inside = Cv2.Mean(new Mat(strip, new Rect(0, 11, strip.Cols, 6))).Val0;
        var outside = Cv2.Mean(new Mat(strip, new Rect(0, 2, strip.Cols, 4))).Val0;
        Assert.True(inside > 200, $"ring rows must be bright, got {inside:0.#}");
        Assert.True(outside < 60, $"rows outside the ring must stay dark, got {outside:0.#}");
    }

    [Fact]
    public async Task PolarUnwrap_UsesCircleInput_AndRejectsBadBand()
    {
        using var source = Ring(40, 40, 10, 20);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new PolarUnwrapNode().ExecuteAsync(
            Ctx(new()
            {
                ["image"] = VisionValue.Image(vision),
                ["circle"] = VisionValue.Circle(new VisionCircle(40, 40, 30))
            }),
            Def("image.polarUnwrap", """{"radialSamples":20,"angularSamples":24}"""), CancellationToken.None);
        Assert.Equal(30d, result.Outputs["outerRadius"].Require<double>("outerRadius"), 3);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PolarUnwrapNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(source.Clone())) }),
                Def("image.polarUnwrap", """{"centerX":40,"centerY":40,"innerRadius":20,"outerRadius":10}"""), CancellationToken.None));
    }

    // ── 双边滤波 ────────────────────────────────────────────────────
    [Fact]
    public async Task Bilateral_DenoisesFlatAreas_AndKeepsTheStepEdge()
    {
        using var source = NoisyStep();
        var noiseBefore = RegionStdDev(source, 4, 24);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new BilateralFilterNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.bilateral", """{"diameter":9,"sigmaColor":15,"sigmaSpace":9}"""), CancellationToken.None);

        var filtered = result.Outputs["image"].Require<VisionImage>("image").Mat;
        var noiseAfter = RegionStdDev(filtered, 4, 24);
        Assert.True(noiseAfter < noiseBefore * .75, $"bilateral must suppress flat-area noise ({noiseBefore:0.##} -> {noiseAfter:0.##})");

        // 阶跃必须还在：两侧均值差不能塌
        var left = Cv2.Mean(new Mat(filtered, new Rect(4, 4, 20, 56))).Val0;
        var right = Cv2.Mean(new Mat(filtered, new Rect(40, 4, 20, 56))).Val0;
        Assert.True(right - left > 120, $"step edge must survive (left {left:0.#}, right {right:0.#})");
    }

    // ── 白平衡 ──────────────────────────────────────────────────────
    private static Mat FlatBgr(byte b, byte g, byte r, int size = 32)
        => new(size, size, MatType.CV_8UC3, new Scalar(b, g, r));

    [Fact]
    public async Task WhiteBalance_GrayWorld_EqualizesChannelMeans()
    {
        using var source = FlatBgr(100, 150, 200);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new WhiteBalanceNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.whiteBalance", """{"mode":"grayWorld","preserveBrightness":true}"""), CancellationToken.None);

        Assert.Equal(1.5, result.Outputs["gainB"].Require<double>("gainB"), 2);
        Assert.Equal(0.75, result.Outputs["gainR"].Require<double>("gainR"), 2);

        var balanced = result.Outputs["image"].Require<VisionImage>("image").Mat;
        Cv2.Split(balanced, out var channels);
        try
        {
            foreach (var channel in channels)
                Assert.InRange(Cv2.Mean(channel).Val0, 148, 152);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    [Fact]
    public async Task WhiteBalance_ManualGains_AndRejectsGrayInput()
    {
        using var source = FlatBgr(100, 100, 100);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new WhiteBalanceNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.whiteBalance", """{"mode":"manual","manualB":2,"manualG":1.5,"manualR":1,"preserveBrightness":false}"""),
            CancellationToken.None);
        var balanced = result.Outputs["image"].Require<VisionImage>("image").Mat;
        Cv2.Split(balanced, out var channels);
        try
        {
            Assert.InRange(Cv2.Mean(channels[0]).Val0, 198, 202);
            Assert.InRange(Cv2.Mean(channels[1]).Val0, 148, 152);
            Assert.InRange(Cv2.Mean(channels[2]).Val0, 98, 102);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }

        using var gray = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(100));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new WhiteBalanceNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(gray.Clone())) }),
                Def("image.whiteBalance"), CancellationToken.None));
    }

    // ── 质量指标 ────────────────────────────────────────────────────
    [Fact]
    public async Task QualityMetrics_ReadsContrastSharpnessAndSaturation()
    {
        // 渐变图：对比度必须接近满量程
        using var gradient = Gradient(96, 48);
        var gradientResult = await new QualityMetricsNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(gradient.Clone())) }),
            Def("image.qualityMetrics", """{"saturationThreshold":254,"darkThreshold":1}"""), CancellationToken.None);
        Assert.True(gradientResult.Outputs["contrast"].Require<double>("contrast") > 200);
        Assert.Equal(96L * 48, gradientResult.Outputs["pixelCount"].Require<long>("pixelCount"));

        // 平场 + 右下 24×24 白块：饱和占比 ≈ 576 / 4608 = 12.5%
        using var masked = new Mat(48, 96, MatType.CV_8UC1, Scalar.All(100));
        Cv2.Rectangle(masked, new Rect(72, 24, 24, 24), Scalar.All(255), -1);
        var maskedResult = await new QualityMetricsNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(masked.Clone())) }),
            Def("image.qualityMetrics", """{"saturationThreshold":254,"darkThreshold":1}"""), CancellationToken.None);
        Assert.InRange(maskedResult.Outputs["saturationRatio"].Require<double>("saturationRatio"), .11, .14);
        Assert.Equal(0d, maskedResult.Outputs["darkRatio"].Require<double>("darkRatio"), 6);

        // 噪声图必须报出更大的 noiseSigma
        using var flat = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(120));
        using var noisy = NoisyStep(48, 48, 6);
        var flatResult = await new QualityMetricsNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(flat.Clone())) }),
            Def("image.qualityMetrics"), CancellationToken.None);
        var noisyResult = await new QualityMetricsNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(noisy.Clone())) }),
            Def("image.qualityMetrics"), CancellationToken.None);
        Assert.True(noisyResult.Outputs["noiseSigma"].Require<double>("noiseSigma")
            > flatResult.Outputs["noiseSigma"].Require<double>("noiseSigma") + .5);
    }

    // ── 位深映射 ────────────────────────────────────────────────────
    [Fact]
    public async Task DepthScale_MapsFullRange_AndHonoursInputBits()
    {
        using var source = new Mat(8, 8, MatType.CV_16UC1, Scalar.All(32768));
        using var vision = VisionImage.Own(source.Clone());
        var to8 = await new DepthScaleNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.depthScale", """{"outputBits":"8"}"""), CancellationToken.None);
        Assert.InRange(to8.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0), 126, 129);
        Assert.Equal(8L, to8.Outputs["outputBits"].Require<long>("outputBits"));

        // 12 位数据装在 16 位容器里：显式 inputBits=12 才能整幅拉满
        using var twelve = new Mat(8, 8, MatType.CV_16UC1, Scalar.All(4095));
        using var twelveVision = VisionImage.Own(twelve.Clone());
        var scaled = await new DepthScaleNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(twelveVision) }),
            Def("image.depthScale", """{"inputBits":12,"outputBits":"8"}"""), CancellationToken.None);
        Assert.Equal(255, scaled.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0));
        Assert.Equal(255d / 4095, scaled.Outputs["scaleUsed"].Require<double>("scaleUsed"), 4);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new DepthScaleNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(new Mat(8, 8, MatType.CV_32FC1, Scalar.All(1)))) }),
                Def("image.depthScale"), CancellationToken.None));
    }

    // ── 通道混合 ────────────────────────────────────────────────────
    [Fact]
    public async Task ChannelMix_UsesWeights_AndRejectsZeroSum()
    {
        using var source = FlatBgr(10, 20, 30);
        using var vision = VisionImage.Own(source.Clone());
        var result = await new ChannelMixNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(vision) }),
            Def("image.channelMix", """{"weight1":0.114,"weight2":0.587,"weight3":0.299,"normalize":true}"""), CancellationToken.None);
        Assert.InRange(result.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0), 21, 22);
        Assert.Equal(3L, result.Outputs["channels"].Require<long>("channels"));

        // 红减蓝：负权是允许的，只要总和不被 normalize 归零
        using var contrast = FlatBgr(30, 30, 200);
        using var contrastVision = VisionImage.Own(contrast.Clone());
        var redMinusBlue = await new ChannelMixNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(contrastVision) }),
            Def("image.channelMix", """{"weight1":-1,"weight2":0,"weight3":1,"normalize":false}"""), CancellationToken.None);
        Assert.Equal(170, redMinusBlue.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ChannelMixNode().ExecuteAsync(
                Ctx(new() { ["image"] = VisionValue.Image(VisionImage.Own(contrast.Clone())) }),
                Def("image.channelMix", """{"weight1":-1,"weight2":0,"weight3":1,"normalize":true}"""), CancellationToken.None));
    }
}
