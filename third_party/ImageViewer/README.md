# ImageViewer

ImageViewer 是一个面向 Windows 桌面应用的 WPF 图像查看、ROI 标注与体数据浏览控件。它提供可嵌入业务系统的控件库、可运行的演示宿主，以及覆盖交互、持久化与 WPF 集成路径的自动化测试。

项目目标是把图像浏览、测量、标注、分析、3D/MPR 浏览与可交付项目文件收敛为稳定的桌面端组件能力。

## 功能概览

### 图像、ROI 与分析

- 打开 `png`、`jpg`、`jpeg`、`bmp`、`tif`、`tiff` 图像，支持缩放、平移、适配视图和实际尺寸浏览。
- 按 EXIF Orientation 自动旋正 JPEG/TIFF，避免手机和相机照片横置或倒置；单图解码默认限制 1 亿像素，超限时给出明确提示而不是耗尽宿主内存。
- 使用矩形、圆、椭圆、多边形、折线等 ROI 进行标注；支持标签、颜色、可见性与锁定状态管理。
- 提供长度、折线、面积、角度、卡尺、点坐标、自动边缘吸附点、自动圆测量、圆心距、三点圆、拟合椭圆与梯度检测对齐等测量和分析辅助工具。
- 显示像素信息、统计摘要、剖面图、直方图、比例尺和属性面板。
- 基于图像分位数给出对比度、亮度与伪彩显示建议；建议不会自动修改当前显示参数。
- 检测质量与尺寸公差判定分开记录；批量失败逐图逐 ROI 留痕，质量门限可按项目保存并随元数据导出。
- 大图会按像素规模自动启用多分辨率金字塔、分块渲染和自适应缓存，缩放平移时只处理当前视口。

### 体数据与 3D 浏览

- 基于 HelixToolkit SharpDX 渲染体数据的 3D 视图。
- 提供轴位、冠状、矢状 MPR 视图，并在 2D MPR 与 3D 交叉切面之间同步位置。
- 在冠状或矢状视图中按 `↑` / `↓` 切换切片；轴位视图支持滑块和鼠标滚轮切换。
- demo 支持多选图像切片导入体数据；冠状和矢状视图提供切片滑块、上一层/下一层按钮和鼠标滚轮切层，并与 3D 交叉切面保持同步。
- 体数据排序优先使用切片空间位置、实例号或采集时间元数据；缺少元数据时才使用文件名自然排序，并要求人工确认。MPR 复用灰度缓存，Gray16 切片保持 16 位一致性。
- 支持 3D 相机预设、重置、适配体数据、切面显示和透明度控制。

### 导出、会话与项目交付

- 导出当前视图 PNG、分析 CSV 与 ROI JSON；分析 CSV 同时生成包含源图像指纹、标定、渲染设置和完整 ROI 输入的 `.metadata.json`。
- 保存 `.ivsession` 会话，包括图像路径、ROI、标定和视图状态。
- 导出 `.ivpkg` 项目包，将会话及图像资产一并打包，适合归档与交接。
- 删除所选 ROI 或清空 ROI 后可使用 `Ctrl+Z` 恢复。
- 分割候选接受后会生成 Blob ROI 并纳入撤销栈；会话修改会显示未保存状态，启动时可恢复自动保存内容。

## 快速开始

### 前置条件

- Windows 10 或更高版本。
- .NET SDK `10.0.302` 或与 [global.json](global.json) 兼容的后续功能带 SDK。
- 支持 WPF 的桌面环境和可用的 DirectX 图形驱动程序。

从仓库根目录执行：

```powershell
dotnet restore ImageViewer.sln --locked-mode
dotnet build ImageViewer.sln --configuration Release --no-restore --nologo
dotnet test ImageViewer.sln --configuration Release --no-build --nologo
dotnet run --project ImageViewerDemo/ImageViewerDemo.csproj --framework net10.0-windows
```

演示程序启动后，可点击“打开图像”或按 `Ctrl+O` 选择图像文件。右键菜单支持直接键入命令搜索；按 `Ctrl+F` 可将图像适应窗口。
第一次使用测量功能时，可点击 Demo 顶部的“测量示例”，按提示完成标定、尺寸检查、公差设置和 CSV 导出。

### NuGet 包

控件库与无 WPF 的 Core 层分别发布为 `ImageViewerControl` 与 `ImageViewer.Core`。在 `net8.0-windows` 或 `net10.0-windows` WPF 宿主中安装控件包即可，Core 会作为项目依赖自动安装。卡尺检测、稳健拟合与 Blob 分析全部由托管代码实现，无需任何原生运行时。

> 图像分析（卡尺边缘采样、稳健直线/圆拟合、Blob 分割）完全在托管代码中运行，不依赖原生视觉库，因此不再限制于 Windows x64 进程；凡 WPF 目标框架支持的 Windows 架构均可使用。

```powershell
dotnet add package ImageViewerControl --version 0.1.0
```

本地生成包和符号包：

```powershell
./build/pack.ps1 -Version 0.1.0-rc.1
```

包输出到 `artifacts/packages`，同时生成两个包各自的 `.nupkg` 和 `.snupkg`。`.github/workflows/nuget-publish.yml` 支持两种发布方式：创建 `v0.1.0` 形式的 Git 标签自动打包并发布；手动运行工作流时可先将 `publish` 设为 `false` 只验证包，确认后再用 `publish=true` 推送。推送前需要在 GitHub 的 `nuget` environment 中配置 `NUGET_API_KEY`。

## 仓库结构

| 目录 | 用途 |
| --- | --- |
| [ImageViewer.Core](ImageViewer.Core/) | 不依赖 WPF 的几何值类型、几何算法、测量统计与可复用核心契约。 |
| [ImageViewerControl](ImageViewerControl/) | WPF 控件、ROI 模型、渲染、菜单、对话框、分析、导出和宿主服务。 |
| [ImageViewerDemo](ImageViewerDemo/) | 演示应用，用于手动验证与集成参考。 |
| [ImageViewer.Core.Tests](ImageViewer.Core.Tests/) | Core 层的跨平台单元测试与覆盖率门槛。 |
| [ImageViewerControl.Tests](ImageViewerControl.Tests/) | xUnit 测试，覆盖命令、交互、持久化、体数据和 WPF smoke 场景。 |
| [docs](docs/) | 用户操作、文件格式、导入导出规则、术语与架构边界文档。 |

## 嵌入宿主应用

使用 `Microsoft.Extensions.DependencyInjection` 的 WPF 宿主可注册 `ImageViewerHost`、`ImageViewerRuntimeServices`、`ImageViewerRenderService` 与 `ImageViewer`。
它们都是 **scoped**（与窗口/对话框同寿命），必须在窗口级作用域内解析：

```csharp
using ImageViewer.Controls;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddImageViewerHost();

using ServiceProvider serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true
});

using IServiceScope windowScope = serviceProvider.CreateScope();
ImageViewer viewer = windowScope.ServiceProvider.GetRequiredService<ImageViewer>();
```

生命周期契约只有三层：

| 生命周期 | 注册项 | 释放责任 |
| --- | --- | --- |
| Singleton | 插件注册表、对话框/文件对话框/视口/会话/最近项目/项目包服务、调度器工厂、遥测与分析诊断、`ImageViewerHostServices` | 容器（进程级） |
| Scoped | `ImageViewerHost`、`ImageViewerRuntimeServices`、`ImageViewerRenderService`（含 4 个接口别名）、`ImageViewer`、`IImageViewerFactory` | 容器在作用域销毁时释放 |
| 无 Transient | —— | 控件与 runtime 都是可释放资源，不再注册为 transient，否则容器会长期持有引用并把释放推迟到容器销毁 |

所有权规则只有一条：**谁创建 runtime services，谁负责释放**。`ImageViewerHost.OwnsRuntimeServices` 为 `true` 时（宿主自己创建，典型是手工 `new ImageViewer()` 或 `ImageViewerHost.CreateDefault()`），宿主在最后一个 viewer 归还租约时释放 runtime；为 `false` 时（容器或调用方提供）宿主从不释放，由所有者负责。

通过 `AddImageViewerHost(builder => { ... })` 可配置宿主构建器；也可以在注册前替换以下服务以接入现有应用基础设施：

- `IImageViewerLatestTaskSchedulerFactory`
- `IImageViewerPeriodicTaskSchedulerFactory`
- `IImageViewerRefreshSchedulerFactory`
- `IImageViewerAnalysisDiagnostics`

`IImageViewerTelemetry` 使用 `ActivitySource` 和 `Meter` 发出非关键分析异常及性能诊断信号，可由 OpenTelemetry 订阅。默认日志写入 `Trace`；使用 `Microsoft.Extensions.Logging` 时，先调用 `services.AddLogging()`，再调用 `services.AddImageViewerMicrosoftLogging()`。

## 文件与安全边界

| 文件 | 内容 | 适用场景 |
| --- | --- | --- |
| ROI JSON | ROI 集合和标定信息 | 交换标注或算法结果。 |
| `.ivsession` | 图像路径、ROI、标定与视图状态 | 继续本地工作。 |
| `.ivpkg` | 会话和可选图像资产的 ZIP 容器 | 归档或向他人交付完整项目。 |

导出 `.ivpkg` 时先生成同目录临时文件，成功后才替换目标文件。加载项目包会限制条目结构并校验路径、条目数量和解压后大小，以防止 Zip Slip、包外文件引用和异常归档消耗过多资源：只能包含一个 `session.ivsession` 和 `assets/` 下的资产，最多 `1,024` 个条目，会话条目最多 `8 MiB`，单条目最多 `256 MiB`，总解压内容最多 `512 MiB`。

详细格式见 [docs/project-file-format.md](docs/project-file-format.md)，导入导出兼容性见 [docs/import-export-conventions.md](docs/import-export-conventions.md)。

## 开发与验证

项目使用集中式包版本、NuGet 锁文件、依赖审计、.NET 分析器和警告即错误策略。提交前建议执行完整验证：

```powershell
dotnet restore ImageViewer.sln --locked-mode
dotnet build ImageViewer.sln --configuration Release --no-restore --nologo
dotnet test ImageViewer.sln --configuration Release --no-build --filter "Category!=Smoke&Category!=Performance" --nologo
dotnet test ImageViewerControl.Tests/ImageViewerControl.Tests.csproj --configuration Release --no-build --filter "Category=Smoke" --nologo
```

运行 `./build/test-batch-performance.ps1` 可复现批量导出基准：使用固定的 24 张 4096 × 4096 图像，默认在独立进程中运行三次，记录峰值私有内存、耗时和标准化 CSV 的 SHA-256。报告写入 `artifacts/performance/batch/report.json`。CI 会检查内存与 CSV 基线；耗时超过观察阈值会发出提示。

所有新增用户可见文本应放在 `ImageViewerControl/Resources/UiText.resx` 或 `ImageViewerDemo/Resources/DemoText.resx` 中。WPF 相关实现保留在控件工程；新建可跨平台算法、数据契约和序列化逻辑应遵循 [docs/architecture-boundaries.md](docs/architecture-boundaries.md) 的依赖边界。

### CI 与覆盖率

GitHub Actions 工作流位于 [.github/workflows/ci.yml](.github/workflows/ci.yml)，在 Windows runner 上执行锁定依赖还原、Release 构建、Core 单测、导出/批量专项、Smoke 测试和完整回归。完整回归作为合并门禁运行；失败时仍会上传测试与覆盖率产物，便于定位问题。

本地生成 Core 覆盖率：

```powershell
dotnet test ImageViewer.Core.Tests/ImageViewer.Core.Tests.csproj `
  --configuration Release --settings coverage.runsettings `
  --collect:"XPlat Code Coverage" --results-directory artifacts/coverage/core
./build/coverage-summary.ps1 -CoverageRoot artifacts/coverage/core `
  -AssemblyPattern 'ImageViewer.Core' -MinimumLineRate 0.82
```

覆盖率使用 Cobertura 和 OpenCover 两种格式输出，CI 会上传原始报告；Core 行覆盖率低于 82%、ImageViewerControl 低于 68% 时构建失败。

## 文档索引

- [使用手册](docs/usage.md) · [离线网页版（可打印、可搜索）](docs/usage.html) · [PDF 版](docs/usage.pdf)
- [变更记录](CHANGELOG.md)
- [集成指南（WPF 宿主）](docs/integration.md)
- [高级测量](docs/advanced-measurements.md)
- [右键菜单](docs/context-menu.md)
- [检测参数推荐](docs/detection-parameter-recommendations.md)
- [项目文件格式](docs/project-file-format.md)
- [导入导出约定](docs/import-export-conventions.md)
- [NuGet 发布](docs/nuget-publishing.md)
- [术语与本地化边界](docs/terminology.md)
- [架构边界](docs/architecture-boundaries.md)

发给没有仓库的读者时，请使用下方命令生成离线 ZIP。解压后从 `START.html` 进入；截图、参考资料和测量练习都随包提供。

### 手册的生成与校验

`docs/usage.html` 由 `docs/usage.md` 生成，版式在 `docs/usage.template.html`，不要手改 HTML：

```powershell
python build/docs.py            # 重新生成 docs/usage.html
python build/docs.py --check    # 校验 HTML 与 md 同步，并检查工具/菜单覆盖度
./build/docs-pdf.ps1            # 用本机 Edge 生成 docs/usage.pdf
python build/docs.py --offline  # 生成 artifacts/docs/imageviewer-manual.zip
```

`--check` 会核对全部 ROI 工具、高级测量项、顶层菜单分组、错误消息标题、标定字段与质量门限字段都在手册中出现，同时检查图片与本地文档链接是否存在。`--offline` 先做相同检查，再收集离线包的引用文件；CI 的 `docs-check` 作业也会打包验证。

手册 HTML 或其中的图片变化后，运行 `build/docs-pdf.ps1` 并一并提交 `docs/usage.pdf` 与 `docs/usage.pdf.source.json`。离线打包和 CI 会核对来源及 PDF 的 SHA-256。

## 许可证

本项目使用 MIT 许可证，见 [LICENSE](LICENSE)。
