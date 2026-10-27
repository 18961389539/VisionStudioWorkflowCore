# 测量与几何专项补全（13 个节点，212 → 225）

来源：`node-gap-analysis.md` 第八节复核结论。本批只做**单帧 2D 影像内可给出可追溯数值**的能力，
不引入新的端口类型，也不碰 3D；全部声明 `Actions: []`，因此在 pipeline 里执行。

| 节点 | 分类 | 一句话 |
|---|---|---|
| `contour.toPoints` | Contour | 轮廓 → `PointList`，补上断链 |
| `measure.arcFit` | Measurement | 弧段拟合：半径 / 圆心角 / 弧长 / 弦长 |
| `measure.mzCircle` | Measurement | 最小区域 / 最小外接 / 最大内切圆 |
| `measure.curvature` | Measurement | 沿轮廓的曲率半径序列与圆角定位 |
| `measure.profileFilter` | Measurement | ISO 16610-21 高斯滤波，拆波纹度 / 残余 |
| `measure.roughness` | Measurement | Ra / Rq / Rz / Rp / Rv / Rmax |
| `measure.contourDistance` | Measurement | 两条任意轮廓的最近 / 最远距离 + 最近点对 |
| `measure.thread` | Measurement | 影像法螺纹：齿距 / 大径 / 小径 / 牙型角 |
| `measure.gear` | Measurement | 直齿轮几何（纯算量，无输入端口） |
| `tolerance.stackup` | Measurement | 尺寸链公差叠加（最坏情况 + RSS） |
| `tolerance.drf` | Measurement | 双基准基准系 A\|B（交点原点 + 正交误差） |
| `geometry.lineConstruct` | Geometry | 垂线 / 平行偏置 / 中线 / 角平分线 / 切线 |
| `coordinate.transformAffine` | Coordinate | 非刚体仿射（缩放 + 剪切 + 旋转 + 平移） |

## 一、断链修复（比单个节点更重要）

修之前：`PointList` 只有 `collection.detectionPoints`（检测框中心）与 `match.feature.inlierPoints`
两个产出源，而 `tolerance.profile`、`tolerance.runout`、`geometry.fitFromPoints` 只吃 `PointList`——
**实测边缘根本进不了轮廓度/跳动**，上一批的 GD&T 节点只能喂检测框中心。

- `contour.toPoints`：`arcLength`（等弧长，剖面类下游要这种）、`uniform`（按序号抽稀，不插值）、
  `everyPoint`（原样回放）。预算超限直接失败，不静默截断。
- `contour.fromPoints` 增加 `pointList` 端口（端口优先于 JSON 参数），补上反方向。

## 二、刻意做出的算法选择

**轴向等距重采样，不是等弧长。** 粗糙度/波纹度的截止波长是**横向长度**单位，只有 u 等距才能直接卷积。
初版对剖面用了等弧长采样，陡坡处 u 间距被拉大，同一个波长被摊平，实测 Ra 恰好掉一半
（1.26 → 0.63）——这是测试断出来的，不是推出来的。现在 `ProfileMath.AxialProfile` 先投影到
最小二乘主轴，再在 u 上等距插值 z。

**高斯核按 ISO 16610-21 归一。** `s(x)=1/(αλc)·exp(-π(x/(αλc))²)`，α=√(ln2/π)，等价 σ≈0.1874λc，
核截断在 3σ。实测 λ=20 / 截止 8 的通过率为 0.896，与解析式一致。

**端部效应不藏。** `reflect` 保全长但两端被反射污染（长波残差幅值会偏大），`discard` 按实情裁掉两端。
所以幅值单独出 `residualPeak` / `wavinessPeak` / `residualRms` 三个标量端口——`PointList` 是像素坐标，
主轴带倾角，下游直接量 Y 会把线性趋势算进去。

**螺纹直径取原始 z，不用平滑值。** 平滑只用于找牙顶位置；用平滑后的峰算大径会系统偏小约 1%
（幅值 5 的牙顶被削到 4.9，大径 10 → 9.8）。

**MZ/MCCI/MIC 用确定性模式搜索。** 目标函数在 max/min 切换处有拐点，梯度法不适用；
从最小二乘圆心出发做 8 方向试探 + 步长减半，结果与点序无关，不收敛时**报错**而不是返回未精化的圆。

## 三、与既有节点的边界

- `measure.arcFit` ≠ `measure.fitCircle`：前者是弧段语义（弧长、圆心角、覆盖角门限），后者是整圆最小二乘。
- `measure.mzCircle` ≠ `measure.fitCircle`：前者给通止规口径的极限圆与区域宽度，后者只给残差；
  `jlvision.regionFeature` 的 smallest/inner circle 只对 Region 且是像素级，不能替代。
- `measure.contourDistance` ≠ `measure.pointLineDistance` / `measure.circleSpacing`：前者是任意折线对折线。
- `tolerance.drf` ≠ `tolerance.datum`：后者单基准（线 A + 点 B），前者双基准且原点取 A|B 交点。
- `geometry.lineConstruct` ≠ `jlvision.contourOp.genParallelContour`：后者偏置轮廓，产不出 `Line2D`。
- `coordinate.transformAffine` ≠ `calibration.planar`：后者是像素→mm 的单应（已覆盖各向异性），
  前者用于工程坐标系内部的缩放/剪切。

## 四、已知限制（必须在报告里说清）

- `measure.roughness` / `measure.profileFilter` 的输入必须是**已采样轮廓**，且名义上直的边：
  曲面上先给名义线，否则最小二乘主轴会把形状误差当成粗糙度。影像法结果只当趋势，不替代触针。
- `measure.thread` 是轮廓法（silhouette），看不到中径牙根，`pitchDiameter` 只是 (大径+小径)/2 的估计。
- `measure.gear` 只做直齿（螺旋角按 0），变位系数限 ±2。
- `tolerance.stackup` 的 RSS 按各环 ±3σ 独立假设，不处理相关环；增环/减环靠 `sign` 显式给出。
- 多轮廓逐个测量仍需复制节点：`flow.forEach` 只吃 `DetectionList`（gap 分析 S3，待引擎侧泛化）。

## 五、验证

- 引擎 2022（本批新增 18 条行为用例，全部断言落在解析解附近：Ra=2A/π、弧长=θR、最坏情况±Σ、行列式=sx·sy）。
- API 443（新增 2 条：13 个节点全量注册 + pipeline 资格 + 无副作用；`contour.toPoints → tolerance.profile` 端口连通）。
- 前端 1131（本地化用例要求**每个**枚举选项都有中文，本批 19 个选项已补）。
- `catalog --check` 通过：225 节点，hash `72dc1ffe…`。
