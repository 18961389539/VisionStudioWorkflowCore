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

---

## 第二轮：渲染路径（2026-10-26 11:50）

第一轮解决的是"每帧重复序列化"。顺着同一条链路还有一处：**拖拽时整块底部结果表在重渲染**。

调用链：`pointermove` → `setNodes` → App 重渲染 → `nodeLabels`（依赖 `nodes`）重新生成 →
`focusNode`（依赖 `nodes`）换引用 → `RunPanel` 收到全新 props → 上百行表格整体重渲染。

| 改动 | 说明 |
|---|---|
| `focusNode` 改用 `nodesRef` | 依赖从 `[nodes, …]` 改为 `[setCenter, setNodes]`，拖拽期间引用稳定 |
| `nodeLabels` 从冻结快照派生 | 用 `snapshotSource.nodes`（拖拽期间不变）；节点名在拖拽中本来就不会变 |
| `RunPanel` 加 `memo` | props（result / nodeLabels / onSelectNode / stale / embedded）稳定时直接跳过 |
| 结果表分页 | `pageSize: 50`、`hideOnSinglePage`、`scroll.y: 320`；此前 `pagination={false}` 全量渲染 |

**可验证的数字**（e2e 用例「大流程运行结果分页渲染」）：用桩返回 **200 条节点报告**，
底部结果区实际渲染的行数 **≤ 50**（`pageSize` 50，单页时不显示分页条），工具条显示"共 200 条报告"。
也就是说，同样是打开结果区，DOM 行数从 200 降到 50。

**注意**：分页不是"隐藏信息"——工具条仍显示总数，翻页可看全部；这与 P1-5 的表格统一方向一致。

**验证**：`tsc -b` 通过；`vitest` 89 文件 / 1120 项全绿；`build` 通过；`e2e` **7 项**通过（新增 1 项）。

---

## 第三轮：追溯列表瘦身（2026-10-26 13:30）

**问题（结构性，不是常数级）**：`GET /api/traces/page` 每条 `RunTraceRecord` 都内嵌完整的
`NodeReports`。一页 50 条 × 每条几十份报告，响应体随"历史条数 × 流程规模"双倍膨胀，
而界面只用得到**选中那一条**的报告——典型的"列表当详情用"。

| 层 | 改动 |
|---|---|
| 契约 | `TraceQuery.IncludeReports`（默认 true 保持兼容）；`/api/traces/page` 新增 `includeReports` 参数 |
| 存储 | `PageAsync` 在 `IncludeReports=false` 时把 `NodeReports` 投影为空数组，`Total` 与排序不变 |
| 前端查询 | `buildTraceParams` 默认 `includeReports=false`（可显式传 true 恢复旧行为） |
| 前端交互 | 选中某条运行后按 `runId` 取详情（`/api/traces/{runId}` 早已存在），按 runId 缓存，翻页来回切不重复请求；报告表带 loading 与"该运行没有节点报告"的空态 |

**为什么不做成"新接口"**：详情端点在 2026-10 之前就有了（追溯面板的预览/回放/依赖都用它），
这次只是让列表不再越俎代庖，属于**契约收紧**而不是新增能力。

**验证**
- 后端新增用例 `Page_CanOmitNodeReports_WhileDetailStillCarriesThem`：
  同一份数据下列表（含报告）2 条 → 列表（不含报告）0 条且 `Total` 一致 → 详情仍返回 2 条。
  `VisionStudio.Api.Tests` **435 项全绿**（4m06s）。
- 前端：`traceQuery` 用例锁定默认 `includeReports=false`；`tracePanel` 用例验证"选中后按需取详情并渲染报告表"。
  全量 **89 文件 / 1122 项**通过；`build` 通过；`e2e` 7 项通过。
- OpenAPI 快照已重导出（+7 行，含 `includeReports`）。

**顺带修掉一个自己埋的坑**：上一轮开 `onlyRenderVisibleElements` 后，
e2e「Ctrl+K 加节点」用"DOM 节点数 +1"断言会失效——新节点落在视口外就不在 DOM 里。
改为断言"最近使用"被写入（`addNode` 的必经副作用），不依赖视口，也不挑具体命中哪个节点。

---

## 第四轮：工作流列表摘要缓存（2026-10-26 13:50）

**问题**：`WorkflowStore.ListAsync` 为了拿到「名称 / 节点数 / 连线数」，对**每个**已保存文件做一次完整
JSON 反序列化。工作流一多（数百份、每份上百 KB），每次打开"打开已有流程"就是几十 MB 的解析。

**实测（本机，200 份 × 7 KB 文档）**：

```
cold = 156 ms   （每份都要反序列化）
warm =   8 ms   （命中缓存，只 stat 文件）
```

**改法**：按路径缓存摘要，失效条件为 `(文件长度, 最后写入时间)`；保存成功时主动移除该条目
（避免"同一秒内改写、长度又恰好相同"命中旧值）；列表结束时清理已删除文件的缓存条目。

**正确性（新增用例 `List_ReflectsSaveAndExternalChange_WithoutStaleCache` 覆盖三条失效路径）**：
1. 再次保存（改名 / 节点数变化）→ 列表立即反映；
2. 绕过 store 直接改文件 → 依赖 `(length, mtime)` 失效，列表反映外部改动；
3. 文件被删除 → 结果与缓存条目都不再保留。

**代价与边界**：缓存只存在进程内，重启即失效（下次列表回到 cold 成本）。
如果将来出现"外部工具批量改名工作流"的场景，`(length, mtime)` 也可能漏判——届时应改用内容哈希，
代价是每次列表都要读全文，等于把缓存收益吃掉，所以现在不做。

**验证**：`VisionStudio.Api.Tests` **436 项全绿**（3m55s）。

---

## 编辑器四域订阅隔离

拖动节点且右键菜单关闭时 Graph 会变化，但等值的选区、搜索结果、布局和运行状态不应因此改变引用。
四域按字段稳定 value，选区和搜索只保留画布所需投影；批注节点单独订阅编辑动作。
可变的历史对象另带版本，避免撤销和重做按钮漏刷新；运行回调以及对齐、分组动作读取最后一次已提交的状态。

测试中连续模拟 120 次仅位置变化：Graph 消费者更新 120 次，Selection、Layout、Run
和批注动作消费者均无额外渲染；实际编辑工具栏对等值搜索结果也无额外渲染。
这不是帧耗时基准，也不意味着整个画布不重渲染：画布仍订阅 Graph，真正隔离的是工具栏、
草稿提示等 memo 子树以及批注节点。测试另覆盖中止的并发渲染、最新回调和历史变化；
Chromium 覆盖搜索定位、分组、撤销、拖拽、草稿恢复和语言切换。

构建体积门禁仍未通过：本轮新增的编辑器分包为 110/107 KiB；总量 1477/1465 KiB
以及 JobPanel 12/11 KiB、DatasetValidationPanel 6/5 KiB 也超限，其中后三项在本轮前已超限。
未放宽体积预算。

## CodeMirror 供应商分包

CodeMirror、Lezer、UIW 编辑器封装及其专属依赖单独进入 `vendor-codemirror`；
脚本节点配置仍按需加载，生产构建的浏览器回归验证打开前未请求该分包、打开后可编辑。
gzip -9 预算按实测登记 `vendor-codemirror` 182 KiB，并将 `ScriptEditor` 从 197 收紧至 10 KiB。
分包主要改善缓存粒度，不承诺减少总下载量：当前产物总量为 1478/1465 KiB，原有门禁超限仍在。
`@types/react` 和 `@types/react-dom` 均固定为锁文件中的 19.3.0。

验证生产分包时，先运行 `npm run build`，再设置 `VISIONSTUDIO_TEST_PRODUCTION_BUNDLE=1`
运行 `npx playwright test editor.spec.ts --grep "脚本编辑器按需加载并可编辑" --project chromium`。
