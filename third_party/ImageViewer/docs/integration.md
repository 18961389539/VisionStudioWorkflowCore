# ImageViewer 集成指南

面向宿主开发者：把 `ImageViewerControl` 嵌进自己的 WPF 程序，并管理服务生命周期。界面操作见[使用手册](usage.md)。

## 依赖注入注册

在应用启动时注册 `AddImageViewerHost()`，在窗口级作用域内解析控件：

```csharp
using ImageViewer.Controls;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddImageViewerHost();

using ServiceProvider provider = services.BuildServiceProvider(
    new ServiceProviderOptions { ValidateScopes = true });
using IServiceScope windowScope = provider.CreateScope();
ImageViewer viewer = windowScope.ServiceProvider.GetRequiredService<ImageViewer>();
// 在窗口使用控件的整个期间保留 windowScope；窗口关闭后再释放。
```

- 同一窗口需要多个控件时，在同一作用域内注入 `IImageViewerFactory`，多次调用 `CreateViewer()`。控件共享该作用域的 runtime 与渲染服务。
- 连续推送同一文档的图像帧可设置 `ImageSource`。需要像用户打开新图一样切换文档时，用 `ReplaceImageDocumentAsync`，处理未保存状态并重置 ROI、标定和撤销历史。
- 可在注册前覆盖 `IImageViewerRefreshSchedulerFactory`、`IImageViewerLatestTaskSchedulerFactory`、`IImageViewerPeriodicTaskSchedulerFactory` 及 `IImageViewerAnalysisDiagnostics`。自定义 ROI 插件可通过 `AddImageViewerHost(builder => { ... })` 配置。检测门限可通过查看器的 `QualityProfile` 设置；门限会随会话和测量配方保存。

### 宿主图像与标注操作

- `ImageSource = null` 清空显示；`FitImageToView()` 适配窗口，`ResetActualSize()` 恢复 1:1。
- `ZoomAtViewportCenter(factor)` 以视口中心缩放，倍率必须为有限正数。
- `SetRois(rois)` 批量替换 ROI 并立即重绘，不向撤销栈添加命令。需要保留用户 ROI 时，
  宿主应将它们与新算法标注一起传入；用户交互式添加仍使用 `AddRoi(roi)`。
- 图像或算法结果属于宿主提供的初始状态时，可调用 `MarkDocumentClean()` 标记基线；
  不要用它覆盖用户尚未保存的编辑状态。

## 生命周期与释放责任

插件注册表、对话框等共享服务由容器在进程级管理；`ImageViewerHost`、`ImageViewerRuntimeServices`、渲染服务、控件和工厂由窗口作用域管理，避免从容器根解析或注册为 Transient。

手工使用 `new ImageViewer()` 或 `ImageViewerHost.CreateDefault()` 时，宿主拥有 runtime，最后一个 viewer 释放后会回收服务；再创建 viewer 需要新的宿主。外部提供的 runtime 由提供方释放。直接使用 `new ImageViewer(dependencies)` 不建立宿主租约，依赖和 runtime 仍由创建者释放。

对宿主最直接的两条约束：

| 场景 | 谁释放 |
| --- | --- |
| 通过 `AddImageViewerHost()` + 窗口作用域解析 | 容器释放作用域时（不要把 viewer 注册为 Transient，也不要从根容器解析）。 |
| 通过 `ImageViewerHost.CreateDefault()` / `new ImageViewer()` 手工创建 | 创建者；拥有型宿主在最后一个 viewer 归还租约后回收 runtime，之后 `CreateViewer()` 会抛 `ObjectDisposedException`。 |

## 主题与本地化

- 主题字典在 `Themes/ViewerTheme.{Dark,Light,HighContrast}.xaml`。控件自己的 `Resources` 会遮蔽应用级资源，所以覆盖外观应通过控件属性或替换主题字典，不要在应用级重复定义同名画刷。
- 仓库当前只提供中文资源（`Resources/UiText.resx`、Demo 的 `DemoText.resx`），没有卫星程序集，也没有语言切换命令。需要其他语言时由宿主提供同名资源并在 `CurrentUICulture` 上生效。

## 打包与架构约束

- 生成 NuGet 包、符号包与发布流程见[NuGet 发布](nuget-publishing.md)。
- 控件与 Core 的依赖方向、允许引用的类型，见[架构边界](architecture-boundaries.md)；改动分层前先读这份，避免把 UI 类型漏进 Core。
