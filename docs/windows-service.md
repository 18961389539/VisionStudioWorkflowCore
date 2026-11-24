# Windows 服务宿主与崩溃转储

目标：把 `VisionStudio.Api` 作为 Windows 服务长期运行——**SCM 停止 = 优雅关闭**（数据库写入落定、
滚动日志排空），进程崩溃时可留证（WER 转储）。本文覆盖安装/卸载、升级、崩溃转储与
`UseWindowsService` 的取舍。

## 前提

- 目标机：Windows 10/Server 2016+，已安装 .NET 10 Runtime（与发布包的 TFM 一致）；
- 发布包由 `scripts/publish-runtime.ps1` 产出（含 `VisionStudio.Api.exe`、`appsettings.json`、
  `wwwroot/`、两个 Worker DLL）；
- 安装/卸载/转储脚本需要管理员权限（`#Requires -RunAsAdministrator`）；
- 生产数据根必须在发布目录**之外**，脚本会拒绝把数据根放进版本包（升级替换目录会连数据一起删）。

## 安装

```powershell
# 默认：artifacts/runtime-publish + artifacts/runtime-data + http://127.0.0.1:5080
./scripts/install-windows-service.ps1

# 现场：
./scripts/install-windows-service.ps1 `
    -PublishDir D:\VisionStudio\0.63 `
    -DataRoot   D:\VisionStudioData `
    -Url        http://127.0.0.1:5080
```

安装后的服务命令行为：

```
VisionStudio.Api.exe --contentRoot <publish> --environment Production
    --urls http://127.0.0.1:5080 --VisionStudio:DataRoot <data>
```

要点：

- 使用发布包自带的 **apphost EXE**，服务进程名为 `VisionStudio.Api`（崩溃转储按此名匹配）；
  若手工改为 `dotnet VisionStudio.Api.dll`，进程名会变成 `dotnet`；
- `--environment Production`：启用 `appsettings.Production.json`（`RuntimeOnly=true`、
  `AutoLoginAdmin=false`、Worker 隔离），设计器变更接口被禁用；
- 默认 URL 只绑 loopback，且脚本拒绝非 loopback 的明文 URL（主机自身的启动守卫也会拒绝）。
  远程访问请在前端放 TLS 终止代理并打开 `ReverseProxy:Enabled`；隔离测试网需要显式
  `-AllowInsecureUrl`；
- 服务默认 `delayed-auto` 启动，避开开机期与设备驱动的竞争；
- 恢复动作：首次/第二次/第三次异常退出后各隔 60 秒重启，一天内三次用完则保持停止，
  等待人工判断——无限重启会掩盖根因并刷爆事件日志；
- 同时创建 Application 事件源 `VisionStudio.Api`（`UseWindowsService` 的事件日志使用宿主名）。

验证：

```powershell
Get-Service VisionStudioApi | Format-List Status,StartType
Invoke-WebRequest http://127.0.0.1:5080/healthz -UseBasicParsing | Select-Object -Expand Content
Get-Content D:\VisionStudioData\logs\*.log -Tail 50
```

`/healthz` 只返回 `status/ready/productionReady`；详细健康（备份新鲜度、审计/日志计数、
脚本 Worker 状态）在登录后访问 `/api/health`。

## 升级

1. `Stop-Service VisionStudioApi` 并等待停止（服务把停止请求转成 `IHost.StopAsync`）。
2. 运行 `scripts/publish-runtime.ps1 -Output <新目录> -DataRoot <同一数据根>`；发布链会做
   数据库在线检查点、暂存/校验/原子切换包目录，并冒烟启动候选包。
3. 用 `sc.exe config VisionStudioApi binPath= "...\新目录\VisionStudio.Api.exe ..."` 更新路径，
   或卸载后重新安装（数据根不变）。
4. `Start-Service`，确认 `/healthz` 与登录后的 `/api/health`。

不要删除旧发布目录，直到新版本验收通过——发布链会保留 `<output>.previous` 供回滚。
回滚数据库需使用 `VisionStudio.SqliteSnapshot` 的检查点恢复（新版本迁移过的库会被旧版本拒绝），
步骤印在 `publish-runtime.ps1` 末尾。

## 卸载

```powershell
./scripts/uninstall-windows-service.ps1
./scripts/uninstall-windows-service.ps1 -RemoveEventSource   # 连事件源一起清理
```

- 先 `Stop-Service` 并最多等 120 秒；**未在时限内停止就拒绝删除**（强杀可能截断数据库写入）；
- 只删除服务注册；**数据根、发布目录、崩溃转储一律保留**；
- 同一数据根同时只允许一个宿主（`DataRootOwnership` 排他锁）：先停控制台实例，再启动服务，
  反之亦然。

## 崩溃转储

### WER LocalDumps（原生崩溃 / fail-fast / 栈溢出）

```powershell
./scripts/configure-crash-dumps.ps1
# 可选：-ProcessName VisionStudio.Api -DumpFolder D:\VisionStudioDumps -DumpType 2 -DumpCount 5
# 移除：./scripts/configure-crash-dumps.ps1 -Remove
```

脚本写入：

```
HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\VisionStudio.Api
    DumpFolder = <目录>   (REG_EXPAND_SZ)
    DumpType   = 1        (1=mini，2=full)
    DumpCount  = 5        (只保留最新 N 个)
```

- `DumpType=2`（full）可能达到 GB 级，只在定位期间临时开启；
- 转储可能包含生产图像、内存中的凭据与客户数据，按敏感材料保管与传输；
- 服务以 `LocalSystem` 运行时对 `C:\ProgramData\VisionStudio\crash-dumps` 有写权限；
  改用受限服务账号时需显式授予该目录写权限。

### 托管崩溃（可选）

.NET 运行时还支持由环境变量触发的托管转储：

| 变量 | 示例 | 说明 |
|---|---|---|
| `DOTNET_DbgEnableMiniDump` | `1` | 进程因未处理异常终止时写转储 |
| `DOTNET_DbgMiniDumpType` | `4` | 4 = 完整转储 |
| `DOTNET_DbgMiniDumpName` | `C:\ProgramData\VisionStudio\crash-dumps\vs-%p.dmp` | 输出路径模板 |

这些是**机器级**环境变量，会影响机器上所有 .NET 进程，所以不放进安装脚本自动设置；
需要时由运维显式设置并记录变更。

## `UseWindowsService` 取舍

`Program.cs` 在 `builder.Build()` 之前调用 `builder.Host.UseWindowsService()`，依赖
`Microsoft.Extensions.Hosting.WindowsServices`（`Directory.Packages.props` 统一登记）。

- **为什么需要**：没有它时，服务只是"SCM 启动的一个控制台进程"，`Stop-Service` 会直接终止
  进程，绕过 `IHost.StopAsync`——正在写数据库/刷日志时被硬杀不是可接受的生产行为。
  该包注册 `WindowsServiceLifetime`，把 SCM 的停止/关机控制映射为宿主优雅关闭，并接入
  Windows 事件日志；
- **控制台行为不变**：`UseWindowsService()` 内部先判断 `IsWindowsService()`，不在服务中运行
  时完全空操作——本地 `run-runtime.ps1`、集成测试与 `publish-runtime.ps1` 的冒烟启动均不受影响；
- **内容根**：作为服务运行时，它会将内容根指向 `AppContext.BaseDirectory`（即发布目录），
  与安装脚本传入的 `--contentRoot` 相同；因此服务必须直接从发布目录运行 EXE；
- **版本**：包版本 10.0.13 是当前可离线还原的补丁版本；其余 `Microsoft.Extensions.*` 仍是
  10.0.12，运行期由 `Microsoft.AspNetCore.App` 共享框架统一解析，不改变应用可见行为；
- **平台**：仅 Windows 使用；本仓库的生产目标即 Windows 工控机。

## 现场核对清单

1. `Get-Service VisionStudioApi`：`Running` + `Automatic (Delayed Start)`；
2. `GET /healthz`：`ready=true`；登录后 `/api/health` 的 `backup.stale` 与 `lastBackupAt` 符合预期；
3. `data/logs` 有当日滚动日志；Application 事件日志有 `VisionStudio.Api` 源记录；
4. 停一次服务再启动：数据库无异常、生产状态按配置恢复（`ProductionRuntime` 自动启动策略）；
5. 崩溃演练后确认 `crash-dumps` 目录出现新转储，并评估是否临时切回 `DumpType=1`。