# 手眼标定（2D / 3D）实现与运维说明

2026-11-12。对应 `calibration.handEye2D`、`calibration.handEye3D` 两个节点与两套标定向导。

## 结论先说

| 需求 | 用哪个 | 求解器 | 输入 |
| --- | --- | --- | --- |
| 平面搬运/贴装，只要 XY + 旋转（SE(2)） | `calibration.handEye2D` | 托管闭合最小二乘（Gauss-Newton + 可选 Tukey） | 机器人 TCP 与视觉位姿对（x, y, thetaDeg） |
| 六轴抓取/3D 视觉，需要完整 SE(3) | `calibration.handEye3D` | OpenCV `Cv2.CalibrateHandEye`（TSAI/PARK/HORAUD/ANDREFF/DANIILIDIS） | 机器人 `robotToBase` 与标定板 `targetToCamera` 的 4×4 齐次矩阵 |

2D 节点**不**做三维刚体解算（`summary.scope = "planar only; no 3D hand-eye"`），3D 节点**不**接受 2D 位姿。两者都有 `mode`：`eyeInHand`（相机随工具，结果 Camera→RobotTool）与 `eyeToHand`（相机固定，结果 Camera→RobotBase）。

## 2D：闭合残差与三个未知数

未知量是 `X = (x, y, θ)`。每个样本的闭合位姿为 `robot_i · X · vision_i`；理想情况下所有闭合位姿相同，于是残差定义为相对**圆周均值**的偏差：

```
r_ix = closure_i.x - meanX
r_iy = closure_i.y - meanY
r_iθ = Wrap(closure_i.θ - meanAngle) · angularWeight
```

两条必须知道的性质：

1. **`angularWeight` 只影响排序与门限，不影响解。** 所有闭合角都随 `X.θ` 同量平移，圆周均值也随之平移，角度残差整块与 `X` 无关（对三个未知数的雅可比恒为 0）。它的实际作用是给 `maxAngleErrorDeg`、`worstSampleIndex`、`outlierSampleIndex` 提供“平移等效单位”。历史文档曾写“会改变平移与旋转对最终拟合的相对影响”，属错误描述，已改写（`catalog.ts` / 目录描述）。
2. **θ 的可观测性来自平移通道。** 因为 `closure_i = R_robot(t_X + R_X t_vision) + t_robot`，只有当视觉平移 `t_vision` 非零时 `θ` 才被约束。纯旋转、相机中心对准工件的样本集会让 `EnsureRank` 报 `non-unique`。

### 质量指标与门限

| 输出 | 含义 | 备注 |
| --- | --- | --- |
| `translationRms` / `angleRmsDeg` | 闭合残差 RMS | 角度由 `angularWeight` 还原为度 |
| `maxTranslationError` / `maxAngleErrorDeg` | 最差样本残差 | 对应两个门限参数，0 = 关闭 |
| `worstSampleIndex` | `hypot(平移误差, 角度误差×weight)` 最大者 | **前端“剔除最差样本”按此索引**（与后端一致） |
| `worstTranslationSampleIndex` / `worstAngleSampleIndex` | 分项最差 | 仅诊断 |
| `outlierSampleIndex` | 综合残差 > 3×中位数且 > 跨度相对下限 | 仅报告，不自动剔除 |
| `conditionNumber` | 观测雅可比**列归一化后**的 `cond(J)` | 见下 |
| `stdDevX` / `stdDevY` / `angleStdDevDeg` | `σ²(JᵀJ)⁻¹` 的对角线 | Tukey 模式下 `σ²` 用加权残差 |
| `converged` / `iterations` | 阻尼高斯-牛顿收敛情况 | 最多 100 轮 |
| `crossCheckTranslationDelta` / `crossCheckAngleDeltaDeg` | 相对运动 `AX=XB` 的独立代数交叉验证 | 仅报告，可作置信信号 |

**条件数的口径**：`conditionNumber` 是 `cond(JᵀJ)` 的平方根，并且先对 `J` 的列做单位归一化。原因有两个：`cond(JᵀJ) = cond(J)²`，直接展示会把病态程度放大成平方；三列量纲不同（x/y 是长度、θ 列是长度/弧度），不归一化时同一个位姿集换个单位就会得到不同数值。`maxConditionNumber` 门限（0 = 关闭）建议按现场基线设成 10 倍余量，量级参考：良好位姿集常在 1e1–1e2。

### `robustLoss`

`tukey` 按闭合残差的 3×中位数截断做 IRLS 重加权，单个坏样本权重会被压到 0。注意两点：逐样本残差与门限仍按**未加权**报告（便于定位坏样本）；`σ²` 与标准差改用最终权重计算，否则被剔除的离群值会反过来污染不确定度。

## 3D：OpenCV 求解与影响度剔除

- 至少 6 组样本，且机器人运动必须包含**绕两个非平行轴的 ≥3° 旋转**（`ValidateMotion`）。纯平移或单轴旋转会在数学上不可解，节点直接报错。
- 矩阵必须是有限、行主序、末行 `[0,0,0,1]`、旋转正交且 `det=+1` 的 4×4。缺失字段不会再“静默当成零矩阵”（旧实现的坑），现在会因 `Length != 16` 报错。
- `outlierRejection=iterative` 用**留一影响度**：每轮分别去掉一个样本重拟合，只有 RMS 低于 `minRmsImprovement × 当前 RMS` 才接受该次剔除，最多剔除 `maxRejections` 个，且不降到 6 组以下。代价是 `O(maxRejections × 样本数)` 次 `CalibrateHandEye`——1000 组样本、默认 20 次上限约 2 万次求解，这也是加 `maxRejections` 的原因（旧实现无上限，最坏约 50 万次，会撞 `__timeoutMs`）。
- 残差与门限只按**保留集**计算，但 `sampleResidualsJson` 会给出全部原始样本（带 `rejected` 标记与 `rejectedSampleIndicesJson`），索引稳定可回溯。
- `crossCheckMethod` 用第二个求解器在同一保留集上重解，报告变换差值；与主方法相同或 `NONE` 时跳过，失败不阻塞主解。

### 单位自检

2D 在 `sourceUnit ≠ unit` 且 `visionScale = 1` 时给出 `unitWarning`。3D 没有 `sourceUnit`，改用比例自检：`maxTranslationError / robotTranslationSpan > 10%` 时给出 `unitWarning` 并置 `warningCount=1`，提示“机器人 mm 与视觉 m 混用”或“标定板非刚性”。两者都是**警告不是失败**，需要门禁时在流程里用 `failOnWarning`。

## 资产与版本

- 节点参数 `handEyeAssetId` / `handEyeAssetVersion`（3D 为 `handEye3DAssetId/Version`）必须成对设置；运行时、发布前与产线依赖检查都会用 `RequireModelMatchAsync` 复核：把节点产出的 `calibrationJson` 反序列化、重新算 `model_hash`，与不可变版本比对，不一致即失败。
- 版本快照由 `model_json + source_document_json + summary_json` 三者哈希成 `snapshot_hash`，读取时重新校验，任何一处被改动都会报 `failed snapshot integrity validation`。
- 证据链：`source_document_json` 是向导会话（含全部位姿对与求解设置），`summary_json` 是当次求解摘要（样本数、剔除索引、RMS、条件数等）。也就是说“样本从哪来、用的什么门槛”随版本一起不可变保存；`calibrationJson` 本身刻意保持精简，不重复塞入样本统计。
- 发布/回滚会写 `*_publication_history`，`published` 版本号独立于 `latest`。

## 向导操作顺序

1. 配置安装方式、坐标系与单位（px→mm 场景先填 `visionScale`）。
2. 录入位姿对：CSV（2D 6 列、3D 32 列）、JSON 粘贴，或从设备逐点采集。列表上方会实时给出覆盖率/跨度/重复位姿预检。
3. 求解：先看预检与门限，再看逐样本残差表。红色“最差”与橙色“离群”是两个不同口径——前者按综合残差，后者是 3×中位数规则。
4. 保存会话（自动落盘，进程重启后可恢复），再“保存为资产版本”→ 发布。

## 本轮踩坑记录

1. **`angularWeight` 的“权重”是排序权重**，不是拟合权重；按拟合权重去理解会得出“调它没用/调它就变”两种相反的错误结论。
2. **条件数别直接展示 `cond(JᵀJ)`**：平方放大 + 单位相关，同一个位姿集换个单位就变。
3. **`VisionPose2D`/矩阵缺字段**：`readonly record struct` 缺字段会反序列化成 0 而不是报错，必须在节点入口按长度/有限性显式校验。
4. **`σ²` 与鲁棒权重**：用未加权残差估 `σ²`，被 Tukey 压到 0 的离群点会抬高所有标准差。
5. **迭代剔除法是二次代价**：`CalibrateHandEye` 本身便宜，但每轮重扫全部样本，样本上千时必须设上限。
6. **会话落盘要原子替换**：`File.Copy(tmp, path, overwrite)` + `File.Delete(tmp)` 不是原子操作，崩溃窗口里可能读到半个文件；改为 `File.Move(tmp, path, overwrite: true)`。

## 相关实现

| 关注点 | 位置 |
| --- | --- |
| 2D 求解与指标 | `backend/src/VisionStudio.Engine/Nodes/CalibrationAdvancedNodes.cs` |
| 3D 求解与剔除 | `backend/src/VisionStudio.Engine/Nodes/HandEye3DCalibrationNode.cs` |
| 参数范围（唯一来源） | `backend/src/VisionStudio.Engine/Nodes/HandEyeSettings.cs` |
| 资产存储（通用基类） | `backend/src/VisionStudio.Api/Calibration/CalibrationAssetStoreCore.cs` |
| 会话存储（原子写） | `backend/src/VisionStudio.Api/Calibration/WizardSessionStore.cs` |
| 向导服务 | `CalibrationWizardService.HandEye.cs` / `.HandEye3D.cs` |
| 前端向导 | `CalibrationHandEyeWizard.tsx` / `CalibrationHandEye3DWizard.tsx` + `handEyeWizardShared.ts` |
