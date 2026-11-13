# 脚本节点下沉 Worker 进程改造方案

状态：Phase 3 已实施（生产默认 WorkerProcess + Windows Job Object 内存/进程上限；CPU 可选；受限令牌/网络 ACL 未实现） · 日期：2026-11-13
关联：[`node-maturity.md`](node-maturity.md)（`script.csharp` = Preview）、`CSharpScriptRunner.cs`、`PluginWorkerSupervisor.cs`

## 1. 结论先行

复用现有插件 Worker 的**进程池 + 命名管道 + 共享内存图像传输 + 超时/重启策略**，新增：

- `VisionStudio.Script.Worker`：只承载 `script.csharp` 的子进程；
- `ScriptWorkerSupervisor`：宿主侧进程池管理；
- `IScriptExecutionBackend`：`CSharpScriptNode` 的执行后端抽象，默认 `InProcess`，可切换 `WorkerProcess`。

目标是把脚本执行从"宿主进程内的 trusted-code"提升为"可强杀、可限内存、崩溃不带走宿主"的隔离单元。
**这不是 OS 沙箱**：denylist、工程师权限、发布确认全部保留；文件/网络等 OS 级限制放到第三阶段。

## 2. 现状盘点

### 2.1 当前执行链

```
CSharpScriptNode.ExecuteAsync
  → 参数校验 / Mat 克隆 / 输入类型映射
  → CSharpScriptRunner.RunAsync（Roslyn，进程内）
  → ScriptExecutionResult → VisionValue 输出
```

风险集中在宿主进程内：

| 风险 | 现状 | 后果 |
|---|---|---|
| 原生崩溃 | OpenCvSharp / 脚本调用的未列明 API 崩溃 | 整个 API 进程退出，产线运行中断 |
| 无限循环 | `maxTicks` + 墙钟检查 | BCL/原生调用不 yield 时无法中断 |
| 内存膨胀 | `maxImagePixels` / `maxOutputBytes` | 大对象只限制脚本输出，不限制中间分配 |
| 线程饥饿 | 脚本同步阻塞 | 占用宿主线程池，影响其它节点/接口 |
| 进程句柄/临时文件 | 脚本可绕过 denylist 调用未列明 API | 只能靠发布前人工审批 |

### 2.2 可直接复用的 Worker 基础设施

| 能力 | 位置 | 复用方式 |
|---|---|---|
| 命名管道协议 | `Abstractions/PluginWorkerWire.cs`、`PluginWorkerContracts.cs` | 原样复用（v3） |
| 值/图像编解码 | `Engine/PluginWorkerValueCodec.cs` | 原样复用 |
| 文件映射共享内存 | `Engine/PluginWorkerSharedMemoryStore.cs` | 原样复用 |
| 进程池 / 排队 / 性能窗口 | `Api/PluginWorkerSupervisor.cs` 的 `WorkerPool` / `WorkerClient` | 抽成通用 `WorkerProcessPool` |
| 超时杀进程 / 重启熔断 | 同上 `WorkerClient` | 原样复用 |
| 健康状态 / 重启接口 | `Endpoints/PluginWorkerEndpoints.cs`、`PluginWorkerStatus` | 仿照新增脚本 Worker 状态 |

### 2.3 不能直接复用的部分

- `PluginWorkerSupervisor` 绑定插件程序集、插件 ID 与工具发现；
- `VisionStudio.Plugin.Worker/Program.cs` 只加载 `IVisionPlugin`；
- `CSharpScriptNode` 直接 `new` 进程内 runner，没有后端抽象。

## 3. 目标架构

```
┌──────────────────────────── WorkflowCore 宿主 ────────────────────────────┐
│                                                                          │
│  CSharpScriptNode                                                        │
│    └─ IScriptExecutionBackend                                            │
│         ├─ InProcessScriptExecutionBackend ──→ CSharpScriptRunner        │
│         └─ WorkerScriptExecutionBackend ─────→ ScriptWorkerSupervisor    │
│                                                    │ 命名管道 + 共享内存  │
└────────────────────────────────────────────────────┼─────────────────────┘
                                                     ▼
                                   ┌──────────────────────────────────────┐
                                   │ VisionStudio.Script.Worker（子进程） │
                                   │  CSharpScriptNode                    │
                                   │    └─ InProcessScriptExecutionBackend│
                                   │         └─ CSharpScriptRunner        │
                                   └──────────────────────────────────────┘
```

关键点：**子进程运行同一份 `CSharpScriptNode` 代码**。宿主只负责编解码与进程管理，
参数校验、输出映射、diagnostics 口径不复制、不漂移。

## 4. 组件改动

| 类型 | 文件 | 改动 |
|---|---|---|
| 新增 | `backend/src/VisionStudio.Script.Worker/VisionStudio.Script.Worker.csproj` | 引用 `VisionStudio.Engine`、`VisionStudio.Scripting` |
| 新增 | `backend/src/VisionStudio.Script.Worker/Program.cs` | 解析 `--worker-kind script`，注册 `script.csharp`，循环处理 `execute` |
| 新增 | `backend/src/VisionStudio.Api/Workers/WorkerProcessPool.cs` | 从 `PluginWorkerSupervisor` 抽出 `WorkerPool`/`WorkerClient`（纯移动，行为不变） |
| 新增 | `backend/src/VisionStudio.Api/Plugin/ScriptWorkerSupervisor.cs` | 脚本 Worker 池：启动、状态、重启、执行 |
| 新增 | `backend/src/VisionStudio.Engine/Scripting/ScriptExecutionBackends.cs` | 进程内/Worker 两个后端实现 |
| 新增 | `backend/src/VisionStudio.Engine/Scripting/ScriptExecutionOutcome.cs` | 后端中立结果 DTO（含 owned Mat） |
| 修改 | `backend/src/VisionStudio.Engine/Nodes/CSharpScriptNode.cs` | 构造函数注入 `IScriptExecutionBackend`，调用点替换 |
| 修改 | `backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs` | 注册 `ScriptWorkerOptions`、supervisor、按配置选择后端 |
| 修改 | `backend/src/VisionStudio.Api/appsettings.json` | 新增 `ScriptWorkers` 配置节 |
| 修改 | `backend/VisionStudio.slnx`、`VisionStudio.Api.csproj` | 新项目引用与 Worker 发布拷贝 |
| 修改 | `scripts/publish-runtime.ps1` / `.sh` | 必需资产加入 `VisionStudio.Script.Worker.dll` |
| 修改 | `scripts/static-audit.py` | 结构性断言（见 §10） |

### 4.1 后端抽象

```csharp
public interface IScriptExecutionBackend
{
    ValueTask<ScriptExecutionOutcome> RunAsync(
        string source,
        ScriptExecutionOptions options,
        IReadOnlyDictionary<string, object?> inputs,
        Mat? image,                         // ownership transferred
        CancellationToken cancellationToken);
}
```

`ScriptExecutionOutcome` 暴露 `Result/Number/Text/Boolean/Json/Logs/Ticks/CompileMs/ExecuteMs/SourceHash/Image`，
`Image` 为 owned `Mat`；`CSharpScriptNode` 的现有输出映射与 diagnostics 保持不变。

### 4.2 Worker 内执行

`Program.cs` 增加 `--worker-kind script` 分支：

```csharp
if (options.WorkerKind == "script")
{
    registry.Register(scriptCatalog, new CSharpScriptNode(new InProcessScriptExecutionBackend()), "builtin");
    // discovery 返回合成 descriptor：id = builtin.script，tools = [script.csharp]
}
```

`execute` 请求继续走现有 `PluginWorkerRequest`：

- `NodeType = "script.csharp"`
- `Node` = 节点定义（source / timeoutMs / maxTicks / ...）
- `Inputs` = `PluginWorkerValueCodec` 编码后的输入
- `WorkflowScope` / `RunId` 原样传递

宿主 `WorkerScriptExecutionBackend` 负责：

1. 编码输入（图像走共享内存）；
2. 发送 `execute`；
3. 解码输出（图像解码为 owned Mat）；
4. 映射为 `ScriptExecutionOutcome`；
5. 失败时清理共享内存文件。

## 5. 超时、取消与内存

| 场景 | 策略 |
|---|---|
| 脚本自身超时 | `CSharpScriptRunner` 内的 `timeoutMs`（脚本参数，10..10000） |
| IPC 兜底超时 | `max(ScriptWorkers:ExecutionTimeoutMs, timeoutMs + 3000)`，覆盖编译与进程调度 |
| 宿主取消 | 与插件 Worker 一致：**终止进程**，保持单流管道同步 |
| Worker 崩溃 | 抛 `InvalidOperationException`，由节点失败语义上报；下一条请求自动重启 |
| 重启风暴 | 复用 `MaxRestartsPerMinute` 熔断，熔断后需显式重启接口 |
| 内存超限 | 先复用响应后的 `MaxWorkingSetMb` 检查；第三阶段改用 Windows Job Object 硬上限 |
| 共享内存失败 | 回落 PNG 传输（与插件一致），并在 diagnostics 标记 |

> 不设"Worker 不可用时自动回落进程内"——生产环境静默回落等于绕过隔离。
> 仅 Development 可选 `FallbackToInProcess=true`，并在日志与节点 diagnostics 中显式标注。

## 6. 配置

```json
"ScriptWorkers": {
  "IsolationMode": "InProcess",          // InProcess | WorkerProcess
  "WorkerAssemblyPath": null,            // 默认 API 目录下 VisionStudio.Script.Worker.dll
  "StartupTimeoutMs": 10000,
  "ExecutionTimeoutMs": 15000,
  "MaxWorkingSetMb": 1024,
  "MaxRestartsPerMinute": 5,
  "PoolSize": 1,
  "SharedMemoryEnabled": true,
  "SharedMemoryThresholdBytes": 1048576,
  "SharedMemoryRootPath": "data/script-workers/shared-memory",
  "SharedMemoryStaleMinutes": 30,
  "FallbackToInProcess": false
}
```

- `PoolSize=1` 是默认值：编译缓存命中率优先于并行度；脚本节点通常不是吞吐瓶颈。
- 生产环境第一阶段仍可显式设 `WorkerProcess` 验证；第三阶段再切默认。

## 7. 失败语义

| 失败 | 节点结果 | 可观测性 |
|---|---|---|
| Worker 程序集缺失 | 启动阶段失败（host 不启动） | 启动日志 + 发布资产检查 |
| Worker 启动超时 | 节点失败，错误含 worker 索引与超时值 | `scriptWorker` 状态 `Stopped` |
| 脚本抛异常 | 节点失败，保留行号与脚本错误文本 | diagnostics `scriptBackend=worker` |
| 脚本超时 | 节点失败，worker 被杀并重启 | `Ticks` / `timeoutMs` 写入 summary |
| 宿主取消 | `OperationCanceledException` | worker 终止原因进日志 |
| 连续崩溃 | 熔断打开 | `/api/script-workers` 显示 `CircuitOpen` |
| 共享内存不可用 | 自动回落 PNG，节点继续 | diagnostics 标记 `png-fallback` |

## 8. 安全边界（必须写清楚）

进程隔离带来的是：

- 原生崩溃不带走宿主；
- 超时/取消可以真正杀掉执行体；
- 内存与线程被限制在子进程；
- 进程级重启与熔断。

**不带来**：

- 文件系统、网络、注册表的 OS 级隔离；
- 对 denylist 绕过者的强制阻断；
- 多租户不可信代码安全。

第三阶段可选硬化：Windows Job Object（内存/CPU/进程数）、受限令牌、AppContainer、
只读工作目录、出站网络 ACL。没有这些之前，`script.csharp` 继续保持 Preview + 发布确认。

## 9. 可观测性

- `/api/health`（认证后）增加 `scriptWorker`：`state / processId / restartCount / workingSetBytes / circuitOpen`；
- 新增 `GET /api/script-workers`（Engineer）与 `POST /api/script-workers/restart`（Engineer）；
- 节点 summary 增加 `scriptBackend`（`in-process` / `worker`）、`workerIndex`、`workerRestartCount`；
- 诊断包纳入脚本 Worker 状态与最近错误；
- trace 的 `NodeRunReport` 不需要新字段，沿用现有 summary 透传。

## 10. 测试与门禁

| 层 | 用例 |
|---|---|
| 单元 | `PluginWorkerValueCodec` 图像往返、共享内存清理、`ScriptExecutionOutcome` 映射 |
| 单元 | 超时杀进程、取消杀进程、崩溃重启、重启熔断 |
| 集成 | 同一脚本在 `InProcess` / `WorkerProcess` 两种后端输出逐字段一致（数字/文本/JSON/日志/图像哈希） |
| 集成 | 输入图像共享内存往返、输出图像 owned 生命周期、异常脚本行号 |
| 集成 | 内存超限拒绝、worker 不可用时不静默回落 |
| E2E | 编辑器“运行此节点”在 Worker 模式下成功；断开 worker 后有可读错误 |
| 长稳 | 连续执行 N 轮无句柄/共享内存泄漏，worker 重启计数稳定 |

门禁补强：

- `static-audit.py`：
  - `CSharpScriptNode` 必须注入 `IScriptExecutionBackend`，不得直接 `new` runner；
  - `VisionStudio.Script.Worker` 项目与发布资产必须存在；
  - `ScriptWorkers` 配置节与 `Visible script backend in diagnostics` 结构性断言；
  - Worker 进程退出必须 `entireProcessTree: true`。
- `publish-runtime.ps1/.sh` 必需资产加入 `VisionStudio.Script.Worker.dll`；
- `CatalogExporter --check` 不受影响（目录不变）；
- 覆盖率与文件规模预算沿用现有阈值。

## 11. 实施顺序

### Phase 0：纯移动
把 `PluginWorkerSupervisor` 内的 `WorkerPool` / `WorkerClient` 抽到 `Workers/WorkerProcessPool.cs`，
插件行为不变，跑全量插件 Worker 测试与基准门禁。

> 已完成（2026-11-13）：`WorkerPool`、`WorkerClient`、`WorkerPoolExchange`、`WorkerClientExchange`
> 已移动到 `backend/src/VisionStudio.Api/Workers/WorkerProcessPool.cs`，命名空间 `VisionStudio.Api.Workers`；
> `PluginWorkerSupervisor` 从 695 行降到 340 行。`VisionStudio.Api.Tests` 533/533 通过，
> 插件 Worker 相关用例 20/20 通过，构建 0 错误、无新增告警。

### Phase 1：脚本 Worker 可独立跑
新增 `VisionStudio.Script.Worker`，能通过命名管道接收 `script.csharp` 请求并返回结果；
添加进程级集成测试（不经宿主图执行）。

> 已完成（2026-11-13）：新增 `backend/src/VisionStudio.Script.Worker`（`Program.cs` + csproj），
> 复用 `PluginWorkerWire` / `PluginWorkerValueCodec` / `PluginWorkerSharedMemoryStore`，
> 已加入 `VisionStudio.slnx`、Api 构建拷贝与发布资产（`publish-runtime.ps1/.sh`）。
> 进程级用例 `ScriptWorkerProcessTests` 覆盖 ping / discover / execute / shutdown，
> `VisionStudio.Api.Tests` 534/534 通过；宿主接线（`IScriptExecutionBackend`、配置开关）留在 Phase 2。

### Phase 2：宿主接线 + 配置开关
新增 `IScriptExecutionBackend`、`ScriptWorkerSupervisor`、`ScriptWorkers` 配置；
`IsolationMode=InProcess` 保持默认，`WorkerProcess` 可显式开启；
补齐 `/api/script-workers`、健康字段、诊断包与 e2e。

> 已完成（2026-11-13）：`CSharpScriptNode` 注入 `IScriptExecutionBackend`；新增
> `ScriptExecutionOutcome`、`InProcessScriptExecutionBackend`、`ScriptWorkerSupervisor`；
> `ScriptWorkers` 配置默认 `InProcess`，`WorkerProcess` 走编译期同一个 Worker 协议；
> `/api/script-workers`（Engineer）与 `/api/health` 的 `scriptWorker` 状态位已接入。
> 测试覆盖 Worker 后端执行、InProcess/Worker 标量+JSON+日志一致性、健康字段。
> e2e 与诊断包字段留待 Phase 3 一并收口。

### Phase 3：生产默认 + 硬化（需单独评审）
- 生产默认 `WorkerProcess`；
- Windows Job Object 内存/CPU 上限；
- 受限令牌 / 只读工作目录 / 网络 ACL 的可行性验证；
- 把 `script.csharp` 从 Preview 升级的条件写清楚（依赖 Phase 3 的安全证据，而不是"进程能跑"）。

> 已完成（2026-11-13）：
> - `appsettings.Production.json` 默认 `ScriptWorkers:IsolationMode=WorkerProcess`；
> - 新增 `Workers/WindowsJobObject.cs`，对脚本 Worker 施加**进程私有内存上限**、**活动进程数 1**、
>   **KillOnJobClose**（宿主释放句柄时整棵进程树终止）；CPU 硬上限提供
>   `JobObjectCpuRatePercent` 配置，默认 0——不同现场核数差异大，hard cap 需现场压测后再开；
> - `WorkerProcess` 模式下 Worker 程序集缺失改为**启动即失败**，不再等到第一次执行；
> - Job Object 在非 Windows 平台安全跳过；插件 Worker 默认不启用（`JobObjectMemoryLimitMb=0`），
>   避免改变既有插件行为。
>
> 仍未实现（明确留白）：受限令牌 / AppContainer / 只读工作目录 / 网络 ACL。
> 这些需要现场账户模型与部署方式配合，不能在代码里假装完成；`script.csharp` 继续保持 Preview。

每个 Phase 的退出标准：全量后端测试、前端测试、e2e、发布脚本冒烟、`--check` 目录一致。

## 12. 风险与取舍

| 风险 | 影响 | 缓解 |
|---|---|---|
| 每次执行多一次 IPC 与图像编码 | 小图约毫秒级，大图取决于共享内存阈值 | 共享内存 + 复用进程池 |
| 编译缓存随进程重启丢失 | 崩溃后首执行变慢 | 池常驻、重启熔断、编译耗时进 diagnostics |
| 进程隔离不等于安全隔离 | 误以为已"沙箱化" | 文档与成熟度理由明确写"不是 OS 沙箱" |
| 宿主/Worker 版本漂移 | 协议不兼容 | 协议版本随发布锁定，启动 discovery 校验 |
| 共享内存残留 | 磁盘增长 | 复用 stale cleanup + 每次请求删除 |
| 脚本作者依赖进程内 API | 行为差异 | 只允许目录声明的输入/输出类型，不做宿主对象传递 |

## 13. 待定问题

1. `PoolSize` 默认 1 是否足够？如需并行，需要评估编译缓存收益与内存上限。
2. 是否保留 Development 的 `FallbackToInProcess`，还是所有环境都强制 Worker？
3. Phase 3 的 OS 硬化做到哪一层：Job Object 即可，还是必须 AppContainer/受限令牌？
4. 脚本 Worker 是否复用插件 Worker 的 `/api/plugin-workers` 性能窗口，还是单独一套轻量指标？
