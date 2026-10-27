using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// JSON 组包与解析（value.jsonBuild / value.jsonParse，2026-10-27 · 缺口分析 P0-2）。
///
/// 现场缺口：`String` 端口到处都是（条码、OCR、时间戳），`output.httpRequest` 也能发 JSON，
/// 但只能把**单个上游对象**原样发出。要给 MES 发 {sn, result, x, y, ts} 这种多字段包，
/// 或者解析响应里的 code/message 再回填流程，之前只能靠 script.csharp 写代码。
///
/// 设计取舍：
/// - JSON 文本走 `String` 端口而不是新增 `Json` 端口类型：`transformJson` / `detectionsJson` /
///   `poses` 等既有输出本来就是 String 里的 JSON 文本，新增类型会与它们并存成两套方言；
///   真正缺的是"构造与解析"这两个动作，不是类型。
/// - 组包按键名列表映射端口顺序（`keys` = "sn,result,x,y"），标量类型从 VisionValue 的实际
///   CLR 类型推断，避免让用户再写一遍类型声明；复杂类型（图像/区域/检测列表）明确拒绝，
///   而不是序列化成一坨不可控的东西。
/// - 解析用固定路径语法（$.a.b[0].c），缺字段默认硬失败：静默给 null 会让下游在几轮之后
///   才炸，那是最难查的一类问题。
/// </summary>
public sealed class JsonBuildNode : IVisionNodeExecutor
{
    public string Type => "value.jsonBuild";

    private static readonly string[] Ports = ["a", "b", "c", "d", "e", "f"];

    /// <summary>中文与引号保持可读（仓库既有落盘 JSON 约定），接收端按标准 JSON 解析不受影响。</summary>
    private static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var mode = node.GetString("mode", "object").Trim().ToLowerInvariant();
        if (mode is not ("object" or "array"))
            throw new InvalidOperationException("value.jsonBuild mode must be object or array.");

        var pretty = node.GetBool("pretty", false);
        var maxBytes = node.GetInt("maxBytes", 65536);
        if (maxBytes is < 64 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("value.jsonBuild maxBytes must be within 64..4194304.");

        var keys = (node.GetString("keys", "sn,result,x,y,z,ts") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries);
        if (mode == "object" && keys.Length < Ports.Length)
            keys = [.. keys, .. Enumerable.Range(keys.Length, Ports.Length - keys.Length).Select(i => $"field{i + 1}")];

        var node2 = new JsonObject();
        var array = new JsonArray();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var fieldCount = 0;

        for (var index = 0; index < Ports.Length; index++)
        {
            var port = Ports[index];
            if (!context.TryGetValue(port, out var value)) continue;

            var converted = JsonSerializer.SerializeToNode(Convert(value, port));
            if (mode == "array")
            {
                array.Add(converted);
            }
            else
            {
                var key = keys[index];
                if (key is "" or "-") continue; // 明确跳过该端口
                if (key.Length > 128)
                    throw new InvalidOperationException(
                        $"value.jsonBuild key for port '{port}' is longer than 128 characters.");
                if (!usedKeys.Add(key))
                    throw new InvalidOperationException($"value.jsonBuild key '{key}' is used by more than one port.");
                node2[key] = converted;
            }
            fieldCount++;
        }

        if (fieldCount == 0)
            throw new InvalidOperationException("value.jsonBuild needs at least one connected input (a..f).");

        var payload = JsonSerializer.Serialize<JsonNode>(mode == "array" ? array : node2,
            pretty ? new JsonSerializerOptions(JsonText) { WriteIndented = true } : JsonText);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (bytes > maxBytes)
            throw new InvalidOperationException(
                $"value.jsonBuild payload is {bytes} bytes which exceeds maxBytes={maxBytes}; drop fields or raise the limit.");

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("fieldCount", fieldCount), ("bytes", bytes), ("pretty", pretty));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["json"] = VisionValue.String(payload),
                ["fieldCount"] = VisionValue.Integer(fieldCount),
                ["bytes"] = VisionValue.Integer(bytes),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["mode"] = mode, ["fieldCount"] = fieldCount, ["bytes"] = bytes, ["pretty"] = pretty,
                ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>只接受标量与字符串；复杂视觉对象必须由用户先降级成编号/坐标再组包。</summary>
    private static object? Convert(VisionValue value, string port) => value.Value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long or ulong => System.Convert.ToInt64(value.Value, CultureInfo.InvariantCulture),
        float or double or decimal => System.Convert.ToDouble(value.Value, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            $"value.jsonBuild input '{port}' carries {value.Type} ({value.Value.GetType().Name}); " +
            "only text, number, integer and boolean can be packed — reduce the value upstream (for example collection.detections.count or measure.* scalars).")
    };
}

/// <summary>
/// JSON 解析（value.jsonParse）：按固定路径取值，输出类型化结果与存在性标记。
/// </summary>
public sealed class JsonParseNode : IVisionNodeExecutor
{
    public string Type => "value.jsonParse";

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var text = context.TryGetValue("json", out var jsonInput)
            ? jsonInput.Require<string>("json")
            : node.GetString("json", string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("value.jsonParse requires a non-empty JSON input.");

        var maxBytes = node.GetInt("maxBytes", 1048576);
        if (maxBytes is < 64 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("value.jsonParse maxBytes must be within 64..16777216.");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > maxBytes)
            throw new InvalidOperationException(
                $"value.jsonParse payload is {bytes} bytes which exceeds maxBytes={maxBytes}; parse a smaller response or raise the limit.");

        var path = node.GetString("path", string.Empty).Trim();
        var kind = node.GetString("type", "auto").Trim().ToLowerInvariant();
        if (kind is not ("auto" or "number" or "text" or "boolean"))
            throw new InvalidOperationException("value.jsonParse type must be auto, number, text or boolean.");
        var required = node.GetBool("required", true);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"value.jsonParse input is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var found = TryResolve(document.RootElement, path, out var element);
            if (!found && required)
                throw new InvalidOperationException(
                    $"value.jsonParse path '{Display(path)}' was not found in the payload; set required=false to get found=false instead.");

            var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(found),
                ["kind"] = VisionValue.String(found ? element.ValueKind.ToString() : "missing"),
                ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(
                    Type, ("path", Display(path)), ("found", found), ("kind", found ? element.ValueKind.ToString() : "missing")))
            };

            if (found)
            {
                var (objectValue, count) = Materialize(element, kind, path);
                outputs["value"] = new VisionValue(VisionDataType.Any, objectValue);
                outputs["count"] = VisionValue.Integer(count);
                switch (objectValue)
                {
                    case double number: outputs["number"] = VisionValue.Double(number); break;
                    case bool flag: outputs["boolean"] = VisionValue.Boolean(flag); break;
                    case string str: outputs["text"] = VisionValue.String(str); break;
                }
            }

            return ValueTask.FromResult(new NodeExecutorResult(outputs,
                new Dictionary<string, object?>
                {
                    ["path"] = Display(path), ["found"] = found, ["type"] = kind,
                    ["kind"] = found ? element.ValueKind.ToString() : "missing"
                }));
        }
    }

    /// <summary>
    /// 路径语法：可选 `$` 前缀，点号分段，段内可带 `[n]` 下标。空路径 = 根节点。
    /// 只支持读取，不支持通配/过滤——工业读响应不需要，也避免把 JSONPath 的语义坑引进来。
    /// </summary>
    private static bool TryResolve(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        if (string.IsNullOrWhiteSpace(path) || path == "$") return true;

        var trimmed = path.StartsWith('$') ? path[1..] : path;
        if (trimmed.StartsWith('.')) trimmed = trimmed[1..];

        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            var bracket = name.IndexOf('[');
            if (bracket >= 0)
            {
                var suffix = name[bracket..];
                name = name[..bracket];
                if (!TryIndexes(suffix, out var indexes)) return false;
                if (name.Length > 0 && !TryProperty(ref element, name)) return false;
                foreach (var index in indexes)
                {
                    if (element.ValueKind != JsonValueKind.Array) return false;
                    if (index < 0 || index >= element.GetArrayLength()) return false;
                    element = element[index];
                }
                continue;
            }

            if (!TryProperty(ref element, name)) return false;
        }
        return true;
    }

    private static bool TryProperty(ref JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty(name, out var next)) return false;
        element = next;
        return true;
    }

    private static bool TryIndexes(string suffix, out List<int> indexes)
    {
        indexes = [];
        var cursor = 0;
        while (cursor < suffix.Length)
        {
            if (suffix[cursor] != '[') return false;
            var close = suffix.IndexOf(']', cursor + 1);
            if (close < 0) return false;
            var span = suffix[(cursor + 1)..close];
            if (!int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return false;
            indexes.Add(index);
            cursor = close + 1;
        }
        return indexes.Count > 0;
    }

    private static (object? Value, long Count) Materialize(JsonElement element, string kind, string path)
    {
        var count = element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Object => element.EnumerateObject().Count(),
            _ => 0
        };

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (kind == "number" && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    return (parsed, count);
                if (kind == "boolean" && bool.TryParse(text, out var flag)) return (flag, count);
                if (kind is "number" or "boolean")
                    throw new InvalidOperationException(
                        $"value.jsonParse path '{Display(path)}' holds text '{text}' which is not a valid {kind}.");
                return (text, count);

            case JsonValueKind.Number:
                var number = element.GetDouble();
                if (!double.IsFinite(number))
                    throw new InvalidOperationException($"value.jsonParse path '{Display(path)}' holds a non-finite number.");
                return (number, count);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return (element.GetBoolean(), count);

            case JsonValueKind.Null:
                return (null, count);

            default:
                // 对象/数组原样返回 JSON 文本：要取子字段请再连一个 jsonParse，保持单职责。
                var raw = element.GetRawText();
                if (kind is "number" or "boolean")
                    throw new InvalidOperationException(
                        $"value.jsonParse path '{Display(path)}' holds a {element.ValueKind} which cannot become {kind}; use type=auto or text.");
                return (raw, count);
        }
    }

    private static string Display(string path) => string.IsNullOrWhiteSpace(path) ? "$" : path;
}
