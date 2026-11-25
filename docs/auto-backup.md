# 自动备份与备份新鲜度

目标：主机自己每天产出可恢复备份，并且"最近一次备份有多旧"在 `/api/health` 可见。
备份包格式、恢复事务、失败锁与保留机制不在本文重复——见
[`StorageBackupService`](../backend/src/VisionStudio.Api/Storage/StorageBackupService.cs)、
[`docs/quality-gates.md`](quality-gates.md) 与 `StorageMaintenanceTests`。

## 默认行为

- 进程启动后延迟 **10 分钟**开始检查，此后每 **30 分钟**检查一次；
  距最近一次备份（**任意来源**，含手工备份）≥ **24 小时**时创建一份。
- 自动备份默认**不打包追溯附件**（`IncludeArtifacts=false`）：附件是可选证据且体积大。
  需要附件时用 `POST /api/storage/backups` 手工备份，或把该开关打开。
- 自动与手工备份共用保留策略 `Storage:BackupRetentionCount`（默认 10 份，超出删最旧）。
- 默认备份仍只在本机数据根的 `backups` 目录，**不等同异机灾备**。配置
  `Storage:BackupSecondaryRoot` 后，每份手工/自动归档都会复制到该目录，并在提交前校验 SHA-256；
  清单与恢复入口在主副本丢失时会从副本读取。副本保留数与主目录使用同一保留值。
- `BackupSecondaryRoot` 应指向独立磁盘、挂载卷或 SMB/NFS 共享；本功能只支持文件系统路径，
  不提供对象存储/S3 客户端，也不能证明两个目录位于不同物理故障域。需在现场确认挂载和 ACL。
- 配置了副本但复制/校验失败时，备份操作会显式失败；本机归档可能已创建，自动备份状态会报告
  `lastError`，修复副本存储后再核对清单。
- 生产运行处于 `Starting/Stopping/Recovering` 过渡态，或存储处于维护窗口、恢复失败锁、
  恢复后待重启状态时，本次检查跳过，下个周期再试——备份绝不与恢复/维护竞争。
- 生产 `Running` 时允许在线备份（附件冻结窗口，见 `StorageBackupService`）；备份窗口内
  新 HTTP 请求会被维护门短暂拒绝，与手工备份一致。需要完全无感时把检查安排在停机班次。
- `Enabled=false` 只关闭"自动创建"；健康端点仍照常报告最近一次备份与新鲜度。

## 配置

| 键 | 默认 | 说明 |
|---|---|---|
| `Storage:AutoBackup:Enabled` | `true` | 自动创建总开关（手工备份不受影响） |
| `Storage:AutoBackup:IntervalHours` | `24` | 到期间隔（1–720） |
| `Storage:AutoBackup:CheckIntervalMinutes` | `30` | 调度检查周期（1–1440） |
| `Storage:AutoBackup:StartupDelayMinutes` | `10` | 启动后延迟，避开启动期的迁移/恢复（0–1440） |
| `Storage:AutoBackup:StaleAfterHours` | `48` | 新鲜度阈值（1–8760），仅用于健康披露 |
| `Storage:AutoBackup:IncludeArtifacts` | `false` | 自动备份是否打包追溯附件 |
| `Storage:BackupSecondaryRoot` | 未配置 | 可选的独立文件系统副本目录；需由部署提供并监控挂载 |

现场可用数据根的站点配置覆盖（优先级高于 `appsettings.json`）：

```json
{ "Storage": { "AutoBackup": { "Enabled": true, "IntervalHours": 12 } } }
```

启用独立文件系统副本（Windows 例）：

```json
{ "Storage": { "BackupSecondaryRoot": "E:\\VisionStudioBackups" } }
```

不要把路径设为主备份目录的子目录。共享卷应配置为服务账户可写且开机自动挂载；在部署验收时
拔除/屏蔽主备份目录后，使用副本中的 `BackupId` 执行恢复演练。

路径：`data/site-config/appsettings.site.json`（随系统备份导出/恢复，见
`VisionStudioSiteConfig`）。环境变量/命令行参数仍可最终覆盖。

## 健康披露（`GET /api/health`，需认证）

新增 `backup` 段：

| 字段 | 含义 |
|---|---|
| `autoEnabled` | 自动创建是否开启 |
| `intervalHours` / `staleAfterHours` | 当前生效的到期间隔与陈旧阈值 |
| `lastBackupId` / `lastBackupAt` / `lastBackupAgeHours` | 最近一次备份（含手工备份）的标识、时间与年龄 |
| `lastBackupIncludesArtifacts` | 最近一次备份是否含附件 |
| `secondaryDestinationConfigured` | 是否配置副本文件系统 |
| `latestSecondaryBackupId` / `latestSecondaryBackupAt` | 副本目录里可读取的最近归档 |
| `secondaryListError` | 副本目录不可用时的错误（不影响主清单返回） |
| `stale` | `lastBackupAt` 缺失或年龄超过阈值 |
| `lastAttemptAt` / `nextCheckAt` | 本进程最近一次自动尝试与下一次计划检查 |
| `lastError` / `listError` | 最近一次自动备份失败 / 备份清单无法读取（不吞错） |

**`stale` 不进入 `degradedReasons` 与 `productionBlockers`**：没有新备份值得告警，但不该让一台
健康主机被判为不可接单。`/healthz`（匿名）保持最小响应，不暴露备份细节。

清单读取失败只记录在 `listError`，健康端点本身照常返回；备份管理器每 30 秒最多重新枚举一次清单。

## 演练与回归

`backend/tests/VisionStudio.Api.Tests/Storage/AutoBackupTests.cs`：

- `Health_ReportsStaleBeforeFirstBackup_AndFreshAfterAutomaticBackup`：
  无备份时 `stale=true`；强制创建后 `lastBackupId/lastBackupAt` 更新、`stale=false`，
  且 `status/ready` 不因备份变化。
- `AutomaticBackup_IsRestorable_Drill`：自动备份产物经 `StorageBackupService.RestoreAsync`
  恢复，并验证数据库回到备份时点——"能备份"与"能恢复"是同一份产物的验收。
- `DisabledScheduler_StillReportsFreshness_ButCreatesNoBackups`：`Enabled=false` 时
  健康披露仍在、且不创建备份。
- `SecondaryBackupCopy_IsVerifiedAndCanRestoreWhenPrimaryCopyIsMissing`：副本经校验、主归档移除后仍可列出并恢复。

## 现场核对清单

1. `GET /api/health` → `backup.lastBackupAt` / `backup.stale` / `backup.lastError`。
2. `GET /api/storage/backups` → 备份清单；`POST /api/storage/backups` → 立即手工备份。
3. 恢复：`POST /api/storage/restore`。恢复成功后主机进入"待重启"（业务 503 `restart_required`），
   重启进程后才恢复服务——这是把"磁盘已恢复、内存未重载"挡在业务之外的设计。
   恢复不会覆盖当前设备动作安全账本及已受理触发队列；这些属于恢复前的现场事实，需按账本和触发状态单独核对。
4. 长期 `stale=true` 且 `lastError` 非空：先处理容量（`GET /api/storage/capacity`）与
   维护/失败锁原因，再手工触发一次备份验证。