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
/// - JSON 文本继续走 `String` 端口（与 `transformJson` / `detectionsJson` 等既有输出一致），
///   同时新增 `Json` 类型的 `document` 端口供类型化链式传递：两套表示并存，String 是对外协议文本，
///   Json 是流程内部的强类型载荷，避免把所有 JSON 都降级成字符串。
/// - 组包按键名列表映射端口顺序（`keys` = "sn,result,x,y"），标量类型从 VisionValue 的实际
///   CLR 类型推断，避免让用户再写一遍类型声明；StringList 直接打包成数组，复杂视觉对象
///   （图像/区域/检测列表）明确拒绝，而不是序列化成一坨不可控的东西。
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
        var emptyPolicy = node.GetString("emptyPolicy", "compact").Trim().ToLowerInvariant();
        if (emptyPolicy is not ("compact" or "preserveNull"))
            throw new InvalidOperationException("value.jsonBuild emptyPolicy must be compact or preserveNull.");
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
        var lastFieldKey = "a";
        var maxFieldBytes = 0;
        var largestFieldKey = "a";

        for (var index = 0; index < Ports.Length; index++)
        {
            var port = Ports[index];
            if (!context.TryGetValue(port, out var value))
            {
                if (mode == "array" && emptyPolicy == "preserveNull")
                {
                    array.Add((JsonNode?)null);
                    fieldCount++;
                }
                continue;
            }

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
                lastFieldKey = key;
            }
            fieldCount++;
            var fieldBytes = System.Text.Encoding.UTF8.GetByteCount(converted?.ToJsonString(JsonText) ?? "null");
            if (fieldBytes > maxFieldBytes) { maxFieldBytes = fieldBytes; largestFieldKey = mode == "array" ? port : lastFieldKey; }
        }

        if (fieldCount == 0)
            throw new InvalidOperationException("value.jsonBuild needs at least one connected input (a..f).");

        var payload = JsonSerializer.Serialize<JsonNode>(mode == "array" ? array : node2,
            pretty ? new JsonSerializerOptions(JsonText) { WriteIndented = true } : JsonText);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (bytes > maxBytes)
            throw new InvalidOperationException(
                $"value.jsonBuild errorCode=maxBytesExceeded: attemptedBytes={bytes}, maxBytes={maxBytes}, fieldKey={largestFieldKey}, largestFieldBytes={maxFieldBytes}; no json/document output was produced.");

        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("mode", mode), ("fieldCount", fieldCount), ("bytes", bytes), ("pretty", pretty), ("emptyPolicy", emptyPolicy));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["json"] = VisionValue.String(payload),
                ["document"] = VisionValue.Json(new VisionJson(payload)),
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

    /// <summary>标量、字符串与文本列表；复杂视觉对象必须由用户先降级成编号/坐标再组包。</summary>
    private static object? Convert(VisionValue value, string port) => value.Value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        byte or sbyte or short or ushort or int or uint or long or ulong => System.Convert.ToInt64(value.Value, CultureInfo.InvariantCulture),
        float or double or decimal => System.Convert.ToDouble(value.Value, CultureInfo.InvariantCulture),
        IReadOnlyList<string> texts => texts,
        VisionJson document => JsonNode.Parse(document.Text) ??
            throw new InvalidOperationException($"value.jsonBuild input '{port}' is an empty JSON document."),
        _ => throw new InvalidOperationException(
            $"value.jsonBuild input '{port}' carries {value.Type} ({value.Value.GetType().Name}); " +
            "only text, number, integer, boolean, StringList and Json can be packed — reduce the value upstream (for example collection.detections.count or measure.* scalars).")
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

        var source = node.GetString("source", "auto").Trim().ToLowerInvariant();
        if (source is not ("auto" or "document" or "json" or "parameter"))
            throw new InvalidOperationException("value.jsonParse source must be auto, document, json or parameter.");
        var hasDocument = context.TryGetValue("document", out var documentInput);
        var hasJson = context.TryGetValue("json", out var jsonInput);
        if (source == "auto" && hasDocument && hasJson)
            throw new InvalidOperationException("value.jsonParse errorCode=ambiguousSource: both document and json ports are connected; select source explicitly.");
        var actualSource = source switch
        {
            "document" when hasDocument => "document",
            "json" when hasJson => "json",
            "parameter" => "parameter",
            "auto" when hasDocument => "document",
            "auto" when hasJson => "json",
            "auto" => "parameter",
            _ => throw new InvalidOperationException($"value.jsonParse source={source} requires its connected input port.")
        };
        var text = actualSource switch
        {
            "document" => documentInput.Require<VisionJson>("document").Text,
            "json" => jsonInput.Require<string>("json"),
            _ => node.GetString("json", string.Empty)
        };
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("value.jsonParse requires a non-empty JSON input.");

        var maxBytes = node.GetInt("maxBytes", 1048576);
        if (maxBytes is < 64 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("value.jsonParse maxBytes must be within 64..16777216.");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > maxBytes)
            throw new InvalidOperationException(
                $"value.jsonParse payload is {bytes} bytes which exceeds maxBytes={maxBytes}; parse a smaller response or raise the limit.");

        var maxDepth = node.GetInt("maxDepth", 64);
        if (maxDepth is < 1 or > 256)
            throw new InvalidOperationException("value.jsonParse maxDepth must be within 1..256.");
        var duplicatePropertyPolicy = node.GetString("duplicatePropertyPolicy", "fail").Trim().ToLowerInvariant();
        if (duplicatePropertyPolicy is not ("fail" or "first" or "last"))
            throw new InvalidOperationException("value.jsonParse duplicatePropertyPolicy must be fail, first or last.");

        var path = node.GetString("path", string.Empty).Trim();
        var kind = node.GetString("type", "auto").Trim().ToLowerInvariant();
        if (kind is not ("auto" or "number" or "text" or "boolean"))
            throw new InvalidOperationException("value.jsonParse type must be auto, number, text or boolean.");
        var required = node.GetBool("required", true);

        JsonNode? root;
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text), new JsonReaderOptions
            {
                // The configured lower limit is enforced by ParseValue so its errorCode is deterministic.
                MaxDepth = int.MaxValue,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (!reader.Read()) throw InvalidJson("input is empty or incomplete");
            root = ParseValue(ref reader, maxDepth, duplicatePropertyPolicy);
            if (reader.Read()) throw InvalidJson("input contains multiple JSON values");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"value.jsonParse errorCode=invalidJson: input is not valid JSON: {ex.Message}", ex);
        }

        {
            var found = TryResolve(root, path, out var selected);
            if (!found && required)
                throw new InvalidOperationException(
                    $"value.jsonParse path '{Display(path)}' was not found in the payload; set required=false to get found=false instead.");

            var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["found"] = VisionValue.Boolean(found),
                ["valid"] = VisionValue.Boolean(found),
                ["kind"] = VisionValue.String(found ? GetKind(selected!) : "missing"),
                ["errorCode"] = VisionValue.String("none"),
                ["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(
                    Type, ("path", Display(path)), ("found", found), ("valid", found), ("source", actualSource), ("kind", found ? GetKind(selected!) : "missing"),
                    ("maxDepth", maxDepth), ("duplicatePropertyPolicy", duplicatePropertyPolicy), ("errorCode", "none")))
            };

            if (found)
            {
                var materialized = Materialize(selected!, kind, path);
                if (!materialized.Valid)
                {
                    outputs["valid"] = VisionValue.Boolean(false);
                    outputs["errorCode"] = VisionValue.String("typeMismatch");
                    outputs["diagnostics"] = VisionValue.String(CoordinateValidation.Diagnostics(
                        Type, ("path", Display(path)), ("found", true), ("valid", false), ("source", actualSource),
                        ("kind", GetKind(selected!)), ("expectedType", kind), ("errorCode", "typeMismatch")));
                    return ValueTask.FromResult(new NodeExecutorResult(outputs,
                        new Dictionary<string, object?>
                        {
                            ["path"] = Display(path), ["found"] = true, ["valid"] = false, ["source"] = actualSource,
                            ["type"] = kind, ["kind"] = GetKind(selected!), ["errorCode"] = "typeMismatch",
                            ["maxDepth"] = maxDepth, ["duplicatePropertyPolicy"] = duplicatePropertyPolicy
                        }));
                }
                var (objectValue, count) = (materialized.Value, materialized.Count);
                outputs["value"] = new VisionValue(VisionDataType.Any, objectValue);
                outputs["count"] = VisionValue.Integer(count);
                switch (objectValue)
                {
                    case double number: outputs["number"] = VisionValue.Double(number); break;
                    case bool flag: outputs["boolean"] = VisionValue.Boolean(flag); break;
                    case string str: outputs["text"] = VisionValue.String(str); break;
                }
                if (selected is JsonObject or JsonArray)
                    outputs["document"] = VisionValue.Json(new VisionJson(selected!.ToJsonString()));
                if (selected is JsonArray array)
                {
                    var texts = array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var str) ? str : null).ToArray();
                    if (texts.All(item => item is not null))
                        outputs["texts"] = VisionValue.StringList([.. texts.Select(item => item!)]);
                }
            }

            return ValueTask.FromResult(new NodeExecutorResult(outputs,
                new Dictionary<string, object?>
                {
                    ["path"] = Display(path), ["found"] = found, ["valid"] = found, ["source"] = actualSource, ["type"] = kind,
                    ["kind"] = found ? GetKind(selected!) : "missing", ["errorCode"] = "none",
                    ["maxDepth"] = maxDepth, ["duplicatePropertyPolicy"] = duplicatePropertyPolicy
                }));
        }
    }

    private static InvalidOperationException ParseFailure(string errorCode, string message)
        => new($"value.jsonParse errorCode={errorCode}: {message}");

    private static InvalidOperationException InvalidJson(string message)
        => new($"value.jsonParse errorCode=invalidJson: input is not valid JSON: {message}");

    /// <summary>Reader-validated tree construction enforces depth and duplicate policies during parsing.</summary>
    private static JsonNode? ParseValue(ref Utf8JsonReader reader, int maxDepth, string duplicatePropertyPolicy)
    {
        if ((reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) && reader.CurrentDepth >= maxDepth)
            throw ParseFailure("maxDepthExceeded", $"nesting depth exceeds maxDepth={maxDepth}");
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                // Utf8JsonReader 不把分隔逗号暴露成 Token，靠 AllowTrailingCommas=false 由读取器拒绝尾随逗号，
                // 这里只需按 PropertyName / EndObject 收敛循环，不要再检查 Comma。
                var obj = new JsonObject();
                while (true)
                {
                    if (!reader.Read()) throw InvalidJson("unterminated object");
                    if (reader.TokenType == JsonTokenType.EndObject) return obj;
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw InvalidJson("expected an object property");
                    var name = reader.GetString()!;
                    var duplicate = obj.ContainsKey(name);
                    if (duplicate && duplicatePropertyPolicy == "fail")
                        throw ParseFailure("duplicateKey", $"object property '{name}' occurs more than once");
                    if (!reader.Read()) throw InvalidJson("unterminated object");
                    var value = ParseValue(ref reader, maxDepth, duplicatePropertyPolicy);
                    if (!duplicate || duplicatePropertyPolicy == "last") obj[name] = value;
                }
            case JsonTokenType.StartArray:
                var array = new JsonArray();
                while (true)
                {
                    if (!reader.Read()) throw InvalidJson("unterminated array");
                    if (reader.TokenType == JsonTokenType.EndArray) return array;
                    array.Add(ParseValue(ref reader, maxDepth, duplicatePropertyPolicy));
                }
            case JsonTokenType.String: return JsonValue.Create(reader.GetString());
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer)) return JsonValue.Create(integer);
                if (reader.TryGetDecimal(out var decimalValue)) return JsonValue.Create(decimalValue);
                return JsonValue.Create(reader.GetDouble());
            case JsonTokenType.True: return JsonValue.Create(true);
            case JsonTokenType.False: return JsonValue.Create(false);
            case JsonTokenType.Null: return null;
            default: throw InvalidJson($"unexpected token {reader.TokenType}");
        }
    }

    private static string GetKind(JsonNode node) => node switch
    {
        JsonObject => "Object",
        JsonArray => "Array",
        JsonValue value when value.TryGetValue<string>(out _) => "String",
        JsonValue value when value.TryGetValue<long>(out _) || value.TryGetValue<decimal>(out _) || value.TryGetValue<double>(out _) => "Number",
        JsonValue value when value.TryGetValue<bool>(out _) => "Boolean",
        _ => "Null"
    };

    /// <summary>
    /// 路径语法：可选 `$` 前缀，点号分段，段内可带 `[n]` 下标。空路径 = 根节点。
    /// 只支持读取，不支持通配/过滤——工业读响应不需要，也避免把 JSONPath 的语义坑引进来。
    /// </summary>
    private static bool TryResolve(JsonNode? root, string path, out JsonNode? element)
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
                    if (element is not JsonArray array || index < 0 || index >= array.Count) return false;
                    element = array[index];
                }
                continue;
            }

            if (!TryProperty(ref element, name)) return false;
        }
        return true;
    }

    private static bool TryProperty(ref JsonNode? element, string name)
    {
        if (element is not JsonObject obj || !obj.TryGetPropertyValue(name, out var next)) return false;
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

    private static (object? Value, long Count, bool Valid) Materialize(JsonNode node, string kind, string path)
    {
        var count = node switch
        {
            JsonArray array => array.Count,
            JsonObject obj => obj.Count,
            _ => 0
        };

        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (kind == "number")
                {
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                        return (parsed, count, true);
                    throw new InvalidOperationException(
                        $"value.jsonParse path '{Display(path)}' holds text '{text}' which is not a valid number.");
                }
                if (kind == "boolean")
                {
                    if (bool.TryParse(text, out var flag)) return (flag, count, true);
                    throw new InvalidOperationException(
                        $"value.jsonParse path '{Display(path)}' holds text '{text}' which is not a valid boolean.");
                }
                return (text, count, true);

            case JsonValue value when value.TryGetValue<long>(out var integer):
                return (Convert.ToDouble(integer, CultureInfo.InvariantCulture), count, true);

            case JsonValue value when value.TryGetValue<decimal>(out var decimalValue):
                return (Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture), count, true);

            case JsonValue value when value.TryGetValue<double>(out var number):
                if (!double.IsFinite(number))
                    throw new InvalidOperationException($"value.jsonParse path '{Display(path)}' holds a non-finite number.");
                return (number, count, true);

            case JsonValue value when value.TryGetValue<bool>(out var booleanValue):
                return (booleanValue, count, true);

            case null:
                return (null, count, kind == "auto");

            default:
                // 对象/数组原样返回 JSON 文本：要取子字段请再连一个 jsonParse，保持单职责。
                var raw = node.ToJsonString();
                if (kind is "number" or "boolean")
                    return (null, count, false);
                return (raw, count, true);
        }
    }

    private static string Display(string path) => string.IsNullOrWhiteSpace(path) ? "$" : path;
}
