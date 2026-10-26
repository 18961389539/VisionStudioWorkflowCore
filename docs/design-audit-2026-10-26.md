# 设计问题排查与改进（2026-10-26）

这轮不看 UI 细节，只看**结构性设计**：同一件事有没有第二个真相源、失败路径是否收敛、
持久化与契约有没有单一入口。结论先行——**没有架构层面的硬伤（分层、依赖方向、错误边界都是对的），
但有 3 类"同一逻辑抄了很多份"的系统性问题**，其中 3 条本轮已修，6 条给出方案与估时。

取证方式：全仓静态检索 + 行号定位，不运行应用；数字都是实际命中数，不是估计。

---

## 一、本轮已修（3 条）

### 1.1 错误契约被绕过：41 处手写解析 vs 1 个适配器

**问题**：`apiErrors.ts` 已经把后端 ProblemDetails 的 `detail / error / title / code / correlationId`
收敛成一个 `apiErrorMessage()`，但代码里仍有大量手写兜底：

| 写法 | 命中 |
|---|---|
| `data.detail ?? data.error ?? 'X'` | 12 |
| `data.error ?? 'X'`（含无空格 `data.error??'X'`） | 21 |
| `data.detail ?? 'X'` | 8 |

后果不是"文案难看"，而是**排障信息在传递中丢失**：手写兜底只取 message，丢掉了业务码与关联号，
现场报错时无法和日志对齐。

**改法**：脚本化替换为 `apiErrorMessage(data, 'X')`（只在 `data` 变量形态匹配时替换，不动 `messageApi` 与 children），
共 41 处 / 11 个文件，自动补齐导入。
**验证**：`tsc -b` 通过；88→89 文件 / 1120 项测试全绿（错误文案断言未变，因为适配器在无 detail 时返回同一句 fallback）。

### 1.2 本地存储散落：21 处直写 / 12 个键 / 无版本无类型

**问题**：`localStorage` 在 9 个文件里各写一遍 try/catch，键名是裸字符串常量：

- `theme.tsx`、`i18n/lang.ts`、`layout.ts`、`appEditorShared.tsx`（2 个键）、`Toolbox.tsx`（2 个）、
  `CanvasMiniMap.tsx`、`imageViewerUtils.tsx`、`draft.ts`、`security.tsx`；
- 后果：键名拼错不会报错（静默丢偏好）、隐私模式/配额满时行为不一致、想加"版本 + 迁移"时无处下手。

**改法**：新增 `storage.ts`——`STORAGE_KEYS` 作为唯一键清单（统一前缀）、`readStored / writeStored /
readStoredJson / writeStoredJson` 统一吞掉存储异常，JSON 读取强制传 `parse` 校验函数（禁止 `as T`）。
本轮迁移 6 个键：theme / lang / node-compact / recent-node-types / toolbox-collapsed / toolbox-favorites。
**新增测试** `storage.test.ts`（5 项）：前缀、fallback、脏 JSON 回退、往返、storage 抛错时不崩。

**未迁移**（本轮刻意留）：`layout`（有自己的 clamp/迁移逻辑）、`draft`（有 TTL 与业务校验）、
`minimap` / `viewer-pixelated` / `auto-login-skipped`（各只有一个读写点，收益低）。
迁移方式统一为：把裸键换成 `STORAGE_KEYS.x`，读写换成 storage 层函数。

### 1.3 原生 `window.prompt` 与设计体系割裂

`JobPanel` 复制配方用 `window.prompt`：不可样式化、不可访问（无 label）、阻塞主线程、与 antd 的确认/校验两套语义。
已替换为 `Modal + Form + Input`（autofocus、名称为空禁用确定、带"源配方"说明）。

---

## 二、发现但本轮未修（6 条，按性价比排序）

### P0-1 后端"按节点类型字符串分派"（411 处）

`VisionStudio.Engine` / `Api` 里对 `"image.*" / "measure.*" / "calibration.*" / "robot.*" / "device.*"` 的字符串命中 **411 处**。
这是全仓最大的设计债：**目录（Catalog）已经声明了端口、参数、Frames、capabilities，但行为分派仍靠字符串**，
导致插件无法声明自己属于哪类语义，新增节点必须改多处 `if/switch`，且漏改只在运行时暴露。

**方案（分三步，不要一次重写）**：
1. 把"类型 → 行为"表从散落的 `if` 收敛到每个目录条目上的能力标记（`supportsRunNode` / `Frames` 已有先例）；
2. 编译器的 92 处按"语义组"归类成策略对象（并行/控制流/数据/副作用），先做纯重构、行为零变化，用现有 1815 项引擎测试兜底；
3. 最后才允许插件声明能力，替换 `VisionExecutionPolicy` 的 40 行白名单。
**估时**：2–3 人日（第 1、2 步），风险中等——必须保持编译器输出 DSL 不变。

### P0-2 11 个 JSON 文件存储无保留上限，`/api/alarms` 无分页

- `AlarmStore` 直接 `JsonSerializer` 读写整份 `alarms.json`，**没有上限、没有分页**；
  `ProductionEndpoints.cs:94` 的 `GET /api/alarms` 返回全量。
- 同类还有 `TraceabilityStore`、`RobotCommandTraceStore`、`RunStore`、`CameraIntrinsicAssetStore` 等，共 11 个 Store。
**后果**：跑一个月后报警/追溯文件持续膨胀，读全量进内存 → 接口变慢、GC 抖动，属于"上线半年才爆"的问题。
**方案**：抽 `BoundedJsonStore<T>`（保留 N 条/按时间裁剪 + 追加写），端点统一加 `take/skip` 与默认上限
（追溯已有分页，可照抄它的形状）。**估时**：1.5 人日。

### P1-1 `types.ts` 1758 行手写后端镜像

后端 DTO 变了，前端靠 `apiContract.test.ts` 的 8 个核心域守卫才发现；其余 DTO 仍可能静默漂移。
**方案**：`openapi.json` 快照已存在且已进 CI，用 `openapi-typescript` 生成类型，`types.ts` 只保留 UI 专用类型
（节点渲染态、表单态）。**估时**：1 人日 + 逐模块替换（可分批）。

### P1-2 面板 `open` 时发请求但不取消（8 处）

`useEffect(() => { if (open) void load(); }, [open])` 的写法在 8 个面板里出现，关面板后请求仍会 setState。
React 19 不再警告，但这属于资源泄漏 + 潜在竞态（旧响应覆盖新数据，追溯面板已经踩过并单独加了代次守卫）。
**方案**：抽 `usePanelResource(open, loader)`，内部 `AbortController` + 代次校验，替换 8 处。
**估时**：0.5–1 人日（有现成的 `useTraceQuery` 可参照）。

### P1-3 i18n 以英文原文为键（`catalog.ts` 2935 行）

改一句英文文案 = 中文静默丢失（键找不到就回落原文）。这轮做语言切换时已经感受到成本。
**方案**：新文案一律走 `messages.ts` 的 `domain.key`；`catalog.ts` 按节点分批迁移（先在 CI 加"新增英文键未登记"的棘轮）。
**估时**：2 人日（机械但量幅大）。

### P2-1 发布脚本 ps1/sh 两套

`scripts/` 下 15 个 `.ps1` + 8 个 `.sh` 有重叠职责，双份维护容易出现"只有一边修了"。
**方案**：以 `.ps1` 为唯一实现，`.sh` 退化为薄包装（或在 CI 里只跑一套）。
**估时**：0.5 人日。

---

## 三、验证基线（本轮实测）

| 项 | 结果 |
|---|---|
| `npx tsc -b` | 通过 |
| `npx vitest run` | **89 文件 / 1120 项**全绿 |
| `npm run test:coverage` | 69.31 / 66.10 / 59.75 / 73.91（阈值 68/65/58/73） |
| `npm run build` | 通过 |
| `npm run test:e2e` | 6 项通过（26.6s） |

新增测试：`storage.test.ts`（5 项）。改动文件：11 个（错误契约）+ 5 个（存储）+ 1 个（配方弹窗）+ 新增 2 个。
