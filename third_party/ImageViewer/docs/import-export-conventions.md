# 导入导出约定

## 图像导入

支持的图像文件类型：

- `.png`
- `.jpg`
- `.jpeg`
- `.bmp`
- `.tif`
- `.tiff`

导入约定：

- 图像路径由宿主程序或 demo 通过打开文件对话框选择。
- 加载图像后，视图相关菜单项才会进入可用状态。
- 图像本身不嵌入 ROI JSON；只有 `.ivpkg` 会在需要时复制图像资产。

## ROI 导入与导出

导出约定：

- 文件格式为 UTF-8 编码、缩进格式化的 JSON。
- 顶层包含 `Version`、`PixelSize`、`PhysicalUnit` 和 `Items`。
- `Items[*].Type` 应使用已注册 ROI 插件的类型键。

导入约定：

- `Version` 高于当前支持版本时拒绝加载，避免旧代码误读新格式。
- 缺失 `PixelSize` 时兼容使用 `1.0`；显式 `PixelSize <= 0` 或非有限值会拒绝加载。
- `PhysicalUnit` 为空时回退到 `px`。
- 未知 ROI 类型会被跳过，以保证其余可识别对象仍可恢复。
- 为兼容老文件，系统同时接受按 ROI 类型名进行的回退匹配。

## 会话与项目包导入

`.ivsession`：

- 适合“同一台机器继续工作”或“图像路径仍可访问”的场景。
- 若 `ImagePath` 为相对路径，将相对于会话文件所在目录解析。

`.ivpkg`：

- 适合移交、归档和跨机器共享。
- 包内必须包含 `session.ivsession`。
- 包内资产默认位于 `assets/`。
- 为避免 Zip Slip 风险，解包时只允许写入受控缓存目录。

## PNG 快照导出

当前视图 PNG 导出遵循以下约定：

- 导出的是用户当前看到的视图状态，而不是原始图像裸数据。
- 缩放、平移、叠加绘制层和当前可见 ROI 状态都会体现在结果中。
- 适合用于报告、沟通和缺陷回归比对，不适合替代原始图像归档。

## 分析 CSV 导出

CSV 文件首行固定为：

```text
Type,Label,Metric1,Metric2,Metric3,Mean,Min,Max,StdDev,PixelCount,DetectionStatus,SpecificationStatus,MeasuredValue,NominalValue,TolerancePlus,ToleranceMinus,PhysicalUnit
```

列含义：

- `Type`：ROI 类型名。
- `Label`：用户标签。
- `Metric1` 至 `Metric3`：按 ROI 类型填充的几何或测量指标。
- `Mean`、`Min`、`Max`、`StdDev`、`PixelCount`：在存在图像统计结果时输出。
- `DetectionStatus`：检测证据状态，例如 `NotMeasured`、`Passed`、`Review`、`Failed`；它不表示尺寸是否合格。
- `SpecificationStatus`：将测量值与 ROI 公差比较后的状态。检测失败或证据不足时为 `NotEvaluable`。
- `MeasuredValue`、`NominalValue`、`TolerancePlus`、`ToleranceMinus`：以 `PhysicalUnit` 表示的测量值和判定参数。
- `PhysicalUnit`：测量单位；未标定时为 `px`。

批量导出会在上述列前增加 `Source,Status,Error`，并在末尾增加 `SourceIndex,MeasurementItemKey`。`SourceIndex` 标识本批次中的输入图像轮次；`MeasurementItemKey` 标识本批次的模板项，在不同输入图像中保持一致，不以可能重复或为空的标签作为身份。输入图像缺失、解码失败或分析失败时，仍会为每个输入图像和每个 ROI 输出一行，确保失败项不会从结果集中消失。

### 成套文件与一致性

带导出上下文的单图 CSV 同时交付 `.metadata.json` 和 `.report.md`；批量 CSV 同时交付 `.metadata.json`、`.summary.json` 和 `.repeatability.md`。所有内容先生成并写入临时文件，再替换目标文件。暂存失败、取消或替换失败时会清理临时文件，并尝试恢复此前已存在的完整文件集；回滚失败会明确返回错误并保留可恢复的备份。

单图导出在启动时捕获 ROI、标定和分析位图。对于具有解码记录的位图，`source.sha256` 对应打开时读取的原始文件字节。内存位图或没有解码记录的位图使用 `source.pixelBufferSha256`，按分析像素格式从上到下、逐行计算 SHA-256，格式记录在 `source.pixelBufferFormat`。仅导出几何结果时，调用方仍可指定一个参考文件，`source.sha256` 对应该文件在导出时的字节。报告显示同一指纹的缩写。

这是进程内的回滚保护，多文件替换不具备断电或进程崩溃时的整体原子性。消费者应校验元数据提供的文件哈希及导出批次标识，避免读取混合版本的结果。哈希针对实际落盘的 UTF-8 文件字节，包含 BOM。

批量导出的 `inputRecords[*].sha256` 针对输入图像的原始文件字节。指纹计算和解码共用同一个只读文件句柄，期间 Windows 阻止写入或替换该文件。已读取指纹但无法解码的输入也会保留指纹，便于追查失败原因。

### 批次离散度与重复性

重复性研究按测量项身份聚合，每个输入轮次对同一项最多贡献一个有效测量值；失败或未测得的值不补零。参与图像数按输入轮次计数，不按结果行数计数。

调用方通过 `RepeatabilityStudyOptions` 明确提供工件上下文。只有 `SameWorkpiece` 才给出同件重复性及其占用公差的结论；缺省的 `Unspecified` 和 `DifferentWorkpieces` 只描述源间变化，不能据此认定测量系统的重复性。角度项使用度，长度项使用标定单位。批次模板编号只保证本批次内的对应关系，不能用于匹配重新排序后的另一个批次。

批量导出元数据的 `result` 同时记录以下状态统计：

- `processedFileCount` / `failedFileCount`：兼容字段，分别表示 ROI 全部成功和存在任意 ROI 或输入失败的输入数。
- `decodedFileCount`：成功读取的输入数，包含 ROI 部分失败或全部失败的输入。
- `fullySuccessfulFileCount`：所有 ROI 都完成分析的输入数。
- `partiallySuccessfulFileCount`：至少一个 ROI 成功、至少一个 ROI 失败的输入数。
- `allRoiFailedFileCount`：图像已读取但所有 ROI 都失败的输入数。
- `inputFailedFileCount`：缺失或无法解码的输入数。
- `unfinishedFileCount`：取消时尚未开始或尚未完成的输入数；这些输入不计入失败数。

界面摘要使用这些分类，因此“读取成功”不会再被显示成“分析成功”。`inputRecords[*].status` 对应 `Processed`、`PartiallyProcessed`、`AnalysisFailed`、`InputMissing` 或 `DecodeFailed`。取消时，`inputs` 仍保留完整输入清单；CSV、`inputRecords` 和重复性报告只纳入已完成的输入（含已记录失败的输入）。

### 过程能力与 SPC

过程能力按“同一量名 + 同一单位”聚合：

- 单图报告聚合当前图像上的重复特征；批次报告按 `MeasurementItemKey` 跨输入图像聚合，顺序即输入轮次顺序。
- 未测得或非有限值不参与统计，也不补零。
- 组内 σ 由个体值移动极差均值除以 `d2 = 1.128` 估算；整体 σ 为样本标准差（`n - 1`）。`Cp/Pp` 需要双侧公差，`Cpk/Ppk` 在单侧公差时也可计算，并取较近的一侧。σ 为零时指数无定义，输出 `null` 而不是无穷大。
- 同一序列内公差互相矛盾时，规范限按“未配置”处理，避免用一套自相矛盾的判据算出看似精确的指数。

单图 `.report.md` 的 SPC 章节位于结论页脚之前；批次 SPC 章节附在 `.repeatability.md` 尾部。两份 `.metadata.json` 均包含 `processCapability` 数组，字段为 `key`、`label`、`unit`、`sampleCount`、`mean`、`sigmaWithin`、`sigmaOverall`、`lowerSpecificationLimit`、`upperSpecificationLimit`、`cp`、`cpk`、`pp`、`ppk`。界面中的控制图使用 Western Electric 判异规则子集，仅用于人工复核，不写入导出文件。

### 可打印报告与 XPS

界面中的“打印报告”与 `BuildReportModel` 共用同一份结构化报告（结论、标定身份、前置检查、测量明细、标定能力、SPC），因此打印稿与 `.report.md` 的数字和判定一致。

- 预览固定为 A4（793.92 × 1122.52 DIP，48 DIP 页边距）。
- 实际打印时按 `PrintDialog.PrintableAreaWidth/Height` 重建文档，页边距降为 36 DIP，避免打印机驱动边距造成二次裁切。
- XPS 导出使用 A4 版式，写入固定版式分页文档；不引入 PDF 依赖。
- 交互式打印窗口只带图像文件名，不带源图 SHA-256；需要完整来源哈希与机器可读字段时，以 CSV 导出的 `.metadata.json` 为准。
- 打印/导出只消费模型，不重新评估 ROI；窗口打开后继续编辑 ROI 不会改变已生成的报告内容。

分析元数据中的 `qualityThresholds` 保存本次运行实际使用的检测门限。项目应通过查看器实例的 `QualityProfile` 配置设备级门限，避免把默认值误认为现场验收标准。

不同 ROI 的典型指标示例：

- 直线：长度、dX、dY。
- 角度：角度、两条边长度。
- 圆：半径。
- 多边形：面积、周长、顶点数。
- 折线：长度、点数、是否自由绘制。

## 最近项目列表

最近项目列表按最近打开时间倒序排序，默认最多保留 10 条。对用户来说，这个列表是一个工作流入口，不是持久化主文件格式，因此不建议把它当作长期归档机制。
