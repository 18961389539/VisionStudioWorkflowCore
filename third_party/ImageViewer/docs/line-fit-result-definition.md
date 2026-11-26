# 直线拟合结果定义

直线卡尺的几何结果统一使用源图像像素中心坐标：整数坐标表示像素采样中心，坐标允许亚像素值。当前定义与显示缩放、WPF 像素对齐和导出单位无关。

## 两类线段

- `FittedLine`、`FittedEdge1`、`FittedEdge2`：参与拟合的边缘点集合的有限支撑段。它们只表示拟合点覆盖的起止范围。
- 单边缘的 `DetectedP1/DetectedP2`：将原始 ROI 两个端点正交投影到拟合无限直线后的结果段。
- 双边缘的 `DetectedP1/DetectedP2`：测量中心线分别与两条拟合无限直线的交点，结果段长度就是中心线方向的测量宽度。

拟合时从轮廓首尾各裁剪指定数量的点。裁剪点只影响拟合，不影响输出端点；输出端点始终是原始轮廓首点和末点投影到拟合无限直线后的结果。裁剪数量为 0 时使用全部点。

## 无限拟合直线

`LineFitGeometry` 由以下值组成：

```text
Normal.X * X + Normal.Y * Y = Offset
```

其中 `Normal` 是单位法向量，方向固定为 X 分量优先为正；接近垂直时 Y 分量为正。这样端点顺序改变不会改变同一条无向直线的偏移量符号。`AngleDegrees` 是无向切线角，归一化到 `[-90, 90)`。

`ResidualRms` 和 `ResidualMax` 始终表示参与拟合点到该无限直线的正交距离，单位为源图像像素；它们不包含显示缩放，也不包含像素尺寸换算。

## 代码入口

- `ImageViewerControl/Models/LineFitGeometry.cs`：无限直线、投影和距离计算。
- `LineCaliperDetectionResult.FittedGeometry`：单边缘拟合直线。
- `LineMeasureGradientDetectionResult.FittedEdge1Geometry` / `FittedEdge2Geometry`：双边缘两条拟合直线。
- `FittingAlgorithmMetadata.LineGeometry`：导出元数据中的结果定义版本。

直线拟合使用 `LineFitMode` 配置，支持 regression、huber、tukey、drop 和 gauss；边缘采样统一由托管卡尺算法（`CaliperAlgorithm`）完成。

## 参数说明

| 参数 | 作用 | 托管算法传值 |
| --- | --- | --- |
| `CaliperLineFitMode` | 选择拟合权重模型。`Regression` 使用全部点的普通回归，其余模式启用稳健迭代。 | `LineFitMode` 枚举 |
| `CaliperFitClippingEndPoints` | 从有序边缘轮廓首尾各去掉的点数，只影响拟合输入，不改变输出端点投影范围。 | `clippingEndPoints` |
| `CaliperOutlierThreshold` | 卡尺采样点进入拟合前的残差筛选阈值；设为 `0` 时使用自适应 MAD 阈值。 | ImageViewer 采样层筛选 |
| `CaliperEdgeSigma` | 卡尺剖面的一维高斯平滑 sigma，单位为像素。 | `sigma` |
| `CaliperMinimumGradient` | 接受边缘的最小梯度幅值。 | `minimumGradient` |

迭代次数与截断因子由拟合模式统一映射：`Regression` 对全部点做普通最小二乘回归；稳健模式使用 3 次迭代，Huber 的截断因子为 1.0，其余模式为 2.0。调用方不再维护第二套拟合参数或私有拟合实现。
