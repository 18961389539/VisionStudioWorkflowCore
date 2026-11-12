using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

public sealed partial class VisionWorkflowCompiler
{
    private CompileResult CompileStructured(
        WorkflowDefinition workflow,
        IReadOnlyList<EdgeDefinition> controlEdges,
        CompileContext context)
    {
        var ir = new StructuredWorkflowIrBuilder(workflow, controlEdges).Build();
        var steps = CompileSequence(ir.Root, continuationStepId: null, context);
        return new CompileResult(steps, ir.VisualOrder, ir.Regions);
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileSequence(
        StructuredSequenceIr sequence,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var steps = new List<Dictionary<string, object?>>();
        var items = sequence.Items;

        for (var i = 0; i < items.Count;)
        {
            if (loopNodeId is null && items[i] is StructuredNodeIr firstNode && IsPipelineEligible(firstNode.Node))
            {
                var segment = new List<NodeDefinition>();
                var j = i;
                while (j < items.Count && items[j] is StructuredNodeIr pipelineNode && IsPipelineEligible(pipelineNode.Node))
                {
                    segment.Add(pipelineNode.Node);
                    j++;
                }

                var next = j < items.Count ? FirstStepId(items[j]) : continuationStepId;
                steps.Add(CreatePipelineStep(segment, next, context));
                i = j;
                continue;
            }

            var item = items[i];
            var nextStepId = i + 1 < items.Count ? FirstStepId(items[i + 1]) : continuationStepId;
            switch (item)
            {
                case StructuredNodeIr node:
                    steps.Add(CreateVisionStep(node.Node.Id, nextStepId, loopNodeId));
                    break;

                case StructuredIfIr conditional:
                    steps.AddRange(CompileIf(conditional, nextStepId, context, loopNodeId));
                    break;

                case StructuredSwitchIr branch:
                    steps.AddRange(CompileSwitch(branch, nextStepId, context, loopNodeId));
                    break;

                case StructuredTryIr attempt:
                    ValidateTryBody(attempt, context.Edges);
                    steps.AddRange(CompileTry(attempt, nextStepId, context, loopNodeId));
                    break;

                case StructuredForEachIr loop:
                    steps.AddRange(CompileForEach(loop, nextStepId, context, loopNodeId));
                    break;

                case StructuredLoopIr loop:
                    steps.AddRange(CompileLoop(loop, nextStepId, context, loopNodeId));
                    break;

                case StructuredBreakIr breakItem:
                    steps.AddRange(CompileBreak(breakItem, nextStepId, context, loopNodeId));
                    break;

                case StructuredParallelIr parallel:
                    steps.AddRange(CompileParallel(parallel, nextStepId, context, loopNodeId));
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported structured IR item '{item.GetType().Name}'.");
            }
            i++;
        }

        return steps;
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileIf(
        StructuredIfIr conditional,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var node = conditional.Node;
        var truePrimitiveId = $"{node.Id}.__if_true";
        var falsePrimitiveId = $"{node.Id}.__if_false";
        var conditionExpression = $"data.BranchConditions[{JsonSerializer.Serialize(node.Id)}]";

        var marker = CreateVisionStep(node.Id, truePrimitiveId, loopNodeId);
        var truePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = truePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"data.Halted == false && {conditionExpression}" },
            ["Do"] = new object[] { CompileSequence(conditional.TrueBranch, continuationStepId: null, context, loopNodeId).ToArray() },
            ["NextStepId"] = falsePrimitiveId,
            ["ErrorBehavior"] = "Terminate"
        };
        var falsePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = falsePrimitiveId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"data.Halted == false && {conditionExpression} == false" },
            ["Do"] = new object[] { CompileSequence(conditional.FalseBranch, continuationStepId: null, context, loopNodeId).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            falsePrimitive["NextStepId"] = continuationStepId;

        return [marker, truePrimitive, falsePrimitive];
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileSwitch(
        StructuredSwitchIr branch,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var steps = new List<Dictionary<string, object?>>();
        var firstPrimitiveId = branch.Branches.Count == 0 ? continuationStepId : $"{branch.Node.Id}.__switch_{branch.Branches[0].Port}";
        steps.Add(CreateVisionStep(branch.Node.Id, firstPrimitiveId, loopNodeId));
        for (var index = 0; index < branch.Branches.Count; index++)
        {
            var item = branch.Branches[index];
            var primitiveId = $"{branch.Node.Id}.__switch_{item.Port}";
            var condition = $"data.Halted == false && data.SwitchSelections[{JsonSerializer.Serialize(branch.Node.Id)}] == {JsonSerializer.Serialize(item.Port)}";
            var primitive = new Dictionary<string, object?>
            {
                ["Id"] = primitiveId,
                ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
                ["Inputs"] = new Dictionary<string, string> { ["Condition"] = condition },
                ["Do"] = new object[] { CompileSequence(item.Sequence, continuationStepId: null, context, loopNodeId).ToArray() },
                ["ErrorBehavior"] = "Terminate"
            };
            if (index == branch.Branches.Count - 1 && continuationStepId is not null) primitive["NextStepId"] = continuationStepId;
            else if (index + 1 < branch.Branches.Count) primitive["NextStepId"] = $"{branch.Node.Id}.__switch_{branch.Branches[index + 1].Port}";
            steps.Add(primitive);
        }
        return steps;
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileTry(
        StructuredTryIr attempt,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        var successId = $"{attempt.Node.Id}.__try_success";
        var errorId = $"{attempt.Node.Id}.__try_error";
        var successCondition = $"data.TrySucceeded[{JsonSerializer.Serialize(attempt.Node.Id)}]";
        var marker = CreateVisionStep(attempt.Node.Id, successId, loopNodeId);
        var success = new Dictionary<string, object?>
        {
            ["Id"] = successId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = successCondition },
            ["Do"] = new object[] { CompileSequence(attempt.Success, continuationStepId: null, context, loopNodeId).ToArray() },
            ["NextStepId"] = errorId,
            ["ErrorBehavior"] = "Terminate"
        };
        var error = new Dictionary<string, object?>
        {
            ["Id"] = errorId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string> { ["Condition"] = $"{successCondition} == false" },
            ["Do"] = new object[] { CompileSequence(attempt.Error, continuationStepId: null, context, loopNodeId).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null) error["NextStepId"] = continuationStepId;
        return [marker, success, error];
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileForEach(
        StructuredForEachIr loop,
        string? continuationStepId,
        CompileContext context,
        string? enclosingLoopNodeId)
    {
        var primitiveId = $"{loop.Node.Id}.__foreach";
        var marker = CreateVisionStep(loop.Node.Id, primitiveId, enclosingLoopNodeId);
        var foreachPrimitive = new Dictionary<string, object?>
        {
            ["Id"] = primitiveId,
            ["StepType"] = "WorkflowCore.Primitives.Foreach, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string>
            {
                ["Collection"] = $"data.ForEachCollections[{JsonSerializer.Serialize(loop.Node.Id)}]",
                ["RunParallel"] = "false"
            },
            ["Do"] = new object[] { CompileSequence(loop.Body, continuationStepId: null, context, loop.Node.Id).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            foreachPrimitive["NextStepId"] = continuationStepId;
        return [marker, foreachPrimitive];
    }

    /// <summary>
    /// 通用循环编译：marker（循环节点自身执行一次，初始化循环状态）→ While 原语
    /// （Do = [begin, 循环体..., end]）。条件表达式只用到 &amp;&amp; / == 与字典索引器，
    /// 与既有 BranchConditions 表达式同一形态，不依赖求值器对比较运算符或方法调用的支持。
    /// </summary>
    private IReadOnlyList<Dictionary<string, object?>> CompileLoop(
        StructuredLoopIr loop,
        string? continuationStepId,
        CompileContext context,
        string? enclosingLoopNodeId)
    {
        var loopId = loop.Node.Id;
        var whileId = $"{loopId}.__while";
        var beginId = $"{loopId}.__loop_begin";
        var endId = $"{loopId}.__loop_end";

        var marker = CreateVisionStep(loopId, whileId, enclosingLoopNodeId);
        // 循环体统一以 end 步收尾：break 守卫即使被跳过也要走到 end，
        // 才能刷新条件并读到中断标志，从而在下一轮条件求值时退出。
        var bodySteps = CompileSequence(loop.Body, continuationStepId: endId, context, loopId).ToList();
        // begin 必须显式指向循环体第一步：Workflow Core 的容器从 Do 的第一个步骤开始，
        // 之后只沿 NextStepId 前进，缺少链路的步骤会被直接跳过（body 永不执行）。
        var firstBodyStepId = bodySteps.Count > 0 ? bodySteps[0]["Id"] as string : null;
        bodySteps.Insert(0, CreateLoopStep(beginId, loopId, "begin", loop.Kind, source: null, nextStepId: firstBodyStepId ?? endId));
        bodySteps.Add(CreateLoopStep(endId, loopId, "end", loop.Kind, loop.ConditionSource));

        var whilePrimitive = new Dictionary<string, object?>
        {
            ["Id"] = whileId,
            ["StepType"] = "WorkflowCore.Primitives.While, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string>
            {
                ["Condition"] = $"data.LoopConditions[{JsonSerializer.Serialize(loopId)}] && data.LoopBreaks[{JsonSerializer.Serialize(loopId)}] == false"
            },
            ["Do"] = new object[] { bodySteps.ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null) whilePrimitive["NextStepId"] = continuationStepId;
        return [marker, whilePrimitive];
    }

    /// <summary>
    /// break 编译：节点本身先执行（记录原因）→ 置位中断标志 → 用 If 守卫本轮剩余步骤。
    /// 守卫被跳过时直接接 continuation（循环末尾的 end 步），所以仍会刷新条件并正常退出。
    /// </summary>
    private IReadOnlyList<Dictionary<string, object?>> CompileBreak(
        StructuredBreakIr breakItem,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId)
    {
        if (loopNodeId is null)
            throw new InvalidOperationException($"flow.break '{breakItem.Node.Id}' must be compiled inside a flow.while or flow.repeat body.");
        var setBreakId = $"{breakItem.Node.Id}.__break";
        var marker = CreateVisionStep(breakItem.Node.Id, setBreakId, loopNodeId);
        var guardId = $"{breakItem.Node.Id}.__break_guard";
        var setBreak = CreateLoopStep(setBreakId, loopNodeId, "break", "while", source: null, nextStepId: guardId);
        var guard = new Dictionary<string, object?>
        {
            ["Id"] = guardId,
            ["StepType"] = "WorkflowCore.Primitives.If, WorkflowCore",
            ["Inputs"] = new Dictionary<string, string>
            {
                ["Condition"] = $"data.LoopBreaks[{JsonSerializer.Serialize(loopNodeId)}] == false"
            },
            ["Do"] = new object[] { CompileSequence(breakItem.Remaining, continuationStepId, context, loopNodeId).ToArray() },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null) guard["NextStepId"] = continuationStepId;
        return [marker, setBreak, guard];
    }

    private Dictionary<string, object?> CreateLoopStep(string stepId, string loopNodeId, string action, string kind, LoopConditionSource? source, string? nextStepId = null)
    {
        var inputs = new Dictionary<string, string>
        {
            ["LoopNodeId"] = JsonSerializer.Serialize(loopNodeId),
            ["Action"] = JsonSerializer.Serialize(action),
            ["Kind"] = JsonSerializer.Serialize(kind)
        };
        if (source is not null)
        {
            inputs["SourceNodeId"] = JsonSerializer.Serialize(source.NodeId);
            inputs["SourcePort"] = JsonSerializer.Serialize(source.Port);
        }
        var step = new Dictionary<string, object?>
        {
            ["Id"] = stepId,
            ["StepType"] = _loopStepType,
            ["Inputs"] = inputs,
            ["ErrorBehavior"] = "Terminate"
        };
        if (nextStepId is not null) step["NextStepId"] = nextStepId;
        return step;
    }

    private void ValidateTryBody(StructuredTryIr attempt, IReadOnlyList<EdgeDefinition> edges)
    {
        if (attempt.Body.Items.Any(item => item is not StructuredNodeIr))
            throw new InvalidOperationException($"Try '{attempt.Node.Id}' body must be a linear sequence of pure compute nodes; nested control regions are not supported.");
        var bodyIds = attempt.Body.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validConsumers = bodyIds.Concat(attempt.Success.NodeIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidEdge = edges.FirstOrDefault(edge => !WorkflowGraph.IsControlEdge(edge) &&
            bodyIds.Contains(edge.SourceNodeId) && !validConsumers.Contains(edge.TargetNodeId));
        if (invalidEdge is not null)
            throw new InvalidOperationException($"Try '{attempt.Node.Id}' body output '{invalidEdge.SourceNodeId}.{invalidEdge.SourcePort}' cannot flow to '{invalidEdge.TargetNodeId}.{invalidEdge.TargetPort}'; body values may only be consumed inside the body or success route, never error/outside routes.");
        foreach (var bodyNode in attempt.Body.Items.Cast<StructuredNodeIr>().Select(x => x.Node))
        {
            var catalog = registry.Require(bodyNode.Type).Catalog;
            if (!string.Equals(catalog.PluginId, "builtin", StringComparison.OrdinalIgnoreCase) ||
                VisionExecutionPolicy.Resolve(catalog) != VisionExecutionMode.Pipeline ||
                catalog.Capabilities?.Deterministic == false || catalog.Actions is not { Count: 0 })
                throw new InvalidOperationException($"Try '{attempt.Node.Id}' cannot retry body node '{bodyNode.Id}' ({bodyNode.Type}); automatic retry requires a deterministic built-in Pipeline node with an explicit empty Actions declaration.");
        }
    }

    private IReadOnlyList<Dictionary<string, object?>> CompileParallel(
        StructuredParallelIr parallel,
        string? continuationStepId,
        CompileContext context,
        string? loopNodeId = null)
    {
        ValidateParallelCapabilities(parallel.Branch1, parallel.Node.Id, "branch1");
        ValidateParallelCapabilities(parallel.Branch2, parallel.Node.Id, "branch2");

        var sequenceId = $"{parallel.Node.Id}.__parallel";
        // V0.64: 非循环体（loopNodeId is null）内，两条分支若全部由可融合节点组成，则合并为一个流水线段，
        // 由 VisionPipelineExecutor 按分支分组并发执行（此前用 Sequence 顺序执行两条分支，时间线上永远不会重叠）。
        // 含非融合节点（flow.result、设备节点等）的分支仍回退为顺序 Sequence；while/repeat 体内的 Parallel 因
        // loopNodeId 非空而不融合，保持串行语义。
        if (loopNodeId is null && TryCollectFusedBranchNodes(parallel.Branch1, out var branch1Nodes, out _) &&
            TryCollectFusedBranchNodes(parallel.Branch2, out var branch2Nodes, out _))
        {
            var fusedNodes = branch1Nodes.Concat(branch2Nodes).ToArray();
            var fusedMarker = CreateVisionStep(parallel.Node.Id, sequenceId, loopNodeId);
            return [fusedMarker, CreatePipelineStep(fusedNodes, continuationStepId, context, stepIdOverride: sequenceId)];
        }

        // 退化不能静默：画布上写的并行与产物（Sequence）不一致，必须让调用方看到原因。
        string sequentialReason;
        if (loopNodeId is not null)
            sequentialReason = "the Parallel node is inside a loop body, where branch fusion is disabled";
        else if (!TryCollectFusedBranchNodes(parallel.Branch1, out _, out var branch1Blocker))
            sequentialReason = $"branch1 contains {branch1Blocker}";
        else if (!TryCollectFusedBranchNodes(parallel.Branch2, out _, out var branch2Blocker))
            sequentialReason = $"branch2 contains {branch2Blocker}";
        else
            sequentialReason = "the branches could not be fused";
        context.AddWarning(new CompileWarning(
            "flow.parallel.sequential",
            $"Parallel node '{parallel.Node.Id}' runs both branches sequentially: {sequentialReason}. " +
            "Branch outputs and disposition are unchanged; only concurrency (cycle time) is affected.",
            parallel.Node.Id));

        var marker = CreateVisionStep(parallel.Node.Id, sequenceId, loopNodeId);
        var sequence = new Dictionary<string, object?>
        {
            ["Id"] = sequenceId,
            ["StepType"] = "WorkflowCore.Primitives.Sequence, WorkflowCore",
            ["Do"] = new object[]
            {
                CompileSequence(parallel.Branch1, continuationStepId: null, context, loopNodeId).ToArray(),
                CompileSequence(parallel.Branch2, continuationStepId: null, context, loopNodeId).ToArray()
            },
            ["ErrorBehavior"] = "Terminate"
        };
        if (continuationStepId is not null)
            sequence["NextStepId"] = continuationStepId;

        return [marker, sequence];
    }

    /// <summary>
    /// 分支可整体融合为一个流水线段的条件：全部为可融合普通节点（无嵌套控制结构）。
    /// blocker 说明第一条不满足条件的项，供“退化顺序执行”的编译提示使用。
    /// </summary>
    private bool TryCollectFusedBranchNodes(StructuredSequenceIr branch, out List<NodeDefinition> nodes, out string? blocker)
    {
        nodes = [];
        blocker = null;
        if (branch.Items.Count == 0)
        {
            blocker = "an empty branch";
            return false;
        }
        foreach (var item in branch.Items)
        {
            if (item is not StructuredNodeIr node)
            {
                blocker = "a nested control structure";
                nodes = [];
                return false;
            }
            if (!IsPipelineEligible(node.Node))
            {
                blocker = $"node '{node.Node.Id}' ({node.Node.Type}), whose execution mode or actions forbid pipeline fusion";
                nodes = [];
                return false;
            }
            nodes.Add(node.Node);
        }
        return true;
    }

    private void ValidateParallelCapabilities(StructuredSequenceIr sequence, string parallelId, string branchName)
    {
        foreach (var node in EnumerateNodes(sequence))
        {
            var catalog = registry.Require(node.Type).Catalog;
            if (catalog.Capabilities?.SupportsParallel == false)
                throw new InvalidOperationException(
                    $"Node '{node.Id}' ({node.Type}) declares SupportsParallel=false and cannot execute inside Parallel '{parallelId}' {branchName}.");
        }
    }

    private static IEnumerable<NodeDefinition> EnumerateNodes(StructuredSequenceIr sequence)
    {
        foreach (var item in sequence.Items)
        {
            switch (item)
            {
                case StructuredNodeIr node:
                    yield return node.Node;
                    break;
                case StructuredIfIr conditional:
                    yield return conditional.Node;
                    foreach (var child in EnumerateNodes(conditional.TrueBranch)) yield return child;
                    foreach (var child in EnumerateNodes(conditional.FalseBranch)) yield return child;
                    break;
                case StructuredSwitchIr branch:
                    yield return branch.Node;
                    foreach (var child in branch.Branches)
                        foreach (var node in EnumerateNodes(child.Sequence)) yield return node;
                    break;
                case StructuredTryIr attempt:
                    yield return attempt.Node;
                    foreach (var child in EnumerateNodes(attempt.Body)) yield return child;
                    foreach (var child in EnumerateNodes(attempt.Success)) yield return child;
                    foreach (var child in EnumerateNodes(attempt.Error)) yield return child;
                    break;
                case StructuredForEachIr loop:
                    yield return loop.Node;
                    foreach (var child in EnumerateNodes(loop.Body)) yield return child;
                    break;
                case StructuredLoopIr loop:
                    yield return loop.Node;
                    foreach (var child in EnumerateNodes(loop.Body)) yield return child;
                    break;
                case StructuredBreakIr breakItem:
                    yield return breakItem.Node;
                    foreach (var child in EnumerateNodes(breakItem.Remaining)) yield return child;
                    break;
                case StructuredParallelIr parallel:
                    yield return parallel.Node;
                    foreach (var child in EnumerateNodes(parallel.Branch1)) yield return child;
                    foreach (var child in EnumerateNodes(parallel.Branch2)) yield return child;
                    break;
            }
        }
    }

    private string FirstStepId(StructuredIrItem item) => item switch
    {
        StructuredNodeIr node => IsPipelineEligible(node.Node) ? PipelineStepId(node.Node.Id) : node.Node.Id,
        StructuredIfIr conditional => conditional.Node.Id,
        StructuredSwitchIr branch => branch.Node.Id,
        StructuredTryIr attempt => attempt.Node.Id,
        StructuredForEachIr loop => loop.Node.Id,
        StructuredLoopIr loop => loop.Node.Id,
        StructuredBreakIr breakItem => breakItem.Node.Id,
        StructuredParallelIr parallel => parallel.Node.Id,
        _ => throw new InvalidOperationException($"Unsupported structured IR item '{item.GetType().Name}'.")
    };
}
