# 掩码 → 区域 / 轮廓（region.fromMask / contour.fromMask）

日期：2026-10-26 · 节点数 188 → 190

## 为什么加它们

`inference.segmentation` 的 `mask` 是 `Image`。此前要往下走只有两条别扭的路：

- `region.fromThreshold` 再阈值一次 —— 隐含"0/255 二值"的假设，语义晦涩；
- 或者绕 `region.toDetections` 再取点 —— 多一跳，且丢掉了孔洞信息。

两个节点直接吃掩码：

| 节点 | 输出 | 用途 |
|---|---|---|
| `region.fromMask` | Region + 二值回显 + count/area | 交给 region.properties / filter / boolean |
| `contour.fromMask` | Contour 列表 + count | 交给 contour.properties / 测量 / 拟合 |

## 共用一套阈值语义（这是重点）

两个节点用**完全相同**的 `threshold` / `invert` 参数与实现（共用同一个 `Binarize` 帮助函数），
所以同一张掩码在两条支路上的前景判定不会出现分歧。测试里有一条专门验证这点。

- `threshold`（默认 1）= "像素值 ≥ 该值属于区域"，1 等价于任意非零；
- `invert` 取反，用于"目标编码为暗色"或反相输出的模型。

## 连通域语义

`region.fromMask` 按 **8 邻域连通域**拆成多个 component，而不是整幅图一个 component。
原因：`region.properties` / `region.filter` 的连通域计数、面积分布都依赖这个划分；
如果合并成一整块，"两块缺陷"会变成"一块大缺陷"，下游按面积筛选全部失真。

行程（run）按标签累积，**不按扫描顺序**输出，而是按标签值排序 —— 同一张掩码两次运行得到相同的 component 顺序。

## 轮廓语义

| 参数 | 说明 |
|---|---|
| `retrieval` | `external` 只取外轮廓（测量常用）；`all` 保留孔洞层级，`ParentIndex` 指向**已输出**的父轮廓（被 minArea 滤掉的父轮廓会让子轮廓的 parent 回到 -1） |
| `approximation` | `simple` 压缩共线点；`none` 保留每个边界像素 |
| `minArea` | 滤掉分割噪点 |
| `maxContours` / `maxPoints` | 硬预算，**超限失败**而不是静默截断 |

## 一个容易踩的尺寸认知

OpenCV 的 `ContourArea` 沿**像素中心**描边：10×10 的实心块面积为 `(10−1)² = 81`，不是 100。
所以 `contour.fromMask` 的 `area` 会比 `region.fromMask` 的 `area`（前景像素数 = 100）略小。
两处面积口径不同是刻意的：区域给你们"多少个像素"，轮廓给你们 OpenCV/HALCON 系的几何面积。
需要严格一致时，用 `region.fromMask` 的 `area` 判定，用轮廓做几何拟合。

## 典型接线

```
inference.segmentation ─┬─ mask ─→ region.fromMask ─→ region.filter(minArea) ─→ region.properties
                        └─ mask ─→ contour.fromMask ─→ measure.fitCircle / measure.fitLine
```

## 验证

`MaskToVisionNodesTests` 8 项：

| 用例 | 覆盖点 |
|---|---|
| `RegionFromMaskSplitsConnectedComponents` | 两块方块 → 2 个 component、area=200、尺寸正确 |
| `RegionFromMaskInvertSwapsForegroundAndBackground` | 取反后 area 由 100 变 300 |
| `RegionFromMaskThresholdAboveContentYieldsEmptyRegion` | 阈值高于内容 → found=false |
| `ContourFromMaskExtractsOneContourPerBlob` | 每块一个轮廓、面积 81（像素中心口径）、ParentIndex=-1 |
| `ContourFromMaskMinAreaDropsSpeckle` | 2×2 噪点被 minArea 滤掉 |
| `ContourFromMaskKeepsHoleHierarchy` | 圆环：external=1 条，all=2 条且孔洞 ParentIndex=0 |
| `ContourFromMaskRejectsBudgetOverflowInsteadOfTruncating` | maxContours 超限报错 |
| `BothNodesShareThresholdSemantics` | 同一阈值下两个出口一致 |

Engine 全量 1916 项通过；前端 i18n 全量中文。

## 已知限制

- 掩码按 8 位灰度处理（内部 `ToGray8`）：高动态范围的软掩码（如 0.0–1.0 浮点概率图）会被截断成 0/255 两档；
  需要软阈值时先用 `image.threshold` 设定合适的位深与阈值。
- 没有 ROI 参数：掩码本身就是 ROI 语义的产物；需要局部时先 `image.crop`。
- `region.fromMask` 的 component 数量上限沿用 `VisionRegion` 的 10000 与行程总量 100 万预算，超限由区域构造直接拒绝。
