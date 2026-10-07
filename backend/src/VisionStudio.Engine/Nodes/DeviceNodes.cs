using VisionStudio.Engine.Device;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

public sealed class DeviceReadTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.readTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var deviceId = node.GetString("deviceId", "virtual-modbus-1");
        var tagId = node.GetString("tagId", "trigger");
        var fresh = node.GetBool("fresh", false);
        var autoConnect = node.GetBool("autoConnect", true);
        var sample = await devices.ReadTagAsync(deviceId, tagId, fresh, autoConnect, cancellationToken);
        return Result(sample, deviceId);
    }

    internal static NodeExecutorResult Result(DeviceTagSample sample, string deviceId)
    {
        var tagValue = new VisionDeviceTagValue(deviceId, sample.TagId, sample.DataType.ToString(), sample.Value, sample.Quality.ToString(), sample.Timestamp);
        bool boolValue;
        try { boolValue = sample.Value is not null && sample.AsBoolean(); } catch { boolValue = false; }
        double numberValue;
        try { numberValue = sample.AsDouble(); } catch { numberValue = 0; }
        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["value"] = VisionValue.DeviceTag(tagValue),
                ["boolValue"] = VisionValue.Boolean(boolValue),
                ["numberValue"] = VisionValue.Double(numberValue),
                ["textValue"] = VisionValue.String(sample.AsText()),
                ["quality"] = VisionValue.String(sample.Quality.ToString()),
                ["timestamp"] = VisionValue.String(sample.Timestamp.ToString("O"))
            },
            new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["tagId"] = sample.TagId,
                ["dataType"] = sample.DataType.ToString(),
                ["value"] = sample.Value,
                ["quality"] = sample.Quality.ToString(),
                ["timestamp"] = sample.Timestamp
            });
    }
}

public sealed class DeviceWriteTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.writeTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var deviceId = node.GetString("deviceId", "virtual-modbus-1");
        var tagId = node.GetString("tagId", "statusText");
        var autoConnect = node.GetBool("autoConnect", true);
        object? value;

        if (context.TryGetValue("value", out var incoming))
        {
            value = incoming.Value is VisionDeviceTagValue tagged ? tagged.Value : incoming.Value;
        }
        else
        {
            value = ParseFallback(node.GetString("valueType", "String"), node.GetString("fallbackValue", "Vision OK"));
        }

        await devices.WriteTagAsync(deviceId, tagId, value, autoConnect, cancellationToken);
        var sample = await devices.ReadTagAsync(deviceId, tagId, fresh: true, autoConnect: autoConnect, cancellationToken: cancellationToken);
        var result = DeviceReadTagNode.Result(sample, deviceId);
        return result with
        {
            Summary = new Dictionary<string, object?>(result.Summary)
            {
                ["operation"] = "Write",
                ["writtenValue"] = sample.Value
            }
        };
    }

    private static object ParseFallback(string type, string text)
        => type.ToLowerInvariant() switch
        {
            "boolean" or "bool" => bool.TryParse(text, out var b) ? b : text is "1" or "on" or "ON",
            "integer" or "int" => long.TryParse(text, out var i) ? i : 0L,
            "double" or "number" => double.TryParse(text, out var d) ? d : 0d,
            _ => text
        };
}

public sealed class DeviceWaitTagNode(DeviceManager devices) : IVisionNodeExecutor
{
    public string Type => "device.waitTag";

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        var deviceId = node.GetString("deviceId", "virtual-modbus-1");
        var tagId = node.GetString("tagId", "trigger");
        var op = node.GetString("operator", "True");
        var compare = node.GetString("compareValue", "1");
        var timeoutMs = Math.Clamp(node.GetInt("timeoutMs", 5000), 50, 120000);
        var pollMs = Math.Clamp(node.GetInt("pollMs", 25), 10, 5000);
        var autoConnect = node.GetBool("autoConnect", true);
        var started = DateTimeOffset.UtcNow;
        var sample = await devices.WaitForTagAsync(
            deviceId,
            tagId,
            value => Matches(value, op, compare),
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(pollMs),
            autoConnect,
            cancellationToken);
        var waitMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        var result = DeviceReadTagNode.Result(sample, deviceId);
        var outputs = new Dictionary<string, VisionValue>(result.Outputs)
        {
            ["matched"] = VisionValue.Boolean(true),
            ["waitMs"] = VisionValue.Double(waitMs)
        };
        var summary = new Dictionary<string, object?>(result.Summary)
        {
            ["operator"] = op,
            ["compareValue"] = compare,
            ["waitMs"] = Math.Round(waitMs, 2),
            ["matched"] = true
        };
        return new NodeExecutorResult(outputs, summary);
    }

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
                _ => false
            };
        }

        var number = sample.AsDouble();
        var expected = double.TryParse(compare, out var parsed) ? parsed : 0d;
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
        var deviceId = node.GetString("deviceId", "virtual-modbus-1");
        var pass = context.Require<bool>("pass");
        var x = context.TryGetValue("x", out var xv) ? Convert.ToDouble(xv.Value) : 0d;
        var y = context.TryGetValue("y", out var yv) ? Convert.ToDouble(yv.Value) : 0d;
        var r = context.TryGetValue("r", out var rv) ? Convert.ToDouble(rv.Value) : 0d;
        var autoConnect = node.GetBool("autoConnect", true);

        var readyTag = node.GetString("resultReadyTag", "resultReady");
        var okTag = node.GetString("resultOkTag", "resultOk");
        var xTag = node.GetString("xTag", "resultX");
        var yTag = node.GetString("yTag", "resultY");
        var rTag = node.GetString("rTag", "resultR");

        // One mutually exclusive device command: lower Ready, write payload, raise Ready last.
        // WriteSequenceAsync holds the device command lock for all six writes, so a concurrent run
        // on the same device queues behind the transaction instead of interleaving fields.
        await devices.WriteSequenceAsync(
            deviceId,
            [
                (readyTag, (object?)false),
                (okTag, (object?)pass),
                (xTag, (object?)x),
                (yTag, (object?)y),
                (rTag, (object?)r),
                (readyTag, (object?)true)
            ],
            autoConnect,
            cancellationToken);

        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["written"] = VisionValue.Boolean(true),
                ["pass"] = VisionValue.Boolean(pass),
                ["x"] = VisionValue.Double(x),
                ["y"] = VisionValue.Double(y),
                ["r"] = VisionValue.Double(r)
            },
            new Dictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["resultReadyTag"] = readyTag,
                ["pass"] = pass,
                ["x"] = Math.Round(x, 3),
                ["y"] = Math.Round(y, 3),
                ["r"] = Math.Round(r, 3),
                ["writeOrder"] = $"{readyTag}=0 → payload → {readyTag}=1"
            });
    }
}
