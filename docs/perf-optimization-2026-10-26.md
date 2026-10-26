# 前端性能优化（2026-10-26）

方法：**先找"渲染期做重活"的代码，再用基准量化，最后才改**。不靠"感觉这行慢"。
基准脚本已入库：`node frontend/scripts/bench-payload.mjs`（默认 200 节点 / 300 边）。

## 结论先行

编辑器拖拽一帧原来要做 **两次整份文档的 `JSON.stringify`**，实测单次 2.7–3.6 ms（268 KB 文档）。
16 ms 的帧预算里近一半烧在序列化上，而位置变化对执行语义毫无影响。本轮把这条链路拆掉。

| 改动 | 之前 | 之后 |
|---|---|---|
| 快照序列化 | 每个 pointermove 一次 `JSON.stringify(workflowPayload)` | 拖拽期间冻结快照源，松手（`onNodeDragStop` / `onSelectionDragStop`）后算一次 |
| 运行控制器指纹 | 渲染期再序列化一次整份文档 | 复用调用方已算好的 `workflowSnapshot`（新增 `workflowFingerprint` 参数） |
| 负载构造 | 每条边 `nodes.find` + 端口 `find`（O(E×N×P)） | 先建 id→节点、id→端口 Map（O(N+E)），0.22 → 0.18 ms |
| 工具箱收藏数量 | 每次渲染跑一遍 `searchTools(171 条)` | `useMemo`，仅在目录/收藏变化时重算 |
| 结果区类型名 | 每次渲染 `Object.fromEntries(171 条)` | `useMemo` |
| 画布渲染 | 视口内外节点全部渲染 | `onlyRenderVisibleElements`，视口外不维护 DOM 与更新 |

## 实测数字（本机，200 节点 / 300 边 / 268 KB）

```
build (find):            0.22 ms
build (map):             0.18 ms
build + JSON.stringify:  2.67 ms   <- 热点
per-frame saving (2 stringify/frame -> 0 while dragging): ~5.3 ms
```

**注意结论的层次**：构造算法的收益只有 0.04 ms，几乎可以忽略；真正值钱的是"不要每帧序列化"（省 5.3 ms/帧）。
这也是为什么先量化再改——如果凭直觉去优化 `nodes.find`，会花时间却看不到效果。

## 正确性约束（改性能不能改语义）

1. **dirty / 版本判定不能漏**：拖拽期间快照冻结，松手后立刻重算一次。若拖拽被中断（例如松手事件丢失），
   下一次负载变化仍会刷新快照——因为 `snapshotSource` 的同步条件是 `!dragging`，而拖拽结束状态由 React Flow 保证回调。
2. **运行结果过期判定不变**：`resultStale` 比较的仍是"结果对应提交时指纹 vs 当前指纹"，只是指纹来源改为复用同一份字符串。
3. **快照仍是完整文档**（含 position）：移动节点后仍会正确变 dirty，只是延迟到落点。

## 验证

- `npx tsc -b` 通过
- `npx vitest run` 89 文件 / 1120 项全绿
- `npm run build` 通过；`npm run test:e2e` 6 项通过（含"节点在真实布局中获得非零尺寸"，
  这条同时守住 `onlyRenderVisibleElements` 没有把可见节点也裁掉）

## 下一步（按收益排序，本轮未做）

1. **Trace 面板 500 行表格**：目前一次性渲染 500 行；antd Table 的 `virtual` 需要固定列宽与 `scroll.y`，
   属于需要调布局的活（约 0.5 人日）。
2. **vendor 体积**：antd 976 kB（gzip 311 kB）是全仓最大依赖；按需引入（`antd/es/...` 或 unplugin）可去掉未用组件，
   但会改变构建方式，需单独验证（约 1 人日）。
3. **后端 `RunStore` / `WorkflowStore` 仍是整份文件读写**：流程与报告变大后，单次保存/读取是 O(文档大小)。
   报警已按保留策略收敛，运行记录还没有（约 1–1.5 人日）。
4. **编译期缓存**：同一份流程图重复校验/运行时都会重新编译，可在后端按 `workflowHash` 缓存编译产物（需失效策略）。
