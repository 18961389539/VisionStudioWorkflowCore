# 易用性与 UI 改进建议（2026-10-25）

本轮基于对 `frontend/src` 的静态取证（App.tsx 1497 行、components 47 个 tsx、styles.css 1113 行），
不运行应用、只看代码与样式；每条结论都附了可复核的位置。

## 结论先行

**能力层面已经越过同类工控视觉编辑器的平均线**——撤销/重做与操作名、未保存守卫（三选一）、快捷键帮助、
端口感知工具箱（搜索/收藏/最近/分组折叠）、问题面板可点击定位、模板画廊、结果区"已过期"徽标，这些都不是随手能做出来的。

真正的体验缺口集中在四类系统性问题，外加一处真 bug：

| 类别 | 本质 | 现场后果 |
|---|---|---|
| **失败被渲染成空** | 6+ 面板把 `!response.ok` 当"没有数据" | 网络/后端故障被读成"设备没注册 / 没有标定 / 没有数据集" |
| **不可逆操作无护栏** | 标定发布、插件回滚、模块替换、清库、停线直接执行 | 误点一次就要返工，且无法撤销 |
| **大面板全是阻塞式模态** | 18 个面板走 Modal，最宽 1680px | "看着画布调参数"做不到，1080p 上弹窗几乎满屏 |
| **元数据与文案漂移** | 版本号写死 V0.52/V0.59（实际 0.63）、中英混排、无语言切换入口 | 交付与售后对不上版本，海外/双语客户体验割裂 |
| **（bug）Delete 键穿透弹窗** | 全局删除监听只排除了输入框 | 在追溯/配方面板按 Delete 会删掉画布节点 |

---

## 一、P0：信任与误操作（建议本迭代内清掉）

### P0-1 Delete/Backspace 穿透模态与抽屉 → 误删画布节点

**证据**：`useEditorShortcuts.ts:66` 的排除名单只有
`input, textarea, select, [contenteditable="true"], .toolbox`；而监听是捕获阶段的 `window.addEventListener('keydown', …, true)`（`:120`）。
只有节点配置弹窗被特判短路（`if (nodeConfigurationId) return;`）。

**影响**：在追溯、配方、设备、插件等面板里，用户按 Delete 想清空筛选框/删表格行（很多控件并不吞这个键），
画布上选中的节点会被静默删除。虽有撤销，但用户不知道发生了什么。

**建议**：白名单补 `.ant-modal, .ant-drawer, [role="dialog"], .ant-select-dropdown`；
更稳的写法是反向判断——`if (!target.closest('.flow-area, body') || target.closest('.ant-modal,.ant-drawer'))`。
配套加一条 vitest：打开任意面板、聚焦非输入区按 Delete，节点数不变。

**验收**：任一面板打开时按 Delete，画布节点数与连线数不变。

**估时**：0.5 人日（含回归测试）。

---

### P0-2 请求失败被渲染成"空"

**证据**（全部为"失败 = 没有数据"）：

- `components/CalibrationPanel.tsx:72-77` — `catch { setAssets([]); }`，标定资产加载失败后呈现"暂无资产"；
- `components/DatasetValidationPanel.tsx:38-54` — 四处 `if (!response.ok) return;`，静默不更新，界面保持空表；
- `components/PluginPackagePanel.tsx:50` — `.catch(() => undefined)` 把刷新失败彻底吞掉；
- `components/ModelPackagePanel.tsx:22-25` — 仅 `if (response.ok)` 时 `setPackages`，非 2xx 无任何反馈。

**影响**：这是工控现场最贵的一类误导。用户看到空表会去重新注册设备、重做标定、重导数据集——
而真实原因是后端没起来或网络抖动。

**建议**：抽一个 `DataBoundary`（`state: idle|loading|empty|error`，error 带 `重试` 与 `错误码/HTTP 状态`），
把"空"与"失败"在视觉上强制区分（空态用 Empty + 行动按钮，失败用红色 Alert + 重试）。
再补一条棘轮测试/脚本：`components/*Panel.tsx` 内禁止裸 `if (!response.ok) return`（用 grep 断言，和现有 apiUsage 棘轮同一思路）。

**验收**：断开后端后打开标定面板，看到红色错误条与"重试"，而不是"暂无资产"。

**估时**：1.5 人日（抽象 0.5 + 先接标定/数据集/插件/模型包四处 1.0）。

---

### P0-3 不可逆操作没有二次确认

**有确认的基线**（说明这不是做不到）：设备移除（DevicePanel）、相机配置/媒体删除（CameraPanel）、
备份恢复（StoragePanel）、工作流覆盖保存（App.tsx）、未保存修改拦截、配方发布复核（RecipePublicationDialog）。

**无确认且不可撤销的**：

| 操作 | 位置 | 风险 |
|---|---|---|
| 发布最新标定 / 回滚到 V1 | `CalibrationPanel.tsx:306` 附近 | 直接影响在跑产线的坐标换算 |
| 插件版本移除/回滚/重启进程池/重置性能统计 | `PluginPackagePanel.tsx:236/248/277/298` | 运行中插件被抽走 |
| "提取模块并替换所选节点" | `WorkflowModulePanel.tsx:193` | 改写画布结构 |
| 清除硬件声明 | `HardwareProvenancePanel.tsx:129` | 溯源链断裂 |
| 训练包删除 / 数据集样本移除 | `ModelPackagePanel.tsx:132`、`DatasetValidationPanel.tsx:186` | 数据不可恢复 |
| 清理过期追溯数据 | `TracePanel.tsx:128` | 审计证据销毁 |
| 产线停止 / 恢复 | `ProductionPanel.tsx:103-104` | 停线是物理动作 |
| 恢复出厂默认参数 | `PropertyPanel.tsx:353` | 一次抹掉全部调参结果 |

**建议分级，不要一律弹窗**：

1. **可撤销的**（删节点/连线，已有 undo）→ 不弹窗，改为 toast 带"撤销"按钮，保留熟练用户的连贯操作；
2. **不可撤销但局部**（插件回滚、模块替换、恢复默认）→ `Popconfirm`，文案说清"将失去什么"；
3. **影响产线或销毁证据**（发布标定、停线、清理追溯、删除训练包）→ 二次确认 + 输入资源名/关键数字，文案写明影响范围。

**验收**：上表每项都有与风险等级匹配的确认；确认文案包含后果与影响对象。

**估时**：1 人日（纯前端，模式统一后复制）。

---

### P0-4 版本号写死，与实际版本脱节

**证据**：`version.ts` 的 `APP_VERSION` 取自 `package.json`（现为 0.63.0），但
`WorkflowModulePanel.tsx:171` 标题写死 `可复用模块 · V0.52`，
`PluginPackagePanel.tsx:215` 描述写死 `V0.59 … V0.58`。

**影响**：客户报版本、售后核对升级记录时对不上号；这类"元数据说谎"比功能缺失更伤信任。

**建议**：统一 `V{APP_VERSION}`；CI 加一条 grep 守卫（`V\d+\.\d+` 硬编码即失败）。

**估时**：0.2 人日。

---

## 二、P1：干活效率

### P1-1 18 个功能面板全是阻塞式模态，最宽 1680px

**证据**（`components/` 内 Modal 宽度）：参数调优 1680、数据集验证 1560、标定 1480、离线回放 1480、
配方 1380、设备 1360、相机 1320、诊断 1280、插件 1280、硬件溯源 1180、坐标系树 1120、生产 1120。
唯一例外是流程模块（`WorkflowModulePanel.tsx:171`，Drawer 1080）。

**影响**：调参、验证、追溯这三件事天生要"边看画布边做"。现在打开即遮住画布，且面板之间互斥
（想对照"追溯里的失败帧"和"参数调优的候选解"只能来回关开）。在 1920×1080 上，1680 宽弹窗几乎满屏。

**建议（成本递增，可分三步走）**：

- **A（低成本，先做）**：Modal → Drawer（右侧，720–900，`destroyOnHidden` 保留），画布保持可见可平移缩放；
- **B（中）**：引入"停靠"语义——面板可 pin 到右栏，与画布共享宽度。你们已有可复用的基建：
  `SplitHandle`（含 ←/→/Home/End 键盘调整）、`layout.ts` 的持久化与 `--vs-*width` 变量；
- **C（长期）**：把"参数调优/数据集验证/追溯"降级为底部结果区的页签（与 `result`/`issues` 并列），
  让"看画布 + 看数据"成为同一个工作区，而不是两层模态。

**验收**：打开标定/调优面板时画布仍可见，且能用画布缩放控件操作；面板宽度可拖拽并持久化。

**估时**：A 1 人日；B 2–3 人日；C 3–5 人日。

---

### P1-2 长任务没有进度感，运行请求也没有超时

**证据**：

- `useRunController.ts:223-226` — 运行请求不传 `timeoutMs`（对比同文件节点测试的 60s、相机 5s、配方 15s），
  `abortRun`（`:245`）只 abort 前端请求；
- `EditorHeader.tsx:134-136` — 运行中把"运行"换成"中止"，没有已用时间、阶段、剩余节点；
- 标定求解/自动采集、内参/手眼向导、自动调参、离线回放：只有按钮 loading，无 Progress、无取消；
- 全仓只有一个实时通道 `components/DiagnosticsPanel.tsx:38`（`/hubs/diagnostics`），运行链路没有用上。

**影响**：现场调试最怕"点了没反应"。挂起时 `running` 永久为真，用户只能中止重来，也无法区分
"后端在算"与"连接断了"。

**建议**：

1. 运行头显示 `elapsed` + 阶段（编译/执行/回写）+ 已完成节点数/总数（结果区已有逐节点报告，可在执行中增量刷新）；
2. 复用诊断面板那套 SignalR 模式，为运行加 `/hubs/runs` 推送"节点开始/结束"事件；
3. 客户端加兜底超时（如 30 分钟），超时文案要说明"任务可能仍在后端运行，请到追溯查看"，而不是简单报失败；
4. 标定求解与自动调参加 `Progress` + 取消（后端支持取消的话）。

**验收**：任意 >10s 的操作，界面持续显示已用时间与阶段；超时/取消后给出可执行的下一步。

**估时**：2 人日（前端 elapsed/阶段 + 兜底超时 1 人日；hub 增量推送另计 1–2 人日后端）。

---

### P1-3 错误不能直达追溯

**证据**：全仓没有从错误处跳转到"运行追溯"的入口（`运行追溯` 只命中 TracePanel 自身标题）。
`RunPanel` 能定位到节点（`onSelectNode`），但拿不到"这次 run 的完整上下文"。

**建议**：错误横幅统一携带 `runId`，加一个"在追溯中打开"；`TracePanel` 接受 `initialRunId` prop
（它已有筛选状态机与分页，接一个初始值即可）。

**验收**：运行失败后一键进入该 run 的追溯详情，且筛选条件已就位。

**估时**：0.5 人日。

---

### P1-4 缺一个统一命令入口（Ctrl+K）

**现状**：能力都在，但入口分散——工具箱搜索（`toolboxModel.searchTools`，支持模糊/高亮）、
画布内 Ctrl+F 查找、顶部菜单里的 18 个面板、快捷键帮助弹窗。171 个节点 + 18 个面板 + 若干命令，
熟练用户记不住在哪个菜单。

**建议**：单一命令面板（Ctrl/Cmd+K），索引四类目标：节点类型（"添加 阈值分割"）、面板（"打开 标定"）、
命令（"运行""校验""适应画布""切换主题"）、最近工作流。直接复用 `searchTools` 的匹配算法与高亮组件。

**验收**：不碰鼠标即可完成"加一个节点并打开标定面板"。

**估时**：1.5 人日。

---

### P1-5 表格可用性不统一

**证据**：47 处 `pagination={false}`、0 处列 `sorter`、仅 8 处 `scroll`、仅 1 处 `fixed:'right'`、
仅 3 处自定义空文案。长表（插件五张表、追溯、配方版本、诊断资产）在窄容器里固定列宽被压成多行。

**建议**：封装 `PanelTable`（默认 `size="small"`、分页 20/50/全部、数值列右对齐、列排序、
`scroll={{ x: 'max-content' }}`、空态组件、行 `tabIndex=0`+Enter 打开）。先替换插件包、追溯、配方三处最痛的。

**验收**：三张大表在 1280 宽容器内不换行挤压，可按列排序，空态有引导。

**估时**：1.5 人日。

---

### P1-6 键盘可达性与焦点可见性

**证据**：

- `node_modules/@xyflow/react/dist/style.css:450` 把 `.react-flow__node.selectable:focus-visible` 的 `outline` 置为 `none`，
  `styles.css` 的 12 处 `focus-visible` 没有一处补偿节点——键盘聚焦节点时看不到焦点；
- 全仓 `autoFocus` 0 次；节点配置弹窗只在以 `result` 页签打开时才主动聚焦（`NodeConfigurationDialog.tsx:1418`）；
- 三个标定向导无 `onKeyDown`/Enter 提交；表格行（RunPanel/TracePanel/DevicePanel 等）不可键盘进入。

**建议**：补 `.react-flow__node:focus-visible { outline: 2px solid var(--vs-accent); }`；
参数页签首焦点落第一个可编辑控件；向导 Enter=下一步（文本域 Ctrl+Enter）；表格行 `tabIndex=0` + Enter 打开。

**验收**：全程 Tab 可以完成"选中节点→打开配置→改一个参数→应用"。

**估时**：1 人日。

---

### P1-7 双语能力建好了但没接线

**证据**：

- `i18n/catalog.ts` 有 2258 条中英词表、`i18n/provider.tsx` 定义了 Provider，但 `main.tsx` 没有挂载它；
- `theme.tsx:47` 把 antd `locale` 固定为 `zhCN`；
- 全仓没有任何"切换语言"入口（grep 0 命中），语言只能靠 localStorage 手工改；
- 面板内大量整段英文：`InvestigationPanel.tsx:128-132`（Start investigation / Resolve / Verify fix / Close case）、
  `CalibrationPanel.tsx` 表头（Use / Image X / World X (mm)）、`CameraPanel` 30+ 处英文 toast；
- 术语不统一："工作流" 93 次、"流程" 92 次；单位 `ms` / "毫秒" / `µs` 混用。

**影响**：这是"付了成本没拿到收益"的典型——词表和 `text(zh,en)` 双写遍布代码，但用户看不到语言开关。
对外交付（尤其海外客户）会直接暴露。

**建议**：

1. 挂载 `I18nProvider`，顶栏加语言切换（zh / en），antd locale 跟随；
2. 先清点"整面板英文"的两处（调查面板、标定表头）——它们是中文用户的高频障碍；
3. 术语表落成常量（工作流 = Workflow；"流程"只允许出现在示例名/口语文案），加禁用词检查；
4. 时长格式化收敛到 `formatDuration()` 单一实现。

**验收**：切换语言后，顶栏/工具箱/节点配置/追溯表头同步变化，antd 内置文案（分页、日期）也一起变。

**估时**：接线 1 人日 + 英文文案清理 1–2 人日（取决于清点范围）。

---

### P1-8 触屏/一体机的基础缺口

**证据**：画布只绑鼠标语义（`onNodeClick/onNodeDoubleClick/onNodeContextMenu`），**节点操作的主入口是右键菜单**，
触屏没有等价入口；`ImageViewer.tsx:965-971` 同时绑了 mouse 与 touch，但缩放只有 `onWheel`，无双指 pinch；
断点开关 18×18（`styles.css` `.node-breakpoint-toggle`，触屏已用 `@media (hover:none)` 常显兜底，这点做得对）。

**建议**：长按 500ms = 右键菜单；ImageViewer 加 pinch-zoom；`(hover: none)` 下把端口/断点等命中区放大到 ≥32px。

**验收**：无鼠标环境下可完成"选中节点→打开配置→改参数→运行"。

**估时**：1.5 人日（pinch 与长按分开测）。

---

## 三、P2：打磨

| 编号 | 现象（证据） | 建议 | 估时 |
|---|---|---|---|
| P2-1 | 懒加载面板 `Suspense fallback={null}`（`appEditorShared.tsx:24`）→ 首次打开白屏数百毫秒 | 给骨架 + "正在加载 标定…" | 0.3d |
| P2-2 | 顶栏行高 96px 在 `styles.css:133` 与 `App.tsx:365` 双写；无密度档位 | 抽 `--vs-header-height` 变量；加"紧凑/标准"档（1366×768 一体机） | 0.5d |
| P2-3 | 空态多为纯文本（"尚未注册设备""尚未注册机器人"） | 统一 Empty + 主行动按钮（`JobPanel.tsx:332/340` 已是范式） | 0.5d |
| P2-4 | 关闭节点配置：✕/Esc 走二次确认，"取消"按钮直接丢弃暂存参数 | 统一语义：要么都走 guard，要么按钮改名"放弃修改" | 0.3d |
| P2-5 | 浅色主题下画布/图像容器边框与背景对比度未验证 | 用工业现场强光场景实测，必要时提高 `--vs-border-*` | 0.5d |
| P2-6 | 时间单位三种写法；`RunPanel` 用"毫秒"、`VisionNode` 用" ms" | `formatDuration()` 单一实现 + 单位常量 | 0.3d |

---

## 四、建议的落地顺序

| 批次 | 内容 | 理由 |
|---|---|---|
| **第一批（2 天内，可单独发版）** | P0-1、P0-4、P2-1 | 全是小改动，先止住"误删"和"版本说谎"，风险最低 |
| **第二批（1 周）** | P0-2（DataBoundary + 4 个面板）、P0-3 分级确认、P1-3 | 这一批直接决定现场信任度 |
| **第三批（1–2 周）** | P1-1 A 步（Modal→Drawer）、P1-2、P1-6 | 工作区形态与长任务反馈，改动面较大但收益最高 |
| **第四批（按交付节奏）** | P1-7 接线 i18n、P1-4 命令面板、P1-5 PanelTable、P1-8 触屏 | 能力型改进，可跟随大版本 |

**配套护栏**（与你们现有棘轮风格一致）：p0-1/p0-2 各加一条 e2e（面板内按 Delete 节点数不变；断网时显示错误条而非空态）；
P0-4 加 grep 守卫；i18n 接线后加"新增中文硬编码"的棘轮。

---

## 五、刻意不做（避免把预算花在反方向）

1. **不重做暗色主题**。现有 `--vs-*` token 体系一致、`ThemeProvider` 与 antd algorithm 已打通，默认 dark 对工业暗光场景是对的；
2. **不引入第二套 UI 框架**。antd vendor chunk 已 977 kB（gzip 311 kB），再引入一套组件库就是首屏翻倍；
3. **不做自由拖拽仪表盘**。现场需要"稳定可预期"，不是可定制；
4. **不给删节点加确认弹窗**。已可撤销，弹窗只会拖慢熟练用户——正确做法是 toast + 撤销；
5. **不为了"看起来现代"重构画布样式**。节点在缩放分级下的可读性（`styles.css` 的 far/near 规则）已经是踩过坑的产物。

---

## 六、本轮已落地（2026-10-25 18:50）

按"第一批 + 第二批 P0"执行，P0 全部完成，另带一项 P2。

| 编号 | 改动 | 关键文件 | 验证 |
|---|---|---|---|
| **P0-1** | 面板/弹窗打开时画布快捷键整体让位，Delete 不再穿透 | `useEditorShortcuts.ts`、`App.tsx`（`overlayOpen` 汇总 20 个开关） | 新增 `editorShortcuts.test.tsx` 8 项；全量 1105 项通过 |
| **P0-2** | 新增 `DataErrorAlert`（红条 + 原因 + 重试），把 6 个面板的静默失败改成可见失败 | `components/DataErrorAlert.tsx`、`CalibrationPanel`、`DatasetValidationPanel`、`PluginPackagePanel`、`ModelPackagePanel`、`HardwareProvenancePanel`、`CalibrationIntrinsicsWizard` | 新增 `dataErrorAlert.test.tsx` 4 项；覆盖率 69.11/65.76/59.37/73.76 全部过门禁 |
| **P0-3** | 8 处不可逆/影响产线的操作补二次确认，文案写明后果 | `CalibrationPanel`（发布/回滚）、`PluginPackagePanel`（版本移除/回滚/发布者/进程池/性能统计）、`WorkflowModulePanel`（替换节点）、`HardwareProvenancePanel`（清除声明）、`ModelPackagePanel`（删除包）、`TracePanel`（清理过期）、`ProductionPanel`（停线）、`DatasetValidationPanel`（移除样本） | `workflowPanelChanges.test.tsx` 适配 Popconfirm（3 项）后通过 |
| **P0-4** | 版本号收敛到 `APP_VERSION`，并加棘轮 | `WorkflowModulePanel`、`PluginPackagePanel`、`DatasetValidationPanel`、`TraceAnalysisPanel`、`i18n/messages.ts` | 新增 `versionSource.test.ts`（历史表述走白名单，其余 0 容忍） |
| **P2-1** | 懒加载面板占位骨架，首开不再白屏 | `appEditorShared.tsx`（`PanelLoading`）、`styles.css` | 构建产物正常，e2e 4 项通过 |

**构建/测试**：`npx tsc -b` 通过；`npx vitest run` 86 文件 / 1105 项全绿（154s）；
`npm run test:coverage` 195s，四项指标均高于阈值；`npm run build` 通过（index 1225 kB / gzip 295 kB）；
`npm run test:e2e` 4 项通过（16s）。

### 实施中修正的一条判断（值得记住）

审计建议写的是"回查 DOM 判断有没有弹窗"。落地时用真实 antd 6.6.5 验证发现：**关闭后的 Modal 仍把
`.ant-modal-wrap` 留在 DOM 里，而且不带 `display:none` 内联样式**。按 DOM 全局查询判断，会导致
"打开过一次面板之后，画布 Delete 永久失效"——比原 bug 更糟。

最终改为：打开状态由 `App.tsx` 显式汇总成 `overlayOpen` 传入钩子；DOM 判断只用于"焦点是否落在面板内部"
这层兜底。回归用例 `已关闭弹窗残留的 DOM 不会让快捷键永久失效` 专门守这条。

### 顺延与取舍

- `PropertyPanel` 的"恢复出厂默认"**不加**确认：它在节点配置弹窗内可被"还原为打开时"或"取消"回退，
  属于可撤销操作，弹窗只会拖慢熟练用户（与本文第二节的分级原则一致）。
- `InvestigationPanel.loadValidationRuns`、`WorkflowModulePanel` 详情加载保留静默回退：两者都有
  降级路径（可选工作区 / 退回摘要），代码里已注明原因，未纳入本轮改造。

### 新发现（建议下一批处理）

**antd 6.6.5 已把 `Alert` 的 `message` 标为废弃（改用 `title`）**：全仓 146 处 `<Alert>`、89 处 `message=`，
现在每次渲染都会打 deprecation warning，且下一个主版本会移除该属性。建议与 P2-5/P2-6 一起做一次批量迁移
（机械替换 + `dataErrorAlert.test.tsx` 那样的渲染断言兜底）。

---

## 七、第三批已落地（2026-10-25 19:10）

| 编号 | 改动 | 关键文件 | 验证 |
|---|---|---|---|
| **P1-2（部分）** | 运行中显示已用时间；普通运行加 15 分钟前端兜底超时，超时文案明确"任务可能仍在后端执行，请在追溯确认" | `useRunController.ts`（`RUN_TIMEOUT_MS`、`runStartedAt`）、`EditorHeader.tsx`（`RunClock`） | 新增用例：超时后 `running=false`、`runStartedAt` 清空、错误文案含"追溯" |
| **P1-3** | 结果区新增"在追溯中打开"，携带 `runId` 打开追溯并预筛选（最近 7 天 + 该 ID）+ 直接选中 | `EditorBottomDock.tsx`、`App.tsx`（`traceRunId`）、`TracePanel.tsx`（`initialRunId`） | 新增用例：跳转后查询参数为 `{preset:'week', runIdPrefix}` |
| **P1-6（部分）** | 补回画布节点的键盘焦点环（@xyflow/react 自带样式把 `focus-visible` 的 outline 清零） | `styles.css` | 视觉改动，随 e2e 验证 |
| **P2-3（部分）** | 设备 / 机器人 / 追溯详情的纯文本空态改为 `Empty`，并写清"下一步做什么" | `DevicePanel`、`RobotPanel`、`TracePanel` | 全量测试通过 |

### 一条被测试挡回、复核后维持原状的改动

原计划把节点配置弹窗的"取消"改成"放弃修改"（有修改时），以消除"✕/Esc 会确认、取消不确认"的不一致。
落地后 `nodeConfigurationDialogDraft` 的两个用例失败——它们锁定的是既有决策：**✕/Esc 属于"手滑也会碰到"的路径，
需要二次确认；"取消"是明确点选的动作，保持一次点击关闭。** 复核后认为这个区分是对的，不应为"形式统一"牺牲熟练用户的效率：
保留原文案，只补 `title` 说明"会丢弃未应用的修改"，并把判断写进代码注释与本文，避免下次再被当成缺陷改一遍。

**验证**：`tsc -b` 通过；`vitest run` 86 文件 / **1107 项**全绿（154s）；
`test:coverage` 69.15 / 65.78 / 59.40 / 73.79（均高于阈值）；`build` 通过；`e2e` 4 项通过。

### 尚未动的（按价值排序，建议单独立项）

1. **P1-1 停靠式工作区**：不是把 Modal 换成 Drawer 就完事——这几个面板内部是像素下限栅格（标定 680+470、诊断 620+520），
   窄化成右侧抽屉会直接压破布局。真做法是引入"右停靠区 + 面板可 pin"，复用现成的 `SplitHandle`/`layout.ts` 持久化，
   属于设计 + 实现一体的活（估 2–3 人日）。
2. **P1-2 的后半段**：逐节点进度需要后端 `/hubs/runs` 推送（前端已有诊断 hub 的范式），要动 API 与执行器，跨前后端。
3. **P1-7 i18n 接线**：挂 Provider + 顶栏语言切换 + antd locale 跟随（1 人日），之后才能谈英文文案清理。
4. **P1-4 命令面板、P1-5 PanelTable**：各自 1.5 人日，互相独立，可并行。
5. **技术债**：antd 6 的 `Alert.message → title` 批量迁移（146 处 Alert / 89 处 `message=`）。

---

## 八、第四批：i18n 接线（2026-10-25 19:25）

**这条是"成本已付、收益没拿"的典型**：`i18n/catalog.ts` 2258 条词表、`provider.tsx` 的 Provider、
`fallbackCatalogRaw`（专门为语言切换保留英文原文）都在——但 `main.tsx` 没挂 Provider、antd locale 写死 `zhCN`、
界面上没有任何切换入口，语言只能靠手改 `localStorage`。

| 改动 | 说明 |
|---|---|
| 挂载 `I18nProvider` | `main.tsx` 中置于 `ThemeProvider` 外层；Provider 未挂时 `useI18n` 返回默认上下文而不是抛错，面板与单测可独立渲染 |
| antd locale 跟随 | `theme.tsx` 按语言选 `zhCN` / `enUS`：分页、日期、空态、Select 无数据等内置文案一起切换 |
| 顶栏语言开关 | 显示"要切到哪种语言"（中文界面显示 `EN`，英文界面显示 `中`），持久化到 `localStorage`，并同步 `<html lang>` |
| 目录按语言派生 | `catalog` 改为 `rawCatalog + useMemo(localizeCatalog(raw, lang))`；切换语言**不重新请求**、也**不改写画布节点名** |
| 消费方改为上下文 | Toolbox / VisionNode / NodeDescription / SwitchCaseEditor / StoragePanel / EditorHeader / App 从 `getInitialLang()` 改为 `useI18n()`，切换即重渲染 |

**一条刻意的边界**：语言切换**不重命名画布上已有节点**。节点名是文档内容（会随工作流保存），
若切换语言时顺手 rehydrate，会静默把文档弄脏、并把用户改过的名字覆盖掉。宁可接受"新加节点用新语言、
旧节点保持原样"这种混合状态，也不动用户数据。目录来源为后端时，`hydrateNodesFromCatalog` 仍然只在真正
刷新目录时执行（沿用既有行为）。

**当前英文覆盖范围（如实记录，避免误判为"切换失效"）**

| 已跟随切换 | 仍是中文 |
|---|---|
| 工具箱（搜索/收藏/最近/分组）、节点内端口与提示、节点说明、开关分支编辑器、存储维护面板、antd 内置文案、日期时间格式 | 顶栏菜单与按钮文案、绝大多数面板内部文案（相机/机器人/设备/作业/追溯/标定/调优…）、`messageApi` 提示文案 |

顶栏开关的 tooltip 已如实写明"英文覆盖仍在完善"。剩余工作是把各面板里的 `text(zh, en)` 双写字面量
逐块迁到 `t()` + `messages.ts`，属于机械但量大的活（估 1–2 人日，可按面板切分、互不冲突）。

**验证**：`tsc -b` 通过；`vitest` 87 文件 / **1111 项**全绿（155s）；覆盖率 69.25 / 65.90 / 59.56 / 73.87；
`build` 通过；`e2e` **5 项**通过（新增"切换语言后 `<html lang>` 与工具箱文案同步、刷新后保持"，24s）。
