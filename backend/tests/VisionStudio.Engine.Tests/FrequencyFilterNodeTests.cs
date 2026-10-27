using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 频域与自定义卷积（image.convolve / image.fftFilter）行为契约：
/// 核语义（归一化/偏置/边缘核）、按周期挖带、方向窗口、输出模式与预算/参数校验。
/// </summary>
public sealed class FrequencyFilterNodeTests
{
    [Fact]
    public async Task Convolve_IdentityKernelReturnsTheSameImage()
    {
        using var source = Gradient();
        var result = await Run(new CustomConvolutionNode(), "image.convolve", source,
            """{"kernelJson":"[[0,0,0],[0,1,0],[0,0,0]]","normalize":false}""");
        using var output = Result(result);
        Assert.Equal(0, Cv2.Norm(source, output.Mat, NormTypes.L1));
        Assert.Equal(1d, result.Outputs["kernelSum"].Require<double>("kernelSum"), 6);
    }

    [Fact]
    public async Task Convolve_MeanKernelKeepsUniformAreaBrightness()
    {
        using var flat = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(120));
        var result = await Run(new CustomConvolutionNode(), "image.convolve", flat,
            """{"kernelJson":"[[1,1,1],[1,1,1],[1,1,1]]","normalize":true}""");
        using var output = Result(result);
        // 归一化后 3×3 均值核在均匀区应完全不变（含边界，因为反射填充）。
        Cv2.MinMaxLoc(output.Mat, out double min, out double max);
        Assert.Equal(120d, min);
        Assert.Equal(120d, max);
        Assert.Equal(9d, result.Outputs["kernelSum"].Require<double>("kernelSum"), 6);
    }

    [Fact]
    public async Task Convolve_EdgeKernelRespondsOnlyAtTheStep()
    {
        using var step = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0));
        step[new Rect(16, 0, 16, 32)].SetTo(Scalar.All(200));
        var result = await Run(new CustomConvolutionNode(), "image.convolve", step,
            """{"kernelJson":"[[0,1,0],[1,-4,1],[0,1,0]]","normalize":false,"delta":128}""");
        using var output = Result(result);
        // 平坦区被 delta 抬到 128（负旁瓣被截断），边缘列则明显偏离 128。
        Assert.InRange(output.Mat.At<byte>(5, 5), 126, 130);
        Assert.True(Math.Abs(output.Mat.At<byte>(5, 16) - 128) > 50,
            $"edge column should respond, got {output.Mat.At<byte>(5, 16)}");
    }

    [Fact]
    public async Task Convolve_RejectsZeroSumNormalizeAndBadGeometry()
    {
        using var source = Gradient();
        var zeroSum = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CustomConvolutionNode(), "image.convolve", source,
                """{"kernelJson":"[[0,1,0],[1,-4,1],[0,1,0]]","normalize":true}"""));
        Assert.Contains("zero", zeroSum.Message);

        var ragged = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CustomConvolutionNode(), "image.convolve", source,
                """{"kernelJson":"[[1,1],[1]]"}"""));
        Assert.Contains("rectangular", ragged.Message);

        var anchor = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CustomConvolutionNode(), "image.convolve", source,
                """{"kernelJson":"[[1]]","anchorX":5}"""));
        Assert.Contains("anchorX", anchor.Message);

        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CustomConvolutionNode(), "image.convolve",
                new Mat(8, 8, MatType.CV_8SC1, Scalar.All(1)), "{}"));
        Assert.Contains("8-bit", depth.Message);
    }

    [Fact]
    public async Task FftFilter_BandStopRemovesAPeriodicTexture()
    {
        using var textured = PeriodicStripes(periodPx: 4, amplitude: 40, horizontalVariation: true);
        Cv2.MeanStdDev(textured, out var meanIn, out var stdIn);
        Assert.InRange(stdIn[0], 25, 31);

        var result = await Run(new FftFilterNode(), "image.fftFilter", textured,
            """{"filter":"bandstop","wavelengthMinPx":3.5,"wavelengthMaxPx":4.5,"keepDc":true}""");
        using var filtered = Result(result);
        Cv2.MeanStdDev(filtered.Mat, out var meanOut, out var stdOut);
        Assert.True(stdOut[0] < 6, $"band stop should flatten the texture, std={stdOut[0]:0.###}");
        Assert.InRange(meanOut[0], meanIn[0] - 2, meanIn[0] + 2);
        Assert.InRange(result.Outputs["maskPassRatio"].Require<double>("maskPassRatio"), 0.01, 0.99);
    }

    [Fact]
    public async Task FftFilter_RemovedModeIsolatesTheFilteredPattern()
    {
        using var textured = PeriodicStripes(periodPx: 4, amplitude: 40, horizontalVariation: true);
        var result = await Run(new FftFilterNode(), "image.fftFilter", textured,
            """{"filter":"bandstop","wavelengthMinPx":3.5,"wavelengthMaxPx":4.5,"outputMode":"removed"}""");
        using var removed = Result(result);
        Cv2.MeanStdDev(removed.Mat, out var mean, out var std);
        // 被滤掉的成分是零均值信号：8 位输出会把负半周截掉，因此节点把原图均值加回来，
        // 残差应当稳定在 128 附近并保留原始条纹幅度。
        Assert.InRange(mean[0], 126, 130);
        Assert.True(std[0] > 20, $"removed component should keep the texture, std={std[0]:0.###}");
        Assert.Equal(128, removed.Mat.At<byte>(0, 0));
        Assert.InRange(removed.Mat.At<byte>(0, 1), 160, 176);
    }

    [Fact]
    public async Task FftFilter_DirectionWindowOnlyRemovesTheMatchingOrientation()
    {
        // 水平条纹 = 能量沿 Y 方向的频率（方向 90°）。
        using var stripes = PeriodicStripes(periodPx: 4, amplitude: 40, horizontalVariation: false);

        var targeted = await Run(new FftFilterNode(), "image.fftFilter", stripes,
            """{"filter":"bandstop","wavelengthMinPx":3.5,"wavelengthMaxPx":4.5,"directionDeg":90,"directionWidthDeg":20}""");
        using var targetedOut = Result(targeted);
        Cv2.MeanStdDev(targetedOut.Mat, out _, out var targetedStd);

        var missed = await Run(new FftFilterNode(), "image.fftFilter", stripes,
            """{"filter":"bandstop","wavelengthMinPx":3.5,"wavelengthMaxPx":4.5,"directionDeg":0,"directionWidthDeg":20}""");
        using var missedOut = Result(missed);
        Cv2.MeanStdDev(missedOut.Mat, out _, out var missedStd);

        Assert.True(targetedStd[0] < 6, $"targeted band should flatten, std={targetedStd[0]:0.###}");
        Assert.True(missedStd[0] > 20, $"orthogonal band must leave the stripes, std={missedStd[0]:0.###}");
        Assert.InRange(missed.Outputs["maskPassRatio"].Require<double>("maskPassRatio"), 0.9, 1.0);
    }

    [Fact]
    public async Task FftFilter_LowPassKeepsCoarseStructureAndPreservesDepth()
    {
        using var textured = PeriodicStripes(periodPx: 4, amplitude: 40, horizontalVariation: true);
        var result = await Run(new FftFilterNode(), "image.fftFilter", textured,
            """{"filter":"lowpass","wavelengthMinPx":16}""");
        using var output = Result(result);
        Assert.Equal(MatType.CV_8U, output.Mat.Depth());
        Cv2.MeanStdDev(output.Mat, out var mean, out var std);
        Assert.True(std[0] < 6, $"low pass should drop the 4 px texture, std={std[0]:0.###}");
        Assert.InRange(mean[0], 120, 136);
    }

    [Fact]
    public async Task FftFilter_RejectsInvertedBandBudgetAndDepth()
    {
        using var textured = PeriodicStripes(periodPx: 4, amplitude: 40, horizontalVariation: true);
        var inverted = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FftFilterNode(), "image.fftFilter", textured,
                """{"filter":"bandstop","wavelengthMinPx":10,"wavelengthMaxPx":3}"""));
        Assert.Contains("wavelengthMinPx", inverted.Message);

        var budget = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FftFilterNode(), "image.fftFilter", textured, """{"maxPixels":1000}"""));
        Assert.Contains("maxPixels", budget.Message);

        using var floatImage = new Mat(16, 16, MatType.CV_64FC1, Scalar.All(1));
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FftFilterNode(), "image.fftFilter", floatImage, "{}"));
        Assert.Contains("float32", depth.Message);
    }

    // ---------------------------------------------------------------- 辅助

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, Mat image, string json)
    {
        var node = new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(image.Clone())) };
        try
        {
            return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }
    }

    private static VisionImage Result(NodeExecutorResult result)
        => result.Outputs["image"].Require<VisionImage>("image");

    /// <summary>平滑灰度渐变，用于验证恒等核/归一化不改变像素。</summary>
    private static Mat Gradient()
    {
        var mat = new Mat(32, 32, MatType.CV_8UC1);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                mat.Set(y, x, (byte)((x * 4 + y * 2) % 256));
        return mat;
    }

    /// <summary>128×128 周期纹理：均值 128、周期 4 px 的正弦条纹（沿 X 或沿 Y）。</summary>
    private static Mat PeriodicStripes(int periodPx, double amplitude, bool horizontalVariation)
    {
        var mat = new Mat(128, 128, MatType.CV_8UC1);
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var coordinate = horizontalVariation ? x : y;
                var value = 128 + amplitude * Math.Sin(2 * Math.PI * coordinate / periodPx);
                mat.Set(y, x, (byte)Math.Clamp(Math.Round(value), 0, 255));
            }
        }
        return mat;
    }
}
