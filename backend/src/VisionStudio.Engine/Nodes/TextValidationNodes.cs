using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisionStudio.Engine.Nodes;

internal static class TextValidationInput
{
    public static string Read(string text, NodeDefinition node)
    {
        if (text.Length > 65536) throw new InvalidOperationException("Text input exceeds 65536 UTF-16 characters.");
        return node.GetBool("trim", true) ? text.Trim() : text;
    }
}

internal sealed record TextRegexResult(bool Pass, string MatchedText, string GroupsJson, int GroupCount, bool FullMatch, int MatchIndex = -1, int MatchedLength = 0, int MatchCount = 0);

internal static class TextRegexValidation
{
    public static TextRegexResult Execute(string text, NodeDefinition node, CancellationToken cancellationToken, bool countMatches = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pattern = node.GetString("pattern", "[A-Z0-9]+");
        var timeout = node.GetInt("timeoutMs", 100);
        if (pattern.Length > 4096 || timeout is < 1 or > 1000)
            throw new InvalidOperationException("Regex pattern must be at most 4096 characters; timeout must be 1..1000 ms.");
        var options = RegexOptions.CultureInvariant
            | (node.GetBool("ignoreCase", false) ? RegexOptions.IgnoreCase : RegexOptions.None)
            | (node.GetBool("singleline", false) ? RegexOptions.Singleline : RegexOptions.None);
        var full = node.GetBool("fullMatch", true);
        Match match;
        Regex regex;
        var matchCount = 0;
        try
        {
            regex = CreateRegex(pattern, full, options, TimeSpan.FromMilliseconds(timeout));
            if (regex.GetGroupNumbers().Length > 128) throw new InvalidOperationException("Regex group budget exceeded (128).");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            void CheckBudget()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > timeout)
                    throw new InvalidOperationException($"Regex validation exceeded {timeout} ms.");
            }
            match = regex.Match(text);
            CheckBudget();
            matchCount = match.Success ? 1 : 0;
            if (countMatches && !full && match.Success)
            {
                var current = match;
                while (current.Success)
                {
                    CheckBudget();
                    current = current.NextMatch();
                    CheckBudget();
                    if (current.Success) matchCount++;
                }
            }
        }
        catch (ArgumentException ex) { throw new InvalidOperationException("Invalid regular expression: " + ex.Message, ex); }
        catch (RegexMatchTimeoutException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Regex validation exceeded {timeout} ms.", ex);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var pass = match.Success && (node.GetBool("allowEmpty", false) || text.Length > 0);
        var groups = pass ? regex.GetGroupNames().Where(name => match.Groups[name].Success).ToDictionary(name => name, name => match.Groups[name].Value) : new Dictionary<string, string>();
        var json = JsonSerializer.Serialize(groups);
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new InvalidOperationException("Regex group JSON exceeds 1 MiB.");
        return new TextRegexResult(pass, pass ? match.Value : "", json, groups.Count, full, pass ? match.Index : -1, pass ? match.Length : 0, pass ? matchCount : 0);
    }

    private static Regex CreateRegex(string pattern, bool full, RegexOptions options, TimeSpan timeout)
    {
        if (!full) return new Regex(pattern, options, timeout);
        try { return new Regex(@"\A(?:" + pattern + @")\z", options, timeout); }
        catch (ArgumentException)
        {
            // A valid (?x) pattern can end in a line comment, which would consume
            // the closing wrapper. Validate the original before terminating it.
            _ = new Regex(pattern, options, timeout);
            return new Regex(@"\A(?:" + pattern + "\n" + @")\z", options, timeout);
        }
    }
}

/// <summary>
/// 文本校验（原 text.validate + text.match + text.regex 三合一）。
/// 非正则模式按 equals/contains/startsWith/endsWith 比较；正则模式额外提供最短匹配长度与空输入策略。
/// 输出是三个旧节点的并集：pass/text/expected/mode/matchLength/matchedLength/matchIndex/matchCount/groupCount/matchedText/groupsJson。
/// </summary>
public sealed class TextValidationNode : IVisionNodeExecutor
{
    public string Type => "text.validate";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        var actual = TextValidationInput.Read(context.TryGetValue("text", out var connectedText)
            ? connectedText.Require<string>("text") : node.GetString("text", ""), node);
        var mode = node.GetString("mode", "equals").Trim().ToLowerInvariant();
        var expected = mode == "regex" ? string.Empty : TextValidationInput.Read(context.TryGetValue("expected", out var connectedExpected)
            ? connectedExpected.Require<string>("expected") : node.GetString("expected", ""), node);

        bool pass;
        string matchedText;
        string groupsJson;
        int groupCount;
        var matchedLength = 0;
        var matchIndex = -1;
        var matchCount = 0;
        // 原 text.regex 的两个旋钮：空输入策略与最短匹配长度。默认 allow 保持 text.validate 既有语义。
        var emptyPolicy = (node.GetString("emptyInputPolicy", "allow") ?? "allow").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("allow" or "fail" or "nomatch"))
            throw new InvalidOperationException("text.validate emptyInputPolicy must be allow, fail or nomatch.");
        var minMatchedLength = node.GetInt("minMatchedLength", 0);
        if (minMatchedLength < 0) throw new InvalidOperationException("text.validate minMatchedLength must be >= 0.");
        if (mode == "regex")
        {
            if (actual.Length == 0 && emptyPolicy == "fail")
                throw new InvalidOperationException("text.validate received empty text and emptyInputPolicy=fail.");
            // 计数会额外遍历全部匹配，代价高且可能触发回溯超时；只有显式开启才计数（text.regex 兼容路径开启）。
            var countMatches = node.GetBool("countMatches", false);
            var regex = TextRegexValidation.Execute(actual, node, cancellationToken, countMatches);
            matchedLength = regex.MatchedLength;
            matchIndex = regex.MatchIndex;
            matchCount = regex.MatchCount;
            // allow：沿用 TextRegexValidation 的 allowEmpty 语义；nomatch：空输入一律判不合格。
            pass = regex.Pass
                && (minMatchedLength <= 0 || matchedLength >= minMatchedLength)
                && (emptyPolicy != "nomatch" || actual.Length > 0);
            matchedText = regex.MatchedText;
            groupsJson = regex.GroupsJson;
            groupCount = regex.GroupCount;
        }
        else
        {
            var comparison = node.GetBool("ignoreCase", false) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            pass = mode switch
            {
                "equals" => actual.Equals(expected, comparison),
                "contains" => actual.Contains(expected, comparison),
                "startswith" => actual.StartsWith(expected, comparison),
                "endswith" => actual.EndsWith(expected, comparison),
                _ => throw new InvalidOperationException("Text validation mode must be equals, contains, startsWith, endsWith or regex.")
            };
            if (!node.GetBool("allowEmpty", false) && (actual.Length == 0 || expected.Length == 0)) pass = false;
            matchedText = pass ? actual : "";
            matchedLength = matchedText.Length;
            matchCount = pass ? 1 : 0;
            groupsJson = "{}";
            groupCount = 0;
        }

        // 对外仍是旧 text.match 的 camelCase 取值（startsWith / endsWith），下游按字符串分支不受影响。
        var reportedMode = mode switch { "startswith" => "startsWith", "endswith" => "endsWith", _ => mode };
        var diag = CoordinateValidation.Diagnostics(Type, ("pass", pass), ("mode", reportedMode), ("textLength", actual.Length),
            ("expectedLength", expected.Length), ("groupCount", groupCount), ("matchedLength", matchedLength),
            ("matchIndex", matchIndex), ("matchCount", matchCount), ("emptyInputPolicy", emptyPolicy));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["pass"] = VisionValue.Boolean(pass), ["text"] = VisionValue.String(actual), ["expected"] = VisionValue.String(expected),
            ["mode"] = VisionValue.String(reportedMode),
            ["matchLength"] = VisionValue.Integer(mode == "regex" ? matchedLength : actual.Length),
            ["matchedLength"] = VisionValue.Integer(matchedLength),
            ["matchIndex"] = VisionValue.Integer(matchIndex),
            ["matchCount"] = VisionValue.Integer(matchCount),
            ["groupCount"] = VisionValue.Integer(groupCount),
            ["matchedText"] = VisionValue.String(matchedText), ["groupsJson"] = VisionValue.String(groupsJson),
            ["diagnostics"] = VisionValue.String(diag)
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["pass"] = pass, ["mode"] = reportedMode, ["textLength"] = actual.Length, ["expectedLength"] = expected.Length,
            ["groupCount"] = groupCount, ["matchedLength"] = matchedLength, ["matchIndex"] = matchIndex, ["matchCount"] = matchCount, ["diagnostics"] = diag
        }));
    }
}
