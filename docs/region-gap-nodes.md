# 分割·轮廓·特征补全（7 个节点，232 → 239）

来源：`node-gap-analysis.md` 第十节。这一域的判断是"算法不缺、形态缺"：`regionOp` 77 个算子 +
`contourOp` 32 个 + MSER/分水岭 + 8 个特征提取算子已经把 HALCON 的 XLD/Region 面覆盖得差不多，
所以这批**几乎不引入新算法**，只做格式转换与一个凸性度量。

| 节点 | 类型 | 说明 |
|---|---|---|
| `roi.fromGeometry` | P0 | 矩形 / 圆 / 多边形 → 运行时 ROI |
| `roi.fromDetection` | P0 | 检测 → 运行时 ROI（逐目标测量的关键一环） |
| `region.fromContour` | P1 | 轮廓填充成区域 |
| `region.toMask` | P1 | 区域渲染成 8 位掩膜 |
| `contour.convexity` | P1 | 凸性 / 最深凹陷 / 缺陷个数 |
| `feature.superpixel` | P2 | SLIC 超像素（纹理件预分割） |
| `contour.matchXld` | P2 | 轮廓形状匹配（旋转/缩放不变） |

## 一、为什么这四个"形态"比算法重要

**Roi 只出不进。** 补之前产出 `Roi` 的只有 `roi.transform` 与 `image.crop.croppedRoi` 两个，
而消费 Roi 的有 15 个节点（全部卡尺、`match.template`、`contour.extract`、`feature.edgeSubpixel`、
`region.fromRoi`…）。Roi 只能来自画布参数或跟随定位，**定位结果变不成搜索区**。

**Region ⇄ 掩膜图单向。** `region.fromMask`、`contour.fromMask`、`inference.segmentation` 都消费
mask 图像，却没有任何节点产出 mask——区域没法喂推理、没法导出。

**任意轮廓回不了区域。** `jlvision.regionOp` 只有 11 个标量参数（size/count/row/column/phi/
length1/length2/min/max/percent/mode），`genRegionPolygon` / `genRegionPoints` 收不到点集；
`contour.fromMask` 是反方向。于是拟合出来的轮廓算不了面积、IoU、凸性缺陷，也做不了形态学。

## 二、一个实用的链条

`roi.fromDetection` 的真正价值是把逐目标测量在 `flow.forEach` 泛化之前先跑通：

```
region.toDetections → flow.forEach → roi.fromDetection → region.fromRoi → measure.regionIntensity
```

`flow.forEach` 只吃 `DetectionList`，而检测本身带中心/外接框/拟合圆，正好够构造 ROI。
这条链让"每个缺陷单独测灰度"从"复制 N 段"变成"一条循环"。

## 三、实现要点

**旋转矩形按轴对齐外接框处理。** ROI 没有角度概念，`roi.fromGeometry` 遇到带 `AngleDeg` 的矩形
输入时取外接框（`w·|cos|+h·|sin|`），宁可放大不漏检；锚点用 `rectangleAnchor` 显式声明，
因为目录里 `VisionRectangle2D` 的 X/Y 在匹配/ArUco 里是左上角、在旋转矩形里是中心。

**轮廓光栅化用 `Cv2.FillPoly` + `region.fromMask` 的同一套连通域行程。** 复用
`RegionFromMaskNode.BuildComponents`（8 邻域），保证 `region.properties` / `region.filter`
看到的对象数与 `region.fromMask` 完全一致。画布优先取接入图像的尺寸，否则按轮廓包围盒外扩——
坐标原点始终是 (0,0)，区域坐标不会因为裁剪而漂移。

**`region.toMask` 按行程整行填充，不逐像素。** 先把 `step × height` 的缓冲一次算好再
`Marshal.Copy` 进 `Mat.Data`，10 万行程量级也不会掉到秒级。

**凸性缺陷是"到凸包边界的距离"。** 凹进去的那段到凸包边界的距离 > 0，连续超阈值的样本归为同一个
缺陷，取段内最大值作为深度。这比 `convexity_xld` 的缺陷点对更直观，也更容易设阈值（单位是像素）。

**轮廓匹配用半径签名 + 切线角。** 半径签名（质心距 / 平均半径）对平移、旋转、缩放都不变，
所以形状相似度用它算；但**旋转角必须从切线角差反解**——弧长参数化本身随形状一起转，
半径签名根本不含旋转信息（第一版就是这么错的：转角恒为 0）。测试里把房子轮廓旋转 30°、放大 1.5 倍，
现在能同时收回 score > 0.95、rotation ≈ 30°、scale ≈ 1.5。代价是镜像与旋转不可区分。

## 四、验证

- 引擎 2040（本批 9 条：ROI 锚点与旋转外接框、检测居中、区域面积 380~460、region→mask→region 面积不变、缺口深度 10 与 defects=1、超像素像素不丢、旋转 30°/缩放 1.5 可回收、异形掉分）。
- API 447（本批 2 条：7 节点注册 + pipeline 资格 + 无副作用；`Roi`/`Region`/`Image`/`Double` 端口类型逐条锁死）。
- 前端 1131（本批 7 个枚举标签已补中文）。
- `catalog --check`：239 节点，hash `c6b0df80…`。

## 五、仍未做

- **逐对象迭代**（第八节 S3）：`flow.forEach` 仍只吃 `DetectionList`；`roi.fromDetection` 只是绕行，
  真要"每个区域做形态学再测量"还得等引擎侧泛化。
- `region.superpixel` 的 ROI 版本：现在整幅图一次分割，局部超大图建议先 `image.crop`。
