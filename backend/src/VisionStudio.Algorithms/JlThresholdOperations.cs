using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

public sealed class JlThresholdArgs
{
    public required JlImage Image { get; init; }
    public required JlImage Secondary { get; init; }
    public double Size { get; init; } = 3;
    public double Size2 { get; init; } = 3;
    public double Mult { get; init; }
    public double Offset { get; init; }
    public double Threshold { get; init; } = 128;
    public int Count { get; init; } = 5;
    public double Min { get; init; }
    public double Max { get; init; } = 128;
    public string Mode { get; init; } = string.Empty;
    public string Mode2 { get; init; } = string.Empty;
}

public sealed record JlThresholdOperator(string Key, string Label, string Note, Func<JlThresholdArgs, JlRegion> Run, bool NeedsSecond = false);

/// <summary>
/// JLVision 阈值/分割算子表（<see cref="JlImage"/> → <see cref="JlRegion"/>）：
/// 全局、自动、双阈值、快速、滞后、局部、方差、动态阈值与区域生长，输出平台区域。
/// </summary>
public static class JlThresholdOperations
{
    private static readonly Dictionary<string, JlThresholdOperator> Table = new(StringComparer.Ordinal)
    {
        ["threshold"] = new("threshold", "Threshold (min/max)", "min, max", a => a.Image.Threshold(a.Min, a.Max)),
        ["autoThreshold"] = new("autoThreshold", "Automatic threshold", "size=sigma", a =>
            a.Image.AutoThreshold(a.Size <= 0 ? 1 : a.Size)),
        ["binThreshold"] = new("binThreshold", "Binary threshold (histogram)", "无参数", a => a.Image.BinThreshold()),
        ["dualThreshold"] = new("dualThreshold", "Dual threshold", "min/max 自动按大小分配 MinGray/Threshold, count=minSize", a =>
            a.Image.DualThreshold(Math.Max(1, a.Count), Math.Max(a.Min, a.Max), Math.Min(a.Min, a.Max))),
        ["fastThreshold"] = new("fastThreshold", "Fast threshold", "min, max, count=minSize", a =>
            a.Image.FastThreshold(a.Min, a.Max, Math.Max(1, a.Count))),
        ["hysteresisThreshold"] = new("hysteresisThreshold", "Hysteresis threshold", "min=low, max=high, count=maxLength", a =>
            a.Image.HysteresisThreshold((int)a.Min, (int)a.Max, Math.Max(1, a.Count))),
        ["localThreshold"] = new("localThreshold", "Local threshold", "mode=方法（默认 adapted_std_deviation）, mode2=dark|light, size=mask_size", a =>
            a.Image.LocalThreshold(string.IsNullOrWhiteSpace(a.Mode) ? "adapted_std_deviation" : a.Mode,
                string.IsNullOrWhiteSpace(a.Mode2) ? "dark" : a.Mode2, "mask_size", Math.Max(1, (int)a.Size))),
        ["varThreshold"] = new("varThreshold", "Variance threshold", "size/size2=掩膜, mult=stdDevScale, threshold=absThreshold, mode2=lightDark", a =>
            a.Image.VarThreshold(Math.Max(1, (int)a.Size), Math.Max(1, (int)a.Size2), a.Mult <= 0 ? 1 : a.Mult,
                a.Threshold, string.IsNullOrWhiteSpace(a.Mode2) ? "dark" : a.Mode2)),
        ["dynThreshold"] = new("dynThreshold", "Dynamic threshold", "第二输入图=阈值图, mult=offset, mode2=lightDark", a =>
            a.Image.DynThreshold(a.Secondary, a.Mult, string.IsNullOrWhiteSpace(a.Mode2) ? "light" : a.Mode2), NeedsSecond: true),
        ["localMax"] = new("localMax", "Local maxima", "无参数", a => a.Image.LocalMax()),
        ["localMin"] = new("localMin", "Local minima", "无参数", a => a.Image.LocalMin()),
        ["regionGrowing"] = new("regionGrowing", "Region growing", "count=栅格尺寸, mult=tolerance, size=minSize", a =>
            a.Image.Regiongrowing(Math.Max(1, a.Count), Math.Max(1, a.Count), a.Mult <= 0 ? 5 : a.Mult, Math.Max(1, (int)a.Size)))
    };

    public static IReadOnlyDictionary<string, JlThresholdOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlThresholdOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision threshold operator '{key}'. Known operators: {string.Join(", ", Keys)}.");
}
