# 质量门禁与棘轮

本文件说明 `scripts/quality-gate.ps1` 每一段守什么、阈值从哪来、为什么要这么定。
新增检查时请同步更新本文件——门禁条目散落在脚本里而没有口径说明，下一个人只会在红叉面前猜。

## 怎么跑

```powershell
./scripts/quality-gate.ps1                  # 全量（本地提交前）
./scripts/quality-gate.ps1 -Scope Backend   # 静态审计 + 度量下限 + 发布/迁移回归 + dotnet test + 覆盖率
./scripts/quality-gate.ps1 -Scope Frontend  # npm ci + oxlint + 单测覆盖率 + 生产构建 + 产物体积预算
./scripts/quality-gate.ps1 -Scope E2E       # Playwright 真浏览器

./scripts/quality-gate.ps1 -Scope Frontend -SkipInstall   # 本地迭代：跳过 npm ci
```

`-Scope` 只是把同一套检查分到不同 runner 并行跑（2026-11-12 拆分，CI 关键路径从一条 25 分钟
串行流水线变成三段并行），**没有降低任何口径**。`-Scope All` 仍是按原顺序执行全部。

`-SkipInstall` 只跳过 `npm ci` 这一步，后面 lint / 覆盖率 / 构建照常全跑。加它的理由是实打实的：
`npm ci` 会先删空 `node_modules` 再全量重装，本地改一行断言就要等几分钟。代价是它会跳过
"锁文件与 `node_modules` 是否一致"这一项校验——所以**提交前至少跑一次不带该开关的
`-Scope Frontend`，CI 永远不加**。`node_modules` 不存在时该开关直接报错退出，不会让后续步骤
把 "Cannot find module" 误报成代码问题。

原生命令（python / dotnet / npm）不会通过 `$ErrorActionPreference` 抛终止错误，因此每一步都必须
自行检查退出码（`Assert-LastExitCode`）。历史事故：`python` 不存在时返回 9009 被忽略，门禁照样
打印 "PASSED"。

脚本本身可能被 Windows 执行策略拦下（`因为在此系统上禁止运行脚本`）。不要为此改机器级策略，
用进程级放开：`powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/quality-gate.ps1 ...`，
或当前会话先 `Set-ExecutionPolicy -Scope Process Bypass -Force`（进程退出即失效）。

## 后端段

| 检查 | 守什么 | 失败时该做什么 |
|---|---|---|
| `static-audit.py` | 结构性与契约性断言：安全中间件顺序、反向代理边界、生产模拟器边界、发布门禁、脚本编码契约、文档必备 token | 按报文补齐实现；若断言本身过期（实现搬到别处），改断言并写清为什么 |
| `check-metric-floor.py` | 节点目录条数与测试数量下限（`scripts/metric-floor.json`） | 覆盖率挡不住"删测试"——被删的往往是难覆盖的分支，删完覆盖率反而更高。数量下限是唯一能发现"测试悄悄消失"的信号 |
| `check-size-budget.py` | 单文件行数预算（`scripts/size-budget.json`，默认 1200，目标 800） | 拆文件，或**下调**预算记录值。2026-11-13 起后端第二梯队五条大文件已按语义拆分并登记显式预算，`backend/src` 无超过默认上限的文件 |
| `test-publish-data-separation.ps1` / `test-data-migration.ps1` | 发布包与数据根分离；迁移是"暂存→校验→标记→原子切换" | 修实现，不要放宽断言：这两条对应的是现场数据丢失风险 |
| `test-publish-runtime.sh` | Bash 发布路径与 Windows 路径同一安全契约 | 同上；无 bash 的主机会跳过并打印说明 |
| `dotnet test`（Release，排除 Soak，收集覆盖率） | 宿主 + 引擎行为 | 修代码；确实需要改期望时在 CHANGELOG 里写清理由 |
| `check-warning-baseline.ps1` | 编译器/分析器告警不新增（`scripts/warning-baseline.txt`） | 修掉告警；确属误报则显式加入基线，让下一次 review 看得到 |
| `check-coverage.py` | 合并行/分支覆盖率下限 | 行 84 / 分支 62 是棘轮：只升不降（2026-11-13 实测 85.0% / 62.5%，各留约 1 点余量），提升后同步上调脚本默认值与 workflow 的 `MIN_LINE_COVERAGE`/`MIN_BRANCH_COVERAGE` |
| `static-audit.py` 的空 catch 规则（2026-11-12，2026-11-13 清零） | 未写忽略原因的空 `catch` 不得新增（`scripts/empty-catch-baseline.json` 存量已清零） | 写 `catch { } // 原因`（或 `/* */`），或补上真正的错误处理。新增即失败，不再有按文件锁定的存量额度 |
| `dotnet list package --vulnerable`（2026-11-12） | NuGet 已知漏洞只拦 high/critical；离线时打印跳过而不是失败 | 在 `backend/Directory.Packages.props` 升版本。版本只在这一处改——24 个包原先散落在 15 个 csproj 里 |

覆盖率棘轮的由来：原阈值 60 只能防"腰斩"，挡不住每次合并掉零点几个点的慢性退化。分支覆盖率单独
设门槛，是因为行覆盖挡不住"行走了、分支只走一边"——`if (x) return;` 走一次就算整行覆盖。

## 前端段

| 检查 | 守什么 |
|---|---|
| `npm ci` | 锁文件与 package.json 一致（不允许"本地能装、CI 装不上"） |
| `npm audit --audit-level=high`（2026-11-12） | 前端依赖已知漏洞（只拦 high/critical）。首次接入即发现 vite 8.3.2 与 undici 各带 high |
| `npm run lint`（oxlint，`--max-warnings 0`） | 未使用变量/导入、hooks 规则、JSX 正确性等；**零告警**口径。另运行 `check-hooks-deps.mjs`：`react/exhaustive-deps` 存量基线 65 处只许减（新代码新增即失败） |
| `npm run check:theme`（2026-11-13） | 主题令牌守卫：颜色只能出现在 `:root` / `[data-vs-theme='light']` 两个块里；语义 token 在深/浅主题下按 WCAG 校验对比度（文字 4.5:1、图形 3:1）。此前 62 行硬编码颜色在浅色主题下最低仅 2.2:1 |
| `npm run test:coverage` | 单测 + 覆盖率阈值（写在 `vite.config.ts` 的 `test.coverage.thresholds`，棘轮同理）；其中 `apiUsage.test.ts` 另有静默失败棘轮：组件里禁止用 `if (!response.ok) return;` 把失败渲染成空数据，白名单仅保留附加信息路径与组件文档反例 |
| `npm run build` | `tsc -b`（`strict: true`）+ 生产构建；类型不过不产出包 |
| `check-bundle-budget.py`（2026-11-13） | 构建产物 gzip 体积预算（`scripts/bundle-budget.json`）：未登记 chunk 不超过默认 20 kB，已登记 chunk 只许降，所有产物之和不超过总量上限。中文词表曾以 445 kB 裸体积进过 index——单测和覆盖率都看不见这类回退 |

覆盖率产物写在**系统临时目录**（`vite.config.ts` 的 `reportsDirectory`），门禁跑完只把
`coverage-summary.json` / `lcov.info` 复制回 `artifacts/test-results/frontend-coverage/` 留档。
原因：vitest 的 v8 provider 会无条件递归删除内部的 `.tmp`（每个测试文件一个 json，100+ 个），
在本机会撞上批量删除守卫并抛 unhandled error——日志只剩一行 `RUN`，看起来像测试自己崩了。
临时目录是守卫明确放行的范围，而覆盖率本来就是一次性中间物。同理，Vite 的 `cacheDir` 与
Vitest 5 的 `fsModuleCachePath` 也指到系统临时目录：默认 `node_modules/.vitest-cache` 连跑几轮
会攒出 500+ 文件，开跑前的清理同样撞守卫并报成 `Startup Error`。
后端覆盖率同理单独放 `artifacts/test-results/backend-coverage/`，每轮只清几个 GUID 子目录，
避免旧报告混进"并集覆盖"把本轮没走到的行顶成已覆盖。

2026-11-12 起 `testTimeout` / `hookTimeout` 为 30 s、`maxWorkers` 封顶 4：CI 的 2~4 vCPU runner
上，15 s + 8 个 jsdom worker 会让重渲染用例反复超时，进而连锁拖垮同批的 DOM 断言。放宽的只是
"卡住才失败"的上限与并发度，断言内容与覆盖率阈值一点没松；两个会抢在 state 更新前的
`getByText` 也改成了等待。

### 为什么用 oxlint 而不是 ESLint

项目用 **TypeScript 7 原生编译器**，而 `typescript-eslint` 的 peer 范围是 `>=4.8.4 <6.1.0`，
在 TS 7 上无法解析（不是"能跑但报警"，是直接解析失败）。oxlint 是 Rust 实现，不依赖 TS 编译 API，
规则覆盖 `no-unused-vars` / `rules-of-hooks` / JSX 正确性等我们真正需要的那部分。

React Compiler 系列规则（`set-state-in-effect` 82 处、`refs` 56 处，2026-11-12 首次启用时统计）暂未开启：
这些是"重写渲染方式"级别的改造，一次性开启会让门禁长期变红而失去意义。
`exhaustive-deps` 已先行接入（2026-11-13）：规则本体仍保持 `off`，由 `frontend/scripts/check-hooks-deps.mjs`
用 `oxlint -D` 单独统计并与 `lint-hooks-baseline.json` 的存量基线（当前 63，2026-11-13 由 65 下调）比较——新代码新增违规直接失败，
修掉一处则要求同步下调基线。这样不必一次性改写 26 个存量组件里的内联 `refresh/load`。

`no-unused-vars` 对 `src/__tests__/**` 关闭：测试里 `const { result, rerender } = renderHook(...)`
的解构本身就是对被调 API 的说明，逐个改名只会增加噪音。生产代码不受此豁免。

### 类型安全基线

`frontend/tsconfig.app.json` 开启 `strict` + `noImplicitOverride` + `noFallthroughCasesInSwitch`
（2026-11-12）。此前是 `strict: false`，`null` 与 `undefined` 全靠运行时兜底——一次性收口时修掉
50 处错误，其中 10 处是后端可空字段被前端收窄为不可空（`ParameterDescriptor.min/max/options`、
`RunResult.controlFlowDecisions` 等），属于真实的契约不一致。

## E2E 段

Playwright + Chromium，覆盖 jsdom 无法验证的部分：布局引擎、Canvas 绘制、指针事件链、真实导航。
失败时上传 `artifacts/test-results/playwright-report`。单列一段的原因：它和单测的稳定性与耗时不是一个量级，
混在一起会把"断言挂了"和"浏览器没起来"混成同一个红叉。

spec 用 `page.route('**/api/**')` 把后端全量桩化，**不依赖 .NET 进程**：真实浏览器 + 固定响应，
既跑得了"点发布 → 收 409 → 确认 → 重发"这种跨请求链路，又不会因为宿主没起来而变红。
目前两条：

| spec | 覆盖 |
|---|---|
| `editor.spec.ts` | 编辑器基本链路 |
| `recipe-publication.spec.ts` | 发布遇成熟度门禁 → 面板列 findings → 确认按钮禁用 → 勾选 → 成功；并断言第二次请求带 `acknowledgeMaturityNodes=true` |

`recipe-publication` 断言的是**请求本身**而不只是 toast：门禁参数没真发出去时，后端仍会拒绝，
而界面看起来"成功了"。

此外 `real-backend-smoke.spec.ts` 走 `request` fixture 直连真宿主，不经过浏览器：门禁先在
`127.0.0.1:5199` 以独立数据根拉起 Debug 版 API，等 `/healthz` 返回 200 后再跑
health → 登录 → `/api/catalog` → `/api/run` → `/api/traces/{runId}` → `/api/diagnostics/node-spans`
整条链路；宿主不可达时该用例 skip 并打印原因，桩化用例照旧。真后端链路证明的是"契约端到端
跑得通"，桩化用例证明的是"前端遇到某种 JSON 怎么表现"，两者不互相替代。

下面几个环境相关的坑已固化在 `quality-gate.ps1` 里，别再删掉：

- 启动宿主前调用 `Normalize-EnvironmentForStartProcess`，把 `HTTP_PROXY`/`http_proxy` 这种仅
  大小写不同的重复键做进程内等价去重（取一个值、删全部变体、用规范名写回）。Windows PowerShell
  5.1 的 `Start-Process` 用大小写不敏感字典复制环境，遇到它们会直接抛 `ArgumentException`，
  宿主根本起不来，而且中文控制台里错误信息全是乱码。
- `npx playwright install chromium --no-shell`：`playwright.config.ts` 用 `channel:'chromium'`
  启动完整 Chromium，headless shell 从不被使用；默认安装会额外下载 118 MiB，在带代理的机器上
  能把门禁拖十几分钟甚至卡死。跳过它不减少任何用例。
- `Start-Process -WorkingDirectory $apiDll.DirectoryName`：ASP.NET Core 的 `ContentRootPath` 默认取
  CWD，留在仓库根启动 `dotnet <dll>` 会让宿主找不到 `appsettings.json`，`Security` 全部退化成
  默认值，`/api/auth/status` 的 `autoLogin` 变 false，冒烟第二步就红。门禁因此显式设置
  `Security__AutoLoginAdmin=true`，并把 status 响应体打进日志；配置漂移时不用翻宿主日志。
- 门禁给每轮注入唯一的 `VISIONSTUDIO_E2E_OUTPUT_DIR`（系统临时目录）：Playwright 启动会递归清空
  `outputDir`，本机安全删除包装器对单批 >50 个条目要求人工确认，历史 trace 攒到上千个时测试
  还没开始就红。不删历史产物就不触发守卫，CI 的用例口径也不变。`Upload Playwright report`
  的路径已与 html reporter 的 `outputFolder` 对齐（原先指向不存在的位置，失败时等于没传产物）。
- 三段并行时每段都是独立 checkout，E2E job 不经过 Frontend 段：`-Scope E2E` 会在缺少
  `frontend/node_modules/@playwright/test` 时按锁文件 `npm ci`，否则 `npx playwright` 会临时拉一个
  与 `package-lock.json` 不一致的版本，随后 `npm run test:e2e` 找不到命令。本地已有依赖时跳过。
  环境去重只在 Windows PowerShell 5.1 执行，pwsh（CI 的 shell）直接跳过——已用 pwsh 7 +
  `CI=true` 本地复跑 9 个用例确认这条路径。

## 前端的棘轮（在 `npm run test:coverage` 里）

| 用例 | 守什么 |
|---|---|
| `apiContractParityCoverage.test.ts` | 契约对账的**覆盖面**只许变长：读 `types.ts` 与 `generated/apiContractParity.ts` 源码，要求全部同名 DTO 都有 `BackendFitsFrontend` 断言；`PENDING` 已于 2026-11-13 清空，棘轮收紧到 `PENDING.size <= 0`——此后再出现同名 DTO 未对账会直接失败，没有登记缓冲 |
| `i18nStatusKeys.test.ts` | 状态键必须是稳定标识符（`^[A-Za-z][A-Za-z0-9_.-]{0,39}$`），挡住"用英文句子当键"；并断言 `Running/Succeeded/Failed/Cancelled/Queued/Interrupted/Breakpoint` 必须存在 |
| `i18nStableKeys.test.ts` | 目录翻译表的稳定键守卫：描述/参数描述/节点名的键必须是 `类型` 或 `类型.参数`；分类/分组只允许短契约 token；悬空语义键相对目录金样只许减（基线 126）；分组中文值必须唯一（否则 `canonicalParameterGroup` 反查会写回错的契约值）；分类/分组零回落；用真实目录金样跑双语本地化 |
| `i18nPortCoverage.test.ts` | 端口中文覆盖：目录里每个端口名必须有中文映射（动态 `caseN`/`pointN` 由规则生成）；基线固定 **0**，新增端口未补中文直接失败 |

契约断言本身是编译期的（`BackendFitsFrontend<Frontend, Backend extends Frontend>`），
覆盖率用例防的是"新加一个 DTO 但没人写断言"——那种缺口不会让编译失败。

请求方向（前端 → 后端）只断言**键集**（`apiContractParity.ts` 末尾的 `WorkflowPayloadRequestKeys` 等）：
前端刻意允许表单数字为 `string | number`、可空字段缺省，用 `extends` 卡会逼着前端把类型写死。
多余键才是静默失效的来源——`System.Text.Json` 默认忽略未知成员。**断言必须写在类型约束位置**，
写在条件类型里（`Cond extends never ? true : Cond`）不会报错，等于空转。

## 发布门禁（产品行为，不是 CI）

节点成熟度不只影响界面：含 Preview / Experimental 节点的配方版本，发布时必须显式确认
（`?acknowledgeMaturityNodes=true`），确认结果写入审计 `target`。回滚不设门禁。
细节见 [`node-maturity.md`](node-maturity.md)。
