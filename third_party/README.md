# 外部源码

外部依赖的源码与许可证保存在本目录，跟随主仓库版本管理，不依赖工作区之外的目录。
保留上游目录结构、包版本配置、文档、Demo 和测试，不将其源码混入 `backend` 或 `wpf` 业务目录。

## ImageViewer

| 项目 | 内容 |
|---|---|
| 上游 | <https://github.com/18961389539/ImageViewer> |
| 本地目录 | [`ImageViewer`](ImageViewer/) |
| 许可证 | [MIT，Copyright 2026 ImageViewer contributors](ImageViewer/LICENSE) |
| 导入来源 | 用户提供的 `ImageViewer-master.zip` 源码快照 |
| 原始 ZIP SHA256 | `A19E58EC67C3D0EC8169D00008C48785698B513AA036A5AB60F60BFD5358EFC4` |

ZIP 不含 Git 历史，因此不声明未经核实的上游提交号。导入时保留 635 个源码、配置与文档文件，
逐个核验内容哈希，不将原目录的 `bin`、`obj` 等构建残留作为源码导入。

### 本地接入修改

- [`ImageViewer.xaml.cs`](ImageViewer/ImageViewerControl/Controls/ImageViewer.xaml.cs)：
  增加宿主用的 `SetRois` 与 `ResetActualSize`。
- [`ImageViewer.CodeBehind.cs`](ImageViewer/ImageViewerControl/Controls/ImageViewer.CodeBehind.cs)：
  公开 `ZoomAtViewportCenter`，明确拒绝非正数或非有限倍率。
- [`integration.md`](ImageViewer/docs/integration.md)：补充公共宿主 API 说明。

WPF 的默认引用、UI 适配和业务回归测试位于主仓库 `wpf`，不直接使用上游 Demo。
外部控件通过业务项目引用参与构建；上游多目标项目不单独加入根解决方案的默认构建列表。
运行数据仍位于主仓库数据根，不写入外部源码目录。

### 升级约定

1. 获取新的上游源码，保留许可证并确认版本来源；不要用新 ZIP 直接覆盖本地修改。
2. 对比、保留或上游化以上公共宿主 API；不改变主仓库的算法、窗口与数据契约。
3. 在主仓库根目录执行 `dotnet build .\VisionStudio.slnx` 和 WPF 门禁，
   验证默认外部显示、标注映射、窗口生命周期及显式内建模式。
4. 同步记录新的源码来源与本地修改；不要提交构建缓存、测试报告或运行数据。
