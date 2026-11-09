using System.Globalization;

namespace VisionStudio.Engine;

/// <summary>
/// 参数首屏分层（2026-11-06）。
///
/// 目录参数按声明顺序放置核心参数，配置页默认只展示前 8 个可见参数；其余参数标记为
/// <c>Advanced</c>，折叠到“显示全部参数”。这解决的是参数密集节点（match.shape 35 个可见参数、
/// inference.* 20+、device.serialPort 18）一打开就是长表单的问题，不改变任何执行语义：
/// 执行器仍按名字读取全部声明参数，校验也照旧覆盖全量。
///
/// 为什么集中在一处而不是写进 34 个目录文件：
/// 1. 首屏预算是展示策略，不是节点算法语义，后续调整只需改这一张表；
/// 2. 自动规则（声明顺序前 8 个）能让 40+ 个节点立即受益，只有少数“关键参数声明在后”的
///    节点需要 <see cref="EssentialOverrides"/> 手工指定核心 8 项。
///
/// 可见性条件参数（<c>VisibleWhen</c> / <c>VisibleWhenAll</c>）默认隐藏，用户切换模式后才出现，
/// 因此不折叠——只标记默认可见却超出预算的参数。这样“选了某个模式后新出现的参数”不会被二次藏起来。
///
/// 守卫见 ParameterTierTests：默认可见且未折叠的参数不超过 8 个；覆盖表的类型与参数名必须存在；
/// 内部参数不得被标记。
/// </summary>
public static class ParameterTierCatalog
{
    /// <summary>配置页首屏允许展示的默认可见参数上限。</summary>
    public const int EssentialLimit = 8;

    /// <summary>
    /// 参数密集节点的核心参数清单。未列出的节点按目录声明顺序取前 8 个默认可见参数。
    /// 只允许使用参数密集节点，且每张表最多 8 项。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> EssentialOverrides { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // 匹配：训练覆盖、金字塔与搜索角度/尺度是实际调参入口，不能被诊断参数挤出首屏。
            ["match.shape"] =
            [
                "modelMode", "trainingAngleRangeDeg", "pyramidLevels", "scaleTolerancePercent",
                "searchAngleStartDeg", "searchAngleEndDeg", "searchScaleMinPercent", "searchScaleMaxPercent"
            ],
            ["match.template"] =
            [
                "templatePath", "angleRangeDeg", "minScore", "searchAngleStartDeg",
                "searchAngleEndDeg", "scaleMinPercent", "scaleMaxPercent", "method"
            ],

            // 推理：模型与输入尺寸之后必须有阈值/后处理，否则用户找不到“为什么没检出”。
            ["inference.detection"] =
            [
                "packagePath", "modelPath", "outputName", "inputWidth", "inputHeight",
                "confidenceThreshold", "nmsThreshold", "layout"
            ],
            ["inference.classification"] =
            [
                "packagePath", "modelPath", "inputWidth", "inputHeight",
                "crop", "topK", "minimumConfidence", "labels"
            ],
            ["inference.anomaly"] =
            [
                "packagePath", "modelPath", "outputName", "inputWidth", "inputHeight",
                "threshold", "useSeparateMapThreshold", "thresholdMode"
            ],
            ["inference.segmentation"] =
            [
                "packagePath", "modelPath", "outputName", "inputWidth", "inputHeight",
                "binaryThreshold", "lowConfidenceThreshold", "layout"
            ],
            ["inference.obb"] =
            [
                "packagePath", "modelPath", "outputName", "inputWidth", "inputHeight",
                "confidenceThreshold", "nmsThreshold", "maxCount"
            ],
            ["inference.pose"] =
            [
                "packagePath", "modelPath", "inputWidth", "inputHeight", "numKeypoints",
                "confidenceThreshold", "keypointThreshold", "maxCount"
            ],
            ["inference.instanceSegmentation"] =
            [
                "packagePath", "modelPath", "outputName", "inputWidth", "inputHeight",
                "confidenceThreshold", "nmsThreshold", "maskThreshold"
            ],

            // 卡尺：圆心/半径/角度范围与采样对比度是“测得到/测不到”的直接旋钮。
            ["measure.caliperCircle"] =
            [
                "centerX", "centerY", "radius", "angleStartDeg",
                "angleEndDeg", "caliperCount", "minContrast", "polarity"
            ],
            ["measure.caliperArray"] =
            [
                "startX", "startY", "endX", "endY",
                "caliperCount", "edgeThreshold", "polarity", "nominalWidth"
            ],
            ["measure.caliperLine"] =
            [
                "startX", "startY", "endX", "endY",
                "sampleWidth", "minContrast", "direction", "caliperCount"
            ],
            ["measure.caliperWidth"] =
            [
                "startX", "startY", "endX", "endY",
                "sampleWidth", "polarity", "direction", "nominalWidth"
            ],

            // 通信：串口/落盘的请求内容与读取方式比流控位更重要。
            ["device.serialPort"] =
            [
                "deviceId", "portName", "baudRate", "payload",
                "payloadEncoding", "sendTerminator", "readMode", "timeoutMs"
            ],
            ["output.saveImage"] =
            [
                "path", "namingMode", "filenameTemplate", "partId",
                "resultMode", "conflictPolicy", "format", "jpegQuality"
            ]
        };

    /// <summary>把超出首屏预算的默认可见参数标记为 Advanced；不改变参数顺序或可见性条件。</summary>
    public static NodeCatalogItem Apply(NodeCatalogItem item)
    {
        var parameters = item.Parameters;
        var defaultVisible = parameters
            .Where(parameter => IsVisibleByDefault(parameter, parameters))
            .ToArray();
        if (defaultVisible.Length <= EssentialLimit) return item;

        var essential = ResolveEssential(item, defaultVisible);
        var advanced = parameters
            .Where(parameter => !parameter.Internal && !essential.Contains(parameter.Name))
            // 上下文参数（由 visibleWhen 控制）只在用户切换模式后出现，不再二次折叠。
            .Where(parameter => !HasVisibilityCondition(parameter) || IsVisibleByDefault(parameter, parameters))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (advanced.Count == 0) return item;

        return item with
        {
            Parameters = parameters
                .Select(parameter => advanced.Contains(parameter.Name)
                    ? parameter with { Advanced = true }
                    : parameter)
                .ToArray()
        };
    }

    /// <summary>与前端 isParameterVisible 相同的默认值判定：省略值使用目录默认值。</summary>
    public static bool IsVisibleByDefault(ParameterDescriptor parameter, IReadOnlyList<ParameterDescriptor> parameters)
    {
        if (parameter.Internal) return false;
        if (parameter.VisibleWhen is not null && !Matches(parameter.VisibleWhen, parameters)) return false;
        if (parameter.VisibleWhenAll is not null)
            foreach (var condition in parameter.VisibleWhenAll)
                if (!Matches(condition, parameters)) return false;
        return true;
    }

    private static HashSet<string> ResolveEssential(NodeCatalogItem item, IReadOnlyList<ParameterDescriptor> defaultVisible)
    {
        if (!EssentialOverrides.TryGetValue(item.Type, out var configured))
            return defaultVisible.Take(EssentialLimit)
                .Select(parameter => parameter.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configured.Count > EssentialLimit)
            throw new InvalidOperationException(
                $"Parameter tier override for '{item.Type}' lists {configured.Count} essential parameters; limit is {EssentialLimit}.");

        var known = item.Parameters
            .Where(parameter => !parameter.Internal)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = configured.Where(name => !known.Contains(name)).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException(
                $"Parameter tier override for '{item.Type}' references unknown parameter(s): {string.Join(", ", unknown)}.");

        return configured.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool Matches(ParameterVisibility condition, IReadOnlyList<ParameterDescriptor> parameters)
    {
        if (condition.Values.Count == 0) return false;
        var controller = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals(condition.Parameter, StringComparison.OrdinalIgnoreCase));
        if (controller is null) return false;
        var actual = Convert.ToString(controller.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return condition.Values.Any(value => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasVisibilityCondition(ParameterDescriptor parameter) =>
        parameter.VisibleWhen is not null || parameter.VisibleWhenAll is { Count: > 0 };
}
