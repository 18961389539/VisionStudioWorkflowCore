using System;

namespace ImageViewer.Core.Spc;

/// <summary>
/// 过程能力指数：Cp/Cpk（组内 σ）与 Pp/Ppk（整体 σ）。
/// Chinese: 组内 σ 由移动极差均值估算（个体值序列的 n=2 移动极差除以 d2=1.128），
/// 衡量"过程本身能有多稳"；整体 σ 是样本标准差（n-1），包含漂移与批次间差异。
/// Cp/Pp 需要双侧公差才能定义（分母是公差带宽度）；Cpk/Ppk 只需一侧即可定义，
/// 两侧都有时取较小的一侧——这正是"离哪条限更近"的含义。σ 为零时指数无定义，返回 null 而不是无穷大。
/// English: Within σ from the average moving range (d2 = 1.128); overall σ is the sample standard deviation.
/// Cp/Pp require both limits; Cpk/Ppk work with one side and take the nearer limit. A zero σ yields null, not infinity.
/// </summary>
public sealed record ProcessCapability
{
    /// <summary>个体值移动极差（n=2）的 d2 常数。</summary>
    private const double MovingRangeD2 = 1.128;

    /// <summary>没有样本时的占位值。</summary>
    public static ProcessCapability Empty { get; } = new();

    /// <summary>参与计算的有限样本数。</summary>
    public int SampleCount { get; init; }

    /// <summary>样本均值。</summary>
    public double Mean { get; init; }

    /// <summary>组内标准差（移动极差估计）。</summary>
    public double SigmaWithin { get; init; }

    /// <summary>整体标准差（样本标准差）。</summary>
    public double SigmaOverall { get; init; }

    /// <summary>下规范限；null 表示未设置。</summary>
    public double? LowerSpecificationLimit { get; init; }

    /// <summary>上规范限；null 表示未设置。</summary>
    public double? UpperSpecificationLimit { get; init; }

    /// <summary>潜在过程能力指数 Cp = (USL-LSL)/(6σwithin)；缺一侧公差时为 null。</summary>
    public double? Cp { get; init; }

    /// <summary>实际过程能力指数 Cpk = min(USL-μ, μ-LSL)/(3σwithin)；缺公差时为 null。</summary>
    public double? Cpk { get; init; }

    /// <summary>潜在过程性能指数 Pp = (USL-LSL)/(6σoverall)；缺一侧公差时为 null。</summary>
    public double? Pp { get; init; }

    /// <summary>实际过程性能指数 Ppk = min(USL-μ, μ-LSL)/(3σoverall)；缺公差时为 null。</summary>
    public double? Ppk { get; init; }

    /// <summary>是否配置了任意一侧规范限。</summary>
    public bool HasSpecificationLimits => LowerSpecificationLimit.HasValue || UpperSpecificationLimit.HasValue;

    /// <summary>
    /// 由样本计算过程能力。
    /// Chinese: 非有限值（NaN/∞）被跳过而不是当成 0；至少需要两个有限样本，否则离散度无从谈起。
    /// 规范限必须有限，且同时给出时 LSL 必须严格小于 USL。
    /// English: Non-finite samples are skipped rather than coerced; at least two finite samples are required.
    /// </summary>
    /// <param name="samples">按时间/顺序排列的测量值。</param>
    /// <param name="lowerSpecificationLimit">下规范限（可选）。</param>
    /// <param name="upperSpecificationLimit">上规范限（可选）。</param>
    public static ProcessCapability Calculate(
        IReadOnlyList<double> samples,
        double? lowerSpecificationLimit = null,
        double? upperSpecificationLimit = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ValidateLimit(lowerSpecificationLimit, nameof(lowerSpecificationLimit));
        ValidateLimit(upperSpecificationLimit, nameof(upperSpecificationLimit));
        if (lowerSpecificationLimit is { } lower && upperSpecificationLimit is { } upper && lower >= upper)
        {
            throw new ArgumentException("The lower specification limit must be below the upper specification limit.", nameof(lowerSpecificationLimit));
        }

        int count = 0;
        double sum = 0;
        double movingRangeSum = 0;
        double? previous = null;
        foreach (double sample in samples)
        {
            if (!double.IsFinite(sample))
            {
                continue;
            }

            sum += sample;
            if (previous.HasValue)
            {
                movingRangeSum += Math.Abs(sample - previous.Value);
            }

            previous = sample;
            count++;
        }

        if (count < 2)
        {
            throw new ArgumentException("At least two finite samples are required for process capability.", nameof(samples));
        }

        double mean = sum / count;
        double squaredDeviationSum = 0;
        foreach (double sample in samples)
        {
            if (!double.IsFinite(sample))
            {
                continue;
            }

            double deviation = sample - mean;
            squaredDeviationSum += deviation * deviation;
        }

        double sigmaWithin = movingRangeSum / (count - 1) / MovingRangeD2;
        double sigmaOverall = Math.Sqrt(squaredDeviationSum / (count - 1));
        return new ProcessCapability
        {
            SampleCount = count,
            Mean = mean,
            SigmaWithin = sigmaWithin,
            SigmaOverall = sigmaOverall,
            LowerSpecificationLimit = lowerSpecificationLimit,
            UpperSpecificationLimit = upperSpecificationLimit,
            Cp = PotentialIndex(lowerSpecificationLimit, upperSpecificationLimit, sigmaWithin),
            Cpk = ActualIndex(mean, lowerSpecificationLimit, upperSpecificationLimit, sigmaWithin),
            Pp = PotentialIndex(lowerSpecificationLimit, upperSpecificationLimit, sigmaOverall),
            Ppk = ActualIndex(mean, lowerSpecificationLimit, upperSpecificationLimit, sigmaOverall)
        };
    }

    private static double? PotentialIndex(double? lower, double? upper, double sigma)
    {
        if (sigma <= 0 || !lower.HasValue || !upper.HasValue)
        {
            return null;
        }

        return (upper.Value - lower.Value) / (6 * sigma);
    }

    private static double? ActualIndex(double mean, double? lower, double? upper, double sigma)
    {
        if (sigma <= 0 || (!lower.HasValue && !upper.HasValue))
        {
            return null;
        }

        double? upperDistance = upper.HasValue ? upper.Value - mean : null;
        double? lowerDistance = lower.HasValue ? mean - lower.Value : null;
        // 两侧都存在时取较近的一侧；只看距离大小，中心偏出公差带时指数自然为负。
        double distance = upperDistance.HasValue && lowerDistance.HasValue
            ? Math.Min(upperDistance.Value, lowerDistance.Value)
            : upperDistance ?? lowerDistance!.Value;
        return distance / (3 * sigma);
    }

    private static void ValidateLimit(double? limit, string parameterName)
    {
        if (limit is { } value && !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Specification limits must be finite.");
        }
    }
}
