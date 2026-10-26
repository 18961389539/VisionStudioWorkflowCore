# 通用列表类型与统计（NumberList / PointList）

日期：2026-10-26 · 节点数 180 → 185

## 为什么先补类型

缺口分析里"没有统计节点"的真正原因不是缺算法，而是**类型系统里只有 `DetectionList` 一种集合**：
多个测量值无法在图上传递，均值/极差/CP 这类节点就算写出来也没有端口可接。

所以这一轮先补两个通用类型，再补四个配套节点：

| 类型 | 载荷 | 用途 |
|---|---|---|
| `NumberList` | `IReadOnlyList<double>` | 测量值序列 → 统计、趋势、报表 |
| `PointList` | `IReadOnlyList<VisionPoint2D>` | 二维点集 → 拟合、轮廓重建 |

兼容性规则保持严格：只与自身或 `Any` 兼容，**不做隐式加宽**（`Double → NumberList` 不允许），
避免"把单个测量值当成长度 1 的列表"这类静默错误。既有的 `Integer → Double` 规则不受影响。

## 配套节点

| 节点 | 输入 → 输出 | 说明 |
|---|---|---|
| `value.numberList` | a..h（8 个可选 Double）→ NumberList | 汇合多个标量测量值 |
| `collection.detectionNumbers` | DetectionList → NumberList | 取一列（x/y/宽高/面积/周长/圆度/置信度/类别/序号） |
| `value.statistics` | NumberList → count/mean/std/min/max/median/range/sum（+cp/cpk/pass） | 批次统计与过程能力 |
| `collection.detectionPoints` | DetectionList → PointList | 取各目标中心 |
| `geometry.fitFromPoints` | PointList → line / circle + residualPx | 点集拟合 |

## 统计口径（写清楚，否则"看起来对"）

- 标准差默认**样本**口径（÷ n−1），可切总体口径（÷ n）。
- 偶数样本的中位数取中间两值平均。
- `cp = (USL−LSL)/(6σ)`、`cpk = min(USL−mean, mean−LSL)/(3σ)`，σ 与上面选择的偏差口径一致。
- **只有启用规格限时才输出 `cp`/`cpk`/`pass`**，避免下游把"没有规格"读成"合格"。
- 启用限值但 σ=0 时明确失败，不返回无穷大（下游无法判断）。
- 空列表、非有限值一律失败。

## 两个刻意的"不补 0"决定

1. `value.numberList` 只收集**已接线**的输入。未接线补 0 会让均值、极差、CP/CPK 静默失真 —— 这是统计类节点最危险的错误。
2. `collection.detectionNumbers` 遇到缺失的可选列（`confidence`/`classId` 为 null）计入 `skipped`，不参与统计。

两项都可以在诊断里看到实际参与的条数。

## 拟合算法口径

- **直线**：对质心去均值后取协方差主方向（总体最小二乘）。相比"以 x 为自变量"的最小二乘，竖直方向的线同样稳定。
  返回沿用主方向、覆盖全部点投影的两个端点，可直接当下游线段用。
- **圆**：Kåsa 代数拟合求圆心，再用平均半径收尾；共线点会得到退化矩阵 → 明确失败，而不是随机圆心。
- `residualPx` 是各点到拟合几何的**正交距离均方根**，两种模式都输出，作为拟合质量参考（不是精度承诺）。

## 典型接线：批次置信度统计

```
inference.detection.detections ─ collection.detectionNumbers(field=confidence) ─ value.statistics(enableLimits, lsl, usl)
                                                                                  ├─ mean / std / cpk → flow.log 或 device.writeTag
                                                                                  └─ pass → flow.if → 分支
```

点集拟合：

```
inference.detection.detections ─ collection.detectionPoints ─ geometry.fitFromPoints(operation=circle) ─ circle → measure.caliperCircle 的参考圆
```

## 验证

`ListTypeNodesTests` 13 项：

| 用例 | 覆盖点 |
|---|---|
| `NumberAndPointListAreDistinctTypes` | 列表类型不与标量互转；`Integer→Double` 不受影响 |
| `NumberList_CollectsConnectedInputsInPortOrder` | 按端口顺序收集，未接线跳过 |
| `NumberList_RequiresAtLeastOneInputAndRejectsNonFinite` | 全未接线失败；NaN 失败 |
| `DetectionNumbers_ExtractsColumnAndCountsMissingValues` | 提取列；缺失计入 skipped |
| `DetectionNumbers_RejectsUnknownField` | 未知列名失败 |
| `Statistics_UsesDocumentedSampleSemantics` | 均值/样本 σ/中位数/极差/合计；未启用限值时不输出 cp |
| `Statistics_ComputesCpAndCpkWhenLimitsEnabled` | cp/cpk 数值与 pass |
| `Statistics_RejectsEmptyNonFiniteAndZeroVarianceWithLimits` | 空列表、Infinity、σ=0、LSL≥USL 全部失败 |
| `DetectionPoints_UsesDetectionCentres` | 中心点顺序与坐标 |
| `FitFromPoints_LineUsesTotalLeastSquares` | 斜率 2 的共线点：角度、残差 0 |
| `FitFromPoints_CircleRecoversKnownCentreAndRadius` | 已知圆心半径恢复到 1e-6 |
| `FitFromPoints_RejectsDegenerateInputs` | 点数不足、共线圆拟合失败 |
| `Catalog_DeclaresListTypesAndNodes` | 五个节点的端口类型声明 |

## 已知限制

- 列表是**只读快照**，没有 append 节点：需要跨轮累积时，目前靠 `flow.repeat` 内重新组装（每轮最多 8 路），
  或先从检测列表批量提取。真正的累加器需要 run 级状态，留到后续。
- `PointList` 目前只有检测中心一个来源，没有"从轮廓取点"或"手绘点集"节点。
- 统计只做一维；没有直方图分箱、趋势图或 SPC 判异规则（Western Electric 等）。
