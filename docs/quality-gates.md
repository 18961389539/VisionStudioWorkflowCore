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
bash ./scripts/quality-gate.sh --scope Backend           # Linux/macOS/Git Bash 同口径入口（支持 All/Backend/Frontend/E2E 与 --skip-install）
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

## Git 换行契约

仓库以 [`.gitattributes`](../.gitattributes) 为准：自动识别的文本在索引中保存为 LF，
默认检出为 LF（包括 PowerShell、Shell、Python 和前后端源码）；只有 `.bat` / `.cmd`
检出为 CRLF，索引仍为 LF。自动识别的二进制文件不转换，已有 UTF-8 BOM 也不移除。
[`.editorconfig`](../.editorconfig) 使用相同的编辑器保存规则。

显式 `eol` 规则不依赖个人或系统的 `core.autocrlf`，无需修改全局 Git 配置。规则生效不会
立即重写已有工作区文件；旧检出的 CRLF 可以暂时保留，后续正常保存或检出时再遵循契约。
PowerShell 的 LF 规则不改变静态审计要求的编码契约：含非 ASCII 字符的脚本仍须 UTF-8 BOM。

排查时使用 `git ls-files --eol` 和 `git check-attr text eol -- <文件>`。
本次固化时索引文本已全部为 LF，因此没有批量归一化源码；不要在有在途改动时盲目运行
`git add --renormalize .`。未来确需归一化时，应先隔离业务改动，再单独审查并提交换行差异。

## 后端段

| 检查 | 守什么 | 失败时该做什么 |
|---|---|---|
| `static-audit.py` | 结构性与契约性断言（入口 `scripts/static-audit.py` 协调 `scripts/audit/*.py` 各域模块）：安全中间件顺序、反向代理边界、生产模拟器边界、发布门禁、脚本编码契约、文档必备 token | 按报文补齐实现；若断言本身过期（实现搬到别处），改断言并写清为什么 |
| `python -m unittest discover -s scripts/tests` | 静态审计守卫的回归测试，随 Backend 门禁运行 | 修复文档缺失、token 缺失及 OpenCV 节点调用等边界的检查逻辑 |
| `static-audit.py` 文档契约 | 已保留的设计文档必须是文件，声明的必备 token 必须存在；已退役文档不再登记为空转检查 | 缺文件或 token 直接失败；文档有意迁移时同步更新断言 |
| `static-audit.py` OpenCV 节点调用守卫 | 节点执行器文件中的非豁免 `Cv2.*` 算法直调为零，新增即失败；旧的 91 项存量基线及重写入口已退役 | 将完整算法移至算法层；仅保留现有图像 I/O、绘图、3D 及特定能力例外，不以扩大例外代替迁移 |
| `check-metric-floor.py` | 节点目录条数与测试数量下限（`scripts/metric-floor.json`） | 覆盖率挡不住"删测试"——被删的往往是难覆盖的分支，删完覆盖率反而更高。数量下限是唯一能发现"测试悄悄消失"的信号 |
| `check-size-budget.py` | 单文件行数预算（`scripts/size-budget.json`，默认 1200，目标 800；覆盖前后端源码及 `scripts/` / `frontend/scripts/` 治理脚本） | 拆文件，或**下调**预算记录值。2026-11-13 起后端第二梯队五条大文件已按语义拆分并登记显式预算，`backend/src` 无超过默认上限的文件 |
| `test-publish-data-separation.ps1` / `test-data-migration.ps1` | 发布包与数据根分离；迁移是"暂存→校验→标记→原子切换" | 修实现，不要放宽断言：这两条对应的是现场数据丢失风险 |
| `test-publish-runtime.sh` | Bash 发布路径与 Windows 路径同一安全契约 | 同上；无 bash 的主机会跳过并打印说明 |
| `dotnet test`（Release，排除 Soak，收集覆盖率） | 宿主 + 引擎行为 | 修代码；确实需要改期望时在 CHANGELOG 里写清理由 |
| `check-warning-baseline.ps1` | 编译器/分析器告警不新增（全方案 `VisionStudio.slnx` 按 `(相对路径, 告警码)` 多重集比对 `scripts/warning-baseline.txt`，重构移位不再误报；`-Update` 保留头部注释） | 修掉告警；确属误报则显式加入基线，让下一次 review 看得到 |
| `check-coverage.py` | 合并行/分支覆盖率下限 | 行 84 / 分支 62 是棘轮：只升不降（2026-11-13 实测 85.0% / 62.5%，各留约 1 点余量），提升后同步上调脚本默认值与 workflow 的 `MIN_LINE_COVERAGE`/`MIN_BRANCH_COVERAGE` |
| `static-audit.py` 的空 catch 规则（2026-11-12，2026-11-13 清零） | 未写忽略原因的空 `catch` 不得新增（`scripts/empty-catch-baseline.json` 存量已清零） | 写 `catch { } // 原因`（或 `/* */`），或补上真正的错误处理。新增即失败，不再有按文件锁定的存量额度 |
| `static-audit.py` 自动备份断言（2026-11-23） | 自动调度服务/宿主注册/`/api/health` 新鲜度字段/`Storage:AutoBackup` 配置/恢复演练测试/文档声明必须同时存在；文档必须写明 `stale` 不参与 `degradedReasons` | 补齐服务、注册或健康字段；不要为了让 `stale=false` 而删掉披露字段——陈旧备份是告警，不是就绪条件 |
| `static-audit.py` 安全响应头断言（2026-11-23） | `SecurityHeadersMiddleware` 必须位于转发头之后、静态文件/异常处理器之前；CSP/HSTS/XFO 等 token、配置项、错误路径测试与文档的放宽理由必须同时存在 | 按 `docs/security-headers.md` 核对管线顺序；收紧 CSP 前先删除对应的前端用法（antd 内联样式、blob 预览、SignalR WebSocket） |
| `static-audit.py` Windows 服务断言（2026-11-23） | `WindowsServices` 包/`UseWindowsService`/安装脚本（数据根隔离、恢复动作、延迟启动）/卸载脚本（优雅停止后才删除）/WER 转储脚本/运维文档必须同时存在 | 按 `docs/windows-service.md` 补齐；不要为绕过检查删除"停止超时拒绝删除"与"数据根不得在包内"两条安全断言 |
| `dotnet list package --vulnerable`（2026-11-12） | NuGet 已知漏洞只拦 high/critical；离线时打印跳过而不是失败 | 在 `backend/Directory.Packages.props` 升版本。版本只在这一处改——24 个包原先散落在 15 个 csproj 里 |

覆盖率棘轮的由来：原阈值 60 只能防"腰斩"，挡不住每次合并掉零点几个点的慢性退化。分支覆盖率单独
设门槛，是因为行覆盖挡不住"行走了、分支只走一边"——`if (x) return;` 走一次就算整行覆盖。

OpenCV 守卫递归扫描包含 `IVisionNodeExecutor` 或 `VisionToolBase` 的节点文件，允许清单中的调用
及精确登记的例外；它约束的是**节点到算法层的边界**，不禁止算法层内部使用 OpenCV，
也不表示所有算法都已迁移到 JLVision。涉及输出图像、数值、取消与原生内存所有权时须做行为回归。

### 程序集依赖边界

`Engine.Tests/Contracts/AssemblyBoundaryTests` 为 `backend/src` 下全部 16 个项目登记精确的
`ProjectReference` / `PackageReference` 白名单，并检查项目清单与登记项一致，新增程序集不能漏测。
覆盖基础契约、硬件契约、图像、算法、脚本、相机适配器、引擎、API、两个 Worker、示例插件、
打包器、SQLite 快照工具、Benchmark 与 CatalogExporter；直接 DLL `Reference` 不得绕过项目边界。

共享 `Directory.Build.props` 单独断言只引入 `PrivateAssets=all` 的编译器工具包，
API 对两个 Worker 的引用必须保持 `ReferenceOutputAssembly=false`（构建依赖，不是程序集依赖）。
这些测试核对项目声明，不把命名空间当程序集边界，也不声称覆盖运行时插件动态加载的完整依赖图。

脚本编辑器及执行测试还检查：校验不加载提交程序集；超过 64 个缓存条目的不同脚本、并发同源码、
运行失败和超时后，程序集及其加载上下文的弱引用最终均可回收。仅检查缓存条目数不足以证明卸载。

### EF Core 与手写 SQL 的 Schema 对账

后端的 `EfCoreSchemaParityTests` 在两个隔离的内存 SQLite 数据库中分别执行完整
`SqliteSchemaMigrationRunner` 迁移链和 EF `EnsureCreated`，对账的是**实际落库结构**，
不是实体属性名或 `CREATE TABLE` 文本快照。生产建库/升级仍只走手写迁移，不调用 `EnsureCreated`，
不得为让测试变绿修改已发布迁移及其校验和。

覆盖表和列的双向集合、声明类型、NOT NULL、默认值、复合主键顺序、列排序规则、
自增/生成列、CHECK、外键列配对和更新/删除/延迟行为，以及索引的名称、唯一性、键列顺序、
升降序、排序规则和部分索引谓词；视图、触发器和表选项也参与比较。
CHECK 与谓词按 SQL token 规范化，只忽略格式和标识符引用，保留字符串内容与大小写。
未建模的 DDL `ON CONFLICT` 策略直接报错，不能静默跳过。

已登记的边界只有：

- `schema_info`、`schema_migration_history`、`legacy_alarm_import` 为迁移管理表，不由 EF 映射；
  测试断言它们在迁移库存在，EF 额外生成同名表仍会失败。`sqlite_sequence` 为 SQLite 内部表，
  通过所属表的 AUTOINCREMENT 契约间接检查。
- 38 个历史主键在 SQLite 元数据中未显式标为 NOT NULL，EF 主键则必须非空。测试以
  **表/列 + SQL=0 / EF=1** 精确登记；新增差异或旧差异消失都要求审查，不能豁免所有主键。
  其中 TEXT 主键的历史空值许可与 EF 严格性并不相同；INTEGER rowid 主键仍由数据库生成。
- 8 个 EF 显式唯一索引与 SQL 内联 UNIQUE 约束按键列及唯一性、排序和 collation 对齐，
  仅登记的 EF 索引名允许等价；其它显式索引名称照常检查。不改成 EF alternate key，
  避免让用户名等原本可更新的字段变成不可修改的跟踪键。

EF 外键索引约定已关闭，索引必须显式映射，与生产迁移一致。对账同时修复了 4 个遗漏的
CHECK、3 处 `NO ACTION` 外键行为和 durable counter 的 rowid/AUTOINCREMENT 差异。
另有故意改变列、索引、外键、CHECK 的回归用例，确保对账器能检出差异，而不只是当前两库恰好相等。

定向运行：

```powershell
dotnet test backend/tests/VisionStudio.Api.Tests/VisionStudio.Api.Tests.csproj -c Release --filter "FullyQualifiedName~EfCoreSchemaParityTests|FullyQualifiedName~EfCoreInfrastructureTests"
```

## 前端段

| 检查 | 守什么 |
|---|---|
| `npm ci` | 锁文件与 package.json 一致（不允许"本地能装、CI 装不上"） |
| `npm audit --audit-level=high`（2026-11-12） | 前端依赖已知漏洞（只拦 high/critical）。首次接入即发现 vite 8.3.2 与 undici 各带 high |
| `npm run lint`（oxlint，`--max-warnings 0`） | 未使用变量/导入、hooks 规则、JSX 正确性等；**零告警**口径，`react/exhaustive-deps` 直接作为错误检查，不再使用存量基线 |
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
`exhaustive-deps` 的 14 处存量已清零，规则在 `.oxlintrc.json` 中设为 `error`，由
`npm run lint` 直接执行；原独立检查脚本和基线已移除。依赖修复仍须保持请求时机和编辑草稿语义，
不能仅用禁用规则或忽略依赖消除告警。

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

编辑器、配方发布和生产面板的界面回归通过 `page.route('**/api/**')` 提供固定响应，可独立于 .NET 宿主运行。

| spec | 覆盖 |
|---|---|
| `editor.spec.ts` | 编辑器基本链路 |
| `recipe-publication.spec.ts` | 发布遇成熟度门禁 → 面板列 findings → 确认按钮禁用 → 勾选 → 成功；并断言第二次请求带 `acknowledgeMaturityNodes=true` |
| `production-panel.spec.ts` | 活动告警统计、历史服务端分页、确认操作及状态接口失败时的降级 |
| `imageViewer-measurement.spec.ts` | 在真实 Konva 画布上拖拽重绘搜索线：绘制提示读数 → 参数写回 → 重新打开配置后数值仍在（不比对像素）；同时守住“绘制提示条不得把画布行挤成 0 高”的布局回归 |
| `handeye-wizard.spec.ts` | 手眼向导：默认样本预检 → 求解残差/最差/离群标记 → 导出把手眼参数写回节点，重开配置后位姿对仍在 |

`recipe-publication` 断言的是**请求本身**而不只是 toast：门禁参数没真发出去时，后端仍会拒绝，
而界面看起来"成功了"。

`real-backend-smoke.spec.ts` 连接真实宿主。门禁在 `127.0.0.1:5199` 以每轮独立的数据根启动 Debug 版 API，
覆盖登录、流程运行、追溯、节点 span，以及浏览器内的「生产故障 → SignalR 推送 → 操作员确认 → 刷新后仍已确认」。
手工运行告警用例时，设置 `VISIONSTUDIO_E2E_API` 指向使用隔离数据根的 Development 宿主。

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

## 夜间引擎性能门禁（不是每 PR 跑）

`engine-performance-gate.yml` 每天在**固定 self-hosted runner** 上跑
`scripts/engine-benchmark-gate.ps1`：用与金样回归相同的固定 PNG 夹具，测高斯平滑、手动阈值
（640×480 / 1280×960）与 managed chamfer 匹配的执行中位耗时。基线存在
`ci/engine-benchmark-baseline.json`，只有当前中位数比基线慢 **超过 25%** 才失败（退出码 10），
避免把机器抖动带进 PR；用例结果自检失败（算法输出不对）是退出码 11，不当成"变快"。
基线记录机器名，跨机器时输出 `MACHINE_MISMATCH` 并跳过比较——绝对耗时没有跨机可比性，
换 runner 后用 `workflow_dispatch` 的 `update_baseline` 重新钉基线。口径与处置见
[`engine-benchmark.md`](engine-benchmark.md)。

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
