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

public sealed class TextMatchNode : IVisionNodeExecutor
{
    public string Type => "text.match";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var actual = TextValidationInput.Read(context.TryGetValue("text", out var connectedText) ? connectedText.Require<string>("text") : node.GetString("text", ""), node);
        var expected = TextValidationInput.Read(context.TryGetValue("expected", out var value) ? value.Require<string>("expected") : node.GetString("expected", ""), node);
        var mode = node.GetString("mode", "equals").Trim().ToLowerInvariant() switch
        {
            "equals" => "equals", "contains" => "contains", "startswith" => "startsWith", "endswith" => "endsWith",
            _ => throw new InvalidOperationException("Text match mode must be equals, contains, startsWith or endsWith.")
        };
        var comparison = node.GetBool("ignoreCase", false) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pass = mode switch
        {
            "equals" => actual.Equals(expected, comparison), "contains" => actual.Contains(expected, comparison),
            "startsWith" => actual.StartsWith(expected, comparison), "endsWith" => actual.EndsWith(expected, comparison),
            _ => throw new InvalidOperationException("Text match mode must be equals, contains, startsWith or endsWith.")
        };
        if (!node.GetBool("allowEmpty", false) && (actual.Length == 0 || expected.Length == 0)) pass = false;
        var diag = CoordinateValidation.Diagnostics(Type,
            ("pass", pass), ("mode", mode), ("textLength", actual.Length), ("expectedLength", expected.Length),
            ("ignoreCase", node.GetBool("ignoreCase", false)), ("allowEmpty", node.GetBool("allowEmpty", false)));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pass"] = VisionValue.Boolean(pass), ["text"] = VisionValue.String(actual),
                ["expected"] = VisionValue.String(expected),
                ["mode"] = VisionValue.String(mode),
                ["matchLength"] = VisionValue.Integer(actual.Length),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?> { ["pass"] = pass, ["mode"] = mode, ["textLength"] = actual.Length, ["expectedLength"] = expected.Length, ["matchLength"] = actual.Length, ["diagnostics"] = diag }));
    }
}

public sealed class TextValidationNode : IVisionNodeExecutor
{
    public string Type => "text.validate";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
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
        if (mode == "regex")
        {
            var regex = TextRegexValidation.Execute(actual, node, cancellationToken);
            pass = regex.Pass;
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
            groupsJson = "{}";
            groupCount = 0;
        }

        var diag = CoordinateValidation.Diagnostics(Type, ("pass", pass), ("mode", mode), ("textLength", actual.Length),
            ("expectedLength", expected.Length), ("groupCount", groupCount), ("matchedLength", matchedText.Length));
        var outputs = new Dictionary<string, VisionValue>
        {
            ["pass"] = VisionValue.Boolean(pass), ["text"] = VisionValue.String(actual), ["expected"] = VisionValue.String(expected),
            ["matchedText"] = VisionValue.String(matchedText), ["groupsJson"] = VisionValue.String(groupsJson),
            ["diagnostics"] = VisionValue.String(diag)
        };
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["pass"] = pass, ["mode"] = mode, ["textLength"] = actual.Length, ["expectedLength"] = expected.Length, ["groupCount"] = groupCount, ["diagnostics"] = diag
        }));
    }
}

public sealed class TextRegexNode : IVisionNodeExecutor
{
    public string Type => "text.regex";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

var emptyPolicy = node.GetString("emptyInputPolicy", "fail").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("fail" or "nomatch")) throw new InvalidOperationException("text.regex emptyInputPolicy must be fail or nomatch.");
        var text = TextValidationInput.Read(context.Require<string>("text"), node);
        if (text.Length == 0 && emptyPolicy == "fail")
            throw new InvalidOperationException("text.regex received empty text and emptyInputPolicy=fail.");
        var pattern = node.GetString("pattern", "[A-Z0-9]+");
        if (string.IsNullOrWhiteSpace(pattern)) throw new InvalidOperationException("text.regex pattern must be non-empty.");
        var minMatchedLength = node.GetInt("minMatchedLength", 0);
        if (minMatchedLength < 0) throw new InvalidOperationException("text.regex minMatchedLength must be >= 0.");
        var result = TextRegexValidation.Execute(text, node, cancellationToken, countMatches: true);
        var effectivePass = text.Length > 0 && result.Pass && (minMatchedLength <= 0 || result.MatchedLength >= minMatchedLength);
        var diag = CoordinateValidation.Diagnostics(Type,
            ("pass", effectivePass), ("rawPass", result.Pass), ("fullMatch", result.FullMatch), ("textLength", text.Length),
            ("patternLength", pattern.Length), ("groupCount", result.GroupCount),
            ("matchedLength", result.MatchedLength), ("matchIndex", result.MatchIndex), ("matchCount", result.MatchCount), ("minMatchedLength", minMatchedLength), ("timeoutMs", node.GetInt("timeoutMs", 100)),
            ("ignoreCase", node.GetBool("ignoreCase", false)), ("emptyInputPolicy", emptyPolicy));
        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["pass"] = VisionValue.Boolean(effectivePass),
                ["matchedText"] = VisionValue.String(result.MatchedText),
                ["matchedLength"] = VisionValue.Integer(result.MatchedLength),
                ["matchIndex"] = VisionValue.Integer(result.MatchIndex),
                ["matchCount"] = VisionValue.Integer(result.MatchCount),
                ["groupsJson"] = VisionValue.String(result.GroupsJson),
                ["groupCount"] = VisionValue.Integer(result.GroupCount),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["pass"] = effectivePass, ["fullMatch"] = result.FullMatch, ["textLength"] = text.Length, ["matchedLength"] = result.MatchedLength, ["matchIndex"] = result.MatchIndex, ["matchCount"] = result.MatchCount, ["minMatchedLength"] = minMatchedLength,
                ["groupCount"] = result.GroupCount, ["diagnostics"] = diag
            }));
    }
}
