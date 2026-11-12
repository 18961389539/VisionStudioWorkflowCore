# VisionStudio WorkflowCore

工业机器视觉流程编排与产线运行宿主。工程师在浏览器画布上编排检测流程、调参并用数据集验证，
通过后固化成"配方"发布到产线；宿主侧负责执行、留证（trace / 依赖清单 / 审计）与现场运维。

版本：V0.63 · 后端 .NET 10 · 前端 React 19 + TypeScript 7 + Vite 8

---

## 1. 目录结构

| 路径 | 内容 |
|---|---|
| `backend/src/VisionStudio.Api` | 宿主：Minimal API、认证/审计、限流、存储、发布与运行入口 |
| `backend/src/VisionStudio.Engine` | 执行引擎：节点执行器、图调度、相机/设备/机器人、算法与诊断 |
| `backend/src/VisionStudio.Abstractions` | 跨层契约：图定义、目录条目、数据值、成熟度 |
| `backend/src/VisionStudio.Camera.*` | 厂商相机适配（Basler pylon / 海康 MVS），按需装载 |
| `backend/src/VisionStudio.Plugin.*` | 插件打包、Worker 与示例插件 |
| `backend/src/VisionStudio.*Tool`/`SqliteSnapshot`/`Benchmark.Cli` | 目录导出、快照维护、基准 CLI |
| `backend/tests` | xUnit：`VisionStudio.Api.Tests`（宿主/接口）、`VisionStudio.Engine.Tests`（算法/调度） |
| `frontend/src` | 编辑器：画布、节点配置、调参工作区、配方与生产面板 |
| `frontend/src/generated` | 生成物：目录快照、OpenAPI 快照、由快照生成的 TS 类型 |
| `scripts` | 开发/发布/质量门禁脚本（见下） |
| `docs` | 设计与审计文档，索引见 [`docs/README.md`](docs/README.md) |
| `samples` | 可直接导入的示例流程 |

## 2. 本地开发

```powershell
# 终端 1：后端（Development 环境，自动登录内置管理员，端口 5180）
./scripts/run-dev.ps1

# 终端 2：前端（Vite 5173，/api 与 /hubs 代理到 5180）
cd frontend; npm install; npm run dev
```

- 后端**必须**以 Development 运行：Production 会拒绝 `Security:AutoLoginAdmin=true`（免密码管理员
  不得进入现场），直接用 `dotnet run` 默认按 Production 处理并拒绝启动。
- 数据根默认 `artifacts/dev-data`，与发布数据根分离，调试不会污染现场数据。

## 3. 发布与现场运行

```powershell
./scripts/publish-runtime.ps1 -Output D:\VisionStudio\0.63 -DataRoot D:\VisionStudioData
./scripts/run-runtime.ps1      # 或手工 dotnet <publish>/VisionStudio.Api.dll --contentRoot <publish>
```

发布链的硬约束（都有回归测试守着，不要绕过）：

- 数据根必须在版本目录**之外**，升级换目录不会迁移或重建数据库；
- 目录快照过期会直接失败（`--check`），不允许发布与代码不一致的节点目录；
- 老数据迁移是"暂存 → 校验 → 标记 → 原子切换"，中断重试不会留下半份数据。

### 现场配置要点

| 配置 | 默认 | 说明 |
|---|---|---|
| `Security:Enabled` / `AutoLoginAdmin` | `true` / 生产 `false` | 现场必须登录；免密码仅限 Development |
| `Security:AllowInsecureRemoteTransport` | `false` | 非 loopback 明文监听默认拒绝启动（只给隔离测试网开） |
| `ReverseProxy:Enabled` | `false` | 部署在 TLS 终止代理后**必须**打开，否则限流按代理 IP 分区（全厂共享 10 次/分钟登录配额）、会话 Cookie 丢掉 Secure、审计来源 IP 全是代理地址。开启后按 `KnownProxies` / `KnownNetworks` 声明可信代理 |
| `RobotTcpSimulator:Enabled` | 开发 `true` / 生产 `false` | 内置 TCP 模拟机器人；生产环境代码层面强制不启动（配了也跳过并告警），避免"以为在驱动真机" |
| `VISIONSTUDIO_DATA_ROOT` | `artifacts/runtime-data` | 生产数据根，随系统备份往返 |

## 4. 质量门禁

```powershell
./scripts/quality-gate.ps1                          # 全量（提交前本地跑这个）
./scripts/quality-gate.ps1 -Scope Backend           # 后端段
./scripts/quality-gate.ps1 -Scope Frontend          # 前端段（lint + 单测覆盖率 + 构建）
./scripts/quality-gate.ps1 -Scope E2E               # Playwright 真浏览器
./scripts/quality-gate.ps1 -Scope Frontend -SkipInstall   # 本地迭代：复用现成 node_modules
```

口径、棘轮与"为什么这么定"见 [`docs/quality-gates.md`](docs/quality-gates.md)。CI 把三段并行跑，
三段全绿才算通过。`-SkipInstall` 只在本地迭代时用，它跳过 `npm ci`，因此可能掩盖
`package-lock.json` 漂移——提交前请至少跑一次不带该开关的 `-Scope Frontend`。

> 脚本被系统执行策略拦下（`因为在此系统上禁止运行脚本`）时，用进程级放开即可，不改机器策略：
> `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/quality-gate.ps1 -Scope Backend`
> 或在当前会话先执行 `Set-ExecutionPolicy -Scope Process Bypass -Force`。

## 5. 契约生成链（改了后端 DTO 就看这里）

```
Produces<T>()  →  frontend/src/generated/openapi.json  →  api-types.ts  →  apiContractParity.ts 编译期对账
```

```powershell
dotnet build backend/VisionStudio.slnx            # 导出脚本用 --no-build 启动
pwsh scripts/export-openapi.ps1                    # 刷新 OpenAPI 快照
cd frontend; npm run generate:api-types            # 刷新 TS 类型（一次性 TS5 环境，不污染依赖）
bash scripts/generate-frontend-catalog.sh          # 节点目录有改动时刷新目录快照
```

`apiContractParity.ts` 用 `Backend extends Frontend` 做编译期对账：后端新增字段不会失败，但前端
声明了后端没有的字段会失败——这正是"手写镜像先漂移、运行时才发现"的那类问题。
