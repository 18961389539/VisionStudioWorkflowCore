# 改进建议与落地记录（2026-10-23）

本轮基于对仓库的只读审计（代码规模、CI 配置、构建产物实测）产出的改进清单，以及已落地项的实测数据。
结论先行：**工程底座（CI 门禁、认证授权、存储耐久性、代码整洁度）没有短板；真正的缺口集中在"测试可见性"与"契约单一真相源"。**

审计基线：后端 519 个 `.cs` / 114,699 行；前端 208 文件 / 42,363 行；`TODO/FIXME/HACK` 标记 0 个；
CI 已有 quality-gate（后端覆盖率 60%）、soak（工作日 18:30）、plugin-performance-gate（self-hosted 手动）。

**本轮产出**：前端覆盖率棘轮、vendor 分组、关键路径 e2e 三项落地，并新增 CI 门禁。
其中 e2e 首跑就抓到一个真实缺陷——**接口契约漂移会让整个编辑器变成错误页**（详见 1.3），已修复并留回归护栏。
另修正一条此前写入配置的**错误技术结论**（"vendor 分组在 Vite 8 不生效"），见 1.2。

---

## 一、已完成

### 1.1 前端覆盖率棘轮（P1）

**问题**：后端有 `coverage.runsettings` + `check-coverage.py --min-line 60`，前端 1091 个测试却没有任何覆盖率口径。
在 App.tsx（1493 行）、NodeConfigurationDialog（1465 行）持续拆分的阶段，"拆完测试还在、覆盖退化"这类变化 CI 看不见。

**做法**：
- 新增 `@vitest/coverage-v8`，配置写入 `frontend/vite.config.ts` 的 `test.coverage`；
- 报告输出 `../artifacts/test-results/frontend-coverage/`（text-summary + json-summary + lcov），与后端覆盖率产物同目录，CI 一次上传；
- 排除项：测试文件、`src/generated/**`（机器生成）、`src/main.tsx`（引导）、`src/types.ts`（纯类型）、`vite-env.d.ts`；
- 新增脚本 `npm run test:coverage`。

**基线（83 文件 / 1091 项全绿实测）与阈值**：

| 维度 | 实测基线 | 门禁阈值 | 余量 |
|---|---|---|---|
| Statements | 69.29%（7316/10558） | 68 | ~1.3% |
| Branches | 65.99%（8135/12326） | 65 | ~1.0% |
| Functions | 59.35%（1539/2593） | 58 | ~1.4% |
| Lines | 74.02%（5866/7924） | 73 | ~1.0% |

阈值取整并各留约 1% 余量，只为拦住退化，不制造 flaky。**棘轮规则：只升不降**；覆盖提升后应同步上调阈值。

**代价**：开插桩后全量耗时 179.5s → 213.4s（+19%）。其中 `propertyPanel` 的 120 顶点多边形用例从 4.7s 涨到超过 15s 超时，
已单独放宽到 30s（断言未放宽，注释说明是插桩开销）。

**环境差异**：沙箱内 `vitest` 收尾时会清理 `artifacts/test-results/frontend-coverage/.tmp`（84 个文件），
被宿主批量删除守卫拦截而报 unhandled error——**报告仍正常生成**，GitHub Actions 与用户本地终端无此 shim，不受影响。

### 1.2 vendor 分组（原 P1-1，收益重新定位）

**先说修正**：我最初的判断"主包 2.23 MB 需要分包"**部分是误判**。实测首屏总传输在分包前后完全一致（923 kB gzip），
所以这项改动的价值不在减小首屏字节，而在**缓存粒度与请求数**。原 `vite.config.ts` 里"手工分组不生效"的注释也是错的。

**验证过程**（vite 8.3.2 + rolldown 1.2.12）：
- `build.rollupOptions` 是废弃层级；正确的键是 `build.rolldownOptions.output.codeSplitting.groups`，
  而 rolldown 的 `advancedChunks` 已废弃（类型定义中标注 `@deprecated`）；
- 对照实验 `codeSplitting: false` → 单包 3,571 kB（基线 2,179 kB），证明该键确实被传给打包器，旧结论站不住；
- 分组生效后产物如下。

| Chunk | 分包前 | 分包后（raw / gzip） |
|---|---|---|
| index（业务） | 2,179 kB / 597 kB | 1,548 kB / 407 kB |
| vendor-antd | 混在 index/自动 chunk | 977 kB / 311 kB |
| vendor-konva | 混在 index | 325 kB / 99 kB |
| vendor-react | 混在 index | 219 kB / 68 kB |
| vendor-xyflow | 混在 index | 180 kB / 58 kB |
| vendor-signalr | 混在 index | 55 kB / 14 kB |
| **首屏合计** | **15 个请求 / 3,190 kB raw / 923 kB gzip** | **7 个请求 / 3,226 kB raw / 923 kB gzip** |

**实际收益**：
1. 首屏 JS 请求 15 → 7，HTTP/1.1 下的队头排队消失；
2. vendor chunk 的内容哈希只随依赖版本变化——改业务代码不再让 antd 的 311 kB 缓存失效；
3. 产物结构可预测，便于后续按 chunk 做体积预算。

**不改的部分**：`React.lazy` 的面板级拆分继续保留（11 个面板，每个独立 chunk，首屏不下载），两套机制互补。

### 1.3 关键路径 e2e（P1）

**问题**：1091 个前端测试全跑在 jsdom 里——布局是假的（所有元素 0×0）、没有真实 Canvas、React Flow 的命中判定与
事件链无法验证。画布拖拽、Konva 渲染、面板布局这些"用户真正看到的东西"没有任何自动化护栏。

**做法**：Playwright + 完整 Chromium（`channel: 'chromium'`，复用本机已有构建，不额外下载 headless shell）。
`/api/**` 全部走 `page.route` 桩，因此 **e2e 不依赖 .NET 进程**，在任意机器与 CI 上都能跑。
产物统一到 `artifacts/test-results/`，与后端覆盖率同区（`artifacts/` 已忽略）。

**4 条用例**（`frontend/e2e/editor.spec.ts`，4/4 通过，约 18s）：

| 用例 | 覆盖点 |
|---|---|
| 首屏渲染画布与初始流程，且无未捕获异常 | `pageerror` 计数为 0 + 初始 `linearDemo` 节点渲染 |
| 节点在真实布局中获得非零尺寸并落在画布可视区内 | 真实布局引擎（jsdom 下恒为 0×0）+ 视口包含关系 |
| 双击节点打开节点配置面板 | React Flow 命中判定 + 双击事件链 + 懒加载面板 |
| 插件接口返回非数组时画布仍可用 | 契约漂移防御（回归护栏，见下） |

**e2e 抓到的真实缺陷（已修复）**：
`/api/plugins` 返回非数组时，`App.tsx` 工具栏标签里的 `plugins.filter((p) => p.loaded)` 抛出
`plugins.filter is not a function`。该调用位于**面板级错误边界之外**，异常冒泡到 `main.tsx` 的顶层边界，
**整个编辑器被替换成"界面遇到未处理的错误"**，用户丢掉全部画布上下文。

触发条件不是假设：网关错误体、反向代理返回的 JSON 对象、后端字段改名，都会让 `setPlugins` 收到非数组。
修复为两处入口的 `Array.isArray` 校验（`App.tsx` 的 `pluginEndpoints.list` 与 `plugins/rescan` 分支），
元素读取改为 `p?.loaded`。这条用例保留为回归护栏。

**环境注意**：`baseURL` 必须用 `localhost` 而非 `127.0.0.1`——Vite 绑定 localhost（可能解析到 `::1`），
在带 HTTP 代理的环境里 `127.0.0.1:4173` 会被代理拦成 502，表现为 webServer 启动超时。

### 1.4 CI 门禁接入

`scripts/quality-gate.ps1` 的前端段由 `npm test` 改为：

```powershell
npm run test:coverage            # 覆盖率阈值在 vite.config.ts 内，超阈即失败
npm run build
npx playwright install chromium  # windows-latest 自带 Edge，但 Playwright 需自己的构建
npm run test:e2e
```

**代价**：前端步骤由约 3 分钟增至约 4.5 分钟（覆盖率插桩 +34s，e2e +18s，浏览器安装首次较长后走缓存）。
quality-gate 的 `timeout-minutes` 为 25，当前总时长未触及上限，但已接近需要关注的水平。

---

## 二、待办

### P1-4 契约单一真相源：`Produces<T>` → 生成 `types.ts`

**现状**：`frontend/src/types.ts` 1,758 行手写后端 DTO 镜像；`src/generated/openapi.json` 是快照（101 schema），
`apiContract.test.ts` 用 `PENDING_SPEC_DTOS` 棘轮记录"尚未进 spec 的核心 DTO"。

**根因**：核心域端点未声明 `Produces<T>`，Swagger 生成不到这些 DTO 的 schema，因此无法用 `openapi-typescript` 生成类型替换手写文件。

**动作**：按资源域分批（工作流 → 运行/追溯 → 标定 → 生产 → 插件）给端点补 `Produces<T>`，每批递减 `PENDING_SPEC_DTOS`，
归零后用生成类型替换 `types.ts`，并把 `apiContract.test.ts` 改为"生成类型与手写类型零差异"断言。

**验收**：`PENDING_SPEC_DTOS` 为空；`types.ts` 不再手工维护；改后端 DTO 而忘记同步前端时 CI 变红。

### P2-5 后端巨型文件第二梯队

`JLVisionMatching` 2,731 / `ProductionRuntime` 1,498 / `NodeCatalog` 1,474 / `InferenceNodes` 1,378 /
`RuntimeDependencies` 1,095 / `SubpixelNodes` 1,058。

前端已完成第一轮拆分（App 2022→1493 等），后端尚未开始。建议按语义切分而非机械拆行：
`JLVisionMatching` 按「模型训练 / 搜索匹配 / 一致性剔除 / 序列化」四块拆，`RuntimeDependencies` 按资源域拆注册模块。

**验收**：单文件 ≤ 800 行；拆分前后 `VisionStudio.Engine.Tests` 1815 项与 `Api.Tests` 421 项全绿且用例数不减。

### P2-6 i18n 键设计

`frontend/src/i18n/catalog.ts` 2,935 行，**以英文原文为键**（2,258 条）。改一句英文文案会静默丢掉对应中文。
守卫测试只保证"英文源 + 中文覆盖存在"，抓不到文案漂移。

**动作**：迁到稳定 key（`node.<type>.<param>.label` 形式），保留一层英文原文到 key 的映射以维持渐进迁移；
新增"未翻译 key"棘轮（计数只减不增）。

### P2-7 可观测性：ActivitySource

已有 `RunObservability`、`RollingFileLogger`（含丢弃计数并经 `/api/health` 暴露）、`/api/health`，但无标准遥测。
跨节点执行耗时的因果链只能事后从自研 trace 推断，无法接入 OTLP/Jaeger。

**动作**：节点执行入口包 `Activity`（含 nodeType / runId / frame 标签），导出 OTLP 设为可选（默认关闭，不改变现场行为）。

### P2-8 AlarmStore 全量 JSON 读写

无保留上限、`/api/alarms` 无分页。长期运行会单调膨胀。**动作**：改为环形缓冲 + 分页查询，保留上限可配置。

---

## 三、已核实无需重复提出的项

以下常被误判为缺口，实际已具备，避免后续审计重复消耗：

| 能力 | 证据 |
|---|---|
| 认证 / 鉴权 | `VisionStudioAuthenticationHandler` + 39 处 `RequireAuthorization` + `RequireEngineer` 动作级授权 |
| 限流 | `auth`（固定窗口）、`media-import`（并发）策略 |
| 错误契约 | `AddProblemDetails` + `UseExceptionHandler` + 审计事件 |
| 存储耐久性 | WAL + 关键提交 `synchronous=FULL` + `busy_timeout=5000` + 备份 `integrity_check` |
| 文档版本闸 | `WorkflowDocumentSchema.CurrentVersion`，高版本读取返回 409，禁止降级覆盖 |
| 中断运行收尾 | `InterruptedRunRecovery` 启动时把遗留 Running/Queued 统一收为 `Interrupted` |
| 门禁完整性 | quality-gate 含 `CatalogExporter --check`（目录与前端生成物一致性） |
| 代码整洁 | `TODO/FIXME/HACK` = 0 |
