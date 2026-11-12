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

### 升级注意

- 数据根必须位于版本目录**之外**；升级换目录不会迁移或重建数据库。发布脚本会在
  `publish-runtime.ps1` 里做升级前 SQLite 在线备份 checkpoint，产物见数据根的 `upgrade-checkpoints/`。
- 数据库 schema **只向前**：被新版本迁移过的库会被旧版本拒绝启动。回退旧版本必须同时恢复
  checkpoint（发布脚本输出里有完整步骤），只换包不够。
- 生产环境（Production）会强制跳过内置虚拟机器人/PLC 适配器并告警，配了也不会启动；
  现场联调请使用 Testing 环境或真实设备。
- 非 loopback 明文监听默认拒绝启动；隔离测试网需要显式设置
  `Security:AllowInsecureRemoteTransport=true`。

### 回滚

1. 停宿主，把 `<output>.previous` 换回 `<output>`；
2. 用 `VisionStudio.SqliteSnapshot restore <checkpointDir> <liveDb>` 恢复升级前数据库
   （工具会校验 checkpoint 并清理目标 WAL/SHM）；
3. 重新核对物理设备状态后再恢复生产——数据回滚**不能**替代设备动作核对。
