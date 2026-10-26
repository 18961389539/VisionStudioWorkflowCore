# 子流程调用现状：`module.call`（2026-10-26 复核）

## 结论先说

**子流程调用已经实现，不是缺口。** 我在 `node-gap-analysis.md` 里把"无子流程调用"列为 P0-4 是**误判**：
当时只查了 `catalog.generated.json` 里没有 `flow.call` / `module.call`，就下了结论，没有往 API 层看。
实际情况是这套能力在 API 层完整存在，并且每个运行入口都已经接上展开器。

判据：`module.call` 在目录里确实不存在（0 命中），但它在 `WorkflowModuleExpander` 里被**编译期内联展开**，
展开后图里不再有该类型，所以既不需要目录条目，也不需要节点执行器。

## 已有能力

| 能力 | 位置 |
|---|---|
| 把选中的节点提取成可复用模块（自动推导入/出接口与参数表） | `WorkflowModuleAuthoringService.ExtractAsync` |
| 模块版本不可变 + 内容哈希 | `WorkflowModuleStore`（SQLite，schema v15） |
| 调用点钉住版本与哈希，哈希不符直接拒绝 | `WorkflowModuleExpander.ExpandOneLevelAsync` |
| 调用级参数覆盖内部参数（`模块参数名 → 内部节点.参数`） | `ResolveParameterOverrides` |
| 编译期内联展开（`实例ID::内部ID` 前缀、嵌套最多 12 层） | `WorkflowModuleExpander.ExpandAsync` |
| 运行前统一展开 | `ModuleAwareVisionWorkflowRunner`（包装 `WorkflowCoreVisionRunner`），以及 `/api/workflows/run`、`/api/jobs`、调试会话、配方发布等入口 |
| 提取前先验证宿主可编译，避免把坏图写进模块库 | `ExtractAsync` 内 `ExpandAsync` + `Compile` |
| 前端面板：插入模块、升级到新版本、选中模块节点 | `frontend/src/components/WorkflowModulePanel.tsx` + `App.tsx` 的 `insertModule` / `upgradeSelectedModule` |

接线方式：模块的入/出接口是从**跨边界的边**推导出来的，控制端口（`exec` / `next`）也算接口的一部分，
因此 `module.call` 可以接在控制链中间：

```
… → [module.call] exec   ← 由内部入口节点的 exec 映射
                 结果端口  ← 由内部出口节点的数据端口映射
                 退出 → …
```

## 两个真实限制

1. **模块内部节点不能单步调试。** `ModuleAwareVisionWorkflowRunner` 在非 `Full` 模式下若命中模块节点，
   直接返回失败并提示"先在复用模块里打开调试"。原因是展开后的节点 ID 带 `实例::内部` 前缀，
   断点/单步的目标无法稳定映射回画布上的调用节点。
2. **`module.call` 不在目录里，因此工具箱与命令面板搜不到。** 只能通过"流程模块"面板插入。
   这是**有意的**：该节点的 `moduleId` / `moduleVersion` / `__moduleInputs` / `__moduleOutputs` 必须由面板按
   具体模块版本填充，允许从工具箱拖一个空壳出来只会得到"requires moduleId"的失败节点。

## 本轮补的验证

既有 `WorkflowModuleTests` 覆盖到"提取 → 钉版本 → 展开 → **编译**"，缺最后一步。新增
`backend/tests/VisionStudio.Api.Tests/ModuleCallExecutionTests.cs`：

| 用例 | 断言 |
|---|---|
| `ModuleCall_RunsExpandedBodyInsideHost` | 经 `ModuleAwareVisionWorkflowRunner` 真实跑通；报告中出现 `实例::threshold` 内部节点，宿主 `source`/`blob` 仍在，调用节点本身不出现（已被展开替换） |
| `ModuleCall_SingleNodeDebugIsRejectedWithActionableMessage` | 对调用节点单步调试被拒绝，错误信息包含 "expanded before execution"，且不产生任何节点报告 |

两条均通过，说明"子流程调用"在运行期确实可用，而不只是能编译。

## 给后续评审的提醒

判断"某能力是否存在"时，目录（`catalog.generated.json`）只反映**可放置的原子节点**。控制结构类与编译期
构造类能力（`module.call`、`flow.loopEnd` 的配对语义、编译期守卫）必须同时看：

- `WorkflowModuleExpander`（内联展开类）
- `StructuredWorkflowIr` + `VisionWorkflowCompiler`（结构化控制流类）
- `ModuleAwareVisionWorkflowRunner` / `VisionWorkflowCompiler` 的编译产物（DSL）

只看目录会把"已有"误判成"缺失"。
