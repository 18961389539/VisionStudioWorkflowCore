# 节点级遥测（ActivitySource）

引擎有 `RunObservability`、`RollingFileLogger` 和 `/api/health`（认证后读取；匿名探针 `/healthz` 只报
status/ready/productionReady），但它们回答的是"这一轮跑成没跑成"。
要回答"哪一类节点在拖慢整条流程""超时集中在哪个节点"，只能靠 `node-durations.json` 的累计分位数——
拿不到单次执行的耗时，也没法跟外部 APM 对接。

`EngineTelemetry.ActivitySource` 补的就是这一层：每一次节点执行一个 `Activity`，标准
`System.Diagnostics` 口径，**没有监听者时零开销**（`StartActivity` 在无监听者时返回 null，
后续 `activity?.SetTag` 全部是空引用判断）。

- 定义：`backend/src/VisionStudio.Engine/Diagnostics/EngineTelemetry.cs`
- 埋点：`backend/src/VisionStudio.Engine/Runtime/VisionNodeDispatcher.cs`
- Source 名：`VisionStudio.Engine`

## 标签口径

| 标签 | 何时写 | 说明 |
|---|---|---|
| `vision.node.id` | span 起始 | 流程里的节点实例 id，用于对齐画布上的节点 |
| `vision.node.type` | span 起始 | 节点类型（`measure.caliper` 等），聚合分析按它分组 |
| `vision.run.id` | 有 runId 时 | 同一轮的多个节点共享，用于串成一条链 |
| `vision.scope` | 有工作域时 | 模块调用（`module.call`）下的作用域限定 |
| `vision.node.duration_ms` | `finally` | 与 `durations.Record` 复用同一个 `elapsedMs`，避免两处取时算出差值 |
| `vision.node.timed_out` | 超时分支 | `true`。只在本节点超时时出现 |

失败与超时会把 span 状态置为 `ActivityStatusCode.Error`，`description` 用异常消息。
异常本身没有被吞掉——`catch` 之后照原样重抛/记录，埋点不改变控制流。

## 并行分支故障与执行记录

`flow.parallel` 的融合分支共享运行数据，但运行级故障通过一个不可变快照原子发布：
`NodeId`、原始 `Exception` 和 `Error` 属于同一次失败。首个成功发布的未处理故障胜出，
后续失败不能覆盖错误文本或把节点 ID 与另一条异常拼接。这个“首个”由实际并发时序决定，
不是固定优先 branch1；运行结果、错误码和异常观察者均读取同一快照。

- 所有实际执行过的分支节点仍各自留下成功/失败报告，包括后续失败的原始错误文本与时间线。
  `NodeRunReport.Summary` 新增 `parallelRegionId`、`parallelBranch`，表示最内层并行区域及分支；
  隐式控制扇出、退化顺序执行也使用相同归属，普通顺序节点不增加这两个字段。
- 不改变调度策略：分支内仍顺序执行；某分支失败后不再执行其剩余节点，但不主动取消兄弟分支。
  并行段等待全部分支结束才向 Workflow Core 报错，失败时不执行后续 Join。
  没执行的节点不会伪造成功记录，报告数组按完成入队顺序保存；开始顺序看 `ExecutionSequence`。
- `flow.try` 只清除节点 ID **和原始异常实例**都匹配的已处理故障，不再依赖全局“最后失败节点”。
  已处理的失败报告保留；交互调试清错同时清除故障归因，下一次失败可重新占据槽位。
- 引擎扩展写入故障应调用 `VisionWorkflowData.RecordFault`，清除已处理故障须提供节点和异常；
  原 `Error`、`FaultedException`、`FaultedNodeId`、`LastFaultedNodeId` 改为只读视图，
  不允许继续分开赋值。需要成组读取时使用 `Fault` 快照。

`ParallelDispositionTests` 使用同步屏障确保两条分支都已进入，并控制第二分支在首条失败记录发布后
再完成/失败，验证错误归因不被覆盖、兄弟分支记录完整，以及失败分支尾部和 Join 不会执行。

## 为什么 span 建在 ValidateInputs 之后

端口接错（类型不匹配、必填端口没连）在编译期或输入校验阶段就被拒绝，那属于**流程定义错误**，
不是一次节点执行。如果把它也算进 span：

- 节点耗时统计里会混进 0 ms 的假执行，分位数被拉低；
- APM 上会看到大量"执行过但没干活"的节点，掩盖真实瓶颈。

同理，span 用 `using var` 绑定在方法作用域上，`finally` 里先补耗时标签再退出——
超时和异常路径都会留下带状态与耗时的 span，只记录成功的埋点等于只测了健康路径。

## 进程内消费者（2026-11-12，默认开启）

只埋点没有消费者时 `StartActivity` 返回 null，这一层在产线上恒为空。宿主因此内置了
`NodeSpanBuffer`（`backend/src/VisionStudio.Engine/Diagnostics/NodeSpanBuffer.cs`）：

- 构造时向 `EngineTelemetry.ActivitySource` 注册 `ActivityListener`，`Dispose` 时摘除；
- 环形缓冲 1024 条（约 200 KB），记录 `nodeId/nodeType/runId/scope/durationMs/timedOut/status/error`；
- **不落盘**：重启清零，避免上一批工艺的耗时混进本次排障；
- 两个出口：`GET /api/diagnostics/node-spans?recent=64`（Operator）与诊断包里的
  `diagnostics/node-spans.json`（Administrator）。

读的时候必须看 `overwritten`：大于 0 表示缓冲已回卷，`recent` 只是尾部，不是全量。分位数仍归
`NodeDurationStatistics`（`/api/diagnostics/node-durations`）——两者分工是"平时多慢"与"刚才那一次"。

测试隔离：`NodeSpanBuffer(capacity, source)` 的第二个参数允许传入私有 `ActivitySource`，
断言不会与并行测试共用静态 Source 而互相污染。

## 外部导出（可选）

进程内注册监听（自测、临时排障）：

```csharp
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == EngineTelemetry.SourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
        Console.WriteLine($"{activity.DisplayName} {activity.Duration.TotalMilliseconds:F1}ms")
};
ActivitySource.AddActivityListener(listener);
```

导出到 OpenTelemetry 时把 `EngineTelemetry.SourceName` 加进 `AddSource(...)`，
不要按 ActivitySource 的实例名去猜——名字改了要同步改这里，所以它是 `const`。

## Prometheus 指标出口（2026-11-13）

`GET /api/diagnostics/metrics`（Operator）按 Prometheus 0.0.4 文本格式暴露进程内可观测值，
不需要额外 NuGet 依赖，也不改变任何埋点路径：

| 指标 | 类型 | 来源 |
|---|---|---|
| `visionstudio_node_spans_captured_total` | counter | `NodeSpanBuffer.Snapshot().Captured` |
| `visionstudio_node_duration_{p50,p95,max}_milliseconds{node_type}` | gauge | `NodeDurationStatistics` 最近 256 个样本 |
| `visionstudio_node_duration_samples{node_type}` / `visionstudio_node_timeouts_window{node_type}` | gauge | 同上（窗口值，不是累计值） |
| `visionstudio_production_*` | counter/gauge | 生产运行状态与周期计数 |
| `visionstudio_audit_persist_failures_total` / `visionstudio_log_*_total` | counter | 审计/滚动日志失败台账 |
| `visionstudio_script_worker_*` | counter/gauge | 脚本 Worker 重启、忙碌数、熔断 |
| `visionstudio_camera_sdk_failures_total{driver,operation}` | counter | 相机 SDK 静默失败台账 |

抓取端需要携带 Operator 会话（与其它诊断端点一致）：先 `POST /api/auth/login` 获取 Cookie，
再带 Cookie 抓取。指标里不含参数值、图像或文件路径。

## 边界

| 不做 | 原因 |
|---|---|
| 不记录节点参数值 | 参数里可能有路径、阈值组合；参数属于流程文档，已经在库里 |
| 不记录图像 | 体积与隐私，留证走 `media` 接口 |
| 不在 `CameraFrameHub` 每帧建 span | 10 fps × 8 相机会把 span 数量打爆，帧层面的统计走 `framePool` 计数与 `node-durations` |
| 不做采样决策 | 采样是监听者/导出器的职责，引擎侧只负责产生完整数据 |

## 加埋点的地方要一起改的

1. 新标签写进 `EngineTelemetry` 的注释表格（标签散落在各节点里就再也对不齐了）；
2. 同步 `docs/quality-gates.md` 里没有涉及，但 `scripts/static-audit.py` 会断言
   `EngineTelemetry` 的存在与 Source 名——改常量名会让门禁红。
