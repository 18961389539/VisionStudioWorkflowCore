namespace VisionStudio.Abstractions;

/// <summary>
/// 主动失败（flow.fail）抛出的异常。它和运行时异常的区别是"人是故意判停的"：
/// 运行报告据此写出 intentional / failureCode，监控不应把计划内的停机当成程序缺陷。
/// </summary>
public sealed class IntentionalWorkflowFailureException : InvalidOperationException
{
    public const int MaxCodeLength = 64;
    public const int MaxMessageLength = 4096;
    public const int MaxDetailsBytes = 8192;

    public IntentionalWorkflowFailureException(string code, string message, string? detailsJson = null)
        : base(message)
    {
        Code = code;
        DetailsJson = detailsJson;
    }

    /// <summary>稳定失败码（机器判定用）：1..64 字符，首字符为字母，其余限 [A-Za-z0-9_.:-]。</summary>
    public string Code { get; }

    /// <summary>可选的上下文 JSON（工位/批次/测量值），已按字节上限与对象类型校验。</summary>
    public string? DetailsJson { get; }
}
