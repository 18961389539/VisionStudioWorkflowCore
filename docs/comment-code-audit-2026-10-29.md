# 注释与代码行为一致性 · 穷尽式审查报告

- 日期：2026-10-29
- 范围：`backend/src` 全部 **386 个 `.cs`**（12 个项目），逐文件完整通读
- 方法：按目录切成 11 个互不重叠区段并行扫描 → **每条候选均由人工回读源码复核**，只保留能同时摘出"注释原文"与"矛盾代码"的条目
- 结果：**58 条确认问题**（🔴 4 / 🟡 23 / 🔵 31）；另有 1 条代理报告经复核属"文档精度"而非行为矛盾，已剔除
- 不含：`backend/tests`（234 个测试文件）、`frontend`、`docs`

## 覆盖清单

| 区段 | 文件数 | 确认条数 |
|---|---:|---:|
| Engine/Nodes（A–C） | 20 | 6 |
| Engine/Nodes（D–I） | 29 | 7 |
| Engine/Nodes（J–M） | 23 | 4 |
| Engine/Nodes（M–Z） | 26 | 7 |
| Engine 根目录（NodeCatalog/各 Catalog/迁移等） | 51 | 9 |
| Engine/Algorithms | 40 | 12 |
| Engine/Camera·Device·Robot·Runtime 等 | 48 | 5 |
| Api 根目录 | 51 | 4 |
| Api/Endpoints + Contracts | 33 | 2 |
| Api/Diagnostics·Hosting·Infrastructure·Media·Provenance·Security | 29 | 2 |
| Abstractions / Camera.Basler / Camera.Hikrobot / Scripting / SqliteSnapshot / 插件与工具 | 36 | 3 |
| **合计** | **386** | **58** |

---

## 🔴 高危（4 条）

| # | 位置 | 注释声称 | 实际行为 |
|---|---|---|---|
| 1 | `Engine/Nodes/BarcodeQualityNode.cs:16`（对照 :43-49） | 摘要表：符号对比度 SC = "ROI 内灰度 **5%/95% 分位**之差（抗孤立热点）" | `Cv2.MinMaxLoc` 取**绝对最小/最大**值；同文件 :43 内联注释自认"SC = Rmax − Rmin"。文档内部自相矛盾，单个镜面反光点即可抬高 SC |
| 2 | `Engine/Nodes/FeatureDetectorNodes.cs:30`（对照 :46-47） | "角点检测：Shi-Tomasi / Harris / **FAST**" | 只允许 `shi-tomasi`/`harris`，传 `fast` 直接抛 `InvalidOperationException`；目录亦只注册两种 |
| 3 | `Engine/Nodes/FeatureMatchNode.cs:272`（对照 :280-283） | `ToGray8`："16 位按 1/257 **压缩到 8 位**" | `ConvertTo(scaled, MatType.CV_16U, 1/257)` 输出仍是 **CV_16U**（仅数值缩放），方法名与注释均不成立；SIFT/ORB 收到 16U 会断言失败 |
| 4 | `Api/Endpoints/TraceEndpoints.cs:125-128`（对照 :227-246） | "失败锁激活时**唯一**受认证+审计的**解锁路径**……核对后解锁" | 即使 `problems.Count==0` 且无事务标记，:245 仍**无条件抛 ApiConflictException**；全端点无任何清除 `IsFailureLocked` 的调用。实际"永不解锁" |

---

## 🟡 中危（23 条）

| # | 位置 | 注释声称 | 实际行为 |
|---|---|---|---|
| 5 | `Engine/Nodes/ArucoMarkerNode.cs:20` | 鱼眼/远心模型"**不做位姿**"（隐含降级、仍出 ID/角点） | :244-245 直接抛异常，整节点失败，连标记 ID 都拿不到 |
| 6 | `Engine/Nodes/ContourNodes.cs:16`（对照 :415-416） | 共享约定："没找到轮廓是正常业务结果（count=0/found=false），不是执行失败" | `contour.properties` 对空列表直接抛 `InvalidOperationException`，违反同文件头声明 |
| 7 | `Engine/Nodes/DefectAnalysisNodes.cs:302`（对照 :272-274） | `angleMode` 支持 length-weighted / **mean** 两种模式 | 三元表达式两侧**完全相同**（`? Math.Atan2(plainAngleSum, angleSum) : Math.Atan2(plainAngleSum, angleSum)`），且 :274 无条件按长度加权；`mean` 模式从未生效 |
| 8 | `Engine/Nodes/DetectionNodes.cs:165`（对照 :94-108, :158） | "单斑点便利口：**最大斑点**的布尔、外接尺寸、圆度" | 取 `detections[0]`，而排序受 `sortBy` 控制；当 `sortBy=x/y/circularity` 时首元素并非最大斑点 |
| 9 | `Engine/Nodes/ImageGeometryNodes.cs:62-63`（对照 :225, :274, :286） | "stretch **只用于预览**……**数值指标仍用原始极值，不走这里**" | `p99Magnitude` 正是由 `HistogramPercentile` 近似算出并作为**数值输出**（:274）与 Summary（:286）；且 stretch 修改的是真实输出图，不是预览 |
| 10 | `Engine/Nodes/TableReadNode.cs:225`（对照 :166-192） | "RFC4180 风格拆分：支持引号包裹、字段内分隔符与**换行**" | `Load()` 用 `reader.ReadLine()` 逐行读，`SplitLine` 只处理单行字符串；引号内换行会把一条记录拆成两行，静默错行 |
| 11 | `Engine/Nodes/ValueNodes.cs:8`（对照 :135） | 家族约定："上下限颠倒等非法配置**显式报错**……**不对调区间**" | `value.range` 用 `var swapped = min > max; lower = Math.Min(...)` 静默对调，仅写 `boundsSwapped` 诊断，不抛异常 |
| 12 | `Engine/Nodes/SubpixelNodes.cs:160`（对照 :161-163） | "Clip the segment to the image rectangle without changing its direction" | 该 summary 挂在 `SegmentBoundsOutsideImage`——纯布尔"是否完全在画外"判定，不做任何裁剪；真正裁剪在 :165 `ClampLine` |
| 13 | `Engine/Nodes/PreprocessGapNodes.cs:74`（对照 :80-84） | "按直方图求分位灰度（**线性插值在箱内**）" | 累加直方图后 `if (running >= target) return i;` 直接返回箱号，无任何插值 |
| 14 | `Engine/NodeCatalog.cs:1277`（对照 :1279-1289） | "Strictly one of add/subtract/multiply/divide" | Options 实为 **10 项**：add/subtract/multiply/divide/pow/sqrt/abs/min/max/clamp |
| 15 | `Engine/GeometryToleranceCatalog.cs:34`（对照 :31, `GeometryToleranceNodes.cs:201-209`） | "Key params: maxCenterDistance, **reportMode**……Empty geometry follows **emptyPolicy**" | 参数表只有 `maxCenterDistance`；执行器只读该参数；全仓不存在 `reportMode`。缺圆时直接抛异常，不按 `emptyPolicy` 处理 |
| 16 | `Engine/Algorithms/InferenceStats.cs:460`（对照 :489-498） | "crop to the box and **bilinear-resize** into an output buffer" | 整数索引取样（`(int)(x*scaleX)`）→ **最近邻**；:489 自注亦为 "Nearest upsample" |
| 17 | `Engine/Runtime/VisionExecutionPolicy.cs:66`（对照 :67-69） | "Non-Full debug modes cannot safely resume across **loop iterations** or Try retries" | `HasIterativeOrRetryControl` 只查 `flow.forEach` 与 `flow.try`，**漏掉 `flow.while`/`flow.repeat`**，调试模式会在这些循环上放行 |
| 18 | `Engine/ExternalTrain/ExternalTrainPackage.cs:169`（对照 :171-276） | "**Held-out inference** runs only when a smoke image is present and openSession is true" | `SmokeEvaluate` 全程**无任何推理**：只有文件存在性/大小校验与 `InferenceSession` 打开；`manifest.SmokeImage` 只在必填文件校验中出现 |
| 19 | `Api/TraceabilityStore.cs:170`（对照 `ProductionRuntime.cs:1113`） | "production cycles **skip the start record** and insert their full row at finalization" | 每个生产周期都先 `BeginAsync(...)` 写入强持久起始行，随后 `RecordAsync` 走 `ON CONFLICT` 更新；注释描述的是旧实现 |
| 20 | `Api/StorageBackupService.cs:498`（对照 :499-503） | "必须先把双方 **VACUUM 规范化**再比较逻辑内容" | 全后端仅此注释提及 VACUUM，**无任何 VACUUM 语句**；实际用 `ComputeSnapshotContentSha256Async` 按表名+rowid 逐行哈希 |
| 21 | `SqliteSnapshot/Program.cs:335`（对照 :575-577, :684-689） | "从保护副本恢复原**文件组**……目标始终保持可读"；:325 又称 sidecar 内容在保护副本中"能完整恢复" | `ToMarker` 把 `ProtectedFiles` 固定为 `[]`；`TryRestoreFromBackup` 先删目标 `-wal/-shm`，随后只复制主库。备份目录中的 WAL/SHM **永不恢复** |
| 22 | `Engine/Nodes/MergedNodeAliases.cs:10`（对照 :51/:64/:77） | 别名"行为与规范节点**逐位一致**" | 规范 `MorphologyNode` 显式传 `applyRoi: true`（`PreprocessNodes.cs:395`），三个形态学别名未传，走默认 `false`；设置 ROI 时像素/统计输出不一致 |
| 23 | `Engine/Algorithms/CalibrationPoseHints.cs:72`（对照 :73） | "**Prefer empty corners**, then mid cells, then tilt / count" | `hints.Where(cover-).Take(4)` 按 3×3 网格行优先扫描取前 4 条，不区分角/边/中心；"优先补角"未实现 |
| 24 | `Engine/Algorithms/CalibrationIntrinsicsWizard.cs:133`（对照 :134-135） | "Prefer **explicit size from quality path**; otherwise **pad to a safe even size**" | 未读取 `quality`；直接 `Math.Max(16, (int)Math.Ceiling(maxU) + 1)`，无偶数补齐，可能得到奇数宽高 |
| 25 | `Engine/Nodes/FeatureDetectorNodes.cs:12`（对照 :407-410） | "特征检测**四个节点**的共用校验……**三处**都走同一条路径"（同句自相矛盾） | `feature.ridge` 用 `VisionGeometryHelpers.ToGray/ApplyRoi`，绕过 `FeatureDetectorHelpers.Prepare`，无 8 位/尺寸/空图校验 |
| 26 | `Engine/FeatureDetectorCatalog.cs:158`（对照 :135-144） | `directionMap`："Also emit an **8-bit image** where each pixel holds the orientation" | 输出端口只有 image/energy/dominantDirectionDeg/anisotropy/…，**没有方向图端口**，参数承诺的输出无处承载 |
| 27 | `Engine/Algorithms/JLVisionMatching.cs:2536`（对照 :2537-2538） | "rotatedMaskOwned disposed via probeMask path when assigned" | `finally` 中 `if (rotatedMaskOwned is null) { }` 是**空语句**，仅 `rotatedOwned?.Dispose()`；`MatchTemplate` 抛异常时旋转掩膜 Mat 泄漏 |

---

## 🔵 低危（31 条）

| # | 位置 | 注释声称 | 实际行为 |
|---|---|---|---|
| 28 | `Engine/NodeCatalog.cs:1568` | 三个类型"尚未声明 SupportsRoi（能力位**待补**）" | `recognition.barcode` 已在 `Batch3RecognitionCatalog.cs:65` 声明 `SupportsRoi: true`（另两个同类）；注释已过时 |
| 29 | `Api/Infrastructure/RollingFileLogger.cs:39` | 满队列丢弃计数对应 `BoundedChannelFullMode.**DropWrite**` | 实际 `FullMode = Wait`；:54-55 明确写"不能用 DropWrite" |
| 30 | `Api/Security/Audit.cs:30` | 审计记录"会**静默丢失**，此计数是**唯一信号**" | 失败路径先 `RecordPersistFailure` 再 `logger.LogError(...)`；非静默，且计数不是唯一信号 |
| 31 | `Scripting/CSharpScriptRunner.cs:18` | 以 **syntax-tree allowlist** 拒绝宿主/IO/反射 API | `ForbiddenApiWalker` 是**黑名单**（ForbiddenNamespaces/Types/Members/Cv2Members）；未列出的 API 默认放行 |
| 32 | `Scripting/ScriptEditorService.cs:4` | 三个编辑器服务"use the same reference set and forbidden-API walker as execution" | 仅 `Validate` 走 walker；`Format` 只做语法树+NormalizeWhitespace；`Complete` 只用 `SemanticForbiddenNamespaces` 过滤补全项 |
| 33 | `Api/Endpoints/SystemEndpoints.cs:15-19` | Q11/R07 关于 `/api/health` 的 `status/ready/productionReady` 语义 | 这 5 行注释紧贴在 `MapPost("/api/leases/{kind}/{id}/release")`（:22）之上；health 端点在 :30 |
| 34 | `Engine/Batch5PrecisionCatalog.cs:118` | `selectBy`："largest area **or** the index parameter" | Options 为 largest/smallest/first/last/index 五选一 |
| 35 | `Engine/Batch5PrecisionCatalog.cs:344` | 同上（ellipse 版） | 同为五项选项，说明漏掉 smallest/first/last |
| 36 | `Engine/Batch5PrecisionCatalog.cs:49` | "Key params: hierarchy mode, **approximation**" | `contour.extract` 参数表无 `approximation`（该键只在 `contour.fromMask`） |
| 37 | `Engine/DefectCatalog.cs:32`（对照 :35） | `outputMode`："Legacy setting; region and mask are **both emitted**. Emit a VisionRegion (default) **or** an 8-bit mask image." | 同句自相矛盾；长描述称"两者恒输出"，参数却称其决定输出形态，且标记 `Internal: true` |
| 38 | `Engine/FeatureDetectorCatalog.cs:49` | `maxCorners`：描述 "**FAST** keeps everything above the threshold" | `method` 只有 shi-tomasi/harris，无 FAST 分支 |
| 39 | `Api/RuntimeDependencies.cs:113` | 描述"Captures runtime dependencies… ManifestHash is semantic" | 该 summary 下方紧跟 :118 另一个 summary，实际成员是 `DeviceStateVerification(bool Ok, IReadOnlyList<string> Issues)`——文档块错位 |
| 40 | `Api/RuntimeDependencies.cs:242` | "**Reject** manual reconciliation while a robot command… is still active" | 方法 `IsDeviceCommandInFlight` 只查询并 `catch { return false; }`，不拒绝/不阻断；真正阻断在 :270 |
| 41 | `Engine/Nodes/JLVisionMatrixNode.cs:13` | 覆盖"…逐元素/**范数**/求和…" | `JlMatrixOperations` 算子表无 norm/范数 实现 |
| 42 | `Engine/Nodes/MeasurementGapNodes.cs:143`（对照 :156-162） | "先中心化再解 **3×3** 正规方程" | 中心化后解 **2×2**（`det = a11*a22 - a12*a12`），半径由 `(suu+svv)/n` 收尾 |
| 43 | `Engine/Nodes/CustomProtocolNodes.cs:344`（对照 :392, TCP :209） | 与 TCP 节点"语义一致……**差别只在传输层**" | 串口对空负载**无条件**抛错；TCP 仅在 `readMode != "none"` 时要求非空——请求校验规则不同，非传输层差异 |
| 44 | `Engine/Nodes/FlowObservabilityNodes.cs:80`（对照 :88） | "只接受 debug/info/warning/error" | 额外接受 `"warn"` 别名，可接受集合与注释不符 |
| 45 | `Engine/Nodes/MatchingGeometryNodes.cs:329`（对照 :330-334） | 畸变矩阵"必须是 1xN 且 **N∈{4,5,8,12,14}**" | 仅按 `distortion.Length` 建 Mat，无长度校验；`[0]` 会构造出注释所说"会被拒绝"的 1×1 |
| 46 | `Engine/Nodes/PreprocessNodes.cs:223` | `GaussianBlurFilter`"**只由** image.filter(method=gaussian) 调用，**不再单独暴露为节点**" | `MergedNodeAliases` 的 `image.blur.gaussian` 直接调用它并作为独立节点类型注册 |
| 47 | `Engine/Nodes/PreprocessNodes.cs:299` | `MedianBlurFilter` 同上 | `image.blur.median` 别名在调用 |
| 48 | `Engine/Nodes/RegionNodes.cs:121` | `RegionFromThresholdCore`"只由 region.fromRange(mode=gray) 调用，不再单独暴露" | `region.fromThreshold` 别名在调用 |
| 49 | `Engine/Nodes/OpenCvNodes.cs:338` | "这里**只补 manual 等**没有该输出的分支" | `EnrichThresholdResult` 仅在 adaptive(:272)/otsu(:277) 调用；manual 分支 :279 后直接另走，**永不进入**该方法 |
| 50 | `Engine/Algorithms/ChamferShapeMatching.cs:232`（对照 :233） | "point count, **oriented fraction**, mean radius" | 返回 `OrientedCount`（个数，非比例），且多出第 4 项 `MeanNormalMagnitude` |
| 51 | `Engine/Algorithms/VisionAlgorithmRuntime.cs:27`（对照 :40） | 词汇表："jlvision \| opencv \| managed \| zxing \| tesseract \| onnx \| hybrid" | `script.csharp` 实际返回 **"roslyn"**，在声明的封闭集合之外 |
| 52 | `Engine/Algorithms/JlContourOperations.cs:112`（对照 :116） | "length1=**圆角半径**" | 半径硬编码 `new JlTuple(new[]{0.1,0.1,0.1})`，`a.Length1` 从未使用 |
| 53 | `Engine/Algorithms/JlImageOperations.cs:110`（对照 :111） | "**mode=derivate_gauss**, mult=alpha" | 过滤器名写死 `"gauss"`，`a.Mode` 未使用 |
| 54 | `Engine/Algorithms/JlMatrixOperations.cs:109`（对照 :112-113） | "rows=向量长度, **count=对角线序号**" | `a.Rows` 同时当长度与对角线序号，`a.Count` 未使用 |
| 55 | `Engine/Algorithms/JlGeometryOperations.cs:202`（对照 :204-208） | "**row1..column4**=线段端点, **count=条数**" | 只用 Row1/Column1/Row2/Column2 构造单条线段，条数硬编码 `1` |
| 56 | `Engine/Algorithms/JlRegionOperations.cs:143`（对照 :145-146） | "size/length1=宽范围, **row/column=高范围**" | 高范围用 `a.Row` + `a.Length2`，`a.Column` 未使用 |
| 57 | `Engine/Runtime/VisionWorkflowCompiler.cs:1406`（对照 :1409） | "两条分支若全部由可融合节点组成，则合并为一个流水线段" | 实际条件多一个 `loopNodeId is null`；while/repeat 体内的 Parallel **永不融合**，仍串行 |
| 58 | `Engine/Nodes/BarcodeQualityNode.cs:188`（对照 :202-208） | cellSize 模式"1D 码也可以走，**行数配 1**" | cellsize 分支只读 `cellSizePx`，按 `Region.Height/cellSize` 反推行列，**无法配置行数=1** |

---

## 修复优先级建议

1. **先修 4 条 🔴**：其中 #3（ToGray8 位深）与 #4（恢复端点永不解锁）是**会直接导致现场失败或误判**的功能性缺陷；#2 是"文档承诺了不存在的算法"，建议改文档或补实现（FAST 在 OpenCV 有现成 `Cv2.FastFeatureDetector`）。
2. **再修影响判定的 🟡**：#7（`mean` 模式死分支）、#8（最大斑点取错）、#1（SC 用极值）、#9（数值指标口径）、#10（CSV 跨行）、#21（WAL 不恢复）。
3. **批量清理 🔵 中的"过时叙述"**：#46/#47/#48/#28/#19/#20 一类属"实现已改、注释未跟"，建议在 CI 里加一条 doc 漂移检查（本次审查的 58 条中约 40% 属于此模式）。

## 说明

- 所有 58 条均经人工回读源码复核，摘录的注释与代码均为原文/原行。
- 1 条被剔除：`Engine/Runtime/VisionNodeStep.cs:14`（注释把 `LoopNodeId` 限定为 `forEach`，编译器对 while/repeat 也写入该字段，但运行时仅在 `context.Item is VisionDetection` 时使用，未构成行为矛盾）。
- 本次不含测试与前端；如需对 `backend/tests`（234 文件）或 `frontend` 做同类审查，可按同一方法另起一轮。
