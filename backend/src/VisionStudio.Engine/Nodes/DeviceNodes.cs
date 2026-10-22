using System.Diagnostics;
using System.Globalization;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public static class DeviceNodeValidation
{
    public static string RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} must be non-empty.");
        var trimmed = value.Trim();
        if (trimmed.Length > 128) throw new InvalidOperationException($"{name} must be at most 128 characters.");
        return trimmed;
    }
}

public sealed class DeviceReadTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.readTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");

        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "virtual-modbus-1"), "device.readTag deviceId");
        var tagId = DeviceNodeValidation.RequireId(node.GetString("tagId", "trigger"), "device.readTag tagId");
        var fresh = node.GetBool("fresh", false);
        var autoConnect = node.GetBool("autoConnect", true);
        var requireGood = node.GetBool("requireGoodQuality", false);
        var maxAgeMs = node.GetDouble("maxAgeMs", 0);
        if (!double.IsFinite(maxAgeMs) || maxAgeMs < 0) throw new InvalidOperationException("device.readTag maxAgeMs must be finite and >= 0 (0 disables).");
        var rejectNullValue = node.GetBool("rejectNullValue", false);
        DeviceTagSample sample;
        try { sample = await devices.ReadTagAsync(deviceId, tagId, fresh, autoConnect, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new InvalidOperationException($"device.readTag failed for '{deviceId}/{tagId}': {ex.Message}", ex); }
        cancellationToken.ThrowIfCancellationRequested();
        if (requireGood && !string.Equals(sample.Quality.ToString(), "Good", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"device.readTag requireGoodQuality rejected quality '{sample.Quality}' for '{deviceId}/{tagId}'.");
        var ageMs = Math.Max(0d, (DateTimeOffset.UtcNow - sample.Timestamp).TotalMilliseconds);
        if (maxAgeMs > 0 && ageMs > maxAgeMs)
            throw new InvalidOperationException($"device.readTag sample age {ageMs:0} ms exceeds maxAgeMs {maxAgeMs:0} ms for '{deviceId}/{tagId}'.");
        if (rejectNullValue && sample.Value is null)
            throw new InvalidOperationException($"device.readTag rejectNullValue rejected null value for '{deviceId}/{tagId}'.");
        return Result(sample, deviceId, fresh, autoConnect, requireGood, maxAgeMs);
    }

    internal static NodeExecutorResult Result(DeviceTagSample sample, string deviceId, bool? fresh = null, bool? autoConnect = null, bool? requireGoodQuality = null, double? maxAgeMs = null)
    {
        if (sample.Value is double d && !double.IsFinite(d) || sample.Value is float f && !float.IsFinite(f))
            throw new InvalidOperationException($"Device tag '{deviceId}/{sample.TagId}' returned a non-finite numeric value.");
        var tagValue = new VisionDeviceTagValue(deviceId, sample.TagId, sample.DataType.ToString(), sample.Value, sample.Quality.ToString(), sample.Timestamp);
        var boolValue = false;
        var boolValueValid = false;
        try { boolValue = sample.AsBoolean(); boolValueValid = true; } catch (InvalidOperationException) { }
        var numberValue = 0d;
        var numberValueValid = false;
        try { numberValue = sample.AsDouble(); numberValueValid = true; } catch (InvalidOperationException) { }
        var ageMs = Math.Max(0d, (DateTimeOffset.UtcNow - sample.Timestamp).TotalMilliseconds);
        var diag = CoordinateValidation.Diagnostics("device.readTag",
            ("deviceId", deviceId), ("tagId", sample.TagId), ("dataType", sample.DataType.ToString()),
            ("quality", sample.Quality.ToString()), ("fresh", fresh), ("autoConnect", autoConnect),
            ("requireGoodQuality", requireGoodQuality), ("maxAgeMs", maxAgeMs), ("ageMs", ageMs),
            ("hasValue", sample.Value is not null), ("boolValue", boolValue), ("numberValue", numberValue),
            ("boolValueValid", boolValueValid), ("numberValueValid", numberValueValid));
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.DeviceTag(tagValue),
                ["boolValue"] = VisionValue.Boolean(boolValue),
                ["boolValueValid"] = VisionValue.Boolean(boolValueValid),
                ["numberValue"] = VisionValue.Double(numberValue),
                ["numberValueValid"] = VisionValue.Boolean(numberValueValid),
                ["textValue"] = VisionValue.String(sample.AsText()),
                ["quality"] = VisionValue.String(sample.Quality.ToString()),
                ["timestamp"] = VisionValue.String(sample.Timestamp.ToString("O")),
                ["ageMs"] = VisionValue.Double(ageMs),
                ["isGoodQuality"] = VisionValue.Boolean(sample.Quality == DeviceTagQuality.Good),
                ["diagnostics"] = VisionValue.String(diag)
            },
            new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["tagId"] = sample.TagId,
                ["dataType"] = sample.DataType.ToString(),
                ["value"] = sample.Value,
                ["quality"] = sample.Quality.ToString(),
                ["timestamp"] = sample.Timestamp,
                ["boolValueValid"] = boolValueValid,
                ["numberValueValid"] = numberValueValid,
                ["diagnostics"] = diag
            });
    }
}

public sealed class DeviceWriteTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.writeTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "virtual-modbus-1"), "device.writeTag deviceId");
        var tagId = DeviceNodeValidation.RequireId(node.GetString("tagId", "statusText"), "device.writeTag tagId");
        var autoConnect = node.GetBool("autoConnect", true);
        object? value;

        var hasInput = context.TryGetValue("value", out var incoming);
        if (hasInput)
        {
            value = incoming.Value is VisionDeviceTagValue tagged ? tagged.Value : incoming.Value;
        }
        else
        {
            value = ParseFallback(node.GetString("valueType", "String"), node.GetString("fallbackValue", "Vision OK"));
        }

        try
        {
            if (value is null) throw new InvalidOperationException($"device.writeTag value is null for '{deviceId}/{tagId}'.");
            await devices.WriteTagAsync(deviceId, tagId, value, autoConnect, cancellationToken);
            DeviceTagSample sample;
            try
            {
                sample = await devices.ReadTagAsync(deviceId, tagId, fresh: true, autoConnect: autoConnect, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"device.writeTag wrote '{deviceId}/{tagId}' but read-back failed; the write may already be applied. {ex.Message}", ex);
            }
            var result = DeviceReadTagNode.Result(sample, deviceId, fresh: true, autoConnect: autoConnect);
            var diag = CoordinateValidation.Diagnostics(Type,
                ("deviceId", deviceId), ("tagId", tagId), ("operation", "Write"),
                ("quality", sample.Quality.ToString()), ("autoConnect", autoConnect),
                ("valueSource", hasInput ? "input" : "fallback"),
                ("valueType", hasInput ? null : node.GetString("valueType", "String")),
                ("boolValueValid", result.Summary["boolValueValid"]), ("numberValueValid", result.Summary["numberValueValid"]));
            var outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["diagnostics"] = VisionValue.String(diag) };
            return result with
            {
                Outputs = outputs,
                Summary = new Dictionary<string, object?>(result.Summary)
                {
                    ["operation"] = "Write",
                    ["writtenValue"] = sample.Value,
                    ["diagnostics"] = diag
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"device.writeTag failed for '{deviceId}/{tagId}': {ex.Message}", ex);
        }
    }

    private static object ParseFallback(string type, string text)
        => type.Trim().ToLowerInvariant() switch
        {
            "boolean" or "bool" => bool.TryParse(text, out var b)
                ? b
                : text.Trim().Equals("1", StringComparison.Ordinal) || text.Trim().Equals("on", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : text.Trim().Equals("0", StringComparison.Ordinal) || text.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
                        ? false
                        : throw new InvalidOperationException($"device.writeTag fallbackValue '{text}' is not a valid boolean."),
            "integer" or "int" => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : throw new InvalidOperationException($"device.writeTag fallbackValue '{text}' is not a valid integer."),
            "double" or "number" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                ? d
                : throw new InvalidOperationException($"device.writeTag fallbackValue '{text}' is not a finite number."),
            "string" => text,
            _ => throw new InvalidOperationException($"device.writeTag valueType '{type}' is not supported.")
        };
}

public sealed class DeviceWaitTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.waitTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "virtual-modbus-1"), "device.waitTag deviceId");
        var tagId = DeviceNodeValidation.RequireId(node.GetString("tagId", "trigger"), "device.waitTag tagId");
        var op = CoordinateValidation.RequireNonEmpty(node.GetString("operator", "True"), "operator").Trim();
        if (!new[] { "True", "False", "==", "!=", ">", ">=", "<", "<=" }.Contains(op, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"device.waitTag operator '{op}' is not supported.");
        var compare = op.Equals("True", StringComparison.OrdinalIgnoreCase) || op.Equals("False", StringComparison.OrdinalIgnoreCase)
            ? "" : node.GetString("compareValue", "1");
        var timeoutMs = TimingMs(node, "timeoutMs", 5000, 50, 120000);
        var pollMs = TimingMs(node, "pollMs", 25, 10, 5000);
        var stableMs = TimingMs(node, "stableMs", 0, 0, 120000);
        if (pollMs > timeoutMs) throw new InvalidOperationException("device.waitTag pollMs must not exceed timeoutMs.");
        var autoConnect = node.GetBool("autoConnect", true);
        var triggerMode = node.GetString("triggerMode", "Level").Trim();
        var qualityMode = node.GetString("qualityMode", "Any").Trim();
        var onTimeout = node.GetString("onTimeout", "Fail").Trim();
        if (triggerMode.ToLowerInvariant() is not ("level" or "rising" or "falling"))
            throw new InvalidOperationException($"device.waitTag triggerMode '{triggerMode}' is not supported.");
        if (qualityMode.ToLowerInvariant() is not ("any" or "good" or "goodoruncertain"))
            throw new InvalidOperationException($"device.waitTag qualityMode '{qualityMode}' is not supported.");
        if (onTimeout.ToLowerInvariant() is not ("fail" or "continue"))
            throw new InvalidOperationException($"device.waitTag onTimeout '{onTimeout}' is not supported.");
        if (stableMs > timeoutMs)
            throw new InvalidOperationException($"device.waitTag stableMs {stableMs} is greater than timeoutMs {timeoutMs}.");

        var started = Stopwatch.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        DeviceTagSample? last = null;
        bool? previousLevel = null;
        var edgeArmed = false;
        long? stableSince = null;
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                var sample = await devices.ReadTagAsync(deviceId, tagId, fresh: true, autoConnect, timeoutCts.Token);
                timeoutCts.Token.ThrowIfCancellationRequested();
                last = sample;
                if (!QualityAccepted(sample.Quality, qualityMode))
                {
                    stableSince = null;
                    await Task.Delay(pollMs, timeoutCts.Token);
                    continue;
                }

                var level = Matches(sample, op, compare);
                var armed = Arm(triggerMode, level, previousLevel, ref edgeArmed);
                previousLevel = level;
                if (!armed)
                {
                    stableSince = null;
                    await Task.Delay(pollMs, timeoutCts.Token);
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                stableSince ??= now;
                if (Stopwatch.GetElapsedTime(stableSince.Value, now).TotalMilliseconds >= stableMs)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    return Matched(sample, deviceId, op, compare, triggerMode, qualityMode, Stopwatch.GetElapsedTime(started, now).TotalMilliseconds);
                }
                await Task.Delay(pollMs, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (onTimeout.Equals("Continue", StringComparison.OrdinalIgnoreCase))
                return TimedOut(deviceId, tagId, last, op, compare, triggerMode, qualityMode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new TimeoutException($"Timed out waiting for device '{deviceId}' tag '{tagId}' after {timeoutMs:0} ms.");
        }
    }

    private static int TimingMs(NodeDefinition node, string name, int fallback, int minimum, int maximum)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value < minimum || value > maximum || value != Math.Truncate(value))
            throw new InvalidOperationException($"device.waitTag {name} must be an integer between {minimum} and {maximum} ms.");
        return (int)value;
    }

    private static bool Arm(string triggerMode, bool level, bool? previousLevel, ref bool edgeArmed)
    {
        switch (triggerMode.ToLowerInvariant())
        {
            case "rising":
                if (previousLevel == false && level) edgeArmed = true;
                if (!level) edgeArmed = false;
                return edgeArmed && level;
            case "falling":
                if (previousLevel == true && !level) edgeArmed = true;
                if (level) edgeArmed = false;
                return edgeArmed && !level;
            default:
                return level;
        }
    }

    private static bool QualityAccepted(DeviceTagQuality quality, string qualityMode)
        => qualityMode.ToLowerInvariant() switch
        {
            "good" => quality == DeviceTagQuality.Good,
            "goodoruncertain" => quality is DeviceTagQuality.Good or DeviceTagQuality.Uncertain,
            _ => true
        };

    private static NodeExecutorResult Matched(DeviceTagSample sample, string deviceId, string op, string compare, string triggerMode, string qualityMode, double waitMs)
    {
        var result = DeviceReadTagNode.Result(sample, deviceId);
        var diag = WaitDiagnostics(result, op, compare, triggerMode, qualityMode, waitMs, true);
        var outputs = new Dictionary<string, VisionValue>(result.Outputs)
        {
            ["matched"] = VisionValue.Boolean(true),
            ["waitMs"] = VisionValue.Double(waitMs),
            ["diagnostics"] = VisionValue.String(diag)
        };
        var summary = new Dictionary<string, object?>(result.Summary)
        {
            ["operator"] = op,
            ["compareValue"] = compare,
            ["triggerMode"] = triggerMode,
            ["qualityMode"] = qualityMode,
            ["waitMs"] = Math.Round(waitMs, 2),
            ["matched"] = true,
            ["diagnostics"] = diag
        };
        return new NodeExecutorResult(outputs, summary);
    }

    private static NodeExecutorResult TimedOut(string deviceId, string tagId, DeviceTagSample? last, string op, string compare, string triggerMode, string qualityMode, double waitMs)
    {
        var result = last is null
            ? DeviceReadTagNode.Result(new DeviceTagSample(deviceId, tagId, DeviceTagDataType.String, null, DeviceTagQuality.Disconnected, DateTimeOffset.UtcNow, "Timeout"), deviceId)
            : DeviceReadTagNode.Result(last, deviceId);
        var diag = WaitDiagnostics(result, op, compare, triggerMode, qualityMode, waitMs, false, last is null ? "Timeout" : null);
        var outputs = new Dictionary<string, VisionValue>(result.Outputs)
        {
            ["matched"] = VisionValue.Boolean(false),
            ["waitMs"] = VisionValue.Double(waitMs),
            ["diagnostics"] = VisionValue.String(diag)
        };
        if (last is null) outputs["quality"] = VisionValue.String("Timeout");
        var summary = new Dictionary<string, object?>(result.Summary)
        {
            ["operator"] = op,
            ["compareValue"] = compare,
            ["triggerMode"] = triggerMode,
            ["qualityMode"] = qualityMode,
            ["onTimeout"] = "Continue",
            ["waitMs"] = Math.Round(waitMs, 2),
            ["matched"] = false,
            ["diagnostics"] = diag
        };
        if (last is null) summary["quality"] = "Timeout";
        return new NodeExecutorResult(outputs, summary);
    }

    private static string WaitDiagnostics(NodeExecutorResult result, string op, string compare, string triggerMode, string qualityMode, double waitMs, bool matched, string? quality = null)
        => CoordinateValidation.Diagnostics("device.waitTag",
            ("deviceId", result.Summary["deviceId"]), ("tagId", result.Summary["tagId"]),
            ("dataType", result.Summary["dataType"]), ("quality", quality ?? result.Summary["quality"]),
            ("operator", op), ("compareValue", compare), ("triggerMode", triggerMode), ("qualityMode", qualityMode),
            ("waitMs", waitMs), ("matched", matched),
            ("boolValueValid", result.Summary["boolValueValid"]), ("numberValueValid", result.Summary["numberValueValid"]));

    private static bool Matches(DeviceTagSample sample, string op, string compare)
    {
        if (op.Equals("True", StringComparison.OrdinalIgnoreCase)) return sample.AsBoolean();
        if (op.Equals("False", StringComparison.OrdinalIgnoreCase)) return !sample.AsBoolean();
        if (sample.DataType == DeviceTagDataType.String)
        {
            var actual = sample.AsText();
            return op switch
            {
                "==" => string.Equals(actual, compare, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(actual, compare, StringComparison.OrdinalIgnoreCase),
                _ => throw new InvalidOperationException($"device.waitTag operator '{op}' cannot compare a string tag; use == or !=.")
            };
        }

        if (sample.DataType == DeviceTagDataType.Integer)
        {
            if (!decimal.TryParse(compare, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedInteger))
                throw new InvalidOperationException($"device.waitTag compareValue '{compare}' is not a valid decimal for an integer tag.");
            var comparison = ((decimal)sample.AsInteger()).CompareTo(expectedInteger);
            return op switch
            {
                "==" => comparison == 0,
                "!=" => comparison != 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => false
            };
        }

        var number = sample.AsDouble();
        if (!double.TryParse(compare, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected) || !double.IsFinite(expected))
            throw new InvalidOperationException($"device.waitTag compareValue '{compare}' is not a finite number for operator '{op}'.");
        return op switch
        {
            "==" => Math.Abs(number - expected) < 1e-9,
            "!=" => Math.Abs(number - expected) >= 1e-9,
            ">" => number > expected,
            ">=" => number >= expected,
            "<" => number < expected,
            "<=" => number <= expected,
            _ => false
        };
    }
}

public sealed class DeviceWriteVisionResultNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.writeVisionResult";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null) throw new ArgumentNullException(nameof(context));

        var deviceId = DeviceNodeValidation.RequireId(node.GetString("deviceId", "virtual-modbus-1"), "device.writeVisionResult deviceId");
        var pass = context.Require<bool>("pass");
        var missingPolicy = node.GetString("missingCoordinatePolicy", "skip").Trim().ToLowerInvariant();
        if (missingPolicy is not ("skip" or "fail" or "zero")) throw new InvalidOperationException("缺失坐标策略必须为 skip、fail 或 zero。");
        var autoConnect = node.GetBool("autoConnect", true);

        var readyTag = DeviceNodeValidation.RequireId(node.GetString("resultReadyTag", "resultReady"), "resultReadyTag");
        var okTag = DeviceNodeValidation.RequireId(node.GetString("resultOkTag", "resultOk"), "resultOkTag");
        var writes = new List<(string, object?)> { (readyTag, false), (okTag, pass) };
        var writtenCoordinates = new Dictionary<string, double>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { readyTag };
        if (!tags.Add(okTag)) throw new InvalidOperationException("Ready 和 OK 标签不能相同。");
        foreach (var field in new[] { "x", "y", "r" })
        {
            var tag = node.GetString(field + "Tag", "result" + field.ToUpperInvariant()).Trim();
            if (tag.Length == 0) { skipped.Add(field + ":disabled"); continue; }
            tag = DeviceNodeValidation.RequireId(tag, field + "Tag");
            if (!tags.Add(tag)) throw new InvalidOperationException($"PLC 结果标签重复：{tag}。请为各字段配置独立标签。");
            var connected = context.TryGetValue(field, out _);
            if (!connected && missingPolicy == "fail") throw new InvalidOperationException($"{field} 标签已启用但坐标未接入；请连接坐标、清空标签或调整缺失坐标策略。");
            if (!connected && missingPolicy == "skip") { skipped.Add(field + ":missing"); continue; }
            var coordinate = connected ? context.RequireNumber(field) : 0d;
            if (!double.IsFinite(coordinate)) throw new InvalidOperationException($"PLC 坐标 {field} 必须是有限数值。");
            writes.Add((tag, coordinate)); writtenCoordinates[field] = coordinate;
        }
        writes.Add((readyTag, true));

        // One mutually exclusive device command: lower Ready, write payload, raise Ready last.
        try
        {
            await devices.WriteSequenceAsync(
                deviceId,
                writes,
                autoConnect,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"device.writeVisionResult failed for '{deviceId}': {ex.Message}", ex);
        }

        var diag = CoordinateValidation.Diagnostics(Type,
            ("deviceId", deviceId), ("pass", pass), ("coordinatesWritten", writtenCoordinates), ("skippedCoordinates", skipped), ("missingCoordinatePolicy", missingPolicy),
            ("autoConnect", autoConnect), ("readyTag", readyTag), ("okTag", okTag), ("tagsWritten", writes.Count));
        var outputs = new Dictionary<string, VisionValue>
            {
                ["written"] = VisionValue.Boolean(true),
                ["pass"] = VisionValue.Boolean(pass),
                ["xWritten"] = VisionValue.Boolean(writtenCoordinates.ContainsKey("x")),
                ["yWritten"] = VisionValue.Boolean(writtenCoordinates.ContainsKey("y")),
                ["rWritten"] = VisionValue.Boolean(writtenCoordinates.ContainsKey("r")),
                ["tagsWritten"] = VisionValue.Integer(writes.Count),
                ["diagnostics"] = VisionValue.String(diag)
            };
        foreach (var (field, coordinate) in writtenCoordinates) outputs[field] = VisionValue.Double(coordinate);
        return new NodeExecutorResult(outputs,
            new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["resultReadyTag"] = readyTag,
                ["pass"] = pass,
                ["coordinatesWritten"] = writtenCoordinates,
                ["skippedCoordinates"] = skipped,
                ["tagsWritten"] = writes.Count,
                ["writeOrder"] = $"{readyTag}=0 -> payload -> {readyTag}=1",
                ["diagnostics"] = diag
            });
    }
}

