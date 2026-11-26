# NuGet 发布

仓库发布两个包：

- `ImageViewer.Core`：同时提供 `net8.0` 与 `net10.0` 资产，提供与 WPF 无关的测量统计和核心契约。
- `ImageViewerControl`：目标框架为 `net8.0-windows` 和 `net10.0-windows`，依赖同版本的 `ImageViewer.Core`、HelixToolkit 和 Microsoft.Extensions 运行时包。图像分析全部由托管代码实现，不携带任何原生运行时 DLL。

每个包都会生成 `.nupkg` 和 `.snupkg`。版本由 SemVer 2.0 字符串控制，项目默认版本为 `0.1.0`；发布流水线会用 Git 标签或手动输入版本覆盖它。

本地只打包不发布：

```powershell
./build/pack.ps1 -Version 0.1.0-rc.1
./build/test-packages.ps1 -Version 0.1.0-rc.1
```

第二条命令在独立目录和包缓存中，从 `artifacts/packages` 安装刚生成的控件包及其 Core 依赖。在 .NET 8 和 .NET 10 上分别启动最小 WPF 宿主，打开 Demo 测量示例，校验测量值、公差判定以及 CSV 和元数据导出。两套 Windows Desktop 运行时均须安装。
如需离线验证，可用 `-DependencySource` 指定已有的第三方 NuGet 包目录。发布工作流复用锁定还原时下载的第三方包。

GitHub Actions 的 `Publish NuGet packages` 工作流有两种入口：

1. 推送 `v0.1.0` 标签，会自动打包并进入发布 job。
2. 手动运行工作流，输入版本；`publish=false` 只生成并上传包，`publish=true` 才会推送到 NuGet.org。

只有包验证通过后，才会上传产物并进入发布 job。发布 job 使用 `nuget` environment 中的 `NUGET_API_KEY`，并启用 `--skip-duplicate`，重复版本不会导致第二次推送失败。普通分支和普通 CI 不会推送包。
