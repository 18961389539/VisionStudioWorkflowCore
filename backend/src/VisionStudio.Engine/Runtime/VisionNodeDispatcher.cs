using System.Diagnostics;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Diagnostics;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// 节点执行入口。
/// </summary>
/// <param name="registry">节点注册表。</param>
/// <param name="durations">
/// 可选的每节点耗时统计。**默认 null**：绝大多数单元测试与嵌入式用法不需要它，
/// 加一个必填参数会逼着几十个调用点凭空构造一个从不使用的对象。
/// 宿主在 DI 里注册 <see cref="NodeDurationStatistics"/> 后它会被自动注入。
/// </param>
public sealed class VisionNodeDispatcher(VisionNodeRegistry registry, NodeDurationStatistics? durations = null)
{
    public IReadOnlyDictionary<string, VisionValue> ResolveInputs(VisionWorkflowData data, NodeDefinition node)
        => ResolveInputs(node, data.IncomingDataEdges(node.Id), data.OutputsByNode, registry);

    public ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        CancellationToken cancellationToken)
        => ExecuteAsync(node, inputs, workflowScope, cancellationToken, runId: null);

    public async ValueTask<NodeExecutorResult> ExecuteAsync(
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        CancellationToken cancellationToken,
        string? runId,
        IModelAssetSource? modelAssets = null)
    {
        var registration = registry.Require(node.Type);
        ValidateInputs(node, registration.Catalog, inputs);
        // 标准遥测（2026-11-12）：无监听者时 StartActivity 返回 null，不建对象、不改变控制流。
        // 放在 ValidateInputs 之后：端口校验失败属于"调用方接错线"，不该算一次节点执行。
        using var activity = EngineTelemetry.ActivitySource.StartActivity("node.execute");
        activity?.SetTag("vision.node.id", node.Id);
        activity?.SetTag("vision.node.type", node.Type);
        if (runId is not null) activity?.SetTag("vision.run.id", runId);
        if (workflowScope is not null) activity?.SetTag("vision.scope", workflowScope);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var timeoutMs = NodeTimeoutPolicy.Resolve(node);
        var timedOut = false;
        NodeExecutorResult result;
        try
        {
            result = await ExecuteWithTimeoutAsync(registration, node, inputs, workflowScope, runId, modelAssets, timeoutMs, cancellationToken);
        }
        catch (NodeTimeoutException)
        {
            timedOut = true;
            activity?.SetTag("vision.node.timed_out", true);
            activity?.SetStatus(ActivityStatusCode.Error, "node timeout");
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            // 只统计、不改变任何控制流：耗时样本用于给 __timeoutMs 选值（见 NodeDurationStatistics）。
            // 放在 finally 里是为了把"超时"和"抛异常"两类执行也计入——恰恰是最需要被看到的样本。
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            durations?.Record(
                node.Type,
                elapsedMs,
                timeoutMs,
                timedOut);
            activity?.SetTag("vision.node.duration_ms", elapsedMs);
        }
        try
        {
            result = NodeOutputContracts.Telemetry(node, registration.Catalog, result, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            result = AttachImageCoordinateFrames(node, registration.Catalog, inputs, result);
            ValidateOutputs(node, registration.Catalog, result.Outputs);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // A rejected contract must release returned resources before they enter the run's ownership table.
            var released = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var value in result.Outputs.Values)
                if (value.Value is IDisposable disposable && released.Add(disposable)
                    && !inputs.Values.Any(input => ReferenceEquals(input.Value, disposable)))
                    try { disposable.Dispose(); } catch { /* preserve the contract error */ }
            throw;
        }
    }

    /// <summary>
    /// 执行一次节点，并在节点声明了 <c>__timeoutMs</c> 时套一层节点级预算。
    /// 未声明时走原路径（不建 CTS、不加任何包装），保证既有流程零开销零行为变化。
    /// </summary>
    private static async ValueTask<NodeExecutorResult> ExecuteWithTimeoutAsync(
        VisionNodeRegistration registration,
        NodeDefinition node,
        IReadOnlyDictionary<string, VisionValue> inputs,
        string? workflowScope,
        string? runId,
        IModelAssetSource? modelAssets,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var context = new NodeExecutionContext(inputs, workflowScope, runId, modelAssets);
        if (timeoutMs == 0)
            return await registration.ExecuteAsync(context, node, cancellationToken);

        using var nodeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        nodeBudget.CancelAfter(timeoutMs);
        try
        {
            return await registration.ExecuteAsync(context, node, nodeBudget.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && nodeBudget.IsCancellationRequested)
        {
            // 只有"外层没取消、我们的预算到期"才归因为节点超时；整轮超时仍走原有的取消路径，
            // 否则会被误报成某个节点的责任。
            throw new NodeTimeoutException(node.Type, timeoutMs, System.Diagnostics.Stopwatch.GetElapsedTime(started), ex);
        }
    }

    private static NodeExecutorResult AttachImageCoordinateFrames(
        NodeDefinition node,
        NodeCatalogItem catalog,
        IReadOnlyDictionary<string, VisionValue> inputs,
        NodeExecutorResult result)
    {
        // 帧语义来自目录声明（内置与插件同一路径）：宿主不再按节点类型字符串猜测。
        var rule = NodeFrameSemantics.Resolve(node, catalog);
        var imageInputPort = rule?.InputPort ?? "image";
        var inputFrame = TryGetImageFrame(inputs, imageInputPort);
        var outputFrame = NodeFrameSemantics.ImageFrameKey(
            node.Id,
            rule,
            port => TryGetImageFrame(inputs, port),
            port => inputs.TryGetValue(port, out var value) && value.Value is VisionTransform2D transform
                ? transform.TargetFrame
                : null) ?? inputFrame ?? $"image:{node.Id}:source";

        Dictionary<string, VisionValue>? rewrittenOutputs = null;
        foreach (var port in catalog.Outputs.Where(port => port.DataType == VisionDataType.Image))
        {
            if (!result.Outputs.TryGetValue(port.Name, out var value) || value.Value is not VisionImage image)
                continue;
            var inputImage = inputs.TryGetValue(imageInputPort, out var inputValue) ? inputValue.Value : null;
            if (!ReferenceEquals(image, inputImage))
                image.SetCoordinateFrame(outputFrame);
        }

        if (rule?.Transform is { } transformPolicy
            && transformPolicy != NodeTransformFramePolicy.None
            && result.Outputs.TryGetValue("transform", out var transformValue)
            && transformValue.Value is VisionTransform2D transform)
        {
            var framedTransform = transformPolicy switch
            {
                NodeTransformFramePolicy.Spatial => transform with
                {
                    SourceFrame = inputFrame ?? "CurrentImage",
                    TargetFrame = outputFrame
                },
                NodeTransformFramePolicy.RoiReference => transform with
                {
                    SourceFrame = $"roi:{node.Id}:reference",
                    TargetFrame = inputFrame ?? "CurrentImage"
                },
                NodeTransformFramePolicy.ReferenceInput => transform with
                {
                    SourceFrame = inputFrame ?? "CurrentImage",
                    TargetFrame = TryGetImageFrame(inputs, rule.ReferencePort ?? "reference") ?? "ReferenceImage"
                },
                _ => null
            };

            if (framedTransform is not null)
            {
                rewrittenOutputs = result.Outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                rewrittenOutputs["transform"] = VisionValue.Transform(framedTransform);
            }
        }

        return rewrittenOutputs is null ? result : result with { Outputs = rewrittenOutputs };
    }

    private static string? TryGetImageFrame(IReadOnlyDictionary<string, VisionValue> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var value) || value.Type != VisionDataType.Image)
            return null;
        return value.Value is VisionImage image ? image.CoordinateFrame : "CurrentImage";
    }

    private static Dictionary<string, VisionValue> ResolveInputs(
        NodeDefinition node,
        IReadOnlyList<EdgeDefinition> incomingDataEdges,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, VisionValue>> outputsByNode,
        VisionNodeRegistry registry)
    {
        var result = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase);
        var targetCatalog = registry.Require(node.Type).Catalog;

        foreach (var edge in incomingDataEdges)
        {
            var expected = targetCatalog.Inputs.FirstOrDefault(p => p.Name.Equals(edge.TargetPort, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{edge.TargetPort}' does not exist on node type '{node.Type}'.");

            if (!outputsByNode.TryGetValue(edge.SourceNodeId, out var sourceOutputs))
            {
                // 回边（loop condition）在首轮尚未执行：可选输入按"未接线"处理，
                // 必填输入仍然报错，避免把真正的接线错误吞掉。
                if (!expected.IsRequired(node, targetCatalog)) continue;
                throw new InvalidOperationException($"Source node '{edge.SourceNodeId}' has not executed.");
            }
            if (!sourceOutputs.TryGetValue(edge.SourcePort, out var value))
            {
                if (!expected.IsRequired(node, targetCatalog)) continue;
                throw new InvalidOperationException($"Output '{edge.SourcePort}' does not exist on '{edge.SourceNodeId}'.");
            }

            if (!AreCompatible(value.Type, expected.DataType))
                throw new InvalidOperationException($"Runtime type mismatch on {edge.SourceNodeId}.{edge.SourcePort} -> {node.Id}.{edge.TargetPort}: {value.Type} -> {expected.DataType}.");

            if (!result.TryAdd(expected.Name, value))
                throw new InvalidOperationException($"Input '{node.Id}.{expected.Name}' has multiple data sources. One source per data input is required.");
        }

        ValidateInputs(node, targetCatalog, result);
        return result;
    }

    private static void ValidateInputs(NodeDefinition node, NodeCatalogItem catalog, IReadOnlyDictionary<string, VisionValue> inputs)
    {
        foreach (var required in catalog.Inputs.Where(p => p.DataType != VisionDataType.Control && p.IsRequired(node, catalog)))
        {
            if (!inputs.ContainsKey(required.Name))
                throw new InvalidOperationException($"Required input '{node.Id}.{required.Name}' ({required.DataType}) is not connected.");
        }
        foreach (var input in inputs)
        {
            var port = catalog.Inputs.FirstOrDefault(p => p.Name.Equals(input.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Input port '{input.Key}' does not exist on node type '{node.Type}'.");
            if (!AreCompatible(input.Value.Type, port.DataType))
                throw new InvalidOperationException($"Runtime type mismatch on {node.Id}.{input.Key}: {input.Value.Type} -> {port.DataType}.");
        }
    }

    private static void ValidateOutputs(NodeDefinition node, NodeCatalogItem catalog, IReadOnlyDictionary<string, VisionValue> outputs)
    {
        foreach (var required in catalog.Outputs.Where(port => port.DataType != VisionDataType.Control && port.IsRequired(node, catalog)))
            if (!outputs.ContainsKey(required.Name))
                throw new InvalidOperationException($"Executor '{node.Type}' did not return required output '{required.Name}' on node '{node.Id}'.");
        foreach (var output in outputs)
        {
            var port = catalog.Outputs.FirstOrDefault(p => p.Name.Equals(output.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Executor '{node.Type}' returned undeclared output '{output.Key}'.");
            if (!AreCompatible(output.Value.Type, port.DataType))
                throw new InvalidOperationException($"Executor '{node.Type}' returned {output.Value.Type} on '{output.Key}', catalog declares {port.DataType}.");
        }
    }

    public static bool AreCompatible(VisionDataType source, VisionDataType target)
    {
        if (source == VisionDataType.Control || target == VisionDataType.Control)
            return source == target;
        return source == target || source == VisionDataType.Any || target == VisionDataType.Any ||
            (source == VisionDataType.Integer && target == VisionDataType.Double);
    }
}
