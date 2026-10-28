# 节点级执行超时（`__timeoutMs`）

2026-10-28。对应"流程控制 P0-1"。

## 结论先说

此前**只有整轮预算**一个超时口径：`VisionRunOptions.TimeoutMs`（默认 10 s，钳在 100–600 000 ms）。
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

- `NodeTimeoutPolicy`（`Engine/Runtime/NodeTimeoutPolicy.cs`）：解析与钳制。非正数 = 不设限，上限 600 000 ms。
- `NodeTimeoutException`：**刻意不继承** `OperationCanceledException`。若复用取消异常，
  会被 `catch (OperationCanceledException) when (token.IsCancellationRequested)` 误判为整轮取消而中断运行。
- `VisionNodeDispatcher.ExecuteWithTimeoutAsync`：唯一执行入口加一层 linked CTS。
  `VisionNodeRuntime` 与 `VisionPipelineExecutor`（含并行分支）都经此路径，覆盖完整。
- `WorkflowCoreVisionRunner`：错误码分支加 `NodeTimeoutException` 判定。

未配置 `__timeoutMs` 时**不建 CTS、不加任何包装**，既有流程零开销零行为变化。

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
- 超时的历史统计（哪个节点长期接近预算），可与 SPC/趋势视图合并。
- 并行分支的**独立**预算：当前每个节点各自计时，`Task.WhenAll` 的整体等待仍受整轮约束。

## 验证

- `backend/tests/VisionStudio.Engine.Tests/NodeTimeoutTests.cs`：6 项（含 4 条归因分支 + 钳制理论值）。
- `frontend/src/__tests__/nodeConfigurationDialogDraft.test.tsx`：3 项（回显、写入、清空后移除参数）。
- 前端 `npx tsc -b` 通过；`nodeConfigurationDialog*` 190 + 33 项全过。

### 一个环境坑

`backend/src/VisionStudio.Api/data/visionstudio.db`（57 MB，2026-10-26）里的
`schema_migration_history` v1 checksum 与当前代码不一致，导致 **77 个 Api 测试在 Host 启动阶段**
报 `Schema migration checksum drift detected for v1`。与代码无关——用干净数据根即可：

```bash
export VISIONSTUDIO_DATA_ROOT="<临时目录>"
dotnet test VisionStudio.slnx
```

本地若要在原数据根上跑测试，需先重建该库（或备份后删除 `data/`，测试会重新建）。
