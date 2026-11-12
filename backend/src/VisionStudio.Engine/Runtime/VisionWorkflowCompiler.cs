using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VisionStudio.Engine.Runtime;

/// <summary>
/// Compiles the vendor-neutral visual graph into Workflow Core JSON DSL.
/// V0.29 first resolves explicit Control edges into a recursive structured IR, then emits
/// Workflow Core primitives while retaining VisionPipelineStep fusion inside every nested region.
/// </summary>
public sealed partial class VisionWorkflowCompiler(VisionNodeRegistry registry)
{
    private sealed record CoordinateFrame(string Key, string Label, bool IsTraceable = true);
    private sealed record TransformFrames(
        CoordinateFrame? Source,
        CoordinateFrame? Target,
        IReadOnlyList<CoordinateFrame> MappedFrames);

    private const string ReservedPipelinePrefix = "__vs_pipeline_";
    private const int PlanSchemaVersion = 1;
    private readonly object _identityGate = new();
    private long _catalogRevision = -1;
    private string _catalogIdentity = string.Empty;
    /// <summary>
    /// DSL 是给人读、给 Workflow Core 解析的工件（非 HTML 场景）：默认编码器会把条件表达式里的
    /// 引号转义成 \u0022，视图/日志里难以辨认；relaxed 编码输出标准的 \" 转义，两种形式解析等价。
    /// </summary>
    private static readonly JsonSerializerOptions DslJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string GetPlanCacheKey(WorkflowDefinition workflow)
    {
        // 迁移后再取指纹：同一份旧文档与它迁移后的新文档必须命中同一个计划缓存条目。
        workflow = NodeTypeMigration.Normalize(workflow);
        string identity;
        lock (_identityGate)
        {
            var revision = registry.Revision;
            if (_catalogRevision != revision)
            {
                _catalogIdentity = VisionCatalogSnapshot.ComputeHash(registry.Catalog);
                _catalogRevision = revision;
            }
            identity = _catalogIdentity;
        }
        // Include the full identity: sanitized workflow IDs can otherwise alias each other.
        var input = System.Text.Json.JsonSerializer.Serialize(new
        {
            workflow.Id,
            Workflow = WorkflowFingerprint.Compute(workflow),
            Catalog = identity,
            Compiler = typeof(VisionWorkflowCompiler).Module.ModuleVersionId,
            Schema = PlanSchemaVersion
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private readonly string _visionStepType = typeof(VisionNodeStep).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionNodeStep assembly name.");

    private readonly string _pipelineStepType = typeof(VisionPipelineStep).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionPipelineStep assembly name.");

    /// <summary>通用循环（flow.while / flow.repeat / flow.break）的迭代步类型。</summary>
    private readonly string _loopStepType = typeof(VisionLoopStep).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionLoopStep assembly name.");

    private readonly string _dataType = typeof(VisionWorkflowData).AssemblyQualifiedName
        ?? throw new InvalidOperationException("Cannot resolve VisionWorkflowData assembly name.");

    public CompiledWorkflow Compile(WorkflowDefinition workflow)
    {
        // 已合并的旧节点类型在编译入口改写为规范类型（含参数补齐），旧文档因此无需手工迁移。
        workflow = NodeTypeMigration.Normalize(workflow);
        if (workflow.Nodes.Count == 0)
            throw new InvalidOperationException("Workflow has no nodes.");
        if (workflow.Nodes.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != workflow.Nodes.Count)
            throw new InvalidOperationException("Workflow contains duplicate node IDs.");
        if (workflow.Nodes.Any(x => x.Id.StartsWith(ReservedPipelinePrefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Node IDs beginning with '{ReservedPipelinePrefix}' are reserved for the compiled hybrid runtime.");

        ValidateCatalogAndEdges(workflow);

        var hash = GetPlanCacheKey(workflow);
        var workflowCoreId = $"vision-{Sanitize(workflow.Id)}-{hash}";
        var context = new CompileContext(workflow.Edges);

        var controlEdges = workflow.Edges.Where(WorkflowGraph.IsControlEdge).ToArray();
        var compiled = controlEdges.Length == 0
            ? CompileLegacyLinear(workflow, context)
            : CompileStructured(workflow, controlEdges, context);

        var dsl = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Id"] = workflowCoreId,
            ["Version"] = 1,
            ["DataType"] = _dataType,
            ["Steps"] = compiled.Steps
        }, DslJsonOptions);

        return new CompiledWorkflow(
            workflowCoreId,
            1,
            dsl,
            compiled.VisualOrder,
            context.SnapshotSegments(),
            compiled.ControlRegions,
            context.SnapshotWarnings());
    }
}

public sealed record CompiledWorkflow(
    string WorkflowCoreId,
    int Version,
    string DslJson,
    IReadOnlyList<string> OrderedNodeIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PipelineSegments,
    IReadOnlyList<StructuredControlRegion> ControlRegions,
    IReadOnlyList<CompileWarning> Warnings);

/// <summary>
/// 非致命编译提示：不改变流程结果，但用户需要知道编译产物与画布直觉不一致的地方
/// （典型：flow.parallel 退化为顺序执行）。Warnings 是编译产物的披露，不是校验失败。
/// </summary>
public sealed record CompileWarning(string Code, string Message, string? NodeId = null);
