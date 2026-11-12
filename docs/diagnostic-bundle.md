# 一键诊断包

现场排障此前的动作是"让现场人员把整个数据根拷回来"。那个目录里混着数据库（几百 MB）、
留证图像（几万张）和日志，既传不动，又会把与问题无关的生产数据带出车间。

`GET /api/diagnostics/bundle` 只打包**回答一个问题所需的最小集合**：这台机器现在为什么不正常。

- 服务：`backend/src/VisionStudio.Api/Diagnostics/DiagnosticBundleService.cs`
- 端点：`DiagnosticsEndpoints.cs`，`RequireAdministrator("diagnostics.bundle", "diagnostics")`
- 测试：`backend/tests/VisionStudio.Api.Tests/DiagnosticBundleTests.cs`

## 包里有什么

| 条目 | 内容 | 用来判断 |
|---|---|---|
| `manifest.json` | 版本、机器名、进程号、OS/运行时、contentRoot、dataRoot、日志与审计可靠性计数 | 环境是否被替换、日志有没有在丢 |
| `diagnostics/summary.json` | 资产健康汇总 | 模型/资产缺失或校验失败 |
| `diagnostics/assets.json` | 资产明细 | 具体是哪个资产 |
| `diagnostics/run-counters.json` | 跨轮计数器落盘状态 | 计数器有没有落后于实际轮次 |
| `diagnostics/node-durations.json` | 每节点耗时分位数 | 哪个节点变慢 |
| `diagnostics/node-spans.json` | 最近 64 次节点执行 + 失败清单（含 `overwritten`） | 刚才那一次为什么慢/为什么失败 |
| `audit/tail.json` | 最近 200 条审计事件 | 谁改了什么、发布/解锁动作 |
| `logs/*.log` | 最新一个日志文件的**尾部**（≤ 512 KB） | 直接看栈 |
| `README.txt` | 条目说明与"不含什么" | 现场人员自己就知道该再要什么 |

`manifest.json` 里的 `logging.droppedEntries / writeFailures / flushFailures / drainTimeouts`
与 `auditPersistence.failures / lastFailureAt / lastFailure` 是**可靠性计数而不是状态展示**：
日志在丢、审计写不进库，这类问题不会让任何接口返回错误，只看接口永远发现不了。

## 包里刻意没有什么

| 不含 | 原因 |
|---|---|
| 数据库文件 | 几百 MB，传输成本高；需要时按 `manifest.json` 的 `dataRoot` 由现场单独导出 |
| 留证图像 | 数量以万计，且包含生产件图像 |
| 配方/流程文档 | 体积不小，且导出配方应走正式的导出接口（带版本与清单） |
| **配置文件原文** | `appsettings*.json` 可能含口令、密钥；诊断包会被邮件/IM 转发 |

最后一条是硬约束：**宁可让人按 manifest 里的路径自己去取，也不把可能含密钥的文件塞进一个会被转发的 zip**。
新增条目时先问"这份内容会不会在转发链路里泄露凭据"。

## 日志为什么是尾部而不是整份

现场可能已经连续跑了几天，单个日志文件几十 MB。`WriteLogTailAsync` 从文件末尾回读
`MaxLogBytes`（512 KB），并且在真的截断时丢掉第一行——从中间切会留下半条时间戳或半段 JSON，
排查的人会以为日志本身坏了。

读文件用 `FileShare.ReadWrite | FileShare.Delete`：日志正在被写，用默认共享模式会直接 IOException。

## 怎么改

| 想加的内容 | 注意 |
|---|---|
| 新的诊断 JSON | 加进 `diagnostics/`，同时更新 `README.txt` 与 `DiagnosticBundleTests` 的条目断言 |
| 调大日志上限 | 改 `MaxLogBytes`，并在提交说明里写清"为什么 512 KB 不够"——包越大越难传回 |
| 给非管理员开放 | 不要。审计尾部和日志里有用户名、路径、设备地址 |
