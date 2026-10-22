using System.Globalization;
using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>调参取向：影响层数与角步长的后处理，不改变原生推荐值的语义。</summary>
public enum MatchingAutoTuneProfile
{
    Balanced,
    Speed,
    Accuracy
}

/// <summary>
/// 自动调参输入。角度以度为单位，内部转弧度；尺度容差为百分比（0 = 固定尺度）。
/// <see cref="LockedParameters"/> 为逗号分隔的参数名，锁定项不被推荐值覆盖。
/// </summary>
public sealed record MatchingAutoTuneRequest(
    double AngleStartDeg = -180,
    double AngleRangeDeg = 360,
    double ScaleTolerancePercent = 0,
    string Metric = "use_polarity",
    int MinContrast = 5,
    int CurrentNumLevels = 0,
    double CurrentAngleStepDeg = 1,
    MatchingAutoTuneProfile Profile = MatchingAutoTuneProfile.Balanced,
    string? LockedParameters = null,
    string? Parameters = null);

public sealed record MatchingAutoTuneWarning(string Code, string Message);

/// <summary>模板可匹配性报告。<see cref="LevelPointCounts"/> 在原生封装只返回单层区域时为空。</summary>
public sealed record MatchingModelInspect(
    int Levels,
    int ModelPointCount,
    double CoverageRatio,
    double MeanGradient,
    string Quality,
    string? Note,
    IReadOnlyList<MatchingAutoTuneWarning> Warnings);

public sealed record MatchingAutoTuneRecommendation(
    int PyramidLevels,
    double AngleStepDeg,
    int? Contrast,
    int MinContrast,
    string Optimization,
    IReadOnlyDictionary<string, string> Raw,
    IReadOnlyList<string> IgnoredParameters,
    MatchingModelInspect Inspect,
    IReadOnlyList<MatchingAutoTuneWarning> Warnings)
{
    /// <summary>
    /// 可直接合并进节点参数的载荷（编排期写死，运行期只读）。
    /// 注意：<c>contrast</c> 目前只作为推荐值返回，不进入 apply——节点尚无该参数，
    /// 且模型缓存键与训练配置版本未纳入它；贸然应用会让既有配方静默重训。
    /// </summary>
    public Dictionary<string, object?> ToNodeParameters() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["parameterSource"] = "auto",
        ["pyramidLevels"] = PyramidLevels,
        ["angleStepDeg"] = Math.Round(AngleStepDeg, 3),
        ["minContrast"] = MinContrast
    };
}

/// <summary>
/// 基于 JLVision 原生 <c>determine_shape_model_params</c> / <c>determine_ncc_model_params</c> 的
/// 参数确定服务（编排期调用，运行期不调用）。实测约束：
/// <list type="bullet">
/// <item><c>angle_step</c> 返回弧度，节点参数用度，需转换。</item>
/// <item><c>contrast</c> 不接受 "auto"（HALCON #1308），必须给 1..255 的种子值。</item>
/// <item>NCC 的 determine 是 <c>JlNCCModel</c> 上的静态方法。</item>
/// </list>
/// </summary>
public static class MatchingAutoTune
{
    public const string Version = "auto-v1";
    public const int MinTemplateSize = 16;
    public const double MinAngleStepDeg = 0.1;
    public const double MaxAngleStepDeg = 30;

    /// <summary>原生库版本（写入调参溯源，便于升级后定位推荐值漂移）。</summary>
    public static string NativeLibraryVersion
        => typeof(JlImage).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>节点是否显式启用了自动调参（默认 manual，保证既有配方零行为变化）。</summary>
    public static bool IsAutoEnabled(NodeDefinition node)
        => node is not null
           && node.GetString("parameterSource", "manual").Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 训练配置指纹片段：manual 返回空串（既有配方哈希完全不变），auto 返回版本+取向+锁定项，
    /// 使推荐值变化时训练资产能被判定为过期并重训。
    /// </summary>
    public static string AutoFingerprint(NodeDefinition node)
    {
        if (!IsAutoEnabled(node)) return string.Empty;
        var profile = NormalizeProfileName(node.GetString("autoTuneProfile", "balanced"));
        var locked = string.Join(',', ParseLocked(node.GetString("autoTuneLockedParams", string.Empty)).OrderBy(x => x, StringComparer.Ordinal));
        return $"{Version}:{profile}:{locked}";
    }

    /// <summary>
    /// 训练期自动调参：用训练 ROI 缓冲（与建模型完全相同的图）调用原生 determine，
    /// 返回可直接覆盖 pyramidLevels / angleStepDeg / minContrast 的推荐值。
    /// </summary>
    public static MatchingAutoTuneRecommendation ResolveForShapeTraining(Mat template, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return DetermineShape(template, BuildShapeRequest(node));
    }

    private static MatchingAutoTuneRequest BuildShapeRequest(NodeDefinition node)
    {
        var range = Math.Clamp(
            node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)), 0.1, 180);
        return new MatchingAutoTuneRequest(
            AngleStartDeg: -range,
            AngleRangeDeg: 2 * range,
            ScaleTolerancePercent: Math.Max(
                Math.Clamp(node.GetDouble("scaleTolerancePercent", 0), 0, 50),
                Math.Max(Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50),
                         Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50))),
            Metric: node.GetBool("ignorePolarity", false) ? "ignore_global_polarity" : "use_polarity",
            MinContrast: Math.Clamp(node.GetInt("minContrast", 5), 1, 255),
            CurrentNumLevels: Math.Clamp(node.GetInt("pyramidLevels", 0), 0, 8),
            CurrentAngleStepDeg: Math.Clamp(node.GetDouble("angleStepDeg", 1), MinAngleStepDeg, MaxAngleStepDeg),
            Profile: ParseProfileName(node.GetString("autoTuneProfile", "balanced")),
            LockedParameters: node.GetString("autoTuneLockedParams", string.Empty));
    }

    private static MatchingAutoTuneProfile ParseProfileName(string? profile)
        => (profile ?? "balanced").Trim().ToLowerInvariant() switch
        {
            "speed" => MatchingAutoTuneProfile.Speed,
            "accuracy" => MatchingAutoTuneProfile.Accuracy,
            _ => MatchingAutoTuneProfile.Balanced
        };

    private static readonly Dictionary<string, (MatchingAutoTuneRecommendation Recommendation, long LastUse)> ResolveCache = new(StringComparer.Ordinal);
    private static readonly object ResolveCacheGate = new();
    private const int ResolveCacheCapacity = 32;

    /// <summary>按 key 缓存一次 determine 的推荐值，命中后零原生调用（模板相同则结果必然相同）。</summary>
    private static MatchingAutoTuneRecommendation ResolveCached(string key, Func<MatchingAutoTuneRecommendation> compute)
    {
        lock (ResolveCacheGate)
        {
            if (ResolveCache.TryGetValue(key, out var cached))
            {
                ResolveCache[key] = (cached.Recommendation, Environment.TickCount64);
                return cached.Recommendation;
            }
        }

        var recommendation = compute();

        lock (ResolveCacheGate)
        {
            if (ResolveCache.Count >= ResolveCacheCapacity)
            {
                var oldest = ResolveCache.OrderBy(pair => pair.Value.LastUse).First().Key;
                ResolveCache.Remove(oldest);
            }
            ResolveCache[key] = (recommendation, Environment.TickCount64);
        }
        return recommendation;
    }

    /// <summary>
    /// NCC 运行期自动调参。determine 需要模板像素，逐帧调用会吃节拍，因此按
    /// templatePath+mtime 或模板内容指纹缓存；同一模板只算一次。
    /// </summary>
    public static MatchingAutoTuneRecommendation ResolveForNcc(NodeDefinition node, Mat templateBuffer, bool hasTemplateRoi)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(templateBuffer);
        if (templateBuffer.Empty())
            throw new InvalidOperationException("match.template auto-tune requires a non-empty template image.");

        var range = Math.Clamp(node.GetDouble("angleRangeDeg", 0), 0, 180);
        var request = new MatchingAutoTuneRequest(
            AngleStartDeg: -Math.Max(range, 0.1),
            AngleRangeDeg: 2 * Math.Max(range, 0.1),
            Metric: node.GetBool("ignorePolarity", false) ? "ignore_global_polarity" : "use_polarity",
            MinContrast: Math.Clamp(node.GetInt("minContrast", 5), 1, 255),
            CurrentNumLevels: Math.Clamp(node.GetInt("pyramidLevels", 0), 0, 8),
            CurrentAngleStepDeg: Math.Clamp(node.GetDouble("angleStepDeg", 5), MinAngleStepDeg, MaxAngleStepDeg),
            Profile: ParseProfileName(node.GetString("autoTuneProfile", "balanced")),
            LockedParameters: node.GetString("autoTuneLockedParams", string.Empty));

        var key = CacheKey("ncc", node, templateBuffer, request);
        var recommendation = ResolveCached(key, () => DetermineNcc(templateBuffer, request));
        if (hasTemplateRoi)
        {
            recommendation = recommendation with
            {
                Warnings = [.. recommendation.Warnings, new MatchingAutoTuneWarning(
                    "roi-mask-not-cropped",
                    "Template ROI is used as a model mask, not as a crop: determine saw the whole template image, so pyramid levels may be one step too deep for a small ROI.")]
            };
        }
        return recommendation;
    }

    /// <summary>
    /// legacy 形状匹配（不训练、直接拿模板图建模型）的运行期自动调参。
    /// 与 NCC 同理：按模板身份缓存，避免逐帧调用 determine；参数覆盖层数/角步长/最小对比度。
    /// </summary>
    public static MatchingAutoTuneRecommendation ResolveForShapeLegacy(NodeDefinition node, Mat templateBuffer, bool hasTemplateRoi)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(templateBuffer);
        if (templateBuffer.Empty())
            throw new InvalidOperationException("match.shape auto-tune requires a non-empty template image.");

        var request = BuildShapeRequest(node);
        var key = CacheKey("shape-legacy", node, templateBuffer, request);
        var recommendation = ResolveCached(key, () =>
        {
            var resolved = DetermineShape(templateBuffer, request);
            if (!hasTemplateRoi) return resolved;
            return resolved with
            {
                Warnings = [.. resolved.Warnings, new MatchingAutoTuneWarning(
                    "roi-mask-not-cropped",
                    "Template ROI is used as a model mask, not as a crop: determine saw the whole template image, so pyramid levels may be one step too deep for a small ROI.")]
            };
        });
        return recommendation;
    }

    private static string CacheKey(string mode, NodeDefinition node, Mat template, MatchingAutoTuneRequest request)
    {
        var path = node.GetString("templatePath", string.Empty).Trim();
        string identity;
        if (path.Length > 0)
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            identity = info.Exists
                ? FormattableString.Invariant($"{full}|{info.LastWriteTimeUtc.Ticks}|{info.Length}")
                : full;
        }
        else
        {
            identity = FormattableString.Invariant($"{template.Width}x{template.Height}|{FastFingerprint(template):X16}");
        }
        return FormattableString.Invariant(
            $"{Version}|{mode}|{identity}|{request.AngleStartDeg:R}|{request.AngleRangeDeg:R}|{request.ScaleTolerancePercent:R}|{request.Metric}|{request.Profile}|{ParseLocked(request.LockedParameters).Count}");
    }

    /// <summary>模板内容指纹：均匀采样 256 个字节，避免整图 SHA256 的逐帧开销。</summary>
    private static ulong FastFingerprint(Mat mat)
    {
        var total = checked(mat.Width * mat.Height * mat.ElemSize());
        if (total == 0) return 0;
        var samples = Math.Min(256, total);
        var stride = Math.Max(1, total / samples);
        ulong hash = 14695981039346656037UL;
        for (var offset = 0; offset < total; offset += stride)
        {
            hash ^= System.Runtime.InteropServices.Marshal.ReadByte(mat.Data, offset);
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static string NormalizeProfileName(string? profile)
        => (profile ?? "balanced").Trim().ToLowerInvariant() switch
        {
            "speed" => "speed",
            "accuracy" => "accuracy",
            _ => "balanced"
        };

    private const string ShapeParameterList = "num_levels,angle_step,contrast,min_contrast,optimization";
    private const string NccParameterList = "num_levels,angle_step";

    public static MatchingAutoTuneRecommendation DetermineShape(Mat template, MatchingAutoTuneRequest request)
        => Determine(template, request, ncc: false);

    public static MatchingAutoTuneRecommendation DetermineNcc(Mat template, MatchingAutoTuneRequest request)
        => Determine(template, request, ncc: true);

    private static MatchingAutoTuneRecommendation Determine(Mat template, MatchingAutoTuneRequest request, bool ncc)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Empty())
            throw new InvalidOperationException("matching.auto-tune requires a non-empty template image.");
        if (template.Width < MinTemplateSize || template.Height < MinTemplateSize)
            throw new InvalidOperationException(
                $"matching.auto-tune requires a template of at least {MinTemplateSize}x{MinTemplateSize} px (got {template.Width}x{template.Height}).");

        var warnings = new List<MatchingAutoTuneWarning>();
        var locked = ParseLocked(request.LockedParameters);
        var profile = request.Profile;

        var angleStartRad = request.AngleStartDeg * Math.PI / 180d;
        var angleExtentRad = Math.Clamp(request.AngleRangeDeg, 0.1, 360) * Math.PI / 180d;
        var tolerance = Math.Clamp(request.ScaleTolerancePercent, 0, 50) / 100d;
        var scaleMin = Math.Max(0.05, 1 - tolerance);
        var scaleMax = 1 + tolerance;
        var minContrastSeed = Math.Clamp(request.MinContrast, 1, 255);
        var requested = ParseParameterList(request.Parameters) ?? (ncc ? NccParameterList : ShapeParameterList).Split(',');

        using var image = JLVisionImages.FromMat(template);
        JlTuple? names = null;
        JlTuple? values = null;
        try
        {
            if (ncc)
            {
                using var levels = new JlTuple("auto");
                using var parameters = new JlTuple(requested);
                names = JlNCCModel.DetermineNccModelParams(
                    image, levels, angleStartRad, angleExtentRad, NormalizeMetric(request.Metric), parameters, out values);
            }
            else
            {
                using var levels = new JlTuple("auto");
                using var contrastSeed = new JlTuple(Math.Clamp(request.MinContrast * 4, 1, 255));
                using var minimum = new JlTuple(minContrastSeed);
                using var sMin = new JlTuple(scaleMin);
                using var sMax = new JlTuple(scaleMax);
                using var parameters = new JlTuple(requested);
                names = image.DetermineShapeModelParams(
                    levels, angleStartRad, angleExtentRad, sMin, sMax, "auto", NormalizeMetric(request.Metric),
                    contrastSeed, minimum, parameters, out values);
            }

            var raw = ReadRaw(names, values, requested, out var ignored);

            var levelsValue = ReadInt(raw, "num_levels") ?? 0;
            var angleStepDeg = ReadAngleStepDeg(raw);
            var contrast = ReadInt(raw, "contrast");
            var minContrast = ReadInt(raw, "min_contrast") ?? minContrastSeed;
            var optimization = raw.TryGetValue("optimization", out var opt) && opt.Length > 0 ? opt : "auto";

            var maxLevels = MaxLevelsForTemplate(template.Width, template.Height);
            if (levelsValue <= 0)
            {
                levelsValue = Math.Max(1, maxLevels);
                warnings.Add(new MatchingAutoTuneWarning("levels-missing", "Native determine did not return num_levels; used the size-based level budget."));
            }
            if (levelsValue > maxLevels)
            {
                warnings.Add(new MatchingAutoTuneWarning(
                    "levels-clamped",
                    $"Recommended {levelsValue} levels exceeds the {maxLevels}-level budget for a {template.Width}x{template.Height} template; clamped."));
                levelsValue = maxLevels;
            }
            levelsValue = Math.Clamp(levelsValue, 1, 8);

            if (angleStepDeg is null)
            {
                angleStepDeg = request.CurrentAngleStepDeg > 0 ? request.CurrentAngleStepDeg : 1;
                warnings.Add(new MatchingAutoTuneWarning("angle-step-missing", "Native determine did not return angle_step; kept the current step."));
            }
            if (angleStepDeg < MinAngleStepDeg || angleStepDeg > MaxAngleStepDeg)
            {
                var clamped = Math.Clamp(angleStepDeg.Value, MinAngleStepDeg, MaxAngleStepDeg);
                warnings.Add(new MatchingAutoTuneWarning(
                    "angle-step-clamped",
                    $"Recommended angle step {angleStepDeg.Value:0.###}° is outside [{MinAngleStepDeg}, {MaxAngleStepDeg}]°; clamped to {clamped:0.###}°."));
                angleStepDeg = clamped;
            }

            contrast = contrast is null ? null : Math.Clamp(contrast.Value, 1, 255);
            minContrast = Math.Clamp(minContrast, 1, 255);

            if (locked.Contains("num_levels") && request.CurrentNumLevels > 0)
            {
                warnings.Add(new MatchingAutoTuneWarning("levels-locked", $"num_levels is locked at {request.CurrentNumLevels}."));
                levelsValue = Math.Clamp(request.CurrentNumLevels, 1, maxLevels);
            }
            if (locked.Contains("angle_step") && request.CurrentAngleStepDeg > 0)
            {
                warnings.Add(new MatchingAutoTuneWarning("angle-step-locked", $"angle_step is locked at {request.CurrentAngleStepDeg:0.###}°."));
                angleStepDeg = Math.Clamp(request.CurrentAngleStepDeg, MinAngleStepDeg, MaxAngleStepDeg);
            }
            if (locked.Contains("min_contrast"))
            {
                warnings.Add(new MatchingAutoTuneWarning("min-contrast-locked", $"min_contrast is locked at {minContrastSeed}."));
                minContrast = minContrastSeed;
            }
            if (locked.Contains("contrast"))
                warnings.Add(new MatchingAutoTuneWarning("contrast-lock-ignored", "contrast has no node-level baseline to lock against; the recommendation is kept."));

            if (profile != MatchingAutoTuneProfile.Balanced)
            {
                var beforeLevels = levelsValue;
                var beforeStep = angleStepDeg!.Value;
                if (profile == MatchingAutoTuneProfile.Speed)
                {
                    levelsValue = Math.Max(1, levelsValue - 1);
                    angleStepDeg = Math.Min(MaxAngleStepDeg, angleStepDeg.Value * 2);
                }
                else
                {
                    levelsValue = Math.Min(maxLevels, levelsValue + 1);
                    angleStepDeg = Math.Max(MinAngleStepDeg, angleStepDeg.Value / 2);
                }
                if (beforeLevels != levelsValue || Math.Abs(beforeStep - angleStepDeg.Value) > 1e-9)
                {
                    warnings.Add(new MatchingAutoTuneWarning(
                        $"profile-{profile.ToString().ToLowerInvariant()}",
                        $"Profile adjusted levels {beforeLevels}->{levelsValue} and step {beforeStep:0.###}°->{angleStepDeg.Value:0.###}°."));
                }
            }

            if (angleStepDeg > request.CurrentAngleStepDeg + 1e-9 && request.CurrentAngleStepDeg > 0)
            {
                warnings.Add(new MatchingAutoTuneWarning(
                    "coarser-than-current",
                    $"Recommended step {angleStepDeg.Value:0.###}° is coarser than the current {request.CurrentAngleStepDeg:0.###}°; use profile=accuracy to keep finer coverage."));
            }

            var inspect = Inspect(image, template, levelsValue, contrast ?? 30, warnings);
            return new MatchingAutoTuneRecommendation(
                levelsValue,
                Math.Round(angleStepDeg.Value, 4),
                contrast,
                minContrast,
                optimization,
                raw,
                ignored,
                inspect,
                warnings);
        }
        catch (Exception ex) when (ex is JlOperatorException or JlTupleAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"JLVision auto-tune failed for a {template.Width}x{template.Height} template: {ex.Message}", ex);
        }
        finally
        {
            names?.Dispose();
            values?.Dispose();
        }
    }

    /// <summary>与现有 <c>pyramidLevels=0</c> 启发式保持同一预算：clamp(floor(log2(min/6))+1, 1, 8)。</summary>
    public static int MaxLevelsForTemplate(int width, int height)
        => Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1d, Math.Min(width, height) / 6d))) + 1, 1, 8);

    private static MatchingModelInspect Inspect(JlImage image, Mat template, int levels, int contrast, List<MatchingAutoTuneWarning> warnings)
    {
        var local = new List<MatchingAutoTuneWarning>();
        try
        {
            using var modelImages = image.InspectShapeModel(out var regions, Math.Clamp(levels, 1, 8), Math.Clamp(contrast, 1, 255));
            using (regions)
            {
                var area = regions.AreaCenter(out double regionRow, out double regionColumn);
                _ = regionRow;
                _ = regionColumn;
                var mean = image.Intensity(regions, out double regionDeviation);
                _ = regionDeviation;
                var coverage = area / (double)Math.Max(1, template.Width * template.Height);
                var quality =
                    area < 8 ? "too-few-points" :
                    area < 40 ? "thin" :
                    coverage > 0.9 ? "flat" : "good";
                if (quality == "too-few-points")
                    local.Add(new MatchingAutoTuneWarning("too-few-model-points", $"Only {area} model points were selected; the template may be flat or low-contrast."));
                else if (quality == "thin")
                    local.Add(new MatchingAutoTuneWarning("thin-model", $"{area} model points is thin for stable matching."));
                else if (quality == "flat")
                    local.Add(new MatchingAutoTuneWarning("flat-template", $"Model points cover {coverage:P0} of the template; a flat template rarely discriminates."));
                warnings.AddRange(local);
                return new MatchingModelInspect(
                    levels, area, Math.Round(coverage, 6), Math.Round(mean, 3), quality,
                    "Per-level point counts are unavailable in JLVisionLib 1.0.0 (InspectShapeModel returns a single region).",
                    local);
            }
        }
        catch (Exception ex) when (ex is JlOperatorException or JlTupleAccessException or InvalidOperationException)
        {
            var warning = new MatchingAutoTuneWarning("inspect-unavailable", $"Model inspection failed: {ex.Message}");
            warnings.Add(warning);
            return new MatchingModelInspect(levels, 0, 0, 0, "unavailable", warning.Message, [warning]);
        }
    }

    private static Dictionary<string, string> ReadRaw(JlTuple names, JlTuple values, string[] requested, out IReadOnlyList<string> ignored)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = Math.Min(names.Length, values.Length);
        for (var i = 0; i < count; i++)
        {
            var name = NormalizeName(SafeString(names[i]));
            if (name.Length == 0) continue;
            raw[name] = SafeString(values[i]);
        }
        ignored = requested
            .Select(NormalizeName)
            .Where(key => key.Length > 0 && !raw.ContainsKey(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return raw;
    }

    private static double? ReadAngleStepDeg(IReadOnlyDictionary<string, string> raw)
    {
        if (!raw.TryGetValue("angle_step", out var text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return null;
        // 实测：HALCON/JLVision 返回弧度。
        return value * 180d / Math.PI;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string> raw, string key)
    {
        if (!raw.TryGetValue(key, out var text)) return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? (int)Math.Round(d)
            : null;
    }

    private static string SafeString(JlTupleElements element)
    {
        try { return element.S; } catch (Exception) { }
        try { return element.I.ToString(CultureInfo.InvariantCulture); } catch (Exception) { }
        try { return element.D.ToString("R", CultureInfo.InvariantCulture); } catch (Exception) { }
        return string.Empty;
    }

    private static string NormalizeName(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string NormalizeMetric(string? metric)
    {
        var value = string.IsNullOrWhiteSpace(metric) ? "use_polarity" : metric.Trim().ToLowerInvariant();
        return value is "use_polarity" or "ignore_global_polarity" or "ignore_local_polarity"
            ? value
            : throw new InvalidOperationException($"Unsupported matching metric '{metric}' (use_polarity|ignore_global_polarity|ignore_local_polarity).");
    }

    private static HashSet<string> ParseLocked(string? locked)
        => (locked ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeName)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string[]? ParseParameterList(string? parameters)
        => string.IsNullOrWhiteSpace(parameters)
            ? null
            : parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
