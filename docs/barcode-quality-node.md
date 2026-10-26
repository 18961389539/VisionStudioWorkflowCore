# 条码质量指标（recognition.barcodeQuality）

日期：2026-10-26 · 节点数 187 → 188

## 先把边界说清楚

完整的 ISO/IEC 15415（2D）/ 15416（1D）**认证评级**需要标准光源、规定测量光圈、校准的反射率基准，
并覆盖固定图形损伤、未使用纠错、网格非均匀性、解码等全部参数。这些条件普通工业图 + 一个画布节点给不了。

所以本节点只实现**三项可由普通工业图直接测得**的参数，并明确输出叫 `grade`（指示等级）：

| 参数 | 本节点做法 | ISO 15415 阈值 |
|---|---|---|
| 符号对比度 SC | ROI 内 `Rmax − Rmin`（灰度极值） | A≥70 B≥55 C≥40 D≥20 |
| 调制比 MOD（近似） | 各模块单元灰度偏离全局中值的程度，取最差单元 | A≥0.50 B≥0.40 C≥0.30 D≥0.20 |
| 轴向非均匀性 AN | 左右半区、上下半区均值差比的较大者 | A≤0.06 B≤0.08 C≤0.10 D≤0.12 |

总等级 = 三项中最差的一档；`decodeOk` 接了 `recognition.barcode` 的解码结果且为 false 时**直接判 F**
（ISO 里解不出来就是不合格，不看其他参数）。

**不做的事**：固定图形损伤、未使用纠错、网格非均匀性、校准光源、1D 码的 15416 全套。
诊断里固定写入 `"standard": "ISO/IEC 15415 partial (SC/MOD/AN) - indicator grade, not a certified report"`。

## 网格是精度前提

MOD 与 AN 都依赖"每个模块一个单元"的划分，所以三种网格模式：

| 模式 | 适用 |
|---|---|
| `modules` | 知道符号尺寸（如 20×20 DataMatrix）：行列直接给，最准 |
| `cellsize` | 只知道模块间距（px）：由 ROI 尺寸 ÷ 间距推导 |
| `whole` | 只算 SC：不输出 MOD/AN，**不给出无依据的数字** |

ROI 必须收紧到符号上：镜面反光会把 `Rmax` 抬高、SC 虚高；背景进入 ROI 会把单元均值拉平。
测试里有一条就专门验证"不接 ROI 时等级明显变差、接上 ROI 回到 A"。

## 端口

| 端口 | 说明 |
|---|---|
| `symbolContrast` | Rmax − Rmin（灰度级） |
| `modulation` | 可选；`whole` 模式不输出 |
| `axialNonUniformity` | 可选；`whole` 模式不输出 |
| `darkFraction` | 深色单元占比；用于发现网格配错（接近 0 或 1 说明划分偏了） |
| `grade` / `numericGrade` / `pass` | `enableGrade` 打开时输出；`minGrade` 默认 C |
| `diagnostics` | 含覆盖参数、标准声明、网格参数 |

## 典型接线

```
image.acquire / image.load ─┬─ image ─────────→ recognition.barcode ─ found ─┐
                            └─ image ─────────→ recognition.barcodeQuality ─┼─ pass → flow.if
                                                     ↑ roi（收窄到符号）      └ decodeOk
```

## 验证

`BarcodeQualityNodeTests` 9 项：

| 用例 | 覆盖点 |
|---|---|
| `HighContrastCheckerboardGradesA` | SC=200、MOD>0.9、AN<0.02、等级 A、深色占比≈0.5 |
| `LowContrastIsCappedByContrastGrade` | SC=50 → C；`minGrade=B` 时 pass=false |
| `AxialNonUniformityCatchesBrightnessGradient` | 左右亮度差 → AN>0.12 → F |
| `UndecodableSymbolGradesFImmediately` | 指标全优但 decodeOk=false → F |
| `WholeRoiModeEmitsContrastOnly` | 不输出 modulation/axial |
| `CellSizeModeDerivesGridFromPitch` | 由间距推导网格 |
| `RoiRestrictsTheGridToTheSymbol` | 无 ROI 等级变差，接 ROI 回 A |
| `FlatImageFailsWithExplicitMessage` | 无对比度失败并说明原因 |
| `RejectsInvalidGridConfiguration` | 未知网格模式、行列越界、非法最低等级 |

Engine 全量 1908 项通过；前端 i18n 全量中文（A/B/C/D 译成「A 级」等，避免 CJK 断言失败）。

## 已知限制

- MOD 是**模块均值偏离中值**的近似，不是 ISO 的每模块单元对比度定义；对低密度码（单元内纹理强）会偏乐观。
- 只有 SC/MOD/AN 三项，缺固定图形损伤与未使用纠错——**不能**用于出具合规报告。
- 1D 码可用 `cellsize` 模式近似（行数配 1），但 15416 的 PCS/边缘判定未实现。
- 依赖输入图为 8 位灰度（内部统一转 8 位）；高动态范围图像请先做归一化。
