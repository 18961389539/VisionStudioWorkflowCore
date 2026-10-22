using System.Text.Json;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Shared diagnostics and retry-policy helpers for executors that emit structured diagnostics
/// or implement real retry budgets (file writers, HTTP output).
/// </summary>
public static class NodeCompletionGuards
{
    public static string DiagnosticsLevel(NodeDefinition node)
    {
        var level = CoordinateValidation.RequireNonEmpty(node.GetString("diagnosticsLevel", "standard"), "diagnosticsLevel").ToLowerInvariant();
        if (level is not ("minimal" or "standard" or "verbose"))
            throw new InvalidOperationException("diagnosticsLevel must be minimal, standard, or verbose.");
        return level;
    }

    public static string RetryPolicy(NodeDefinition node)
    {
        var policy = CoordinateValidation.RequireNonEmpty(node.GetString("retryPolicy", "none"), "retryPolicy").ToLowerInvariant();
        if (policy is not ("none" or "once" or "backoff"))
            throw new InvalidOperationException("retryPolicy must be none, once, or backoff.");
        return policy;
    }

    public static int RetryAttempts(string policy) => policy switch
    {
        "none" => 1,
        "once" => 2,
        "backoff" => 3,
        _ => throw new InvalidOperationException($"Unsupported retryPolicy '{policy}'.")
    };

    public static string BuildDiagnostics(string nodeType, string level, int warningCount, int attemptCount, params (string Key, object? Value)[] fields)
    {
        CoordinateValidation.RequireFinite(warningCount, "warningCount");
        CoordinateValidation.RequireFinite(attemptCount, "attemptCount");
        if (warningCount < 0 || attemptCount < 1)
            throw new InvalidOperationException("warningCount must be >= 0 and attemptCount >= 1.");
        var payload = new Dictionary<string, object?>
        {
            ["node"] = nodeType,
            ["valid"] = true,
            ["diagnosticsLevel"] = level,
            ["warningCount"] = warningCount,
            ["attemptCount"] = attemptCount
        };
        if (level is "standard" or "verbose")
        {
            foreach (var (key, value) in fields)
                payload[key] = value is double d ? (double.IsFinite(d) ? Math.Round(d, 6) : d.ToString()) : value;
        }
        if (level == "verbose")
            payload["guard"] = "NodeCompletionGuards";
        return JsonSerializer.Serialize(payload);
    }


    public static int CountWarnings(params bool[] flags) => flags.Count(f => f);
}
