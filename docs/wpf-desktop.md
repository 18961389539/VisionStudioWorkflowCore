# WPF 桌面客户端

## 目录与构建

- [`../VisionStudio.slnx`](../VisionStudio.slnx)：Windows 下统一打开后端、WPF 和相关依赖。
- [`../wpf/VisionStudio.Wpf.slnx`](../wpf/VisionStudio.Wpf.slnx)：只构建或测试桌面端。
- [`../wpf/src/VisionStudio.Wpf`](../wpf/src/VisionStudio.Wpf)：窗口、ViewModel、进程内宿主和图像适配器。
- [`../third_party/ImageViewer`](../third_party/ImageViewer)：完整外部控件源码，包含许可证及本地宿主 API。

从主仓库根目录使用 Windows 与 .NET 10 SDK：

```powershell
dotnet build .\VisionStudio.slnx
dotnet build .\wpf\VisionStudio.Wpf.slnx
```

后端、WPF、ImageViewer 的 `Directory.Build.props` 与中央包管理文件分别保留，
不要为统一目录而合并不同层的包版本配置。上游自己的解决方案、SDK 配置、Demo 和测试
作为源码快照保留；业务开发使用主仓库入口。
统一解决方案只列出后端与 WPF 业务项目，ImageViewer 通过 WPF 项目引用选择匹配的
.NET 10 目标构建，不将上游的 .NET 8、Demo 和测试一起纳入默认业务构建。

## 启动与数据根

```powershell
.\run-wpf.ps1
.\scripts\run-wpf.ps1 -DataRoot 'D:\VisionStudioData'
.\run-wpf.ps1 -UseExternalImageViewer:$false
.\run-wpf.ps1 -ExternalImageViewerProject 'D:\ImageViewer\ImageViewerControl\ImageViewerControl.csproj'
```

根目录脚本仅转发参数，实际实现位于 [`../scripts/run-wpf.ps1`](../scripts/run-wpf.ps1)。
开发启动默认数据根为 `artifacts/wpf-data`；显式 `-DataRoot` 优先，其次使用
`VISIONSTUDIO_DATA_ROOT`。启动脚本与构建命令都不清空运行数据。

执行策略拦截时，只放开当前启动进程：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run-wpf.ps1
```

## 内部试用与离线交付

当前交付定位为 **0.63.0 内部试用候选包**，不是已完成真实硬件调试或无人值守认证的生产版本。
本机验证环境为 Windows 10 Enterprise LTSC 2019（1809 / build 17763）x64。
Windows 11、Windows 10 22H2 的具体版次及真实 DPI 验收仍待执行；逻辑视口回归不能替代实机验收。
微软 [.NET Windows 安装与支持说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
对 Windows 10 有 LTSC/Enterprise 支持边界，不能笼统承诺所有 22H2 版次受支持。
逐项证据与待验证清单见 [`wpf-trial-acceptance.md`](wpf-trial-acceptance.md)。

```powershell
# 在源码仓库构建并验证自包含目录包
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-wpf.ps1
# 交付机上从包目录启动，不需要 SDK、源码、Node.js 或 Web 服务
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-VisionStudio.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-VisionStudio.ps1 -DataRoot 'D:\VisionStudioTrialData'
```

默认发布目录为 `artifacts/wpf-publish`，包含 win-x64 Release 自包含 .NET 运行时、默认 ImageViewer、
示例流程、回放帧、启动脚本、许可证、操作说明和 `build-info.json`。
发布脚本在暂存包内实际打开主窗口，执行流程、回放取图、诊断导出、关闭，再用同一数据根重启；
另验证数据根占用/包内数据根启动失败，并通过交付启动脚本显式指定数据根、打开窗口和正常关闭。
只有这些验收全部成功后才切换目录，旧包保留为 `.previous-<GUID>`。
首次及重启 JSON 证据保存到脚本打印的 `artifacts/test-results/wpf-publish-<GUID>`。
NuGet 恢复与包构建需要开发环境的依赖缓存或网络；**离线**指交付包的运行，不是首次源码构建。

发布启动默认数据根为 `%LOCALAPPDATA%\VisionStudio\Trial\data`，显式 `-DataRoot` 优先于
`VISIONSTUDIO_DATA_ROOT`。发布脚本自身的 `-DataRoot` 仅做路径分离预检，不会写入包内启动配置；
若要固定现场目录，请在交付机使用启动参数或环境变量。数据根不得与版本目录互相包含。
同一数据根只允许一个 WPF 宿主，重复启动明确失败；`.wpf.lock` 可在退出后保留，是否占用以文件句柄为准。
这不代替 Web 宿主的互斥：不要让桌面与 Web/后端共用正在运行的数据根。

升级时先正常关闭、备份外部数据根，再将启动指向新包并沿用该数据根。不要删除运行数据，
也不要把新的数据库直接交给旧包；回滚可能需要恢复与旧版本匹配的数据快照。
自包含 .NET 不代表原生依赖全免安装：VC++ x64 运行库、厂商 SDK 与硬件配置仍须按目标机验证。

### 操作闭环

- 流程编辑：新建/加载、另存为、删除已保存流程、节点参数分组配置、主图交互式 ROI 绘制与参数导入、
  编译校验与问题定位、保存、JSON 导入导出；支持撤销/重做（Ctrl+Z/Y）、节点复制粘贴克隆（Ctrl+C/V/D）、
  拓扑自动排版、单节点运行（F10）与断点会话调试（F9/断点调试/缓存单步重跑）。运行中禁改图及参数；
  新建、切换、导入或关闭前提示保存/放弃/取消；保存检查修订冲突，不静默覆盖他人变更。
- 生产：支持“初始化试用示例”及“将当前编辑器流程发布为工单/配方”（含非 Stable 节点成熟度显式确认）、
  工单版本切换/回滚与载入编辑器、多工位全部启停/单次触发、产线实时结果图像预览、全部/活动报警切换、
  以及基于 `result_records` 的 SPC 过程统计分析。
- 设备：相机打开/采集/单次直采/软触发取图/连续预览开关/关闭、曝光增益帧率触发模式设置及 GenICam 特性浏览；
  PLC/工业设备连接、断开、标签批量读取与确认写入；机器人连接、断开、停止与故障复位。
- 诊断：筛选最近 100 条追溯记录、查看结构化节点报告与 JSON 详情、载入历史快照到编辑器、
  查看节点 P50/P95 耗时分布与单次 Span 遥测、创建数据集并执行批量验证、导出共享诊断 ZIP。
  ZIP 不包含数据库、配置原文或留证图像，但仍可能含本地路径、用户名及日志，按需授权分享。
- 工作区每秒刷新，操作不重叠；失败后明确显示未知/陈旧，不伪报成功。关闭等待取消、生产停止、
  预览与调试会话清理及资源释放；停止失败保留窗口并显示错误，允许重试。

桌面仍为受信任本地 Development 模式，初始化审计身份标为 `LocalDesktopTrial`，不伪装远程认证登录。
真实设备、Windows 11 / Windows 10 22H2 具体版次及真实 DPI 仍待实机验收，不声称已完成硬件认证。

## ImageViewer 接入

默认项目引用为 `third_party\ImageViewer\ImageViewerControl\ImageViewerControl.csproj`，
由 [`../wpf/Directory.Build.props`](../wpf/Directory.Build.props) 相对于自身目录解析。
外部源码缺失时明确报错，不会静默退回内建显示。仅在显式设置
`UseExternalImageViewer=false` 时使用内建图像引擎。

主图像区、相机预览和节点详情预览统一使用外部控件；图像仍由进程内引擎直接传递，
不改变 Web 前后端。点、线、圆、旋转矩形、多边形、轮廓、折线、文字和区域映射为
锁定的原生 ROI；更新算法标注不重复追加，并保留用户绘制的 ROI。区域游程最多采样 2000 条。
缩放、1:1、标注显隐和控件释放均由宿主适配器管理。

本地源码补充了 `SetRois`、`ResetActualSize` 和 `ZoomAtViewportCenter` 公共宿主 API。
如果使用 `UseExternalImageViewerPackage=true`，NuGet 包必须由包含这些 API 的源码构建，
并与 [`../wpf/Directory.Packages.props`](../wpf/Directory.Packages.props) 的包版本一致。
来源、许可证和升级约定见 [`../third_party/README.md`](../third_party/README.md)。

## 验证

```powershell
.\scripts\quality-gate.ps1 -Scope Wpf
# 只运行图像/窗口和工作流相关回归：
dotnet test .\wpf\tests\VisionStudio.Wpf.Tests\VisionStudio.Wpf.Tests.csproj `
    --filter 'FullyQualifiedName~WpfWindowSmokeTests|FullyQualifiedName~WorkflowGraphAndViewModelTests'
```

WPF 门禁构建 Release 并运行全部桌面测试，报告写入 `artifacts/test-results/wpf/`。
门禁还运行发布/启动路径负向回归和 NuGet 通告检查；完整离线包的首次/重启验收由
[`../scripts/publish-wpf.ps1`](../scripts/publish-wpf.ps1) 单独执行。
试用回归覆盖 1000 个模拟生产周期、20 轮运行/启停/取消/关闭，以及
1366×768、1920×1080 在 100%/125%/150% 下的六个逻辑视口尺寸。
所有 UI 用例共用 STA Dispatcher；真实系统缩放、目标系统和硬件验收必须另记结果。
`bin`、`obj`、测试报告与 `artifacts` 已由 Git 忽略，但数据根和发布目录可能包含需要保留的内容，
不能把整个 `artifacts` 当成临时缓存删除。
