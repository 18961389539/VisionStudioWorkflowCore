# 变更日志

按版本记录**对现场有影响**的变更：新增能力、行为变更、升级注意、回滚注意。纯内部重构不逐条列。

约定：

- 每次发布前新增一节，标题 `## <version> - YYYY-MM-DD`，版本必须与
  `backend/src/VisionStudio.Api/VisionStudio.Api.csproj` 的 `<Version>` 一致
  （`scripts/static-audit.py` 会校验，版本升了却忘写日志会直接红）；
- "升级注意"只写会让现场多做一步、或不做会出事的条目；
- 不写"修复若干问题"这类无法验证的措辞——现场要能据此判断"我是否受影响"。

## 0.63.0 - 2026-11-12

### 新增

- **节点级单次执行快照**：`GET /api/diagnostics/node-spans` 与诊断包内的
  `diagnostics/node-spans.json`，记录最近 1024 次节点执行的耗时、超时与错误，重启清零。
  与既有 `node-durations`（分位数）分工：一个回答"平时多慢"，一个回答"刚才那一次为什么慢"。
- **一键诊断包**：`GET /api/diagnostics/bundle`（管理员），打包版本/环境、健康计数、资产与耗时摘要、
  审计尾部、当天日志尾部；刻意不含数据库、留证图像、配置原文（可能含口令）。
- **登录失败锁定**：连续失败按账号锁定，返回 429；管理员可解锁。与按 IP 限流分工不同：
  限流防爆破速率，锁定防单账号被持续猜测。
- **手眼标定向导（2D/3D）**：会话草稿、不可变资产版本（发布/pin/回滚）、条件数门限、
  单位自检、鲁棒剔除上限；新增 `docs/hand-eye-calibration.md` 说明口径与踩坑。
- **模型资产**：`.vsasset` 导入导出、未引用版本回收、进入运行时依赖清单、训练工作区保存为资产。
- **流程级金样回归**：提交进仓库的固定 PNG 夹具 + 真实执行器，按容差而非哈希比对。

### 变更

- 前端类型安全：`tsconfig.app.json` 开启 `strict`，手写镜像类型按后端真实可空性放宽；
  引入 oxlint（`--max-warnings 0`）替代装不上的 typescript-eslint。
- **发布门禁**：含 Preview/Experimental 节点的配方发布必须显式确认，未确认返回
  `409 NODE_MATURITY_ACK_REQUIRED`；发布审计里记录确认的节点数量。回滚端点不设门禁。
- **反向代理支持**：`ReverseProxy:Enabled` 默认关闭；部署在 TLS 终止代理后必须打开，
  否则限流按代理 IP 分区、会话 Cookie 丢失 Secure、审计来源 IP 全为代理地址。
  开启后按 `KnownProxies` / `KnownIPNetworks` 声明可信代理。
- **文件规模预算与度量下限成为门禁**：单文件行数只许降不许升；测试数量与节点目录条数只许升。
- **空 catch 与依赖治理接入门禁**：未写原因的空 catch 锁定 106 处基线只许减；NuGet 版本收敛到
  `Directory.Packages.props`；前后端各跑一次 high/critical 漏洞扫描（离线时明确打印"跳过"，不静默当通过）。
- **监视面板改为推送**：相机/设备/机器人列表由 `/hubs/monitor` 在内容变化时广播，定时轮询降为
  10 s 兜底；数据陈旧的 TTL 判定拆成独立 500 ms 节拍，不跟着轮询放慢。
- **交付可追溯**：发布产物新增 `build-info.json`（版本、commit、分支、工作区是否脏、API DLL 哈希）；
  版本变更必须在本文件有对应小节，否则静态审计失败。
- **参数分组中文反查冲突**：`Lattice`/`Grid`、`Measure`/`Measurement`、`Provenance`/`Source`、
  `Decoding`/`Decode` 四组此前共用同一个中文名，`canonicalParameterGroup` 反查会写回另一个契约值，
  保存配方时可能把错误的 group 写进文档。四组中文现已区分并加唯一性断言；界面文案随之变化：
  `网格`→`点阵`(Lattice)、`测量`→`测量项`(Measurement)、`来源`→`输入来源`(Source)、
  `解码`→`解码设置`(Decode)。已保存的配方若含旧中文分组名，升级后需重新选一次分组。

### 内部改进（不改变现场行为）

- 三个巨型文件按域纯移动拆分：`VisionWorkflowCompiler` 1740 → 5 个 partial（最大 584 行）、
  `NodeCatalog` 1622 → 6 个（最大 471 行）、`ProductionRuntime` 1498 → 5 个（最大 416 行）。
  拆分脚本做逐行/多重集校验，目录导出器金样（242 节点、哈希不变）与 2790 个后端测试确认行为未变。
- i18n 收尾：清掉 `descriptions` 里 11 条以整句英文为键的**死翻译**（查表只按节点类型，原本永不命中）；
  补齐 40 个目录分组的中文登记；新增稳定键守卫（禁止原文键、悬空语义键棘轮 126 条、分类/分组零回落、
  中文值唯一、真实目录双语本地化）。
- 门禁稳定性：v8 覆盖率产物移到系统临时目录（provider 会无条件递归删内部 `.tmp`，在本机会触发
  批量删除守卫并把 vitest 打断成 unhandled error）；后端覆盖率改用独立目录，只清少量 GUID 子目录。
- 门禁稳定性（续）：E2E 段拉起真宿主前，对仅大小写不同的重复环境变量做进程内等价去重
  （`HTTP_PROXY`/`http_proxy` 会让 Windows PowerShell 5.1 的 `Start-Process` 抛 `ArgumentException`）；
  浏览器改用 `playwright install chromium --no-shell`，只装配置里实际启动的完整 Chromium，
  不再每轮额外下载 118 MiB 的 headless shell；真后端宿主改为从 DLL 目录启动（原先 CWD 是仓库根，
  宿主读不到 `appsettings.json` 而把 `AutoLoginAdmin` 退化成 false），并显式钉死该项配置、
  把 `/api/auth/status` 响应打进门禁日志；每轮 e2e 用独立的临时 `outputDir`，避免 Playwright
  递归清空历史 trace 时撞上批量删除守卫；修正 CI 上传 Playwright 报告的路径。
- 门禁稳定性（CI 段）：`-Scope E2E` 在缺 `node_modules` 时按锁文件 `npm ci`——三段并行各独立
  checkout，原先 E2E job 根本没有前端依赖，`npx playwright` 会临时拉一个与锁文件不一致的版本；
  环境去重只在 Windows PowerShell 5.1 执行（pwsh/.NET Core 无此缺陷且删除语义不同），
  已用 pwsh 7 + `CI=true` 本地复跑 9 个 e2e 用例验证。
- 门禁稳定性（前端段）：Vite `cacheDir` 与 Vitest `fsModuleCachePath` 改到系统临时目录
  （默认 `node_modules/.vitest-cache` 攒到 500+ 文件后，清理会撞批量删除守卫并报 `Startup Error`）；
  `testTimeout`/`hookTimeout` 15 s → 30 s、`maxWorkers` 8 → 4，适配 CI 的慢 runner；
  两个会抢在 state 更新前的 DOM 断言改成等待（断言内容不变）。CI 用例口径不变。

### 升级注意

- 数据根必须位于版本目录**之外**；升级换目录不会迁移或重建数据库。发布脚本会在
  `publish-runtime.ps1` 里做升级前 SQLite 在线备份 checkpoint，产物见数据根的 `upgrade-checkpoints/`。
- 数据库 schema **只向前**：被新版本迁移过的库会被旧版本拒绝启动。回退旧版本必须同时恢复
  checkpoint（发布脚本输出里有完整步骤），只换包不够。
- 生产环境（Production）会强制跳过内置虚拟机器人/PLC 适配器并告警，配了也不会启动；
  现场联调请使用 Testing 环境或真实设备。
- 非 loopback 明文监听默认拒绝启动；隔离测试网需要显式设置
  `Security:AllowInsecureRemoteTransport=true`。
- **健康检查拆分（2026-11-13）**：匿名探针改为 `GET /healthz`（仅 `status/ready/productionReady`）；
  `GET /api/health` 需要登录，保留降级原因、阻断项与内部计数。现场监控、反向代理与编排的
  健康检查请改用 `/healthz`，否则会收到 401。

### 回滚

1. 停宿主，把 `<output>.previous` 换回 `<output>`；
2. 用 `VisionStudio.SqliteSnapshot restore <checkpointDir> <liveDb>` 恢复升级前数据库
   （工具会校验 checkpoint 并清理目标 WAL/SHM）；
3. 重新核对物理设备状态后再恢复生产——数据回滚**不能**替代设备动作核对。
