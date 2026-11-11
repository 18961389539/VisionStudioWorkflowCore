# 阈值分割节点选型与语义（2026-11-11）

阈值家族有 4 个会出现在工具箱里的节点，语义并不相同。这篇文档给选型表、统一语义与踩坑记录，
避免"想选 100–200 却拿单阈值节点硬凑"这类返工。

## 选型表

| 节点 | 中文名 | 语义 | 位深 | 输出 | 典型用途 |
|---|---|---|---|---|---|
| `image.threshold` | 阈值分割（单阈值） | 一条分界线：`> 阈值` 为前景 | 转 8 位（16U 默认 ÷257，可按 `inputBitDepth` 声明真实位深） | Image（0/`maxValue`） | 接形态学/连通域前先二值化 |
| `region.fromRange` | 范围分割（区间） | 闭区间 `[min, max]`，或三通道 6 个边界 | **保留原位深**，不缩放 | Region + 掩膜 | 只取 100–200 灰度、颜色区间、16 位图 |
| `jlvision.threshold` | 阈值与区域分割 | 12 个算子（单阈/双阈/滞后/局部/方差/动态/区域生长…） | 交 JLVision 原生 | Region + overlay | 需要滞后阈值、动态阈值等专用算子 |
| `image.dynamicThreshold` | 动态缺陷阈值 | 局部均值/中值 + 亮/暗极性 | 8 位 | Region + 掩膜 | 表面缺陷（局部对比度） |

决策三条：

1. 要「100–200 这样的区间」→ `region.fromRange`（矩形的上下限都填，闭区间两端都包含）。
2. 要一张 0/255 二值图喂形态学/轮廓 → `image.threshold`。
3. 光照不均、目标与背景灰度重叠 → 先 `image.threshold(mode=adaptive)` 或 `image.dynamicThreshold`，
   而不是反复调全局阈值。

## 统一语义（四个节点共有的约定）

- **ROI**：`image.threshold` 与 `region.fromRange` 都支持 canvas ROI。ROI 外清零；
  `image.threshold` 的 Otsu/自适应统计与前景占比分母**都按 ROI 计**，不是整图。
- **前景占比**：`foregroundRatio` = 非零像素 ÷ 统计域像素数。ROI 越小不再被稀释；
  手动阈值选中比例 ≤0.05% 或 ≥99.95% 时会带 `warningCount=1` 与 `suggestedThreshold`（Otsu 对照值）。
- **闭区间**：`fromRange` 的 min/max 两端都包含（`minGray=100, maxGray=200` 会选中 100 和 200）。
- **反选**：`fromRange` 的 `invert` 取补集，底依次是 ROI → region/domain → 整幅画布，不会越界。
- **count vs blobCount**：阈值区域把所有 run 放在**一个**区域对象里，所以 `count` 恒为 1；
  `blobCount` 才是八连通域个数，回答"检出几个目标"。要逐目标属性请再接 `region.connectedComponents`。
- **端口驱动**：`image.threshold.thresholdValue`、`region.fromRange.minGray/maxGray` 可接线，
  端口优先于参数。这样可以用上游统计（良品均值 ±3σ、直方图分位）逐批驱动阈值，
  而不必每次改参数。非 manual 模式接 `thresholdValue` 会直接报错，不静默忽略。

## 参数速查

`image.threshold`

| 参数 | 默认 | 说明 |
|---|---|---|
| `mode` | manual | manual / adaptive / otsu |
| `threshold` | 128 | 仅 manual；0–255；可被 `thresholdValue` 端口覆盖 |
| `inputBitDepth` | auto | auto=满量程 ÷257；10/12/14 位声明后按 255/(2^b−1) 映射 |
| `maxValue` | 255 | 前景赋值（1–255）；要 0/1 掩膜填 1 |
| `blockSize` / `c` / `method` | 31 / 5 / mean | 仅 adaptive |
| `invert` | false | 反转前景/背景 |
| `roi` | 空 | canvas ROI（隐藏参数，画布上编辑） |

`region.fromRange`

| 参数 | 默认 | 说明 |
|---|---|---|
| `mode` | gray | gray / color |
| `minGray` / `maxGray` | 128 / 255 | 闭区间；按输入真实位深校验，可被同名端口覆盖 |
| `space` / `min1..max3` | hsv | 仅 color 模式 |
| `invert` | false | 取区间之外的像素（补集不越过 ROI/域） |
| `emptyPolicy` | empty | 无像素通过时：输出空区域 / 直接失败 |
| `roi` | 空 | canvas ROI |

## 踩坑记录（2026-11-11 修复）

| 症状 | 根因 | 现状 |
|---|---|---|
| 手动阈值前景占比永远是 0 | `long / long` 整除（10000/20000 = 0） | 改双精度并显式转 double |
| ROI 框住目标，Otsu 阈值仍被 ROI 外高亮拉偏 | 阈值在整图直方图上求，ROI 只用于输出清零 | Otsu/自适应在 ROI 包围盒内统计（自适应外扩半窗口保邻域） |
| ROI 内全前景，占比却只有 0.25 | 分母是整图面积 | 分母改为 ROI 像素数 |
| 12 位相机阈值几乎不可用 | 16U 一律 ÷257，4095 只剩 15 级 | `inputBitDepth` 按真实位深映射 |
| `count` 明明是 1，画面里却有两个目标 | 阈值区域不自动拆分连通域 | 新增 `blobCount`，并在描述与本文档说明 |

## 验证

`backend/tests/VisionStudio.Engine.Tests` 下 4 个文件共 18 项，覆盖：ROI 统计口径、占比分母、
16 位位深映射、`maxValue`、端口覆盖参数、反选补集边界、ROI 限制、`blobCount` 与 `count` 的差异、
极端占比告警。目录快照 `CatalogExporter --check` 与 i18n 完整性用例同时锁定参数与文案。
