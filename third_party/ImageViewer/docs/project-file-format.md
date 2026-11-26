# 项目文件格式说明

## 概览

ImageViewer 当前有三类主要持久化文件：

- `.json`：ROI 集合导出文件。
- `.ivsession`：会话文件，记录图像路径、ROI 文档和当前视图状态。
- `.ivpkg`：项目包，实质上是一个 ZIP 容器，包含会话文件和可选图像资产。

## ROI JSON

ROI JSON 由 `RoiPersistenceService` 负责读写，顶层结构如下：

```json
{
  "Version": 1,
  "PixelSize": 0.5,
  "PhysicalUnit": "mm",
  "QualityProfile": {
    "MinimumDetectionConfidence": 0.05,
    "PassConfidence": 0.55,
    "PassResidualRms": 1.5
  },
  "Items": [
    {
      "Type": "circle",
      "Label": "孔位 A",
      "StrokeColor": "#FF00FFFF",
      "StrokeThickness": 2.0,
      "IsVisible": true,
      "IsLocked": false,
      "Center": { "X": 120.0, "Y": 84.0 },
      "Radius": 15.0
    }
  ]
}
```

字段说明：

- `Version`：当前固定为 `1`。读取时会校验：高于当前支持版本的文件会被拒绝加载；缺失或非正数视为早期文件，宽容接受。
- `PixelSize`：像素尺寸；缺失时兼容使用 `1.0`，但显式的 `0`、负数或非有限值会拒绝加载，避免把无效标定静默当成真实尺寸。
- `PhysicalUnit`：物理单位；空值会回退到 `px`。
- `QualityProfile`：本次项目使用的检测质量门限；缺失时使用默认配置，写出时保留完整门限。
- `Items`：ROI 项数组。

### ROI 项约定

`Items` 中的单个对象对外仍保持扁平字段结构，以兼容历史文件；在代码内部已经映射到四组语义数据：

- `Common`：标签、颜色、线宽、可见性、锁定状态。
- `Geometry`：点位、中心、宽高、半径、角度等几何参数。
- `Measurement`：卡尺数量、搜索范围、采样宽度、最小梯度等测量参数。
- `Options`：闭合、多边形自由绘制、阈值、暗目标检测等行为参数。

## `.ivsession` 会话文件

`.ivsession` 是一个 JSON 文件，由 `ImageViewerSessionService` 生成。其概念结构如下：

```json
{
  "Version": 2,
  "SessionName": "sample-session",
  "SavedAtUtc": "2025-05-19T08:30:00+00:00",
  "ImagePath": "images/sample.png",
  "RoiDocument": {
    "Version": 1,
    "PixelSize": 0.5,
    "PhysicalUnit": "mm",
    "QualityProfile": {
      "MinimumDetectionConfidence": 0.05,
      "PassConfidence": 0.55,
      "PassResidualRms": 1.5
    },
    "Items": []
  },
  "Scale": 1.25,
  "TranslateX": 24.0,
  "TranslateY": -12.0
}
```

字段说明：

- `Version`：会话"信封"结构版本，当前为 `2`。读取时会校验：高于当前支持版本的文件会被拒绝加载；缺失或非正数视为早期文件，宽容接受。
  - `1`：ROI 载荷以转义字符串形式内嵌在 `RoiDocumentJson` 字段中（历史格式）。
  - `2`：ROI 载荷作为嵌套对象写在 `RoiDocument` 字段中（当前格式）。
- `SessionName`：默认使用文件名。
- `SavedAtUtc`：保存时间戳。
- `ImagePath`：图像路径，可为绝对路径，也可为相对会话文件所在目录的相对路径。
- `CoordinateSystem` 保存工件坐标系及其定位变换。`ReferenceDatumPoints` 和 `CurrentDatumPoints` 分别保存参考图像与当前图像的基准点，按选择顺序排列，每组最多两个。旧会话缺少这些可选字段时使用空值。
- `ImageSha256`：可选的底图文件 SHA-256（64 位十六进制）。它校验原文件字节，重新命名或移动不会改变校验值；重新编码即使看起来一样，也可能改变校验值。旧文件缺少此字段时仍可打开。
- `RoiDocument`：完整的 ROI 文档对象，结构与 `.json` ROI 文件一致（含自己的 `Version`）。
- `RoiDocument.QualityProfile`：项目实际使用的质量门限。缺失时使用兼容默认值；保存时会把当前项目门限写回，避免只记录算法结果而丢失判定依据。除检测置信度、有效比例、角度覆盖率和残差门限外，还包括体数据过曝、欠曝和低对比度直方图门限。
- `Scale`、`TranslateX`、`TranslateY`：保存当前视图缩放和平移状态。
- `Calibration`：可选的相机标定参数，包括主点、归一化半径、K1/K2/K3 径向系数、P1/P2 切向系数，以及可选的标定 RMS、不确定度、证书标识和标定时间。旧会话缺少扩展字段时仍按 K1/K2 兼容读取。

## `.ivrecipe` 测量配方

配方 `Version` 为 `1`。新保存的配方在根对象的 `QualityProfile` 中记录完整检测质量门限，结构与会话中的 `RoiDocument.QualityProfile` 一致。普通载入、基准对齐载入和另存配方都保留该配置。字段缺失或为 `null` 时沿用当前查看器设置；字段有值但门限不合法时拒绝载入。

## `.ivpkg` 项目包

`.ivpkg` 本质上是 ZIP 包，由 `ImageViewerProjectPackageService` 读写。包内至少包含：

```text
session.ivsession
assets/<original-image-file>
```

规则如下：

- `session.ivsession` 是必需项。
- 如果导出时图像路径存在且文件可访问，会把原始图像复制到 `assets/` 目录中。
- 没有底图的会话只包含会话数据。若已知原图的文件指纹，但导出时找不到原图或图像内容已变，导出会失败并保留已有的项目包。旧调用方只传入无效路径且未提供指纹时，包仍会省略图像引用。
- 会话文件中的 `ImagePath` 会改写为包内相对路径，例如 `assets/sample.png`。
- 加载 `.ivpkg` 时，资产会解压到本机缓存目录，再按相对路径解析图像。
- 包内只能有一个 `session.ivsession`，其余条目必须位于 `assets/` 下；重复条目、路径穿越、反斜杠路径和包外 `ImagePath` 会被拒绝。
- `session.ivsession` 解压后最多 `8 MiB`，条目名称最多 `512` 个字符。
- 导出会先写入同目录临时文件，成功完成后才替换目标包；取消或失败不会留下半成品目标文件。
- 为避免恶意或损坏归档耗尽资源，加载时最多接受 1,024 个条目，单个条目解压后不超过 256 MiB，全部条目解压后合计不超过 512 MiB。

缓存目录模式：

```text
%LocalAppData%/ImageViewer/PackageCache/pkg-<SHA-256>
```

内容完全相同的项目包共享一份解包数据。只有完整解压的条目才可复用；缓存超过目标容量时，优先删除最久未使用且没有被会话或自动恢复引用的条目。

从项目包另存为独立会话时，图像写入会话目录下的 `ImageViewer.assets/<SHA-256>.<扩展名>`，会话以相对路径引用该文件。同一目录下保存相同图像会复用文件。旧版引用解包缓存绝对路径的会话仍受保护；重新另存即可得到带独立图像的会话。

如果位图没有可用的文件路径，保存时会将支持的像素格式编码为 PNG（包括 Gray16）。独立会话沿用相同的相对资产路径，项目包则写入 `assets/image.png`。自动保存使用各快照自己的 `RecoveryImages` 子目录，并在会话提交成功后清理旧图像。会话版本保持不变。

## 向后兼容性

- ROI JSON 读取时同时支持 `Type` 对应插件类型键，以及旧格式中按 ROI 类型名匹配。
- ROI 项字段继续保持扁平 JSON 形态，以兼容既有导出文件。
- 未识别的 ROI 插件项在加载时跳过，而不是导致整份文件失败；其原始载荷会被原样保留并随下次保存写回，因此缺少插件不再等于标注丢失（加载时会给出状态栏提示）。
- 会话文件的 ROI 载荷读取时同时接受嵌套对象（当前格式）与转义字符串（历史格式），字段名同时接受 `RoiDocument` 与 `RoiDocumentJson`；写出时统一使用嵌套对象。
- 版本号只提供单向保护：新程序可以读旧文件，但旧程序读不了新文件（旧程序遇到缺失的 `RoiDocumentJson` 会报解析错误）。因此不要用降级后的程序去打开升级后保存的会话。
