# 通用循环（flow.while / flow.repeat / flow.break）

日期：2026-10-26 · 节点数 172 → 175

## 为什么加它

此前唯一的循环是 `flow.forEach`：它只能遍历 `DetectionList`。于是"重拍直到合格""按次数重试""读到稳定值再继续"这类
常规产线逻辑无法表达，只能在 `flow.try` 里重试纯计算，或者干脆把逻辑挪到上位机。

本轮补三个节点：

| 节点 | 类型 ID | 语义 |
|---|---|---|
| 条件循环 | `flow.while` | do-while：先执行一轮，再在每轮末尾采样条件 |
| 定次循环 | `flow.repeat` | 固定次数，循环体内可读 index / iteration / total / isLast |
| 跳出循环 | `flow.break` | 提前退出当前 while / repeat |

## 接线（与 forEach 同一套配对约定）

```
exec → [flow.while] body → 循环体 ... → [flow.loopEnd] repeat → 回填 flow.while.repeat
                     done → 循环之后
```

- 循环体必须以 `flow.loopEnd` 收尾，并把它的 `repeat` 输出接回循环节点的 `repeat` 输入；编译器按这条回边配对，
  找不到或找到多个都会在编译期报错。
- `flow.while` 的 `condition` 输入接**循环体内**产生的布尔量（如 `flow.result.pass`、`value.range.pass`），
  它由编译器在每轮末尾采样。
- `flow.repeat` 的 `count` 端口接线时优先于参数。

## 关键语义（照实写，不美化）

1. **while 是 do-while，首轮必然执行。** 条件来自循环体，而首轮执行前循环体还没有输出；若定义成先判后做的 while，
   条件只能来自循环之外的常量，实际不可用。这个语义正好对应"先拍一次、不 OK 就重拍"。
2. **条件在循环体之后采样。** 条件来源必须每轮都执行；若它位于未命中的 `If` 分支内，会沿用上一轮的值（不是缺失）。
3. **迭代预算硬兜底。** `flow.while.maxIterations`（默认 100，上限 10000）与 `flow.repeat.count`（上限 100000）
   都会转成运行时的硬上限：条件恒真时循环会退出，而不是把运行时挂死。
4. **break 只跳过"本轮剩余"再退出。** 置位中断标志 → 本轮剩余步骤被 `If(!broken)` 守卫跳过 → 末尾仍会刷新条件 →
   下一轮条件求值失败 → 走 done。所以 break 之后的结束步、trace、条件刷新都还会执行一次，这是有意为之。
5. **break 的位置受编译期约束。** 只允许直接写在 while/repeat 的循环体主序列里：
   - `If`/`Switch`/`Try`/`Parallel` 分支内的 break 会被拒绝 —— 分支结束后外层还有步骤会继续跑，语义不成立；
   - `flow.forEach` 内的 break 会被拒绝 —— forEach 没有提前退出语义，跳过本轮却继续下一轮容易误读。
6. **条件输入是"回边"，因此可选端口在源节点尚未执行时按未接线处理。** 这条改动在 `VisionNodeDispatcher`：
   必填端口仍然照旧报错，可选端口缺失不再抛 `Source node 'x' has not executed`。

## 实现要点（给后续维护者）

| 层 | 文件 | 变化 |
|---|---|---|
| 目录 | `Batch4IterationCatalog.cs` | 新增三个节点条目；`flow.loopEnd` 描述改为通用循环体结束 |
| 节点执行器 | `Nodes/LoopNodes.cs` | while/repeat 只做结构声明与初值校验；break 只记录原因 |
| 结构化 IR | `Runtime/StructuredWorkflowIr.cs` | `StructuredLoopIr` / `StructuredBreakIr`；循环配对逻辑从 forEach 泛化；break 把剩余序列包成一项 |
| 编译器 | `Runtime/VisionWorkflowCompiler.cs` | `CompileLoop` 生成 `WorkflowCore.Primitives.While` + begin/end 迭代步；`CompileBreak` 生成置位步 + `If` 守卫 |
| 迭代步 | `Runtime/VisionLoopStep.cs` | begin 发布本轮序号；end 累加计数并刷新条件；break 置位 |
| 运行时数据 | `Runtime/VisionWorkflowData.cs` | `LoopConditions` / `LoopBreaks` / `LoopCounters` / `LoopTotals` 与 `BeginLoop` |
| 运行时钩子 | `Runtime/VisionNodeRuntime.cs` | 循环节点作为入口标记执行时初始化循环状态 |
| 宿主注册 | `Api/Hosting/VisionStudioServiceRegistration.cs` | `AddTransient<VisionLoopStep>()` |

While 原语的条件表达式只用 `&&` 与 `==`：

```
data.LoopConditions["loop"] && data.LoopBreaks["loop"] == false
```

这样不依赖表达式求值器对比较运算符或方法调用的支持范围 —— 计数与预算判断都在 `VisionLoopStep` 的 C# 代码里完成，
只把结果写进条件槽。

踩过的坑：Workflow Core 的容器从 `Do` 的第一个步骤开始，之后只沿 `NextStepId` 前进。begin 步最初没有 `NextStepId`，
导致循环体整段被跳过、只剩 begin 空转 —— 表现为"跑满 10 秒超时但身体从未执行"。

## 验证

- `LoopNodeTests` 9 项：定次执行与 index 发布、count 端口覆盖参数、条件循环退出、预算兜底、while/repeat 内 break、
  以及三处编译期拒绝（无条件来源、孤立 break、forEach 内 break）。
- Engine 全量：**1853 项通过**（原 1844 + 9）。
- 目录：175 节点，hash `83b7bb57…`，`--check` 通过。
- 前端：1122 项通过，`tsc -b` 无错误；i18n 覆盖节点名/描述/参数/端口。

## 已知限制

- 循环体不参与流水线融合（与 forEach 一致），因此循环内的纯计算节点按 Workflow Core 逐节点执行，吞吐低于线性段。
- 嵌套循环支持（while 里再放 repeat），但内层循环的 `break` 只作用于最内层。
- 没有 `continue`（跳过本轮剩余但继续下一轮）。需要的场景目前用 `If` 包裹剩余步骤表达。
