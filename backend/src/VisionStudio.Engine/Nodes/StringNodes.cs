using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 字符串加工节点（value.string，2026-10-26 · 缺口分析 P0-1）。
///
/// 背景：目录里大量节点会产出 <see cref="VisionDataType.String"/>（条码文本、OCR 文本、时间戳、
/// 文件路径、诊断 JSON），但此前没有任何节点能对字符串做加工——只能原样转交给
/// output.httpRequest / device.writeTag，无法拼出 "SN-OK-12.345" 这类上报串。
///
/// 设计约定（与 value.number / value.arithmetic 保持一致）：
/// 1. 非法**配置**（未知 op、空 find、非单字符 padChar、非法数字格式）一律显式抛错，不静默兜底；
/// 2. 非法**运行时数据**（子串越界）按文档化的裁剪策略处理，并置 truncated=true，不抛错——
///    因为长度取决于上游图像/条码内容，运行时才知道；
/// 3. 所有字符串按 UTF-16 码元计数（与 .NET string.Length 一致），不假装是字素簇或字节数；
/// 4. 未接线的字符串输入视为空串；模板引用 {value} 但未接线时显式报错（否则会静默产出缺字段的上报串）。
/// </summary>
public sealed class ValueStringNode : IVisionNodeExecutor
{
    public string Type => "value.string";

    /// <summary>结果长度硬上限的默认值；目录参数 maxLength 可覆盖，但执行器仍独立校验一次。</summary>
    private const int DefaultMaxLength = 65536;

    /// <summary>模板本身长度上限，避免用超大模板拖垮单次执行。</summary>
    private const int MaxTemplateLength = 8192;

    /// <summary>正则替换的固定超时，避免上游文本触发灾难性回溯（与 text.regex 的 1..1000ms 预算同量级）。</summary>
    private static readonly TimeSpan ReplaceRegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>模板里允许的命名占位符；{0}..{3} 是它们的序号别名。</summary>
    private static readonly string[] TemplateTokens = ["a", "b", "c", "d", "value"];

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var op = node.GetString("op", "template").Trim().ToLowerInvariant();
        var maxLength = node.GetInt("maxLength", DefaultMaxLength);
        if (maxLength is < 16 or > 1_048_576)
            throw new InvalidOperationException("value.string maxLength must be within 16..1048576 characters.");

        // 四个可选字符串输入；未接线按空串处理（模板/拼接的常见用法是只接其中一两个）。
        var a = ReadOptionalString(context, "a");
        var b = ReadOptionalString(context, "b");
        var c = ReadOptionalString(context, "c");
        var d = ReadOptionalString(context, "d");

        var text = string.Empty;
        var truncated = false;
        double? number = null;
        var detail = new List<(string Key, object? Value)>();

        switch (op)
        {
            case "template":
                {
                    var template = node.GetString("template", "{a}");
                    if (template.Length > MaxTemplateLength)
                        throw new InvalidOperationException($"value.string template exceeds {MaxTemplateLength} characters.");
                    text = RenderTemplate(template, a, b, c, d, context, out var usedValue);
                    detail.Add(("usedValueInput", usedValue));
                    break;
                }

            case "join":
                {
                    // 分隔拼接：跳过空段，避免 "SN_|_|_OK" 这类由未接线输入造成的空洞。
                    var separator = node.GetString("separator", "_");
                    text = string.Join(separator, new[] { a, b, c, d }.Where(part => part.Length > 0));
                    detail.Add(("separator", separator));
                    break;
                }

            case "numbertostring":
                {
                    var value = RequireNumberInput(context, "value", op);
                    var format = node.GetString("format", "0.###");
                    if (format.Length is 0 or > 64)
                        throw new InvalidOperationException("value.string format must be 1..64 characters.");
                    try
                    {
                        text = value.ToString(format, CultureInfo.InvariantCulture);
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidOperationException($"value.string format '{format}' is not a valid .NET numeric format string.", ex);
                    }
                    detail.Add(("numberFormat", format));
                    detail.Add(("value", value));
                    break;
                }

            case "tonumber":
                {
                    var raw = a.Trim();
                    if (raw.Length == 0)
                        throw new InvalidOperationException("value.string toNumber requires a non-empty text input on port 'a'.");
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        throw new InvalidOperationException($"value.string toNumber cannot parse '{Clip(raw)}' using invariant culture and NumberStyles.Float.");
                    if (!double.IsFinite(parsed))
                        throw new InvalidOperationException($"value.string toNumber parsed a non-finite value from '{Clip(raw)}'.");
                    number = parsed;
                    text = a; // 文本端口保持透传，便于同一条链路上继续做字符串判定
                    detail.Add(("parsed", parsed));
                    break;
                }

            case "substring":
                {
                    var startIndex = node.GetInt("startIndex", 0);
                    var takeLength = node.GetInt("takeLength", 0);
                    if (startIndex < 0) throw new InvalidOperationException("value.string startIndex must be >= 0.");
                    if (takeLength < 0) throw new InvalidOperationException("value.string takeLength must be >= 0 (0 = to end).");
                    var start = Math.Min(startIndex, a.Length);
                    truncated = start != startIndex;
                    // takeLength=0 表示"取到结尾"；超出剩余长度时同样裁剪并记录。
                    var available = a.Length - start;
                    var take = takeLength == 0 ? available : Math.Min(takeLength, available);
                    truncated |= take != takeLength && takeLength != 0;
                    text = a.Substring(start, take);
                    detail.Add(("startIndex", startIndex));
                    detail.Add(("takeLength", takeLength));
                    detail.Add(("textLength", a.Length));
                    break;
                }

            case "trim":
                {
                    var side = node.GetString("trimSide", "both").Trim().ToLowerInvariant();
                    text = side switch
                    {
                        "both" => a.Trim(),
                        "start" => a.TrimStart(),
                        "end" => a.TrimEnd(),
                        _ => throw new InvalidOperationException("value.string trimSide must be both, start or end.")
                    };
                    detail.Add(("trimSide", side));
                    detail.Add(("trimmedCount", a.Length - text.Length));
                    break;
                }

            case "replace":
                {
                    var find = node.GetString("find", string.Empty);
                    var replaceWith = node.GetString("replaceWith", string.Empty);
                    var replaceAll = node.GetBool("replaceAll", true);
                    var useRegex = node.GetBool("useRegex", false);
                    var ignoreCase = node.GetBool("ignoreCase", false);
                    if (find.Length == 0)
                        throw new InvalidOperationException("value.string replace requires a non-empty find text.");
                    if (find.Length > 4096)
                        throw new InvalidOperationException("value.string find text must be at most 4096 characters.");
                    if (useRegex)
                    {
                        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                        try
                        {
                            // count=1 时只替换第一处；固定 100ms 超时 + 模式长度预算防止上游文本触发灾难性回溯。
                            var regex = new Regex(find, options, ReplaceRegexTimeout);
                            // 用 MatchEvaluator 而不是替换模板串：replaceWith 一律按字面量写入，
                            // 避免用户输入 "$1" 时被当成捕获组引用（不做隐式反向引用展开）。
                            text = regex.Replace(a, _ => replaceWith, replaceAll ? int.MaxValue : 1);
                        }
                        catch (RegexMatchTimeoutException ex)
                        {
                            throw new InvalidOperationException($"value.string replace exceeded {ReplaceRegexTimeout.TotalMilliseconds} ms of regex time.", ex);
                        }
                        catch (ArgumentException ex)
                        {
                            throw new InvalidOperationException("value.string replace received an invalid regular expression: " + ex.Message, ex);
                        }
                    }
                    else
                    {
                        text = replaceAll
                            ? a.Replace(find, replaceWith, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            : ReplaceFirst(a, find, replaceWith, ignoreCase);
                    }
                    detail.Add(("useRegex", useRegex));
                    detail.Add(("replaceAll", replaceAll));
                    detail.Add(("ignoreCase", ignoreCase));
                    break;
                }

            case "pad":
                {
                    var width = node.GetInt("padWidth", 8);
                    var padCharText = node.GetString("padChar", "0");
                    var side = node.GetString("padSide", "left").Trim().ToLowerInvariant();
                    if (width is < 0 or > 1_048_576)
                        throw new InvalidOperationException("value.string padWidth must be within 0..1048576.");
                    if (padCharText.Length != 1)
                        throw new InvalidOperationException("value.string padChar must be exactly one character.");
                    if (side is not ("left" or "right"))
                        throw new InvalidOperationException("value.string padSide must be left or right.");
                    // 够宽则原样返回（不截断），与常见 printf 语义保持一致并在描述里写明。
                    text = a.Length >= width
                        ? a
                        : side == "left" ? a.PadLeft(width, padCharText[0]) : a.PadRight(width, padCharText[0]);
                    detail.Add(("padWidth", width));
                    detail.Add(("padSide", side));
                    break;
                }

            case "upper":
                text = a.ToUpperInvariant();
                break;

            case "lower":
                text = a.ToLowerInvariant();
                break;

            case "length":
                // 文本透传，长度由统一的 charCount 端口给出。
                text = a;
                break;

            default:
                throw new InvalidOperationException($"value.string operation '{op}' is not supported.");
        }

        if (text.Length > maxLength)
            throw new InvalidOperationException($"value.string produced {text.Length} characters which exceeds maxLength {maxLength}.");

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            [("op", (object?)op), ("charCount", text.Length), ("truncated", truncated), ("result", Preview(text)), .. detail]);

        var outputs = new Dictionary<string, VisionValue>
        {
            ["textValue"] = VisionValue.String(text),
            ["charCount"] = VisionValue.Integer(text.Length),
            ["truncated"] = VisionValue.Boolean(truncated),
            ["diagnostics"] = VisionValue.String(diagnostics)
        };
        // 仅 toNumber 产出数值端口：其它运算输出 0 会把"未转换"误读成"转换结果是 0"。
        if (number is { } parsedNumber) outputs["numberValue"] = VisionValue.Double(parsedNumber);

        return ValueTask.FromResult(new NodeExecutorResult(
            outputs,
            new Dictionary<string, object?>
            {
                ["op"] = op,
                ["text"] = Preview(text),
                ["charCount"] = text.Length,
                ["truncated"] = truncated,
                ["number"] = number,
                ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>读取可选字符串输入：未接线返回空串；已接线但类型不是 String 时由 Require&lt;string&gt; 给出明确错误。</summary>
    private static string ReadOptionalString(NodeExecutionContext context, string port)
        => context.TryGetValue(port, out var value) ? value.Require<string>(port) : string.Empty;

    /// <summary>读取必填数值输入，缺失时给出指向 ports 的可操作错误信息。</summary>
    private static double RequireNumberInput(NodeExecutionContext context, string port, string op)
    {
        if (!context.TryGetValue(port, out _))
            throw new InvalidOperationException($"value.string operation '{op}' requires input '{port}'.");
        var value = context.RequireNumber(port);
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"value.string operation '{op}' requires a finite input '{port}'.");
        return value;
    }

    /// <summary>
    /// 展开模板：{a}/{b}/{c}/{d}/{value} 与序号别名 {0}..{3}，{{ 与 }} 表示字面量花括号。
    /// 未知占位符、未闭合花括号、引用未接线的 {value} 都显式报错——静默忽略会让上报串缺字段。
    /// </summary>
    private static string RenderTemplate(string template, string a, string b, string c, string d, NodeExecutionContext context, out bool usedValue)
    {
        usedValue = false;
        var builder = new StringBuilder(template.Length + 32);
        for (var index = 0; index < template.Length; index++)
        {
            var current = template[index];
            if (current == '{')
            {
                if (index + 1 < template.Length && template[index + 1] == '{') { builder.Append('{'); index++; continue; }
                var close = template.IndexOf('}', index + 1);
                if (close < 0) throw new InvalidOperationException("value.string template contains an unclosed '{'.");
                var token = template[(index + 1)..close].Trim();
                if (token.Length == 0) throw new InvalidOperationException("value.string template contains an empty placeholder '{}'.");
                builder.Append(ResolveToken(token, a, b, c, d, context, ref usedValue));
                index = close;
                continue;
            }
            if (current == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}') { builder.Append('}'); index++; continue; }
                throw new InvalidOperationException("value.string template contains an unmatched '}'.");
            }
            builder.Append(current);
        }
        return builder.ToString();
    }

    private static string ResolveToken(string token, string a, string b, string c, string d, NodeExecutionContext context, ref bool usedValue)
    {
        switch (token.ToLowerInvariant())
        {
            case "a" or "0": return a;
            case "b" or "1": return b;
            case "c" or "2": return c;
            case "d" or "3": return d;
            case "value":
                if (!context.TryGetValue("value", out _))
                    throw new InvalidOperationException("value.string template references {value} but input 'value' is not connected.");
                usedValue = true;
                var number = context.RequireNumber("value");
                if (!double.IsFinite(number))
                    throw new InvalidOperationException("value.string template requires a finite input 'value'.");
                // 模板里的数值统一走不丢精度的往返格式，避免 0.1+0.2 这类浮点尾巴进入上报串。
                return number.ToString("R", CultureInfo.InvariantCulture);
            default:
                throw new InvalidOperationException(
                    $"value.string template placeholder '{{{token}}}' is not supported; allowed tokens are {string.Join(", ", TemplateTokens)} and {{0}}..{{3}}.");
        }
    }

    /// <summary>仅替换第一处（区分/不区分大小写），用于 replaceAll=false 的字面量分支。</summary>
    private static string ReplaceFirst(string text, string find, string replaceWith, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = text.IndexOf(find, comparison);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replaceWith, text.AsSpan(index + find.Length));
    }

    /// <summary>诊断与摘要里回显的文本片段，防止把整段 OCR 文本塞进报告。</summary>
    private static string Preview(string text) => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>错误信息中引用的输入片段，避免把超长上游文本整段写进异常。</summary>
    private static string Clip(string text) => text.Length <= 64 ? text : text[..64] + "…";
}
