# 离线读图（image.load）

日期：2026-10-26 · 节点数 185 → 186

## 为什么加它

离线验证、教学、样本跑批此前只有两条路：把图片目录注册成 `FileCameraDevice`（必须先有"相机"），
或者依赖宿主注入的 `__replayInputPath`。节点层没有"读一张本地图"的能力，做离线和做现场是两套心智模型。

`image.load` 补这一块，但**不是**一个任意读盘的后门。

## 安全边界（最关键的一条）

读盘一律经过宿主的 `IReplayInputResolver`，与 `image.acquire` 的回放路径共用同一套授权根：

- 数据根（`VisionStudioDataRoot`）
- 媒体库根（`MediaLibrary:RootPath`）
- 显式配置的 `VisionStudio:ReplayInputRoots`（分号分隔的绝对路径）

三条拒绝路径都带可操作的提示：

| 情况 | 行为 |
|---|---|
| 没有注入授权器（引擎裸跑/单元测试） | 失败并说明"离线读图是宿主能力，请配置 ReplayInputRoots" |
| 路径不在授权根内 | 失败并点名 ReplayInputRoots |
| 文件不存在 / 扩展名不是图像 | 分别报 `FileNotFoundException` / 明确列出支持的扩展名 |

理由写在 `IReplayInputResolver` 的注释里，也写进了节点描述：**流程文档是可导入的 JSON**。
如果节点能读任意路径，一份恶意流程就能读本机文件，再借 `output.httpRequest` / `output.exportResult` 带出去。

## 两种模式

| 模式 | 行为 |
|---|---|
| 文件 | 读单张；`sequence` 必须为 0 |
| 目录 | 枚举 `png/bmp/jpg/jpeg/tif/tiff`，按**自然排序**取第 `sequence` 帧 |

自然排序是刻意设计：`img2` 排在 `img10` 之前。字典序会得到 `img1, img10, img2`，
同一条配方两次跑批拿到不同次序的样本——离线验证就失去意义了。

`sequence` 通常接 `flow.repeat.index`：

```
flow.repeat(count=样本数) ─┬─ index ──────────────→ image.load.sequence
                           └─ body → image.load ─→ image → 检测节点 → value.statistics
```

越界策略：`fail`（默认，报出总帧数）或 `wrap`（按帧数取模）。

## 解码

- `colorMode`：`preserve`（默认，`ImreadModes.Unchanged`，16 位仍是 16 位）/ `gray` / `color`。
- 输出 `bitDepth` 与 `channels`，下游可以据此拒收"分辨率/位深不对"的样本。
- `maxMegabytes`（默认 64，上限 4096）在**解码前**按文件大小拦截，路径写错时不会把内存打满。
- 解不开的文件（截断/非图像）报 `InvalidDataException` 并带上文件名。

## 输出端口

`image`、`width`、`height`、`channels`、`bitDepth`、`fileName`、`resolvedPath`、`sequence`、`frameCount`、`diagnostics`。

`resolvedPath` 是授权后的绝对路径，便于在报告里留证；`frameCount` 让配方能自己判断"样本是否跑完"。

## 验证

`ImageLoadNodeTests` 10 项：

| 用例 | 覆盖点 |
|---|---|
| `FileMode_ReadsSingleImageAndReportsMetadata` | 单文件读取、宽高/帧数/序号/位深 |
| `FolderMode_UsesNaturalOrderSoBatchesAreReproducible` | `img1, img2, img10` 的自然排序（字典序会失败） |
| `SequencePortOverridesIndexAndSupportsWrap` | 端口覆盖参数 + wrap 取模 |
| `OutOfRangeIndexFailsWithFrameCount` | 越界报出总帧数 |
| `ColorModeGrayscaleProducesSingleChannel` | 灰度转单通道 |
| `SixteenBitImagesKeepTheirDepth` | 16 位 PNG 保持位深 16 |
| `OversizeFileIsRejectedBeforeDecoding` | 体积上限在解码前生效 |
| `UnauthorizedPathIsRejectedWithActionableMessage` | 根目录外路径被拒且提示 ReplayInputRoots |
| `MissingHostResolverRejectsReadsInsteadOfFallingBackToArbitraryPaths` | 无授权器时拒绝，不退化成任意读盘 |
| `NonImageExtensionAndUndecodableFileFail` | 非图像扩展名与坏文件分别报错 |

## 与 FileCameraDevice 的分工

| 场景 | 选择 |
|---|---|
| 连续"拍摄"、需要相机语义（序列号/触发/帧组） | `FileCameraDevice` + `image.acquire` |
| 单张/批量样本、离线验证与教学、按帧号精确取样 | `image.load` |
| 生产现场 | 真实相机 |

## 已知限制

- 不支持多页 TIFF 的页选择（只读第一页）、不支持 RAW/DNG、视频帧。
- 不做去畸变/色彩校正：那些是下游 `image.undistort` / `image.colorConvert` 的职责。
- 目录枚举每次调用都会重新列目录。大目录（上万文件）会重复付 I/O 成本；需要高频取样时建议缩小目录或改用相机设备。
