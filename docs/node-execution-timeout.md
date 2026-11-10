# 节点级执行超时（`__timeoutMs`）

2026-10-28。对应"流程控制 P0-1"。

## 结论先说

此前**只有整轮预算**一个超时口径：`VisionRunOptions.TimeoutMs`（默认 10 s，钳在 100 ms–24 h；与节点上限共用 `NodeTimeoutPolicy.AbsoluteMaxTimeoutMs`）。
一个慢节点会吃掉全部预算，超时只报 `Workflow Core execution timed out after N ms`，
**既定位不到节点，也无法给相机采集（500 ms）、HTTP（2 s）、机器人运动（30 s）分别设限**。

本轮加了节点级预算：节点参数 `__timeoutMs`，超出即在该节点失败，错误码 `node_timeout`。

## 语义

| 场景 | 归因 | `ErrorCode` |
|---|---|---|
| 节点超出自身 `__timeoutMs` | 该节点的责任 | `node_timeout` |
| 节点未配 `__timeoutMs`，整轮到点 | 整轮看门狗 | `timeout` |
| 调用方取消（Ctrl+C / 客户端断开） | 调用方 | `cancelled` |
| 节点抛普通异常 | 该节点 | `node_failure` |

三者必须可区分——混在一起正是"超时只说整个流程超时"的根因。

## 实现

- `NodeTimeoutPolicy`（`Engine/Runtime/NodeTimeoutPolicy.cs`）：解析与钳制。非正数 = 不设限。
- `NodeTimeoutException`：**刻意不继承** `OperationCanceledException`。若复用取消异常，
  会被 `catch (OperationCanceledException) when (token.IsCancellationRequested)` 误判为整轮取消而中断运行。
- `VisionNodeDispatcher.ExecuteWithTimeoutAsync`：唯一执行入口加一层 linked CTS。
  `VisionNodeRuntime` 与 `VisionPipelineExecutor`（含并行分支）都经此路径，覆盖完整。
- `WorkflowCoreVisionRunner`：错误码分支加 `NodeTimeoutException` 判定。

未配置 `__timeoutMs` 时**不建 CTS、不加任何包装**，既有流程零开销零行为变化。

## 上限可配置（`Pipeline:NodeTimeoutMaxMs`）

上限默认 **600 000 ms**（`NodeTimeoutPolicy.MaxTimeoutMs`，与整轮预算同级）。
确有长周期节点（烘烤、长时间 TCP 握手、多帧堆叠）的现场可以抬高：

```json
{ "Pipeline": { "NodeTimeoutMaxMs": 1800000 } }
```

- 取值范围 **[1 000, 86 400 000] ms**，越界时**启动即失败**并在异常里写明合法区间。
  不做静默回落：现场会以为"已经调大了"，实际节点照样被 600 s 砍掉；而放任调到天文数字
  又等于关掉看门狗（卡住的节点永不报错，节拍慢慢烂掉）。两种误解都比一条启动错误昂贵。
- 上限本身封顶 24 h。抬高上限**不会**自动延后整轮看门狗——`VisionRunOptions.TimeoutMs` 仍然先到期，
  所以给单节点配 30 min 预算时，整轮预算必须一起抬；整轮预算现在同样允许到 24 h，
  否则 600 s 以上的节点预算永远报不出 `node_timeout`。
- 插件 worker 的执行 IPC 超时跟随节点的 `__timeoutMs`；节点未配置时才使用
  `PluginWorker:ExecutionTimeoutMs`（默认 30 s）作为兜底。
- 运行期不要改：它是启动期一次性配置，不是调参入口。

## 该填多少：`/api/diagnostics/node-durations`

选 `__timeoutMs` 不该靠感觉。`NodeDurationStatistics` 按节点类型保留最近 256 次执行的耗时
（环形覆盖、不落盘），`VisionNodeDispatcher` 每次执行都喂一条——**超时和抛异常的那次也计入**，
因为那正是最需要被看到的样本。

```bash
curl -H "Authorization: Bearer <operator-token>" http://localhost:5000/api/diagnostics/node-durations
```

| 字段 | 用途 |
|---|---|
| `p95Ms` | 选值的依据。不要看平均值：平均值会把偶发尖峰稀释掉 |
| `suggestedTimeoutMs` | `p95 × 1.5`，向上取整到 100 ms，再钳进 `[100, 当前上限]`；样本 < 20 条时为 `null` |
| `configuredTimeoutMs` | 该类型节点最近一次执行携带的值，用来和 `p95Ms` 对照 |
| `timeoutTooTight` | `p95Ms > configured × 0.8`：按正态的尾部走，下一次抖动就会误杀 |
| `timeoutCount` | 窗口内真实触发超时的次数——确凿证据，不是推断 |

样本不足时不给建议是刻意的：5 个样本的"95 分位"就是最大值，据此推建议等于把一次偶发尖峰固化成配置。

## 边界（必须知道的限制）

1. **取消是协作式的**。只能中断会在等待点检查 `CancellationToken` 的节点。
   卡在原生库内部且不轮询取消的调用不会被强行打断。
   硬超时若靠"放弃等待"实现，后台任务会继续持有设备写权限（`DeviceCommandOwnership`），风险高于收益，故不提供。
2. **不按节点类型给默认值**。猜错的默认超时会把正常的慢节点误杀，比没有超时更糟。
   需要默认值的场景应按节点族单独论证，不要一刀切。
3. **与 Try 自动重试的交互**：`flow.try` 的自动重试在编译期只接受"无副作用的纯计算节点"。
   通讯/采集类节点配了预算又放进 Try 重试体，本来就会在编译期被拒绝（既有约束，非本轮引入）。
4. **节点预算包住整个 Try**：`__timeoutMs` 配在 `flow.try` 节点上时，重试与 body 执行共享这一份预算。

## 未做（后续）

- 目录级默认超时（`NodeCatalogItem` 声明），需要契约变更与前端导出刷新。
- 并行分支的**独立**预算：当前每个节点各自计时，`Task.WhenAll` 的整体等待仍受整轮约束。
- `/api/diagnostics/node-durations` 尚未接到前端界面上（现在只能 curl）。接了就变成
  "配置页里直接显示这个节点的 P95 与建议值"，那才是真正省事的一步。

## 验证

- `backend/tests/VisionStudio.Engine.Tests/NodeTimeoutTests.cs`：8 项（4 条归因分支 + 钳制理论值 + 上限配置与非法值）。
- `backend/tests/VisionStudio.Engine.Tests/NodeDurationStatisticsTests.cs`：10 项
  （最近秩分位数、样本门槛、环形覆盖、偏紧判定、排序、非法样本、建议值受上限钳制、
  dispatcher 成功/超时两条路径都记账）。
- `backend/tests/VisionStudio.Api.Tests/SecurityTests.cs`：`node-durations` 也纳入 401 理论值。
- `frontend/src/__tests__/nodeConfigurationDialogDraft.test.tsx`：3 项（回显、写入、清空后移除参数）。

### 一个环境坑（结论与直觉相反）

`backend/src/VisionStudio.Api/data/visionstudio.db`（57 MB，2026-10-26）里的
`schema_migration_history` v1 checksum 与当前代码不一致，会让 **77 个 Api 测试在 Host 启动阶段**
报 `Schema migration checksum drift detected for v1`。与代码无关。

**不要**用 `export VISIONSTUDIO_DATA_ROOT=<临时目录>` 去绕。`VisionStudioDataRoot.Resolve` 会
**优先采用**这个环境变量而非 ContentRootPath，于是 `WorkflowStoreTests` 之类的用例会去找
`<临时目录>/data/workflows`，直接 `DirectoryNotFoundException`——2026-10-29 实测一次带进 107 个失败。

正确做法是**不设**这个变量：

```bash
unset VISIONSTUDIO_DATA_ROOT
dotnet test backend/tests/VisionStudio.Api.Tests/VisionStudio.Api.Tests.csproj
```

本地若要在原数据根上跑测试，需先重建该库（或备份后删除 `data/`，测试会重新建）。
