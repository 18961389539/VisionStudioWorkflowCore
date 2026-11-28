using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 缺陷聚类与分级（defect.cluster / defect.classify，2026-10-27 · 缺口分析 P1-7）。
///
/// 现场缺口：`measure.blobs` / `defect.referenceCompare` 出来的是"几百个小斑点"，
/// 而图纸和客户判定的是"这条划痕"。两件事此前都要在上位机做：
/// 1) 把挨在一起的斑点并成一个缺陷（聚类）——聚类前 300 个斑点在报表里是 300 个 NG，
///    聚类后是 2 条划痕，人工复判量差两个数量级；
/// 2) 按面积/长宽比/圆度把缺陷分成划痕/凹坑/脏污（分级），不同类别走不同的处置。
///
/// 设计取舍：
/// - 聚类用"外接矩形间隙 ≤ linkDistance"而不是圆心距离：划痕是长条形，两个端点相距很远
///   但本体相连，用圆心距离会把同一条划痕拆成两段。
/// - 合并后的周长取成员周长之和（不是并集轮廓的真实周长），因此 circularity 只作粗筛；
///   要精确形状请把 mask 传回 region/contour 节点重算。这个口径写进目录描述，避免误用。
/// - 分级是"有序规则表 + 首条命中"，与 PLC 配方表的心智一致；不做隐式打分排序。
/// </summary>
public sealed class DefectClusterNode : IVisionNodeExecutor
{
    public string Type => "defect.cluster";

    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var linkDistance = node.GetDouble("linkDistancePx", 5);
        if (!double.IsFinite(linkDistance) || linkDistance is < 0 or > 10000)
            throw new InvalidOperationException("defect.cluster linkDistancePx must be finite within 0..10000.");
        var maxDetections = node.GetInt("maxDetections", 5000);
        if (maxDetections is < 1 or > 50000)
            throw new InvalidOperationException("defect.cluster maxDetections must be within 1..50000.");
        var maxMembers = node.GetInt("maxMembers", 0);
        var maxSpan = node.GetDouble("maxSpanPx", 0);
        var limitPolicy = (node.GetString("limitPolicy", "fail") ?? "fail").Trim().ToLowerInvariant();
        if (maxMembers is < 0 or > 50000) throw new InvalidOperationException("defect.cluster maxMembers must be within 0..50000 (0 = unbounded).");
        if (!double.IsFinite(maxSpan) || maxSpan is < 0 or > 1e9) throw new InvalidOperationException("defect.cluster maxSpanPx must be within 0..1e9 (0 = unbounded).");
        if (limitPolicy is not ("fail" or "split")) throw new InvalidOperationException("defect.cluster limitPolicy must be fail or split.");

        var input = context.Require<IReadOnlyList<VisionDetection>>("detections");
        if (input.Count > maxDetections)
            throw new InvalidOperationException(
                $"defect.cluster received {input.Count} detections above maxDetections={maxDetections}; cluster upstream or raise the limit.");

        var parent = new int[input.Count];
        for (var index = 0; index < parent.Length; index++) parent[index] = index;

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        void Union(int left, int right)
        {
            var a = Find(left);
            var b = Find(right);
            if (a != b) parent[b] = a;
        }

        // 扫描线加速：按左边界排序，只与"左边界仍在当前右边界 + linkDistance 之内"的后续检测比较。
        // 用外接矩形（不是圆心）分桶会把相距 2px 但分处两格的两条划痕漏掉，所以这里按 x 扫描，
        // 任意可能合并的一对必然满足 X 邻接条件，复杂度 O(n log n + k)。
        if (linkDistance >= 0)
        {
            var order = Enumerable.Range(0, input.Count)
                .OrderBy(index => input[index].BoundingBox.X)
                .ThenBy(index => input[index].BoundingBox.Y)
                .ToArray();
            for (var position = 0; position < order.Length; position++)
            {
                var index = order[position];
                var limit = input[index].BoundingBox.X + input[index].BoundingBox.Width + linkDistance;
                for (var next = position + 1; next < order.Length; next++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var other = order[next];
                    if (input[other].BoundingBox.X > limit) break; // 后面的更靠右，不可能再相连
                    if (RectGap(input[index], input[other]) <= linkDistance) Union(index, other);
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (var index = 0; index < input.Count; index++)
        {
            var root = Find(index);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(index);
        }

        var splitCount = 0;
        var boundedGroups = new List<List<int>>();
        foreach (var group in groups.Values)
        {
            var groupMinX = group.Min(i => input[i].BoundingBox.X);
            var groupMinY = group.Min(i => input[i].BoundingBox.Y);
            var groupMaxX = group.Max(i => input[i].BoundingBox.X + input[i].BoundingBox.Width);
            var groupMaxY = group.Max(i => input[i].BoundingBox.Y + input[i].BoundingBox.Height);
            var oversized = maxMembers > 0 && group.Count > maxMembers
                || maxSpan > 0 && Math.Max(groupMaxX - groupMinX, groupMaxY - groupMinY) > maxSpan;
            if (!oversized) { boundedGroups.Add(group); continue; }
            if (limitPolicy == "fail")
                throw new InvalidOperationException($"defect.cluster cluster exceeds maxMembers={maxMembers} or maxSpanPx={maxSpan}; set limitPolicy=split to bound it.");
            splitCount++;
            var chunk = new List<int>();
            void Flush() { if (chunk.Count > 0) { boundedGroups.Add(chunk.OrderBy(i => i).ToList()); chunk = []; } }
            foreach (var member in group.OrderBy(i => input[i].BoundingBox.X).ThenBy(i => input[i].BoundingBox.Y).ThenBy(i => i))
            {
                if (maxSpan > 0 && Math.Max(input[member].Width, input[member].Height) > maxSpan)
                    throw new InvalidOperationException($"defect.cluster detection {member} alone exceeds maxSpanPx={maxSpan}.");
                chunk.Add(member);
                var minX = chunk.Min(i => input[i].BoundingBox.X);
                var minY = chunk.Min(i => input[i].BoundingBox.Y);
                var maxX = chunk.Max(i => input[i].BoundingBox.X + input[i].BoundingBox.Width);
                var maxY = chunk.Max(i => input[i].BoundingBox.Y + input[i].BoundingBox.Height);
                if (maxMembers > 0 && chunk.Count > maxMembers || maxSpan > 0 && Math.Max(maxX - minX, maxY - minY) > maxSpan)
                {
                    chunk.RemoveAt(chunk.Count - 1); Flush(); chunk.Add(member);
                }
            }
            Flush();
        }

        var merged = new List<VisionDetection>(boundedGroups.Count);
        var clusterTrace = new List<object>();
        foreach (var members in boundedGroups)
        {
            var first = input[members[0]];
            var minX = members.Min(i => input[i].BoundingBox.X);
            var minY = members.Min(i => input[i].BoundingBox.Y);
            var maxX = members.Max(i => input[i].BoundingBox.X + input[i].BoundingBox.Width);
            var maxY = members.Max(i => input[i].BoundingBox.Y + input[i].BoundingBox.Height);
            var width = maxX - minX;
            var height = maxY - minY;
            var area = members.Sum(i => input[i].Area);
            var perimeter = members.Sum(i => input[i].Perimeter);
            var circularity = perimeter > 0 && area > 0 ? 4 * Math.PI * area / (perimeter * perimeter) : 0;
            var confidence = members.Max(i => input[i].Confidence ?? 0);
            var hasConfidence = members.Any(i => input[i].Confidence is not null);
            // 只有成员同属一个类别时才保留类别；混类会掩盖真实缺陷类型。
            var classIds = members.Select(i => input[i].ClassId).Distinct().ToArray();
            var classId = classIds.Length == 1 ? classIds[0] : null;

            merged.Add(new VisionDetection(
                merged.Count, minX, minY, width, height, area, perimeter, circularity,
                new VisionCircle(minX + width / 2, minY + height / 2, Math.Sqrt(width * width + height * height) / 2),
                new VisionRectangle2D(minX, minY, width, height),
                hasConfidence ? confidence : null,
                classId));
            clusterTrace.Add(new
            {
                index = merged.Count - 1, memberIndices = members, memberClassIds = classIds,
                mixedClass = classIds.Length > 1, classId,
                boundingBox = new { x = minX, y = minY, width, height }
            });
        }

        var largest = merged.Count == 0 ? 0d : merged.Max(d => d.Area);
        var clustersJson = JsonSerializer.Serialize(clusterTrace, JsonText);
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("inputCount", input.Count), ("clusterCount", merged.Count),
            ("linkDistancePx", linkDistance), ("largestArea", largest),
            ("maxMembers", maxMembers), ("maxSpanPx", maxSpan), ("limitPolicy", limitPolicy),
            ("splitClusterCount", splitCount), ("clusters", clustersJson));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["detections"] = VisionValue.Detections(merged),
                ["count"] = VisionValue.Integer(merged.Count),
                ["mergedCount"] = VisionValue.Integer(input.Count - merged.Count),
                ["inputCount"] = VisionValue.Integer(input.Count),
                ["largestArea"] = VisionValue.Double(largest),
                ["clustersJson"] = VisionValue.String(clustersJson),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["inputCount"] = input.Count, ["clusterCount"] = merged.Count,
                ["mergedCount"] = input.Count - merged.Count, ["linkDistancePx"] = linkDistance,
                ["maxMembers"] = maxMembers, ["maxSpanPx"] = maxSpan, ["limitPolicy"] = limitPolicy,
                ["splitClusterCount"] = splitCount, ["clustersJson"] = clustersJson,
                ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>两个外接矩形之间的最短间隙（重叠时为 0）。</summary>
    private static double RectGap(VisionDetection left, VisionDetection right)
    {
        var dx = Math.Max(0, Math.Max(left.BoundingBox.X - (right.BoundingBox.X + right.BoundingBox.Width),
            right.BoundingBox.X - (left.BoundingBox.X + left.BoundingBox.Width)));
        var dy = Math.Max(0, Math.Max(left.BoundingBox.Y - (right.BoundingBox.Y + right.BoundingBox.Height),
            right.BoundingBox.Y - (left.BoundingBox.Y + left.BoundingBox.Height)));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// 缺陷分级（defect.classify）：按有序规则表把每个检测归到一类，首条命中生效。
/// </summary>
public sealed class DefectClassifyNode : IVisionNodeExecutor
{
    public string Type => "defect.classify";

    private sealed record Rule(
        string Name, int ClassId,
        double MinArea, double MaxArea,
        double MinAspect, double MaxAspect,
        double MinCircularity, double MaxCircularity);

    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var rulesJson = node.GetString("rulesJson", DefaultRules) ?? DefaultRules;
        var rules = ParseRules(rulesJson);
        var rulesHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rulesJson))).ToLowerInvariant();
        var rejectUnclassified = node.GetBool("rejectUnclassified", true);

        var input = context.Require<IReadOnlyList<VisionDetection>>("detections");
        var classified = new List<VisionDetection>(input.Count);
        var counts = new Dictionary<int, (string Name, int Count)>();
        var unclassified = 0;
        var detectionDiagnostics = new List<object>();

        for (var index = 0; index < input.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detection = input[index];
            var aspect = detection.Width > 0 && detection.Height > 0
                ? Math.Max(detection.Width, detection.Height) / Math.Min(detection.Width, detection.Height)
                : double.PositiveInfinity;

            Rule? match = null;
            var failedBoundaries = new List<object>();
            var shadowedRules = new List<int>();
            for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                var rule = rules[ruleIndex];
                var failures = new List<string>();
                void Boundary(string name, double actual, double bound, bool lower)
                {
                    if (lower && actual < bound || !lower && actual > bound)
                        failures.Add($"{name}{(lower ? ">=" : "<=")}{bound:0.####} (actual={actual:0.####})");
                }
                if (rule.MinArea > 0) Boundary("minArea", detection.Area, rule.MinArea, true);
                if (rule.MaxArea > 0) Boundary("maxArea", detection.Area, rule.MaxArea, false);
                if (rule.MinAspect > 0) Boundary("minAspect", aspect, rule.MinAspect, true);
                if (rule.MaxAspect > 0) Boundary("maxAspect", aspect, rule.MaxAspect, false);
                if (rule.MinCircularity > 0) Boundary("minCircularity", detection.Circularity, rule.MinCircularity, true);
                if (rule.MaxCircularity > 0) Boundary("maxCircularity", detection.Circularity, rule.MaxCircularity, false);
                if (failures.Count > 0) { failedBoundaries.Add(new { ruleIndex, classId = rule.ClassId, failures }); continue; }
                if (match is null) match = rule;
                else shadowedRules.Add(ruleIndex);
            }
            detectionDiagnostics.Add(new
            {
                detectionIndex = index, ruleIndex = match is null ? (int?)null : rules.IndexOf(match),
                classId = match?.ClassId ?? 0, shadowedRules, failedBoundaries
            });

            if (match is null)
            {
                unclassified++;
                // 未命中：清空类别（0 = 未分类），保证下游不会读到上一步的残留类别。
                classified.Add(detection with { Index = index, ClassId = 0 });
                continue;
            }

            classified.Add(detection with { Index = index, ClassId = match.ClassId });
            var previous = counts.TryGetValue(match.ClassId, out var entry) ? entry.Count : 0;
            counts[match.ClassId] = (match.Name, previous + 1);
        }

        var ngCount = counts.Values.Sum(entry => entry.Count) + (rejectUnclassified ? unclassified : 0);
        var classCountsJson = JsonSerializer.Serialize(
            counts.OrderBy(pair => pair.Key)
                .Select(pair => new { classId = pair.Key, name = pair.Value.Name, count = pair.Value.Count })
                .Append(new { classId = 0, name = "unclassified", count = unclassified }),
            JsonText);

        var detectionDiagnosticsJson = JsonSerializer.Serialize(detectionDiagnostics, JsonText);
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("count", input.Count), ("classCount", counts.Count), ("unclassified", unclassified),
            ("ngCount", ngCount), ("rejectUnclassified", rejectUnclassified),
            ("unclassifiedClassId", 0), ("rulesHash", rulesHash), ("detections", detectionDiagnosticsJson));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["detections"] = VisionValue.Detections(classified),
                ["count"] = VisionValue.Integer(input.Count),
                ["ngCount"] = VisionValue.Integer(ngCount),
                ["ok"] = VisionValue.Boolean(ngCount == 0),
                ["unclassifiedCount"] = VisionValue.Integer(unclassified),
                ["classCountsJson"] = VisionValue.String(classCountsJson),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["count"] = input.Count, ["classCount"] = counts.Count, ["unclassified"] = unclassified,
                ["ngCount"] = ngCount, ["rejectUnclassified"] = rejectUnclassified,
                ["rulesHash"] = rulesHash, ["detectionDiagnosticsJson"] = detectionDiagnosticsJson,
                ["diagnostics"] = diagnostics
            }));
    }

    private const string DefaultRules =
        """[{"name":"scratch","classId":1,"minAspect":3,"minArea":50},{"name":"pit","classId":2,"maxArea":500,"minCircularity":0.6},{"name":"stain","classId":3,"minArea":500}]""";

    private static List<Rule> ParseRules(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("defect.classify rulesJson is required.");

        List<JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<JsonElement>>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"defect.classify rulesJson is invalid: {ex.Message}", ex);
        }
        if (parsed is null || parsed.Count == 0)
            throw new InvalidOperationException("defect.classify rulesJson must contain at least one rule.");
        if (parsed.Count > 64)
            throw new InvalidOperationException("defect.classify accepts at most 64 rules.");

        var rules = new List<Rule>(parsed.Count);
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in parsed)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("defect.classify every rule must be a JSON object.");

            var name = element.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("defect.classify every rule needs a non-empty name.");
            if (name.Length > 64)
                throw new InvalidOperationException($"defect.classify rule name '{name}' is longer than 64 characters.");
            if (!names.Add(name))
                throw new InvalidOperationException($"defect.classify rule name '{name}' is duplicated.");

            var classId = element.TryGetProperty("classId", out var idValue) ? idValue.GetInt32() : rules.Count + 1;
            if (classId is < 1 or > 10000)
                throw new InvalidOperationException($"defect.classify rule '{name}' classId must be within 1..10000.");
            if (!ids.Add(classId))
                throw new InvalidOperationException($"defect.classify classId {classId} is used by more than one rule.");

            double Read(string property)
            {
                if (!element.TryGetProperty(property, out var value)) return 0;
                if (value.ValueKind != JsonValueKind.Number)
                    throw new InvalidOperationException($"defect.classify rule '{name}' field '{property}' must be a number.");
                var number = value.GetDouble();
                if (!double.IsFinite(number) || number < 0)
                    throw new InvalidOperationException($"defect.classify rule '{name}' field '{property}' must be finite and >= 0.");
                return number;
            }

            var minArea = Read("minArea");
            var maxArea = Read("maxArea");
            if (maxArea > 0 && minArea > maxArea)
                throw new InvalidOperationException($"defect.classify rule '{name}' has minArea > maxArea.");
            rules.Add(new Rule(name, classId, minArea, maxArea,
                Read("minAspect"), Read("maxAspect"), Read("minCircularity"), Read("maxCircularity")));
        }
        return rules;
    }
}
