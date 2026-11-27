namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 控制流节点的语义分类（2026-11-10 · 设计审计 P0-1）。
/// 编译器 / 结构化 IR / 运行时原先各自维护一份 "flow.xxx" 字符串比较；这里收敛为单一事实源。
/// 新增控制流节点只需在这里登记一次，避免三处漏改只在运行时暴露。
/// </summary>
public static class FlowSemantics
{
    private static bool Is(string type, string candidate) => BuiltInNodeTypes.Is(type, candidate);

    public static bool IsConditional(string type) => Is(type, BuiltInNodeTypes.FlowIf) || Is(type, BuiltInNodeTypes.FlowIfBoolean);
    public static bool IsForEach(string type) => Is(type, BuiltInNodeTypes.FlowForEach);
    public static bool IsLoop(string type) => IsForEach(type) || IsWhile(type) || IsRepeat(type);
    public static bool IsWhile(string type) => Is(type, BuiltInNodeTypes.FlowWhile);
    public static bool IsRepeat(string type) => Is(type, BuiltInNodeTypes.FlowRepeat);
    public static bool IsLoopEnd(string type) => Is(type, BuiltInNodeTypes.FlowLoopEnd);
    public static bool IsBreak(string type) => Is(type, BuiltInNodeTypes.FlowBreak);
    public static bool IsSwitch(string type) => Is(type, BuiltInNodeTypes.FlowSwitch);
    public static bool IsTry(string type) => Is(type, BuiltInNodeTypes.FlowTry);
    public static bool IsParallel(string type) => Is(type, BuiltInNodeTypes.FlowParallel);
    public static bool IsJoin(string type) => Is(type, BuiltInNodeTypes.FlowJoin);
    public static bool IsMerge(string type) => Is(type, BuiltInNodeTypes.FlowMerge);
    public static bool IsJoinOrMerge(string type) => IsJoin(type) || IsMerge(type);
    public static bool IsResult(string type) => Is(type, BuiltInNodeTypes.FlowResult);
    public static bool IsIterativeOrRetry(string type) => IsLoop(type) || IsTry(type);

    public static string LoopKind(string type) =>
        IsWhile(type) ? "while" : IsRepeat(type) ? "repeat" : "forEach";

    public static string ConditionalLabel(string type) => Is(type, BuiltInNodeTypes.FlowIfBoolean) ? "IfBoolean" : "If";
}
