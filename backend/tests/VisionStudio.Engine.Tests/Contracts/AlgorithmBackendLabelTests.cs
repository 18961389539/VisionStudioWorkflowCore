using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 算法后端标签映射表的契约测试（2026-11-25）。
///
/// 被守护的对象是 <see cref="VisionAlgorithmRuntime"/> 里的**一张**规则表：它同时决定
/// "报告里的 algorithmBackend 缺省值"和"要不要补 domain 标签"。合并前这两件事各有一份表，
/// 逐条重复且靠人工同步——本组测试的第一个作用就是让"再写出第二份表"这件事变得显眼：
/// 改动映射必须回到这里同步期望值。
/// </summary>
public sealed class AlgorithmBackendLabelTests
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    // ---- 1. 表里的精确类型必须真实存在（防止节点改名/删除后表里留垃圾）----

    [Fact]
    public void EveryExactRulePatternIsAKnownNodeType()
    {
        var catalogTypes = BuiltInNodeCatalog.Items.Select(item => item.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 历史别名（如 measure.blob → measure.blobs）不在目录里，但执行器仍会收到旧流程传来的
        // 类型字符串，因此标签表必须继续覆盖它们——这里以 NodeTypeMigration 的映射键为第二来源。
        var legacyTypes = NodeTypeMigration.ReplacementMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactPatterns = RulePatterns().Where(pattern => !pattern.EndsWith('.')).ToArray();

        Assert.NotEmpty(exactPatterns);
        var unknown = exactPatterns
            .Where(pattern => !catalogTypes.Contains(pattern) && !legacyTypes.Contains(pattern))
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "映射表引用了节点目录和历史别名里都不存在的类型（改名或删除后忘了同步）：" + string.Join(", ", unknown));
    }

    // ---- 2. 命中多条规则时，必须选中"最具体"的那条（防止顺序写错导致精确规则被前缀吞掉）----

    [Fact]
    public void EveryCatalogTypeResolvesToItsMostSpecificRule()
    {
        var conflicts = new List<string>();
        foreach (var type in BuiltInNodeCatalog.Items.Select(item => item.Type).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hits = RulePatterns().Where(pattern => Matches(pattern, type)).ToArray();
            if (hits.Length == 0) continue;

            // 精确规则 > 长前缀 > 短前缀。image.acquire 同时命中 "image.acquire" 和 "image."，
            // 正确行为是前者；若表顺序写反，Match 会返回 "image."，这里立刻报出来。
            var expected = hits
                .OrderByDescending(pattern => pattern.EndsWith('.') ? pattern.Length : int.MaxValue)
                .First();
            var actual = VisionAlgorithmRuntime.Match(type)?.Pattern;

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                conflicts.Add($"{type}: 期望 {expected}，实际 {actual}");
        }

        Assert.True(
            conflicts.Count == 0,
            "精确规则必须排在同前缀的前缀规则之前：" + string.Join("; ", conflicts));
    }

    // ---- 3. 推导结果快照：代表性类型的标签不得变化 ----

    [Theory]
    // 设备/流程节点：没有算法后端
    [InlineData("image.acquire", null, null)]
    [InlineData("image.synthetic", null, null)]
    [InlineData("flow.if", null, null)]
    [InlineData("device.plcWrite", null, null)]
    // 单一后端 + 域标签
    [InlineData("inference.detect", "onnx", "inferenceBackend=onnx")]
    [InlineData("recognition.barcode", "zxing", "recognitionBackend=zxing")]
    [InlineData("recognition.ocr", "tesseract", "recognitionBackend=tesseract")]
    [InlineData("recognition.aruco", "opencv", "recognitionBackend=opencv")]
    [InlineData("calibration.charuco", "opencv", "calibrationBackend=opencv")]
    [InlineData("calibration.handEye2D", "managed", "calibrationBackend=managed")]
    [InlineData("calibration.handEye3D", "opencv", "calibrationBackend=opencv")]
    [InlineData("calibration.dualCameraExtrinsics", "opencv", "calibrationBackend=opencv")]
    [InlineData("match.feature", "opencv", "matchingBackend=jlvision")]
    [InlineData("match.shape", "jlvision", "matchingBackend=jlvision")]
    [InlineData("match.template", "jlvision", "matchingBackend=jlvision")]
    // 只有 algorithmBackend
    [InlineData("color.classify", "opencv", null)]
    [InlineData("image.denoise", "opencv", null)]
    [InlineData("image.stitch", "opencv", null)]
    [InlineData("defect.referenceCompare", "hybrid", null)]
    [InlineData("defect.scratch", "jlvision", null)]
    [InlineData("region.blob", "jlvision", null)]
    [InlineData("contour.extract", "jlvision", null)]
    [InlineData("feature.edge", "jlvision", null)]
    [InlineData("measure.caliper", "jlvision", null)]
    [InlineData("measure.caliperCircle", "jlvision", null)]
    [InlineData("measure.metrology", "jlvision", null)]
    [InlineData("measure.grayHistogram", "jlvision", null)]
    [InlineData("jlvision.geometry", "jlvision", null)]
    [InlineData("calibration.detectBoard", "jlvision", null)]
    [InlineData("image.threshold", "jlvision", null)]
    // 未登记类型：一律不推导（新节点必须显式登记，不允许"看起来有后端"）
    [InlineData("measure.unknownFutureNode", null, null)]
    [InlineData("somevendor.thing", null, null)]
    public void ResolvedLabels_MatchExpectations(string type, string? expectedBackend, string? expectedDomain)
    {
        Assert.Equal(expectedBackend, VisionAlgorithmRuntime.BackendFor(type));

        var summary = VisionAlgorithmRuntime.EnsureDomainBackendLabels(type, Empty);
        var domain = summary
            .Where(pair => pair.Key.EndsWith("Backend", StringComparison.Ordinal) && pair.Key != "algorithmBackend")
            .Select(pair => $"{pair.Key}={pair.Value}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        if (expectedDomain is null)
            Assert.Empty(domain);
        else
            Assert.Equal([expectedDomain], domain);
    }

    // ---- 4. match.* 的域值跟随执行器声明的后端（这是合并时最容易丢的一条语义）----

    [Theory]
    [InlineData("match.shape", "managed", "managed")]
    [InlineData("match.shape", "opencv", "opencv")]
    [InlineData("match.shape", "jlvision", "jlvision")]
    [InlineData("match.feature", "managed", "managed")]
    [InlineData("match.feature", "opencv", "opencv")]
    // 白名单之外的值不原样透传：hybrid/自定义值一律报 jlvision（与合并前一致）
    [InlineData("match.shape", "hybrid", "jlvision")]
    [InlineData("match.shape", "vendor-accelerated", "jlvision")]
    public void MatchingDomain_FollowsDeclaredBackend(string type, string declared, string expectedDomain)
    {
        var summary = new Dictionary<string, object?> { [NodeSummaryKeys.AlgorithmBackend] = declared };

        var stamped = VisionAlgorithmRuntime.EnsureDomainBackendLabels(type, summary);

        Assert.Equal(expectedDomain, stamped[NodeSummaryKeys.MatchingBackend]);
    }

    [Fact]
    public void MatchingDomain_DefaultsToJlvisionWhenExecutorSilent()
    {
        var stamped = VisionAlgorithmRuntime.EnsureDomainBackendLabels("match.shape", Empty);

        Assert.Equal("jlvision", stamped[NodeSummaryKeys.MatchingBackend]);
    }

    // ---- 5. 执行器已有的域标签不得被覆盖 ----

    [Fact]
    public void ExistingDomainLabelIsNeverOverwritten()
    {
        var summary = new Dictionary<string, object?>
        {
            [NodeSummaryKeys.RecognitionBackend] = "custom-zxing-wrapper"
        };

        var stamped = VisionAlgorithmRuntime.EnsureDomainBackendLabels("recognition.barcode", summary);

        Assert.Same(summary, stamped);
        Assert.Equal("custom-zxing-wrapper", stamped[NodeSummaryKeys.RecognitionBackend]);
    }

    // ---- 6. script.csharp 的两个键语义不同（全表唯一的 DomainValue ≠ DefaultBackend）----

    [Fact]
    public void ScriptBackendIsLanguageWhileAlgorithmBackendIsCompiler()
    {
        Assert.Equal("roslyn", VisionAlgorithmRuntime.BackendFor("script.csharp"));

        var stamped = VisionAlgorithmRuntime.EnsureDomainBackendLabels("script.csharp", Empty);

        Assert.Equal("csharp", stamped[NodeSummaryKeys.ScriptBackend]);
    }

    // ---- 7. 同一个类型连续调用两次结果稳定（幂等）----

    [Fact]
    public void StampingIsIdempotent()
    {
        var once = VisionAlgorithmRuntime.EnsureDomainBackendLabels("match.shape", Empty);
        var twice = VisionAlgorithmRuntime.EnsureDomainBackendLabels("match.shape", once);

        Assert.Equal(once[NodeSummaryKeys.MatchingBackend], twice[NodeSummaryKeys.MatchingBackend]);
        Assert.Equal(1, twice.Keys.Count(key => key == NodeSummaryKeys.MatchingBackend));
    }

    private static string[] RulePatterns()
    {
        // 通过反射读表，避免为测试把 Rules 提成 public（它不该成为对外 API）。
        var field = typeof(VisionAlgorithmRuntime).GetField(
            "Rules",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var rules = Assert.IsAssignableFrom<System.Collections.IEnumerable>(field!.GetValue(null));
        return rules.Cast<object>()
            .Select(rule => (string)rule.GetType().GetProperty("Pattern")!.GetValue(rule)!)
            .ToArray();
    }

    private static bool Matches(string pattern, string type) => pattern.EndsWith('.')
        ? type.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
        : type.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}
