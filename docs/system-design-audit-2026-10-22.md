# 系统设计审计与改进（2026-10-22）

本轮不再逐个节点修 UI/契约，而是从**系统层**审查"需要几处同步修改才能完成一件事"。
节点级设计已迭代 35 轮，剩余问题集中在扩展点、持久化契约与前端组织方式。

## 结论速览

| 级别 | 问题 | 状态 |
| --- | --- | --- |
| P0 | 源码中文注释被 UTF-8/GBK 双重转码破坏（2 个文件、40 行，含不可逆私用区字符） | ✅ 已修复 |
| P0 | 内置节点需在 3 份手写清单中同步（DI 175 行 + Bootstrap 178 行 + 目录 171 条），漏一处只在运行到该节点时失败 | ✅ 已修复 |
| P1 | 工作流文档没有 schema 版本，旧宿主可打开并覆盖新宿主写出的文档 | ✅ 已修复 |
| P1 | 前端没有统一请求层：186 处 `fetch`、304 处硬编码 `/api/`、160 处无取消信号 | ⚠️ 建议排期 |
| P1 | 巨型组件：`App.tsx` 1998 行、`NodeConfigurationDialog.tsx` 2032 行、`ImageViewer.tsx` 1772 行 | ⚠️ 建议排期 |
| P1 | 运行时按节点类型字符串分派（编译器 92 处、执行策略 40 行白名单、调度器 13 处），插件无法声明空间语义 | ⚠️ 建议排期 |
| P2 | i18n 以英文原文为键（`i18n/catalog.ts` 2258 行），改文案即静默丢翻译 | ℹ️ 观察 |
| P2 | 组合根仍有约 180 个手工注册；目录文件 1474 行 | ℹ️ 观察 |

## P0-1 源码编码损坏

`VisionStudioServiceRegistration.cs` 与 `BootstrapHostedServices.cs` 的部分注释是 UTF-8 字节被按 GBK
解码后再次存为 UTF-8 的结果（`鐢熶骇鐜锛?026...`）。这些字符含私用区码位，`encode(gbk).decode(utf-8)`
无法还原——不是显示问题，而是源文件内容已损坏。

影响：这两处恰好是组合根与启动编排，是排查"节点为什么没注册""监听边界怎么判定"的第一入口；
注释不可读会直接推高误改风险。

处理：按原意重写这两个文件中的全部 40 行注释，并在改动后重新扫描，确认无残留私用区/典型乱码字符。

## P0-2 内置节点三份清单

原设计新增一个节点需要同步：

1. `BuiltInNodeCatalog` 目录条目（类型、端口、参数、动作/资源声明）；
2. `VisionStudioServiceRegistration.AddBuiltInNodes` 的 `services.AddSingleton<TNode>()`；
3. `RuntimeBootstrapHostedService.RegisterBuiltInNodes` 的 `services.GetRequiredService<TNode>()`。

第 2、3 步各 171~175 行，靠人工保持与目录一致。漏加第 3 步的后果最严重：宿主正常启动、`/api/catalog`
也正常返回，直到产线第一次执行该节点才抛 `Node type '...' is not registered`。

处理：

- 新增 `Hosting/BuiltInNodeExecutors.cs`：扫描引擎程序集中可公开构造的 `IVisionNodeExecutor` 实现，
  以 `IVisionNodeExecutor` 单例注册（`TryAddEnumerable`，每个实现类型仍只有一个实例）。
- 启动时用 `FindDrift(executors, catalog)` 做双向核对：目录有、执行器无；执行器有、目录无；
  任一侧重复类型。任一项成立即**拒绝启动**并列出全部问题，不再等到运行期。
- 删除两份手写清单：组合根 556 → 380 行，启动编排 402 → 241 行，新增发现/校验 74 行。

行为边界：

- 节点实例数量、生命周期（DI 单例）、构造方式（构造注入）不变；`HttpRequestNode` 的可选
  `HttpClient` 参数仍由容器按原规则解析。
- 发现顺序不影响目录（`VisionNodeRegistry.Catalog` 仍按分类/名称排序）。
- 今后若引擎程序集中出现"不应暴露为节点"的执行器实现，启动会失败并点名该类型——这是有意的：
  要么在目录声明它，要么把它移出该程序集。

## P1-3 工作流文档 schema 版本

`WorkflowDefinition` 之前没有版本字段，`data/workflows/*.json` 也不记录版本。典型现场场景：
新版本新增/改变了字段 → 现场回滚到旧版本 → 旧宿主读取文档时丢弃不认识的字段 → 再次保存即永久丢失。

处理：

- `WorkflowDefinition` 增加 `SchemaVersion`（`init`，默认当前版本 1）；旧文档缺字段时按 1 读取，
  布局未变，不需要迁移。
- 保存时由宿主盖章为当前版本，调用方传入的版本号不能把文档"降级标记"。
- 读取（`LoadAsync` / `LoadWithRevisionAsync`）遇到高于当前版本的文档返回 409，并在消息中给出
  双方版本与处理建议；不再"尽力打开后覆盖"。
- 列表接口保持宽松：仍可看到文档摘要，点开时才明确拒绝，避免一份未来版本文档让整个列表 500。

行为边界：文档格式只增加字段，不改变既有字段含义；前端当前不读写该字段，保存时由后端盖章，
因此前端无需同步发版。真正出现 schema 2 时，前端需要保留并回传该字段（届时由版本门禁强制暴露）。

## 未实施：建议排期项

### 1. 前端统一请求层（P1）

证据：`frontend/src` 下 186 处 `fetch(`，304 处硬编码 `/api/`，其中 160 处没有 `AbortSignal`。
现状靠约定维持：`apiErrors.ts` 已统一 ProblemDetails 解析（75 处使用），但超时、取消、重复请求、
路径拼接仍由每个面板各写一遍。`CameraPanel.tsx` 单文件 36 处，是最集中的风险点。

建议：新增 `apiClient.ts`（`requestJson<T>(path, {signal, timeoutMs})`：相对路径拼接、ProblemDetails
转 `ApiError`、可选超时、统一 `credentials`），先迁移 `CameraPanel` 与 `useWorkflow*` 系列，
其余面板按"改动时才迁移"推进；不追求一次性替换 186 处，避免大面积回归。

### 2. 巨型组件拆分（P1）

证据：`App.tsx` 1998 行（数十个 `useState`、模板浏览/快捷键/搜索/布局/运行结果都在同一组件）、
`NodeConfigurationDialog.tsx` 2032 行（66 处节点类型判断）、`ImageViewer.tsx` 1772 行。
现有 `useRunController`、`useWorkflowSave` 等 hooks 已证明拆分路径可行，但 UI 层尚未跟上。

建议从无状态块开始：模板浏览弹窗、快捷键说明、搜索结果列表可先抽为纯展示组件（props 进出），
再处理依赖画布状态的交互，避免一次性改动运行/保存主链路。

### 3. 节点能力声明下沉（P1）

证据：`VisionWorkflowCompiler.cs` 有 92 处节点类型字符串字面量；`VisionExecutionPolicy.Resolve`
用 40 行类型白名单决定是否融合执行；`VisionNodeDispatcher.AttachImageCoordinateFrames` 用 13 处
类型判断推断输出坐标帧。目录已声明 `Actions` / `Resources` / `ExecutionMode`，但空间语义仍靠类型猜。

影响：插件节点即使正确声明 `ExecutionMode=Pipeline`，也无法声明"我的输出图像在新坐标系"，
调度器会按输入帧推断；第三方图像节点存在坐标帧错误风险，且每加一个内置节点都要改核心文件。

建议：在 `NodeCatalogItem` 增加 `SpatialOutput`（`None` / `PreserveInput` / `NewFrame(参数名或固定后缀)`）
与 `FrameRule` 声明；调度器优先读声明，内置类型表仅作回退。先迁移 `image.crop` / `image.resize` /
`image.rotate` / `image.perspective` / `image.warpAffine` 五个高频节点验证，再逐步替换类型判断。

### 4. i18n 键设计（P2）

`i18n/catalog.ts` 以英文显示文本为键（节点名除外，按 type 键）。后端把 `Exposure (us, 0=leave)`
改成 `Exposure (µs)`，中文翻译即静默消失并回退英文——没有报错、没有测试。
建议后续把参数/选项翻译键改为 `type.parameter` / `type.parameter.option`，并用
`catalog.generated.json` 做覆盖率测试（允许显式白名单），让缺翻译在 CI 暴露。

### 5. 组合根规模（P2）

`AddVisionStudioHost` 仍有约 180 个 `AddSingleton`（380 行）。当前可读性尚可、顺序敏感，
不建议为拆而拆；若继续增长，可按 Security / Hardware / Runtime / Stores / HostedServices 拆为
扩展方法，保持调用顺序集中在一处。

## 验证

- 后端构建：`dotnet build VisionStudio.slnx`，0 error；warning 全部来自改动前既有文件，
  本轮改动文件（`BuiltInNodeExecutors.cs`、两个 Hosting 文件、`WorkflowStore.cs`、`GraphContracts.cs`）
  无新增警告。
- 全量测试：Engine 1813 项 + Api 418 项，共 **2231 项全部通过**（含新增 6 项：
  节点目录/执行器一致性 4 项、文档版本 2 项）。
- 新增测试：`BuiltInNodeRegistrationTests`（宿主执行器与目录 1:1、缺执行器/多余执行器/重复类型
  三类漂移均可检出）、`WorkflowStoreTests` 新增"保存盖章当前版本""拒绝更高版本文档"。
- 未运行前端构建：本轮未改前端文件。

---

# 第二轮：运行归属、面板隔离与配置遮蔽（2026-10-22 下午）

第一轮解决的是"同一件事要改多处"；第二轮继续找"失败后没人收尾"和"配置看起来生效、实际没生效"两类问题。

## 结论速览（第二轮）

| 级别 | 问题 | 状态 |
| --- | --- | --- |
| P1 | 异步运行只存在进程内存里；宿主崩溃后 `Running` 行永久残留，界面永远轮询，数据集永远无法删除 | ✅ 已修复 |
| P1 | 全站只有一层错误边界；任一 Drawer 面板渲染异常就把整个设计器（含未保存修改）打成恢复页 | ✅ 已修复 |
| P1 | 仓库中的 `vite.config.js`（旧编译产物）优先于 `vite.config.ts` 被加载，开发代理与测试配置从未生效 | ✅ 已修复 |
| P2 | `AlarmStore` 每次读写整个 JSON、历史无上限，`/api/alarms` 无分页 | ⚠️ 建议排期 |
| P2 | 主包 2.1 MB（gzip 604 kB）：面板已拆分，主包仍偏大 | ℹ️ 观察 |
| P2 | `ps1` / `sh` 两套发布脚本各自实现同一流程；`types.ts` 仍是手写后端镜像 | ⚠️ 建议排期 |
| P1 | 第一轮列出的请求层、巨型组件、类型分派三项仍未动 | ⚠️ 待排期 |

## P1-1 异步运行没有"主人"

证据：

- `DatasetValidation.StartAsync` 用 `Task.Run` 启动后台循环，唯一的所有权记录是进程内
  `ConcurrentDictionary _active`；`validation_runs` 行只有 `Running` 状态。
  `PluginPerformanceBenchmarkService` 与 `CameraSynchronizationCommissioningService` 结构相同
  （后者还多一个 `Queued`）。
- 宿主崩溃/断电后这些行永久停在 `Running`/`Queued`：
  - `DatasetValidationPanel` 每 1 秒轮询，只要还有 `Running` 就永不停止；
  - `DeleteDatasetAsync` 以 `status='Running'` 判断"验证中"，该数据集从此**永久无法删除**，
    需人工改数据库才能解锁；
  - 取消接口只写 `cancel_requested=1`，没有 runner 会再消费它。

处理：新增 `InterruptedRunRecovery` 与对应 HostedService，在存储迁移之后、任何请求或运行开始之前执行：

- 三张表统一收尾：`validation_runs`（`Running`）、`camera_sync_commissioning_tests`
  （`Queued`/`Running`）、`plugin_benchmark_runs`（`Running`）→ `Interrupted`，
  写 `completed_at` 与明确原因（"宿主在完成前重启，结果不完整"）。
- 不做时间阈值猜测：本产品已在 `WorkflowStore` 约定单宿主部署，启动时不可能存在属于本进程的运行，
  因此"未完成即为无主"是确定事实；重复执行幂等（第二次返回 0）。
- 保留 `started_at` 与已有 `error`，只补空缺字段，便于追溯时区分"失败"与"未完成"。

行为边界：不改变运行互斥与取消语义；未完成结果仍按 partial 对待；前端新增 `Interrupted: 已中断`
文案，数据集面板的橙色状态即可识别，轮询随 `Running` 消失自动停止。

## P1-2 单一错误边界

证据：`main.tsx` 只有一层 `<ErrorBoundary>`；`App.tsx` 中 15 个 Drawer 面板（相机、机器人、设备、
作业、追溯、标定、验证、调优、模块、生产、诊断、插件、溯源…）都在同一渲染子树内。任一面板在渲染期
抛错（例如后端返回了结构不符的列表、某条记录缺字段），React 会卸载整棵树，操作员看到整页恢复面，
画布、当前选中节点与未保存修改的上下文全部消失——为一个次要面板付整机代价。

处理：`ErrorBoundary` 增加 `variant`（`page` / `panel`）、`scope`、`onDismiss`：

- `panel` 模式只渲染一个 560px 卡片（导出诊断 / 重试 / 关闭面板），画布与其它面板继续工作；
- `App.tsx` 的 `LazyPanel` 统一包裹，三个常驻面板（相机/机器人/设备）与操作员 `runtimeOnly`
  页面（嵌入式生产面板、诊断）单独包裹；
- 关闭按钮把对应 `open` 置 false，不必刷新；重试只重建出错子树。

注意边界：错误边界只能捕获渲染/生命周期异常；事件回调与异步请求异常仍由各面板既有 `try/catch`
与消息提示处理，两者互补，不能互相替代。

## P1-3 Vite 配置被旧产物遮蔽

证据：`frontend/` 下同时存在 `vite.config.ts` 与 `vite.config.js` / `vite.config.d.ts`。
`npx vite build --debug` 实测：

```
configFile: '.../frontend/vite.config.js'
```

Vite 的配置解析顺序 `.js` 先于 `.ts`，因此 `.ts` 里的 `/hubs` WebSocket 代理、`test`（jsdom）
配置、以及历次排查写下的注释**全部没有生效**；`npm run dev` 下诊断面板的 SignalR 连接没有代理，
开发者改 `.ts` 也看不到任何变化。`.js` 是早期 `tsc` 输出遗留（`tsconfig.node.json` 现已 `noEmit`，
不会再生）。

处理：删除 `vite.config.js` 与 `vite.config.d.ts`；实测 `configFile` 指向 `vite.config.ts`，
`npm run build` 通过。同一命令下前端测试从"8 项失败"变为"仅 1 项既有慢测试超时"——失败项正是
用错配置（缺少 jsdom/test 段）造成的假失败。

## 未实施（第二轮新增）

### AlarmStore 全量读写（P2）

`AlarmStore` 把全部历史放在一个 `alarms.json`：`RaiseAsync`/`RecoverAsync`/`AcknowledgeAsync`
每次都"读全量 → 改一条 → 写全量"，且没有保留上限、`/api/alarms` 也没有分页。告警历史随故障次数
线性增长，一次故障恢复的写放大与接口响应都会随之变大。建议按 `TraceabilityStore` 的做法迁到 SQLite
（索引 `code+source` 与 `raised_at`，保留策略按天数/条数），或至少引入条数上限 + 追加式存储。

### 主包体积（P2）

`npm run build` 实测：面板 chunk 已拆分，但主包仍为 `index-*.js` 2,157 kB（gzip 604 kB）。
对局域网操作站可接受，但首次加载与弱网更新偏慢。建议优先检查 antd 的按需引入与图标/日期库的整包
导入，再考虑把画布与运行面板做成路由级拆分。

## 第二轮验证

- 后端：`dotnet build` 0 error；新增 `InterruptedRunRecoveryTests` 3 项通过
  （三表收尾、已完成行不动、幂等）；全量回归 **2234 项通过**（Engine 1813 + Api 421）。
- 前端：`npx tsc -b` 0 error；`npx vitest run` 1043/1044 通过——唯一失败为既有
  `propertyPanel` 120 顶点用例在满载下超 5s（单独运行 4.7s 通过，与本轮改动无关）；
  `npm run build` 通过，`configFile` 已指向 `vite.config.ts`。

---

# 第三轮：节点能力声明下沉（图像坐标帧）（2026-10-22）

第一轮审计列出的 P1「运行时按节点类型字符串分派」在这轮落地了其中影响插件扩展的一项：
**图像坐标帧语义改为目录声明**，调度器与编译器不再按节点类型字符串推断。

## 原设计问题

| 位置 | 原实现 | 后果 |
| --- | --- | --- |
| `VisionNodeDispatcher.AttachImageCoordinateFrames` | 13 处 `type.Equals("image.crop")` / `"jlvision.imageOp"` / `"image.warpAffine"` 等判断，推断输出帧与 transform 的源/目标帧 | 插件节点无法声明"我的输出在新坐标系"，只能被当作保持输入帧；内置新增空间节点必须改核心文件 |
| `VisionWorkflowCompiler.ResolveImageFrame` | 10 个类型分支 + 条件读 `operator` 参数 | 与调度器两套推断，容易漂移（`image.undistort` 编译器认为新帧、调度器实际保持输入帧） |
| `VisionWorkflowCompiler.ResolveTransformFrames` | 4 个类型分支（imageOp / register / crop / roi.transform） | 插件 transform 语义无法表达 |

## 新契约

`NodeCatalogItem.Frames`（`IReadOnlyList<NodeFrameRule>`）与 `NodeCatalogItem.DeclaresFrames`：

```csharp
new NodeFrameRule(
    Policy: NodeImageFramePolicy.NewFixedFrame,
    FrameSuffix: "crop",
    FrameLabel: "crop frame",
    Transform: NodeTransformFramePolicy.Spatial);
```

- 图像输出策略：`PreserveInput`、`NewSpatialFrame`、`NewFixedFrame`、`TransformTarget`、`InputFrame`。
- transform 帧策略：`None`、`Spatial`、`RoiReference`、`ReferenceInput`。
- 条件规则：`WhenParameter` + `WhenValues`，按节点有效参数值（节点值优先、目录默认值兜底）匹配，
  规则按声明顺序取首条；无条件规则作为兜底。
- 未声明（`Frames = null`）沿用旧默认：输出继承输入帧；没有输入时使用 `image:{id}:source`。

## 内置声明（11 个节点）

| 节点 | 帧策略 | transform |
| --- | --- | --- |
| `image.crop` | `NewFixedFrame` / `crop` | `Spatial` |
| `image.resize` / `image.rotate` / `image.perspective` | `NewSpatialFrame` | 无 |
| `image.warpAffine` | `TransformTarget`（缺 transform 时 `:spatial`） | 无 |
| `image.undistort` | `NewFixedFrame` / `undistorted` | 无 |
| `image.register` | `PreserveInput` | `ReferenceInput`（reference 口） |
| `roi.transform` | `InputFrame`（`debugImage`，缺省 `CurrentImage`） | `RoiReference` |
| `jlvision.worldPlane` | `NewFixedFrame` / `worldPlane` | 无 |
| `jlvision.restoration` | `operator ∈ {psfMotion, psfDefocus}` 时 `:psf`，否则保持输入 | 无 |
| `jlvision.imageOp` | 空间算子（cropPart/cropRectangle2/cropDomainRel/rotateImage/zoomImageFactor/addBorder/changeFormat）时 `:spatial`，否则保持输入 | 空间算子为 `Spatial` |

`image.undistort` 的调度器行为随之与编译器对齐：输出帧确为 `:undistorted`（此前调度器保持输入帧，
编译器却按新帧校验，属两套推断漂移）。

### transform-only 声明（纯坐标节点）

`match.shape`、`coordinate.transformCompose`、`coordinate.transformInverse` 只输出 transform，
不输出图像，因此新增独立的 `NodeCatalogItem.TransformFrames`（`NodeTransformFrameDeclaration`）：

| 策略 | 节点 | 语义 |
| --- | --- | --- |
| `TemplateToImage` | `match.shape` | Source = 模板输入帧（未连接时为 `match:{id}:template`，不可追踪），Target = 图像输入帧 |
| `Compose` | `coordinate.transformCompose` | Source = transformA 的 Source，Target = transformB 的 Target，并校验中间帧衔接 |
| `Invert` | `coordinate.transformInverse` | 交换输入 transform 的 Source/Target，反转映射路径 |

编译器原有的三个类型分支已删除；插件声明同名策略即可获得相同的 transform 帧校验。

### ROI / 几何输出帧声明

非图像输出（ROI、直线、圆、检测框）的帧来源此前由 `ResolveGeometryFrame` / `ResolveRoiFrame`
里的类型分支决定。新增 `NodeCatalogItem.OutputFrames`（`NodeOutputFrameRule`）：

| 来源 | 含义 |
| --- | --- |
| `ImageInput` | 指定图像输入端口（默认 `image`）的帧 |
| `ImageOutput` | 本节点图像输出的帧 |
| `GeometryInput` | 指定几何输入端口的帧（递归解析） |
| `DetectionInput` | 指定 detections 输入端的帧 |
| `RegionInput` | 指定区域输入端的帧 |
| `ContourInput` | 指定轮廓输入端的帧 |
| `FittedPoints` | point1..N 拟合点所在帧 |

已声明 9 个节点：`image.crop`（`croppedRoi`→ImageOutput、`cropOrigin`→ImageInput）、
`roi.transform`（`roi`→debugImage）、`jlvision.featureExtract`（全部输出→image）、
`measure.fitRectangle`（contours）、`geometry.intersections`（按 operator 选 circle/line）、
`collection.detections.at` / `flow.forEach`（三个框输出→detections）、
`measure.fitLine` / `measure.fitCircle`（拟合点）。

编译器两条解析函数里的类型分支已删除；仍有图像输出的节点沿用通用兜底（几何输出跟随自身图像帧），
行为不变。`RoiImageInputPort` 的"ROI 输入绑定哪个图像端口"仍是类型映射，留待后续。

### ROI 输入绑定

`NodeCatalogItem.RoiImageBindings`（`NodeRoiImageBinding(RoiPort, ImagePort)`）声明 ROI 输入属于哪个图像：
ROI 帧与该图像不一致时在编译期报错。已声明 9 个节点：`image.crop`、`region.fromRoi`、
`feature.edgeSubpixel`、`measure.caliper` / `caliperWidth` / `caliperArray` / `caliperLine` /
`caliperCircle`（roi→image），以及 `roi.transform`（roi→debugImage）。

编译器 `RoiImageInputPort` 的类型映射已删除；未声明绑定的 ROI 输入不做帧校验，
插件声明后即参与同一校验。

### 参数与端口二选一

`ParameterDescriptor.OverriddenByPorts` 声明“连接这些输入端口时忽略本参数（端口值优先）”。
编译器 `IsParameterOverriddenByConnectedInput` 的 6 类节点判断已删除，改为读参数声明。

已标注 15 个节点、44 个参数：`jlvision.matrixOp`（matrixA/B）、`output.saveImage`（path/partId）、
`output.httpRequest`（url/body/contentType/idempotencyKey）、`device.writeTag`（valueType/fallbackValue
被 value 覆盖）、`coordinate.point` / `point3D` / `pose`（x/y/z/thetaDeg）、`text.match` / `text.validate`
（text/expected）、`feature.edgeSubpixel` 与 `measure.caliper*`（几何参数被 searchLine 覆盖）、
`measure.caliperCircle`（center/radius 被 circle 覆盖）。

## 插件路径

插件只需在目录中声明，宿主无需认识其类型名：

```csharp
Frames: [new(NodeImageFramePolicy.NewSpatialFrame, FrameLabel: "plugin spatial",
             Transform: NodeTransformFramePolicy.Spatial)]
```

新增测试直接驱动未注册类型名的 `plugin.spatial` 节点，断言输出帧为 `image:{id}:spatial`、
transform 的 Source/Target 被补全；未声明帧的插件节点保持输入帧。

## 边界与未完成

- 只改帧语义来源，节点 ID、端口、参数、默认值、图像算法均未变化；旧流程无需迁移。
- `ImageOperationGeometry.IsSpatial` 仍被 `JLVisionOperatorNodes` 用于选择空间执行路径，
  那是执行器内部算子分派，不是帧语义，本轮保留。
- 前端已接入声明：`catalogNodeData` 把 `frames` 放进节点数据，`frameSemantics.ts` 按同一套
  条件规则求值；`NodeConfigurationDialog` 的 ROI 沿革优先读声明，旧目录/旧节点数据才回退到
  类型集合。插件节点因此不再被当作"保持输入帧"。
编译器剩余类型字面量从 ~30 降至 **6 处**（全部为节点特定业务校验，非帧语义）：
`measure.fitCircle` 最低有效卡尺数、`measure.regionIntensity` / `image.flatFieldCorrect` 参数校验、
`jlvision.restoration` PSF 一致性、`device.waitTag` 参数验证、`flow.if` range 模式校验。
`ResolveDetectionFrame` / `ResolveRegionFrame` 的类型分支已全部声明化。
- `match.shape`、`coordinate.transformCompose` 的 transform 帧语义仍是类型分支；可继续按同一契约下沉。


## 验证

- 前端 `frameSemantics.test.ts` 6 项覆盖声明求值、条件规则、目录默认值与旧集合回退。
- 新增 `NodeFrameSemanticsTests` 10 项（内置声明、条件规则、插件空间帧、未声明保持、固定帧）；
  `Catalog_FrameDeclarationsReferenceExistingPortsAndParameters` 校验声明的端口/参数/条件值均存在。
- 后端全量 **2255 项通过**（Engine 1834 + Api 421）；前端 **1053 项通过**（69 文件，含新增
  `frameSemantics` 6 项），`tsc -b` 与生产构建通过。
- 目录 hash 变为 `91688bb7…`，前端 fallback 已重新生成并通过 `CatalogExporter --check`。

---

# 第四轮：前端统一请求层（第一批）（2026-10-22）

审计里"前端无统一请求层：186 处 fetch、304 处硬编码 /api/、160 处无 AbortSignal"开始落地。
本轮建立入口并迁移带测试覆盖的 hooks；其余按文件分批推进。

## 新增 `frontend/src/apiClient.ts`

| 能力 | 说明 |
| --- | --- |
| `apiFetch(input, { signal, timeoutMs, ...init })` | 统一入口；**未指定 timeoutMs 时原样透传调用方 signal**（保持身份与后续 abort 可见），指定超时才组合信号并以 `TimeoutError` 中止 |
| `apiJson<T>` / `apiText` | 读取成功响应；非 2xx 抛 `ApiError(status, ProblemDetails 文案)` |
| `isAbortError` / `isTimeoutError` | 不依赖 `instanceof Error`（jsdom 的 DOMException 不是 Error 子类） |
| 非 2xx 不抛错的 `apiFetch` | 保留调用方按状态码分别处理的既有逻辑（409 覆盖、404 探测等） |

设计边界：
- 不做自动重试。危险写入的重试必须由调用方显式决定（沿用设备动作/幂等键的既有约束）。
- 不吞掉 `response`，需要读响应头（如 `X-Workflow-Revision`）的调用仍使用 `apiFetch`。
- 超时统一用 `timeoutMs`，逐步替换各处自建 `setTimeout + AbortController` 的重复实现。

## 已全部迁移（198 处直接调用清零）

| 批次 | 范围 | 调用数 |
| --- | --- | --- |
| 第一批 | run / production / tuning / trace hooks | 17 |
| 第二批 | JobPanel、DatasetValidationPanel、TracePanel / TraceAnalysisPanel | 31 |
| 第三批 | 其余 25 个文件（CameraPanel 38、PluginPackagePanel 19、CalibrationIntrinsicsWizard 18、InvestigationPanel 8、security 7、DevicePanel 7…） | 150 |

运行时代码中的直接 `fetch(` 已清零；`apiUsage.test.ts` 棘轮基线降为 **0**，
新增直接调用会失败并列出文件。

第三批以行为等价替换为主（`apiFetch` 在未指定 `timeoutMs` 时完全透传参数与 signal 身份）。
迁移后补了 3 条冒烟：`CameraPanel` 打开才轮询、卸载后停止；`SecurityGate` 无会话显示登录、
登录后进入应用；会话过期（focus 校验返回 401）回到登录页。
`PluginPackagePanel` 已补安装失败冒烟（后端 422 拒签时不清理已选文件，用户修正后可直接重试）；
`CalibrationIntrinsicsWizard` 已补步骤 0 创建会话 4xx 冒烟（422 错误消息可见，向导不推进步骤）。

## 迁移配方

1. 直接 `fetch(url, init)` → `apiFetch(url, init)`；需要整体超时的加 `timeoutMs`。
2. 只关心"成功解析 / 失败给文案"的读取 → `apiJson`（不再手写 `if (!response.ok) throw`）。
3. 需要状态码分支（409/404/202）→ 保留 `apiFetch` + 原有分支。
4. 轮询/取消仍由调用方控制器负责；`apiFetch` 只保证取消能到达底层请求。

## 端点常量

新增 `frontend/src/endpoints.ts`：`API_BASE` + 按资源域组织的 builder（jobs/traces/validation/
runs/cameras/auth/plugins/workflows/debug/production 等 10 个域）。新代码必须从这里取路径。
已迁移 6 个文件 55 处（JobPanel 13、DatasetValidationPanel 13、security 7、useRunController 8、
endpoints 3 项测试），剩余 **172 处**硬编码 `/api/`。
`endpoints.ts` 的对应 builder，`API_BASE` 只需改一处即可适配子路径部署。

## 组件拆分

`NodeConfigurationDialog`（原 2042 行）拆分：21 个纯逻辑工具函数提取到
`nodeConfigurationUtils.ts`（192 行），主组件降至 **1876 行**（−166 行）。
`Dialog` 已有 10 个子面板组件（CaliperCircleQuickSettings、CaliperEdgeDiagnosticsPanel、
MatchingAutoTunePanel 等）在独立文件中。

## 后续
- 160 处无 `AbortSignal` 的调用已随迁移保留原行为，轮询类可逐步改为显式控制器。
