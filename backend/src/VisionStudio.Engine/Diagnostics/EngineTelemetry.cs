using System.Diagnostics;

namespace VisionStudio.Engine.Diagnostics;

/// <summary>
/// 引擎标准遥测入口。宿主与嵌入式用法默认**不导出**：没有监听者时
/// <see cref="ActivitySource.StartActivity(string)"/> 返回 null，开销是几次静态判断，
/// 不改变任何执行语义，也不引入 OpenTelemetry 依赖。
///
/// 现场排障需要接 Jaeger/OTLP 时，在宿主侧注册监听即可：
/// <code>
/// using var listener = new ActivityListener
/// {
///     ShouldListenTo = source => source.Name == EngineTelemetry.SourceName,
///     Sample = (ref ActivityCreationOptions&lt;ActivityContext&gt; _) => ActivitySamplingResult.AllDataAndRecorded,
///     ActivityStopped = activity => exporter.Export(activity)
/// };
/// ActivitySource.AddActivityListener(listener);
/// </code>
///
/// 标签口径（改名前先看 docs/trace-query-design-improvements.md 里的查询字段）：
/// <list type="bullet">
/// <item><c>vision.node.id</c> / <c>vision.node.type</c>：节点实例与类型；</item>
/// <item><c>vision.run.id</c>：运行 id，可为空（校验、单节点试跑）；</item>
/// <item><c>vision.scope</c>：子流程/模块作用域；</item>
/// <item><c>vision.node.duration_ms</c> / <c>vision.node.timed_out</c>：耗时与超时归因。</item>
/// </list>
/// </summary>
public static class EngineTelemetry
{
    public const string SourceName = "VisionStudio.Engine";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
}
