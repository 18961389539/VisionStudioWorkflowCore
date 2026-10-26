using Microsoft.Extensions.Logging;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 流程内日志（flow.log，2026-10-26 · 缺口分析 P0-5）。
///
/// 此前流程里唯一能看到的东西是每个节点的 diagnostics 与运行报告，无法表达"这一轮用的是哪个配方""为什么走了 NG 分支"
/// 这类过程信息，排障只能靠事后猜。本节点把自定义日志同时送到两个出口：
/// 1. 宿主日志（Microsoft.Extensions.Logging）—— 落到运行实例的日志文件/控制台，带运行与节点标识；
/// 2. 本次运行报告（节点 Summary）—— 在画布追溯面板里按节点展开可见。
///
/// 刻意不做的事：
/// - 不做消息模板插值。拼字符串用 value.string，本节点只负责"把给定文本按级别记下来"，避免两套模板语法。
/// - 不因为消息为空而失败。日志属于观测手段，不应把产线跑停；空消息照记，便于排查"分支走到了但没内容"。
/// </summary>
public sealed class FlowLogNode(ILogger<FlowLogNode>? logger = null) : IVisionNodeExecutor
{
    public string Type => "flow.log";

    /// <summary>单条日志的字符上限，防止把整幅 OCR 文本或大 JSON 灌进日志。</summary>
    public const int MaxMessageLength = 4096;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var (level, severity) = ReadLevel(node);
        // 端口优先：允许把上一步算出来的文本直接接到日志，而不是复制成参数。
        var message = context.TryGetValue("message", out var connected)
            ? connected.Require<string>("message")
            : node.GetString("message", string.Empty);
        if (message.Length > MaxMessageLength)
            throw new InvalidOperationException($"flow.log message exceeds {MaxMessageLength} characters; shorten it or pass a summary instead of full payloads.");

        // 宿主日志：级别映射到 ILogger；未注入 logger 时（单元测试直接 new）只写运行报告。
        if (logger is { } host)
        {
            var hostLevel = severity switch
            {
                0 => LogLevel.Debug,
                1 => LogLevel.Information,
                2 => LogLevel.Warning,
                _ => LogLevel.Error
            };
            host.Log(hostLevel, "flow.log [{NodeId}] {Message}", node.Id, message);
        }

        var diagnostics = CoordinateValidation.Diagnostics(
            Type,
            ("level", level),
            ("severity", severity),
            ("charCount", message.Length),
            ("message", Clip(message)),
            ("hostLogger", logger is not null));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>
            {
                ["message"] = VisionValue.String(message),
                ["level"] = VisionValue.String(level),
                ["severity"] = VisionValue.Integer(severity),
                ["logged"] = VisionValue.Boolean(true),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["level"] = level,
                ["severity"] = severity,
                ["message"] = Clip(message),
                ["charCount"] = message.Length,
                ["diagnostics"] = diagnostics
            }));
    }

    /// <summary>严格解析级别：只接受 debug/info/warning/error，拼错时明确失败而不是默认成 info。</summary>
    private static (string Level, int Severity) ReadLevel(NodeDefinition node)
    {
        var raw = node.GetString("level", "info").Trim().ToLowerInvariant();
        return raw switch
        {
            "debug" => ("debug", 0),
            "info" => ("info", 1),
            "warning" or "warn" => ("warning", 2),
            "error" => ("error", 3),
            _ => throw new InvalidOperationException($"flow.log level '{raw}' is not supported; use debug, info, warning or error.")
        };
    }

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>
/// 主动失败（flow.fail，2026-10-26 · 缺口分析 P0-5）。
///
/// 语义：**终止整次运行**并把消息作为失败原因抛出，不是"判 NG"。判 NG 用 flow.result（不中断，可继续执行后续留证/上传）。
///
/// 为什么是抛异常而不是置标志位：
/// - 运行器已经把节点异常统一收敛成"失败报告 + 运行结果 Error"，抛异常即可得到正确的用户可见消息与追溯；
/// - 不需要给运行结果新增字段，也不改变既有失败路径。
///
/// 已知边界：flow.try 的执行体只接受可流水线化的确定性内置节点（ValidateTryBody），
/// 因此 flow.fail 不能放进 try 体里去走错误分支——需要"可恢复的错误"时，应当在 try 体里用会失败的测量/判定节点，
/// 或直接用 flow.result 表达 NG。
/// </summary>
public sealed class FlowFailNode : IVisionNodeExecutor
{
    public string Type => "flow.fail";

    public const int MaxMessageLength = 4096;
    public const int MaxCodeLength = 64;

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var code = node.GetString("code", "NODE_FAILURE").Trim();
        if (code.Length == 0) throw new InvalidOperationException("flow.fail code must not be empty.");
        if (code.Length > MaxCodeLength)
            throw new InvalidOperationException($"flow.fail code must be at most {MaxCodeLength} characters.");

        var message = context.TryGetValue("message", out var connected)
            ? connected.Require<string>("message")
            : node.GetString("message", "Workflow stopped by flow.fail.");
        if (message.Length > MaxMessageLength)
            throw new InvalidOperationException($"flow.fail message must be at most {MaxMessageLength} characters.");
        if (string.IsNullOrWhiteSpace(message))
            message = "Workflow stopped by flow.fail.";

        // 抛出前不写任何输出：运行器会把它记成失败节点，并在运行结果里保留这条消息。
        throw new InvalidOperationException($"[{code}] {message}");
    }
}
