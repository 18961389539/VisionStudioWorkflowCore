using System.Text;
using System.Text.Json;

namespace VisionStudio.Engine.Nodes;

internal static class ResultFileWriter
{
    internal static async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedPath = SaveImageNode.RequirePath(node, "artifacts/result.json");
        var format = Choice(node, "format", "json", "json", "csv");
        // Old recipes used overwrite; the visible conflict policy is the sole control for new recipes.
        var policy = node.Parameters?.ContainsKey("overwrite") == true
            ? node.GetBool("overwrite", false) ? "overwrite" : "fail"
            : Choice(node, "conflictPolicy", "fail", "fail", "overwrite", "suffix");
        var newline = Choice(node, "newline", "lf", "lf", "crlf") == "lf" ? "\n" : "\r\n";
        var encoding = new UTF8Encoding(Choice(node, "encoding", "utf8", "utf8", "utf8bom") == "utf8bom");
        var value = context.RequireValue("value");
        var serializable = value.Value switch
        {
            IVisionImage image => (object)new { image.Width, image.Height },
            IVisionFrameSet frames => new { frames.GroupId, frames.TimestampBasis, frames.TriggerSkewUs, frames.WithinTolerance, cameras = frames.Frames.Keys.Order().ToArray() },
            _ => value.Value
        };
        var text = format == "json"
            ? JsonSerializer.Serialize(new { type = value.Type.ToString(), value = serializable }, new JsonSerializerOptions { WriteIndented = node.GetBool("pretty", false) })
            : "type,value" + newline + Csv(value.Type.ToString()) + "," + Csv(JsonSerializer.Serialize(serializable));
        text = text.Replace("\r\n", "\n").Replace("\n", newline);
        if (node.GetBool("appendNewline", true)) text += newline;
        var payload = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var budget = Integer(node, "maxBytes", 10485760, 64, 67108864);
        if (budget is < 64 or > 67108864 || payload.Length > budget)
            throw new InvalidOperationException("Export exceeds maxBytes or the budget is outside 64..67108864.");
        var directory = Path.GetDirectoryName(requestedPath)!;
        if (node.GetBool("createDirectories", true)) Directory.CreateDirectory(directory);
        else if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Export directory does not exist; enable Create Directories.");
        var level = NodeCompletionGuards.DiagnosticsLevel(node);
        var attempts = NodeCompletionGuards.RetryAttempts(NodeCompletionGuards.RetryPolicy(node));
        string? path = null;
        var usedAttempts = 0;
        for (var attempt = 1; path is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temp = SaveImageNode.CreateTempPath(requestedPath);
            try
            {
                usedAttempts = attempt;
                await File.WriteAllBytesAsync(temp, payload, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                for (var suffix = 0; ; suffix++)
                {
                    var candidate = suffix == 0 ? requestedPath : Path.Combine(directory,
                        Path.GetFileNameWithoutExtension(requestedPath) + $"_{suffix}" + Path.GetExtension(requestedPath));
                    try { File.Move(temp, candidate, policy == "overwrite"); path = candidate; break; }
                    catch (IOException) when (policy == "suffix" && File.Exists(candidate) && suffix < 10000) { }
                }
            }
            catch (IOException) when (attempt < attempts && (policy != "fail" || !File.Exists(requestedPath)))
            {
                await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
            }
            finally { SaveImageNode.TryDelete(temp); }
        }
        var diagnostics = NodeCompletionGuards.BuildDiagnostics(node.Type, level, 0, usedAttempts,
            ("path", path), ("format", format), ("bytesWritten", payload.Length), ("conflictPolicy", policy));
        return new(new Dictionary<string, VisionValue>
        {
            ["path"] = VisionValue.String(path), ["bytesWritten"] = VisionValue.Integer(payload.Length),
            ["format"] = VisionValue.String(format), ["valueType"] = VisionValue.String(value.Type.ToString()),
            ["warningCount"] = VisionValue.Integer(0), ["attemptCount"] = VisionValue.Integer(usedAttempts), ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?> { ["path"] = path, ["format"] = format, ["bytesWritten"] = payload.Length,
            ["attemptCount"] = usedAttempts, ["diagnostics"] = diagnostics });
    }

    internal static string Choice(NodeDefinition node, string name, string fallback, params string[] choices)
    {
        var value = node.GetString(name, fallback).Trim();
        return choices.FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{name} must be {string.Join('|', choices)}.");
    }
    internal static int Integer(NodeDefinition node, string name, int fallback, int min, int max)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < min || value > max)
            throw new InvalidOperationException($"{name} must be an integer in {min}..{max}.");
        return (int)value;
    }
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
