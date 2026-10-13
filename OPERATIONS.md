# VisionStudio 运维与交付手册

覆盖：部署拓扑、升级 / 回滚、数据恢复（含设备核对闸门）、诊断入口、发布验证门槛。
命令默认在仓库根目录执行；脚本为 PowerShell（Windows 优先），批注标 Bash 的按需替换。

---

## 1. 部署拓扑

**版本包与生产数据根严格分离**（R08 语义：升级切换不得重置数据）：

| 项 | 位置 | 内容 |
| --- | --- | --- |
| 版本包 | `artifacts/runtime-publish`（`scripts/publish-runtime.ps1` 输出） | 后端 DLL、wwwroot 前端、依赖 |
| 数据根 | 由 `VISIONSTUDIO_DATA_ROOT` 指定（默认 `artifacts/runtime-data`） | 数据库、媒体、设备配置、工作流、插件、备份、日志 |

数据根内容（备份 / 恢复范围）：`visionstudio.db`、`media/`、`devices/`、`provenance/`、
`production/`、`workflows/`、`plugin-packages/`、`plugin-trust/`、`site-config/`、`backups/`、`logs/`。

启动：

```powershell
$env:VISIONSTUDIO_DATA_ROOT='<数据根绝对路径>'
scripts/run-runtime.ps1
```

## 2. 升级与回滚（scripts/publish-runtime.ps1）

发布流程内置安全门（任一失败都不会触碰当前版本）：

1. 预检：`dotnet restore` + 生成目录（catalog）一致性——失败即中止。
2. 前端构建 → 打入 wwwroot。
3. `dotnet publish` 到 staging，**候选包启动烟雾验证**：以候选包自身为内容根、Production 环境 +
   隔离数据根启动候选宿主，90 秒内 `/api/health` 必须返回 **`status=ok` 且 `ready=true`**
   （仅 HTTP 200 不算通过；degraded/未就绪会列出阻断原因），并校验资产清单、打包前端与插件加载
   日志；通过时记录候选包哈希与允许的测试配置项（安全/模拟器关闭）。
4. **升级前数据库检查点**：用 `VisionStudio.SqliteSnapshot`（SQLite Online Backup API）生成
   一致性快照到 `<数据根>/upgrade-checkpoints/<时间戳>/`，附 integrity/schema/哈希清单
   （manifest.json）。活动写者下亦安全；仍建议停止运行时以缩短风险窗口。
5. 事务式目录切换（失败自动还原旧目录）；旧版本保留在 `<输出>.previous`。

**回滚步骤（顺序不可颠倒）**：

1. 停止宿主。
2. 恢复旧包：`<输出>.previous` → `<输出>`。
3. **先恢复检查点数据库**（停机状态）：用维护工具做事务化恢复——校验 manifest 哈希与 schema、
   拒绝活跃 sidecar、建立保护副本、原子替换并验证；任一失败自动从保护副本回滚：

   ```powershell
   dotnet run --project backend/src/VisionStudio.SqliteSnapshot -c Release -- restore <检查点目录> <数据根>\visionstudio.db
   ```

   被新版本迁移过的数据库会触发旧版本的 schema 前瞻保护而拒绝启动（包回滚 ≠ 数据回滚）。
   若宿主未停或目标存在非空 WAL/SHM，工具会**拒绝执行**（`--force` 仅限异常场景的显式逃生口，
   责任自负）。
4. **设备重新核对**：数据回滚不改变物理设备事实。维护工具恢复成功即自动登记设备核对闸门
   （`storage-restore:cli:<id>`，见 3.2/3.3）——启动后生产动作被阻断，直到按第 3.3 节提交
   现场核对证据；未闭合的设备动作意图只能以现场核对证据解除，绝不能用数据回滚消除。

**兼容边界**：数据库 schema 只向前迁移；插件、站点配置、设备配置随数据根保留；
跨版本升级前确认 `.NET` runtime 版本与 `/api/runtime/dependencies` 依赖清单。

## 3. 数据恢复

### 3.1 系统备份 / 恢复（宿主在线可执行）

- 创建备份（管理员）：`POST /api/storage/backups` —— 包含 DB、附件、媒体、设备 / 来源配置、
  生产配置、编辑器工作流、插件包与信任库、站点配置；**不包含**设备动作安全账本（见 3.3）。
- 恢复：要求生产 Runtime 已 Stopped；成功后需要**重启宿主**（503 `restart_required`）。
  恢复完成自动进入"恢复后设备核对"状态。
- **空白机恢复清单**：数据根 + 上述备份资产 + 插件 worker（`VisionStudio.Plugin.Worker.dll`）；
  逐项验证 `/api/health`、追溯页、设备连接、插件加载、工作流文档列表。

### 3.2 数据根手动恢复

停宿主后，用维护工具恢复数据库（校验检查点清单 → 保护副本 → 原子替换 → 验证；失败自动回滚，
中断有自动恢复路径）：

```powershell
dotnet run --project backend/src/VisionStudio.SqliteSnapshot -c Release -- restore <检查点目录> <数据根>\visionstudio.db
```

**统一维护协议（2026-10-13 审查 Q01/Q02/Q04）**：工具与宿主共用同一事实源——
`<数据根>/.storage-maintenance/restore-transaction.json`（旧版工具写在数据根的
`.visionstudio-restore-intent.json` 仍被宿主识别）。关键语义：

- **互斥与活跃保护先行**：恢复前依次取得同数据根命名互斥锁、检查另一维护进程的活跃标记
  （`.storage-maintenance/maintenance-active.json`，pid + 进程启动时间判定残留）、检查目标
  非空 WAL/SHB——全部通过后才可能进入中断恢复 / 删除 / 复制。`--force` 只跳过非空 sidecar
  这一活性启发式；**存活**维护进程的标记不可被 `--force` 越过（进程已死的残留标记按存活
  判定自动接管），协议校验（manifest、意图目标身份、保护副本核对）也永远不跳过。
- **意图目标核对**：恢复前核对意图记录的 `TargetDatabase` 与当前目标一致，绝不错消费属于
  另一个目标的意图。
- **哈希自证的中断恢复**：发现未完成意图时，若目标内容已等于检查点哈希（切换其实已完成、
  崩溃发生在 committed 标记写入之前），补登记 committed 并退役，**绝不误回退**；若目标仍等于
  保护副本哈希（切换未开始 / 已回滚），直接退役；否则才从保护副本恢复并核对恢复结果。
- **提交与清理分离**：committed 标记以原子 + 落盘方式写入，**写入失败返回非零退出码**——
  绝不再把"已成功的恢复"当作清理失败吞掉（旧缺陷：文件锁一幕即让恢复带着 switching 残留
  退出 0，下一次运行误回退）。
- **成功后登记设备核对闸门**：工具恢复成功即在 `production/device-action-safety.json`
  登记 `storage-restore:cli:<id>` 的 Unknown 状态（与 API 恢复同一语义）——生产启动前必须
  完成第 3.3 节核对，不再只是打印文字提醒。已存在的安全状态一律保留。

启动时若检测到未完成的恢复意图（任一标记、任一非 committed 阶段），宿主进入**失败锁**：
`/api/health` 的 `degradedReasons` 列出具体原因，普通变更与设备动作持续 503，直到按提示重跑
恢复或经受控恢复入口（`POST /api/storage/maintenance/recover`）人工核对后清除。工具的保护副本
（`.visionstudio-restore-backup/`）在恢复成功后保留，供审计与最坏情况取用（其内容永远不含
设备动作安全账本——见 3.3）。

### 3.3 设备动作安全闸门（不随数据回退的事实）

- 副作用流程执行前**耐久登记意图**；失败 / 取消 / 超时后"设备动作结果未知"被持久化到
  `production/device-action-safety.json`（含锁定的设备 / 机器人 / 相机清单与身份指纹）。
- 任何启动路径（人工 Start / Recover / 自动启动）都会被闸门阻断，直到管理员通过
  `POST /api/production/device-actions/resolve` 提交**现场核对证据**（含设备身份证据）。
- 手动 / 临时动作意图台账：`production/manual-action-intents.json`（同一语义）。
- **该账本不参与备份、恢复与回滚都不会覆盖它**——系统恢复、数据回滚、宿主重启都不构成
  设备核对；未闭合动作只能以明确证据解除（`GET /api/production/device-actions/pending` 列出待核对项）。

## 4. 诊断入口

| 入口 | 用途 |
| --- | --- |
| `GET /api/health` | 进程 / 存储 / 审计失败计数 |
| `<数据根>/logs/` | 滚动日志（按天，保留 14 天），含完整异常链 |
| `X-Correlation-ID` / ProblemDetails `code`+`correlationId` | 请求级关联，贯通响应 / 审计 / 日志 |
| `GET /api/production/status` / `device-actions/pending` | 生产状态与待核对设备动作 |
| 前端 trace 面板 | 运行历史、节点时间线、失败签名、调查工单 |
| `GET /api/storage/status` | 容量与备份集合 |

## 5. 发布验证门槛

- `scripts/quality-gate.ps1`（或 `.sh`）：静态审查 + 生成目录一致性 + 数据迁移 / 发布分离回归 +
  后端全量测试（排除 Soak）+ 覆盖率下限 + **编译器警告基线** + 前端测试 / 构建。
- 现场验收：`scripts/l3-fault-drills.ps1`（故障演练）、`scripts/l3-soak.ps1`（24/72h 资源趋势），
  记录报告、恢复耗时与设备核对证据。

## 6. 版本与变更核对（发布前检查单）

- [ ] 数据根已备份（`POST /api/storage/backups` 或检查点）且校验通过。
- [ ] 候选包烟雾验证通过（publish 脚本自动执行）。
- [ ] 生产 Runtime 已停止；无未闭合设备动作（`device-actions/pending` 为空或已核对）。
- [ ] schema 迁移方向确认（仅向前）；回滚用检查点已就绪。
- [ ] 插件 / 设备 / 站点配置的兼容性确认（依赖清单一致）。
