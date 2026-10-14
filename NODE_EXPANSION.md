# 节点扩展与使用说明

本次按四批补齐工业视觉流程节点。运行时目录由 `BuiltInNodeCatalog` 组合各批目录，前端离线目录通过 `scripts/generate-frontend-catalog.ps1` 生成。所有节点同时注册到宿主 DI 与执行器注册表。

## 第一批：图像预处理、定位与判定

- `image.grayscale`、`image.channel`：灰度和 BGR／HSV 通道提取。
- `image.blur.gaussian`、`image.blur.median`、`image.morphology`：滤波、腐蚀、膨胀、开闭运算。
- `image.threshold.adaptive`、`image.threshold.otsu`：局部阈值和自动阈值。
- `match.template`：归一化模板匹配与有界旋转搜索；模板来自输入端口或文件。未命中返回 `found=false`，几何端口可能没有值，先判断 found 再使用位置。
- `measure.blobs`：多目标面积／圆度筛选、排序和计数，返回 `DetectionList`。
- `value.number`、`value.arithmetic`、`value.range`、`logic.boolean`：常量、四则运算、闭区间判定和布尔组合。

模板库新增“多目标检测与判定”：测试图 → 灰度 → 高斯滤波 → Otsu → 开运算 → 多 Blob → 数量范围 → OK／NG。纯计算节点可融合为流水线，仍保留节点报告和调试信息。

## 第二批：图像变换、测量、流程与输出

- `image.crop`、`image.resize`、`image.rotate`、`image.perspective`：裁剪、缩放、旋转、四角透视矫正。后续测量坐标以变换后的图像为准。
- `measure.pointLineDistance`、`measure.circleSpacing`、`measure.fitLine`、`measure.fitCircle`：点线距、带符号圆边距、多点最小二乘拟合。拟合通过 `point1`–`point12` 输入，直线至少两个点，圆至少三个非共线点。
- `flow.delay`：可取消延时。
- `flow.ifBoolean`：直接读取布尔条件，true／false 路径在 Join 汇合。
- `flow.switch`：`casesJson` 是 1–16 个唯一字符串；按顺序对应 case1–case16，未匹配走 default。配置的各路径须连接并汇合至 `flow.merge` 的对应 caseN／default 输入。
- `output.saveImage`、`output.exportResult`：编码图像或导出单个类型化结果为 JSON／CSV。默认拒绝覆盖已有文件，启用 overwrite 才覆盖；通过临时文件原子提交。
- `output.httpRequest`：GET／POST／PUT／DELETE／HEAD，非 2xx 报错，支持超时、取消和响应字节上限。默认最大响应 1 MiB。

输出路径相对于宿主进程工作目录解析。文件与 HTTP 节点是实际外部操作，目录禁用单节点调试和并行执行。硬件动作仍由原有设备／机器人／相机动作契约管理。

## 第三批：标定、集合与识别／推理

- `calibration.cameraIntrinsics`：输入多视图 3D 标定板点与对应 2D 像点 JSON，通过 JLVision 原生相机模型解算内参与畸变；至少三个有效视图。对应点需要提前准备；原生畸变参数保存在 `NativeCameraParameters`。
- `image.undistort`：接收标定 JSON 输入端口或参数；不同图像分辨率按比例缩放内参。
- `calibration.handEye2D`：二维 SE(2) 眼在手上／眼在手外标定，至少四组有平移和旋转变化的位姿对。输出 pose 和可连接坐标变换节点的 Transform2D。仅适用于二维，输入单位必须一致。
- `collection.detections.filter`、`.sort`、`.at`、`.count`：检测列表筛选、排序、索引提取和计数；保留 AI 类别与置信度，可按类别和置信度筛选。索引从 0 开始；提取越界返回 found=false。
- `recognition.barcode`：ZXing 一维条码／二维码解码，支持单码和多码。
- `recognition.ocr`：Tesseract OCR，需要配置语言模型目录 `dataPath` 与 `language`（如 eng、eng+chi_sim）。目录内须有对应 `.traineddata`。
- `inference.classification`、`.detection`、`.segmentation`、`.anomaly`：ONNX Runtime 加载 ONNX 模型并推理；模型路径、图像尺寸、归一化、通道顺序按模型训练方式配置。

推理输出契约：分类 `[1,C]` 或 `[1,C,1,1]`；检测 `[N,6]` 或 `[1,N,6]`，每行 `[x1,y1,x2,y2,score,classId]`；分割 `[1,C,H,W]`；异常检测单个 float32 分数。模型原始输出不符合这些布局时需先转换模型或增加专用解码器。节点不内置业务模型或训练功能。

检测和分割要求 `crop=false`，避免居中裁剪后的坐标误映射；分类可使用居中裁剪。英文 OCR 示例模型已下载到本机 `artifacts/tessdata_fast/eng.traineddata`（忽略文件，不随源码提交）；正式部署应随业务配置语言包和 ONNX 模型。

## 第四批：结构化迭代与异常处理

- `flow.forEach`：顺序遍历 DetectionList，body 内读取当前目标 index／center／circle／rectangle／x／y，以及可选 confidence／classId。`body` 接循环体，体末 `flow.loopEnd.repeat` 接回 `flow.forEach.repeat`，`done` 接后续流程。每次循环仍经过统一节点执行与设备动作所有权检查。默认上限 100，配置上限最多 500。循环体判定采用任一 NG 即 NG 的聚合，后续目标 OK 不会掩盖先前 NG。
- `flow.try`：body 执行后按结果走 success 或 error。body／success／error 三条路径分别在同一个 `flow.merge` 的 case1／case2／default 汇合。body 限制为线性、确定性、无设备动作的内置计算节点；重试最多三次。设备指令、文件写入、HTTP 和取消不自动重试。已处理异常保留失败报告与尝试信息，错误路径可显式设置 NG。

迭代与异常块需要完整结构图；不能单节点运行。普通任意环路、跨分支数据引用和未配对边界会被编译器拒绝。

含迭代或重试块的工作流目前只支持完整运行；断点／单节点／从节点运行会被拒绝，避免按节点 ID 恢复缓存时丢失迭代进度。普通无迭代工作流的调试方式保持原有行为。

模板库新增“逐目标顺序执行”，无需连接设备即可查看每个目标的执行报告。

## 第五批：精密定位与测量（审查修复）

- `contour.extract`、`contour.filter`、`contour.properties`：轮廓提取、层级与几何筛选、派生属性及 DetectionList 转换。`ParentIndex` 指向当前输出列表中的父轮廓；父轮廓被过滤、截断或 holes-only 模式省略时设为 -1，`IsHole` 保留孔洞身份。
- `feature.edgeSubpixel`：搜索剖面的亚像素边缘定位。五点二次最小二乘窗口提供梯度峰形的 RMS 残差（灰度单位），不是定位精度承诺；窗口不足或峰形不适合拟合时输出 -1。
- `measure.caliperLine`：沿搜索线的垂直方向分布多条平行剖面，至少找到三个边缘点后进行正交直线拟合。`caliperCount` 默认 9；`edgeLength` 表示卡尺分布跨度，0 时取搜索线长度。`fitError` 为点到拟合线的 RMS 距离（像素），`sampleCount` 为有效边缘点数；短于 2 px 的搜索线明确报错。
- `measure.caliperCircle`：径向搜索边缘并拟合圆；`measure.fitEllipse`：对至少五个有效轮廓点拟合椭圆。无可用边缘返回 found=false；空轮廓等输入错误报错。
- `match.shape`：使用 JLVision 原生形状模型及亚像素位姿搜索，支持固定尺度的平移、旋转和多目标匹配；模型按模板像素和角度配置缓存。旧 signatureSamples 参数不再参与计算。角度为图像坐标中的逆时针旋转，旋转对称目标的方向存在固有歧义，并列分数优先报告绝对值较小的角度。
- `roi.transform`：根据基准及当前位姿刚性修正 ROI，旋转矩形转换为多边形；Roi 和 ContourProperties 在运行报告中保留结构化值。

轮廓提取、亚像素边缘、卡尺找线／找圆、形状匹配的 image 输出均为独立图像副本，成功与未命中路径都可连接下游处理；检测标注通过 overlays 展示，不修改图像像素。

前端离线目录和中文名称、参数、工具分类已同步为 94 个节点。模板库新增“定位与精密测量”，对应 `samples/shape-locate-measure-workflow.json`，包含形状定位、动态圆 ROI、卡尺找圆、公差判定和 JSON 导出。模板中的导出节点会写入 `artifacts/shape-locate-measure-result.json`，启用了 overwrite。

## 验证入口

新增测试位于 `Batch1NodeTests`、`Batch2ImageNodeTests`、`Batch2UtilityNodeTests`、`Batch2ControlRuntimeTests`、`Batch2TryRuntimeTests`、`Batch3GeometryNodeTests`、`Batch3RecognitionNodeTests`、`Batch4IterationTests`、`Batch5PrecisionNodeTests`、`Batch5ShapeMatchTests`、`Batch5PrecisionIntegrationTests`、`Batch5CaliperRegressionTests`、`Batch5ContourRegressionTests`、`Batch5ImagePortTests`，以及 Engine／API 的 `NodeExpansion*Tests`。

最终验收同时检查实际节点算法、完整运行链路、宿主注册、类型化结果快照、目录生成一致性、静态审查和前端测试／构建。

最终结果：内置目录 41 → 85（批次一～四，+44）→ 94（第五批，+9）；Release 后端测试 574/574（Engine 236、API 338，排除 Soak）→ 624/624（Engine 286、API 338），前端 39/39，生产构建通过。目录一致性、静态审查和警告基线通过，无新增编译警告。真实英文 OCR、二维码／CODE_128 和四类 ONNX 推理均通过；新增 JSON 示例实际运行通过。

算法后端已迁移为 JLVision／ONNX Runtime；版本、兼容边界和速度／精度对照见 [算法后端迁移说明](ALGORITHM_BACKEND_MIGRATION.md)。
