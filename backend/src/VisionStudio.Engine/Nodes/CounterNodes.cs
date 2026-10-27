using VisionStudio.Abstractions;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// 跨轮计数（value.counter，2026-10-27 · 缺口分析 P1-9 的核心痛点）。
///
/// 现场缺口：`value.statistics` 只统计**本轮**的列表，没有跨轮记忆。于是
/// "连续 3 件 NG 停机""本班次良率 98.2%""第 N 件产品"这三类最常见的判定全部做不出来，
/// 工程师只能在上位机另写一段统计代码。
///
/// 语义（顺序很关键，写在代码里也写在文档里）：
/// 1. `reset` 端口为真 → 先清零（人工复位/换班）；
/// 2. 判定为通过且 `resetOnPass` → 先清零（"连续 NG" 计数器的复位点）；
/// 3. 判定为失败且 `resetOnFail` → 先清零（"连续 OK" 计数器）；
/// 4. `count += step`；接入了 `pass` 时 `total/pass/fail` 同步累加。
///
/// 先复位再计数，是为了让"复位后这一件"算作第 1 件，而不是第 0 件——这一步搞反会让
/// 连续 NG 门限整体偏移一件，是最容易差一件的地方。
/// </summary>
public sealed class ValueCounterNode(IRunCounterStore? store = null) : IVisionNodeExecutor
{
    public string Type => "value.counter";

    private static readonly InMemoryRunCounterStore Default = new();

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var counterId = node.GetString("counterId", "counter").Trim();
        if (counterId.Length is < 1 or > 64)
            throw new InvalidOperationException("value.counter counterId must be 1..64 characters.");
        var scopeMode = node.GetString("scope", "workflow").Trim().ToLowerInvariant();
        if (scopeMode is not ("workflow" or "run"))
            throw new InvalidOperationException("value.counter scope must be workflow or run.");
        var step = node.GetDouble("step", 1);
        if (!double.IsFinite(step) || Math.Abs(step) > 1_000_000)
            throw new InvalidOperationException("value.counter step must be finite within +/-1000000.");
        var resetOnPass = node.GetBool("resetOnPass", false);
        var resetOnFail = node.GetBool("resetOnFail", false);

        var hasPass = context.TryGetValue("pass", out var passInput);
        var pass = hasPass && passInput.Require<bool>("pass");
        var manualReset = context.TryGetValue("reset", out var resetInput) && resetInput.Require<bool>("reset");

        // 作用域键：workflow 跨 run 保留，run 跟随单次运行；两者都缺失时退化成进程级默认桶（单测场景）。
        var scopeKey = scopeMode == "run"
            ? context.RunId ?? context.WorkflowScope ?? "local"
            : context.WorkflowScope ?? "local";
        var state = (store ?? Default).GetOrAdd(scopeKey, counterId);

        long count;
        long total;
        long passCount;
        long failCount;
        lock (state)
        {
            if (manualReset || (pass && resetOnPass) || (!pass && hasPass && resetOnFail)) state.Reset();
            state.Count += (long)Math.Round(step, MidpointRounding.AwayFromZero);
            if (hasPass)
            {
                state.Total++;
                if (pass) state.Pass++; else state.Fail++;
            }
            count = state.Count;
            total = state.Total;
            passCount = state.Pass;
            failCount = state.Fail;
        }

        var rate = total == 0 ? 0d : passCount / (double)total;
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("counterId", counterId), ("scope", scopeMode), ("count", count), ("total", total),
            ("passCount", passCount), ("failCount", failCount), ("rate", rate),
            ("reset", manualReset), ("step", step));

        return ValueTask.FromResult(new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = VisionValue.Integer(count),
                ["total"] = VisionValue.Integer(total),
                ["passCount"] = VisionValue.Integer(passCount),
                ["failCount"] = VisionValue.Integer(failCount),
                ["rate"] = VisionValue.Double(rate),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["counterId"] = counterId, ["scope"] = scopeMode, ["count"] = count, ["total"] = total,
                ["passCount"] = passCount, ["failCount"] = failCount, ["rate"] = rate,
                ["reset"] = manualReset, ["step"] = step, ["diagnostics"] = diagnostics
            }));
    }
}
