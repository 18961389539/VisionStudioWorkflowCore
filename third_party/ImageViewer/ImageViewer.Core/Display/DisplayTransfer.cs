using System;

namespace ImageViewer.Core.Display;

/// <summary>
/// 由 <see cref="DisplayAdjustment"/> 生成显示查找表，以及从直方图求自动窗宽窗位。
/// Chinese: 这张表是 CPU 路径与 Shader 路径的唯一取值来源——两条路径都不再自己算传递函数，
/// 因此"没有可用着色器"的机器上看到的结果与 GPU 机器一致。表项 i 对应输入 i/(n-1)，
/// 即 8 位显示码的取值方式（0..255）。
/// English: The generated table is the single source of truth shared by the CPU tile path and the shader.
/// </summary>
public static class DisplayTransfer
{
    /// <summary>默认表项数：与 8 位显示码一一对应。</summary>
    public const int DefaultLutEntryCount = 256;

    /// <summary>
    /// 生成归一化传递曲线（每项取值 [0,1]）。
    /// </summary>
    /// <param name="adjustment">显示调节参数。</param>
    /// <param name="entryCount">表项数，至少 2。</param>
    /// <returns>长度等于 <paramref name="entryCount"/> 的归一化输出曲线。</returns>
    public static double[] CreateNormalizedLut(DisplayAdjustment adjustment, int entryCount = DefaultLutEntryCount)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        if (entryCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount), entryCount, "A display LUT needs at least two entries.");
        }

        adjustment.Validate();
        double[] lut = new double[entryCount];
        for (int index = 0; index < entryCount; index++)
        {
            lut[index] = adjustment.Evaluate(index / (double)(entryCount - 1));
        }

        return lut;
    }

    /// <summary>
    /// 按直方图分位数求窗宽窗位（亮度/对比度/伽马保持默认）。
    /// Chinese: 默认取 2%–98% 分位：把绝大多数像素铺满显示范围，同时让两端离群值不至于把中间调压成一片平色。
    /// 分位数重合（几乎无对比度的图）时窗宽退化为一个显示码，仍然给出可读结果而不是拒绝显示。
    /// English: Percentile-based window/level; degenerate percentile pairs fall back to a one-code window.
    /// </summary>
    /// <param name="histogram">显示码直方图（每项为该码值的像素数）。</param>
    /// <param name="lowPercentile">下分位数，取值 [0,1)。</param>
    /// <param name="highPercentile">上分位数，取值 (lowPercentile,1]。</param>
    /// <returns>只带窗宽窗位的调节参数；直方图为空时返回 <see cref="DisplayAdjustment.Identity"/>。</returns>
    public static DisplayAdjustment CreateAutoWindowLevel(
        ReadOnlySpan<int> histogram,
        double lowPercentile = 0.02,
        double highPercentile = 0.98)
    {
        if (histogram.Length < 2)
        {
            throw new ArgumentException("At least two histogram bins are required.", nameof(histogram));
        }

        if (!double.IsFinite(lowPercentile) || lowPercentile < 0 || lowPercentile >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lowPercentile), lowPercentile, "Low percentile must be between 0 and 1.");
        }

        if (!double.IsFinite(highPercentile) || highPercentile <= lowPercentile || highPercentile > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(highPercentile), highPercentile, "High percentile must be greater than the low percentile and no more than 1.");
        }

        long total = 0;
        foreach (int count in histogram)
        {
            if (count < 0)
            {
                throw new ArgumentException("Histogram counts must not be negative.", nameof(histogram));
            }

            total += count;
        }

        if (total == 0)
        {
            return DisplayAdjustment.Identity;
        }

        double fullScale = histogram.Length - 1;
        int low = FindPercentile(histogram, total, lowPercentile);
        int high = FindPercentile(histogram, total, highPercentile);
        double width = Math.Max(high - low, 1) / fullScale;
        double level = (low + high) / 2.0 / fullScale;
        return new DisplayAdjustment
        {
            WindowWidth = Math.Clamp(width, double.Epsilon, 1.0),
            WindowLevel = Math.Clamp(level, 0.0, 1.0)
        };
    }

    private static int FindPercentile(ReadOnlySpan<int> histogram, long total, double percentile)
    {
        long target = Math.Max(1, (long)(total * percentile));
        long running = 0;
        for (int index = 0; index < histogram.Length; index++)
        {
            running += histogram[index];
            if (running >= target)
            {
                return index;
            }
        }

        return histogram.Length - 1;
    }
}
