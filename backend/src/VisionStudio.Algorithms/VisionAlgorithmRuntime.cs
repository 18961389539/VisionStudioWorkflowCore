using System.Runtime.InteropServices;
using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

public sealed record VisionAlgorithmDiagnostics(string TraditionalBackend, string NativeVersion, string InferenceBackend, string InferenceVersion);

/// <summary>
/// 一条类型规则：如何为某个节点类型推导后端标签。
/// </summary>
/// <param name="Pattern">以 <c>'.'</c> 结尾表示前缀匹配，否则精确匹配（均忽略大小写）。</param>
/// <param name="DefaultBackend">
/// 执行器未声明 <c>algorithmBackend</c> 时使用的缺省值；null 表示本类型不由目录推导
/// （例如 <c>image.acquire</c> 是采集设备节点，没有算法后端）。
/// </param>
/// <param name="DomainKey">需要补写的域标签键；null 表示该类型没有域标签。</param>
/// <param name="DomainValue">
/// 域标签字面量；null 且 <paramref name="DomainFollowsBackend"/> 为 false 时取
/// <paramref name="DefaultBackend"/>。
/// </param>
/// <param name="DomainFollowsBackend">
/// 域标签是否跟随"本次实际使用的算法后端"。仅 <c>match.*</c> 需要：同一匹配节点可能是
/// managed chamfer / opencv feature / jlvision，报告不得把 managed 写成 jlvision。
/// </param>
internal sealed record AlgorithmBackendRule(
    string Pattern,
    string? DefaultBackend = null,
    string? DomainKey = null,
    string? DomainValue = null,
    bool DomainFollowsBackend = false)
{
    public bool Matches(string type) => Pattern.EndsWith('.')
        ? type.StartsWith(Pattern, StringComparison.OrdinalIgnoreCase)
        : type.Equals(Pattern, StringComparison.OrdinalIgnoreCase);
}

public static class VisionAlgorithmRuntime
{
    private static readonly Lazy<VisionAlgorithmDiagnostics> Diagnostics = new(() =>
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("JLVision requires Windows and an x64 process.");
        try
        {
            using var version = JLVisionExtendedOperators.Tuples(317, "get_system", 1, new JlTuple("version"))[0];
            var inferenceVersion = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
            return new("JLVision", version[0].S, "ONNX Runtime", inferenceVersion);
        }
        catch (Exception ex) { throw new InvalidOperationException($"JLVision runtime could not initialize: {ex.Message}", ex); }
    });
    public static VisionAlgorithmDiagnostics Initialize() => Diagnostics.Value;
    public static void ClearModelCache() => JLVisionModelCache.Clear();

    /// <summary>
    /// 类型 → 后端标签的唯一映射表（2026-11-25 合并）。
    ///
    /// 此前同一张表写了两份：<c>BackendFor</c>（algorithmBackend 缺省值）与
    /// <c>EnsureDomainBackendLabels</c>（域标签）。两份逐条重复且靠人工同步——
    /// <c>recognition.barcode → zxing</c>、<c>calibration.handEye3D → opencv</c> 各出现两次，
    /// 改一处忘另一处不会报错，只会让报告里的后端标签与实际执行器悄悄不一致。
    ///
    /// 现在一份表同时描述两件事：缺省 algorithmBackend + 需要补的域标签。
    ///
    /// **表内顺序即优先级**：精确规则必须排在同前缀的前缀规则之前
    /// （<c>match.feature</c> 在 <c>match.</c> 前、<c>defect.referenceCompare</c> 在 <c>defect.</c> 前、
    /// <c>image.acquire</c>/<c>image.synthetic</c> 在 <c>image.</c> 前）。
    /// 契约测试 <c>AlgorithmBackendLabelTests</c> 锁住三件事：每条精确类型都存在于节点目录、
    /// 目录里每个类型最多被一条规则命中、推导结果与期望快照一致。
    ///
    /// 特意保留精确类型清单而不收成 <c>measure.</c> 前缀：原语义是"未登记的类型不推导"，
    /// 改成前缀会让新节点自动获得 jlvision 标签，等于把"忘了登记"变成"看起来有后端"。
    /// </summary>
    private static readonly AlgorithmBackendRule[] Rules =
    [
        // ---- 有域标签的类型：缺省后端 + 域键 ----
        new("inference.", "onnx", NodeSummaryKeys.InferenceBackend),
        new("recognition.barcode", "zxing", NodeSummaryKeys.RecognitionBackend),
        new("recognition.ocr", "tesseract", NodeSummaryKeys.RecognitionBackend),
        new("recognition.aruco", "opencv", NodeSummaryKeys.RecognitionBackend),
        // script.csharp 两个键语义不同：algorithmBackend 报"执行后端"(roslyn)，
        // scriptBackend 报"脚本语言"(csharp)。这是全表唯一 DomainValue ≠ DefaultBackend 的条目。
        new("script.csharp", "roslyn", NodeSummaryKeys.ScriptBackend, "csharp"),
        new("calibration.charuco", "opencv", NodeSummaryKeys.CalibrationBackend),
        new("calibration.handEye3D", "opencv", NodeSummaryKeys.CalibrationBackend),
        new("calibration.dualCameraExtrinsics", "opencv", NodeSummaryKeys.CalibrationBackend),
        new("calibration.handEye2D", "managed", NodeSummaryKeys.CalibrationBackend),
        // match.feature 走 OpenCV 特征匹配，但域值仍跟随执行器声明（可能被替成 managed）。
        new("match.feature", "opencv", NodeSummaryKeys.MatchingBackend, null, DomainFollowsBackend: true),
        new("match.", "jlvision", NodeSummaryKeys.MatchingBackend, null, DomainFollowsBackend: true),

        // ---- 只有 algorithmBackend、没有域标签 ----
        new("color.classify", "opencv"),
        new("image.denoise", "opencv"),
        new("image.stitch", "opencv"),
        new("defect.referenceCompare", "hybrid"),

        // JLVision 传统算子（精确登记，见上方为何不用 measure. 前缀）
        new("measure.grayHistogram", "jlvision"),
        new("measure.caliperWidth", "jlvision"),
        new("measure.caliperArray", "jlvision"),
        new("measure.contourThickness", "jlvision"),
        new("measure.metrology", "jlvision"),
        new("measure.roundness", "jlvision"),
        new("measure.straightness", "jlvision"),
        new("measure.contourDeviation", "jlvision"),
        new("measure.regionIntensity", "jlvision"),
        new("measure.colorDifference", "jlvision"),
        new("measure.blob", "jlvision"),
        new("measure.blobs", "jlvision"),
        new("measure.caliper", "jlvision"),
        new("measure.caliperLine", "jlvision"),
        new("measure.caliperCircle", "jlvision"),
        new("measure.fitLine", "jlvision"),
        new("measure.fitCircle", "jlvision"),
        new("measure.fitEllipse", "jlvision"),
        new("measure.rotatedRect", "jlvision"),
        new("calibration.detectBoard", "jlvision"),
        new("jlvision.imageOp", "jlvision"),
        new("jlvision.regionOp", "jlvision"),
        new("jlvision.regionFeature", "jlvision"),
        new("jlvision.geometry", "jlvision"),
        new("jlvision.matrixOp", "jlvision"),
        new("jlvision.homMat", "jlvision"),
        new("jlvision.contourOp", "jlvision"),
        new("jlvision.threshold", "jlvision"),
        new("jlvision.contourFeature", "jlvision"),
        new("jlvision.measure1d", "jlvision"),

        // 按前缀归属 JLVision 的算子族
        new("defect.", "jlvision"),
        new("region.", "jlvision"),
        new("contour.", "jlvision"),
        new("feature.", "jlvision"),

        // 图像处理的例外必须排在前缀之前：采集与合成图没有算法后端。
        new("image.acquire"),
        new("image.synthetic"),
        new("image.", "jlvision"),
    ];

    /// <summary>返回该类型命中的规则；未登记的类型返回 null（报告里就不会出现后端标签）。</summary>
    internal static AlgorithmBackendRule? Match(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        foreach (var rule in Rules)
            if (rule.Matches(type)) return rule;
        return null;
    }

    /// <summary>
    /// 执行器未声明时使用的 algorithmBackend 缺省值。
    /// 词汇表：jlvision | opencv | managed | zxing | tesseract | onnx | roslyn | hybrid。
    /// 设备/流程节点返回 null。
    /// </summary>
    internal static string? BackendFor(string type) => Match(type)?.DefaultBackend;

    /// <summary>补写 recognitionBackend / matchingBackend / inferenceBackend / calibrationBackend / scriptBackend（仅在缺失时）。</summary>
    internal static IReadOnlyDictionary<string, object?> EnsureDomainBackendLabels(string type, IReadOnlyDictionary<string, object?> summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (Match(type) is not { DomainKey: { } domainKey } rule) return summary;
        if (summary.ContainsKey(domainKey)) return summary;

        var domainValue = rule.DomainFollowsBackend
            ? MatchingBackendOf(summary)
            : rule.DomainValue ?? rule.DefaultBackend;
        if (domainValue is null) return summary;

        return new Dictionary<string, object?>(summary) { [domainKey] = domainValue };
    }

    /// <summary>
    /// 匹配节点的域标签跟随"本次实际使用的算法后端"，但只认 managed / opencv 两个特例：
    /// 其余（含未声明、hybrid）一律按 jlvision 报——这与合并前的判定完全一致，
    /// 不要顺手改成"原样透传"，否则 managed 之外的自定义值会漏进报告。
    /// </summary>
    private static string MatchingBackendOf(IReadOnlyDictionary<string, object?> summary)
    {
        if (summary.TryGetValue(NodeSummaryKeys.AlgorithmBackend, out var raw) && raw is string backend)
        {
            if (backend.Equals("managed", StringComparison.OrdinalIgnoreCase)) return "managed";
            if (backend.Equals("opencv", StringComparison.OrdinalIgnoreCase)) return "opencv";
        }
        return "jlvision";
    }
}
