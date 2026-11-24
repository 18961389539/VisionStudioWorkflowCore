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
public sealed class ValueCounterNode(IRunCounterStore? store = null, IDurableRunCounterStore? durableStore = null)
    : IVisionNodeExecutor, IVisionNodeDefinitionValidator
{
    public string Type => "value.counter";

    /// <summary>
    /// 状态存储必须由宿主注入（2026-10-29 修复）。此前这里有一个 <c>static Default</c> 兜底：
    /// 忘注册时节点照样能跑，但计数器落在进程级静态实例里——不同工作流互相串号、重启后静默清零，
    /// 现场只表现为"良率/连续 NG 数对不上"，没有任何报错可查。现在改为显式失败：
    /// 没有存储就说明宿主装配不完整，宁可停机也不产生"看起来对"的假数据。
    /// </summary>
    private IRunCounterStore Store => store ?? throw new InvalidOperationException(
        "value.counter requires an IRunCounterStore: register it in DI " +
        "(services.AddSingleton<IRunCounterStore, InMemoryRunCounterStore>()) or construct the node with a store. " +
        "Running without one would reset counters on every restart and share them across workflows.");

    public void ValidateDefinition(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs)
        => _ = ReadDurability(node);

    private static string ReadDurability(NodeDefinition node)
    {
        var durability = node.GetString("durability", "snapshot").Trim().ToLowerInvariant();
        if (durability is not ("snapshot" or "durable"))
            throw new InvalidOperationException("value.counter durability must be snapshot or durable.");
        if (durability == "durable" && !node.GetString("scope", "workflow").Trim().Equals("workflow", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("value.counter durable mode requires workflow scope.");
        return durability;
    }

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var durability = ReadDurability(node);

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

        var reset = manualReset || (pass && resetOnPass) || (!pass && hasPass && resetOnFail);
        var increment = (long)Math.Round(step, MidpointRounding.AwayFromZero);
        bool? passResult = hasPass ? pass : null;
        RunCounterReading reading;
        var reused = false;
        if (durability == "durable")
        {
            var eventId = context.Require<string>("eventId").Trim();
            if (eventId.Length is < 1 or > 256)
                throw new InvalidOperationException("value.counter eventId must be 1..256 characters in durable mode.");
            if (string.IsNullOrWhiteSpace(context.WorkflowId))
                throw new InvalidOperationException("value.counter durable mode requires a stable workflow ID.");
            var target = durableStore ?? throw new InvalidOperationException("value.counter durable mode requires an IDurableRunCounterStore.");
            (reading, reused) = await target.ApplyAsync(
                new DurableCounterEvent(context.WorkflowId, counterId, eventId, increment, passResult, reset),
                context.IsDebugRun, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var scopeKey = scopeMode == "run"
                ? context.RunId ?? context.WorkflowScope ?? "local"
                : context.WorkflowScope ?? "local";
            var state = Store.GetOrAdd(scopeKey, counterId);
            lock (state)
            {
                reading = new RunCounterReading(state.Count, state.Total, state.Pass, state.Fail).Apply(increment, passResult, reset);
                (state.Count, state.Total, state.Pass, state.Fail) = reading;
            }
        }

        var (count, total, passCount, failCount) = reading;
        var rate = total == 0 ? 0d : passCount / (double)total;
        var diagnostics = CoordinateValidation.Diagnostics(
            Type, ("counterId", counterId), ("scope", scopeMode), ("count", count), ("total", total),
            ("passCount", passCount), ("failCount", failCount), ("rate", rate),
            ("reset", manualReset), ("step", step), ("durability", durability), ("reused", reused),
            ("preview", durability == "durable" && context.IsDebugRun));

        return new NodeExecutorResult(
            new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = VisionValue.Integer(count),
                ["total"] = VisionValue.Integer(total),
                ["passCount"] = VisionValue.Integer(passCount),
                ["failCount"] = VisionValue.Integer(failCount),
                ["rate"] = VisionValue.Double(rate),
                ["reused"] = VisionValue.Boolean(reused),
                ["diagnostics"] = VisionValue.String(diagnostics)
            },
            new Dictionary<string, object?>
            {
                ["counterId"] = counterId, ["scope"] = scopeMode, ["count"] = count, ["total"] = total,
                ["passCount"] = passCount, ["failCount"] = failCount, ["rate"] = rate,
                ["reset"] = manualReset, ["step"] = step, ["durability"] = durability, ["reused"] = reused,
                ["preview"] = durability == "durable" && context.IsDebugRun, ["diagnostics"] = diagnostics
            });
    }
}
