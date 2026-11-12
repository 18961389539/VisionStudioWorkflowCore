# 节点级遥测（ActivitySource）

引擎有 `RunObservability`、`RollingFileLogger` 和 `/api/health`，但它们回答的是"这一轮跑成没跑成"。
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
