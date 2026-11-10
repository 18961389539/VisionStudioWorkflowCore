namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 控制流节点的语义分类（2026-11-10 · 设计审计 P0-1）。
/// 编译器 / 结构化 IR / 运行时原先各自维护一份 "flow.xxx" 字符串比较；这里收敛为单一事实源。
/// 新增控制流节点只需在这里登记一次，避免三处漏改只在运行时暴露。
/// </summary>
public static class FlowSemantics
{
    private static bool Is(string type, string candidate) => type.Equals(candidate, StringComparison.OrdinalIgnoreCase);

    public static bool IsConditional(string type) => Is(type, "flow.if") || Is(type, "flow.ifBoolean");
    public static bool IsLoop(string type) => Is(type, "flow.forEach") || Is(type, "flow.while") || Is(type, "flow.repeat");
    public static bool IsWhile(string type) => Is(type, "flow.while");
    public static bool IsRepeat(string type) => Is(type, "flow.repeat");
    public static bool IsLoopEnd(string type) => Is(type, "flow.loopEnd");
    public static bool IsBreak(string type) => Is(type, "flow.break");
    public static bool IsSwitch(string type) => Is(type, "flow.switch");
    public static bool IsTry(string type) => Is(type, "flow.try");
    public static bool IsParallel(string type) => Is(type, "flow.parallel");
    public static bool IsJoin(string type) => Is(type, "flow.join");
    public static bool IsMerge(string type) => Is(type, "flow.merge");
    public static bool IsJoinOrMerge(string type) => IsJoin(type) || IsMerge(type);
    public static bool IsResult(string type) => Is(type, "flow.result");
    public static bool IsIterativeOrRetry(string type) => IsLoop(type) || IsTry(type);

    public static string LoopKind(string type) =>
        Is(type, "flow.while") ? "while" : Is(type, "flow.repeat") ? "repeat" : "forEach";

    public static string ConditionalLabel(string type) => Is(type, "flow.ifBoolean") ? "IfBoolean" : "If";
}
