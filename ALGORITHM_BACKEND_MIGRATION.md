# JLVision 与 ONNX Runtime 迁移

日期：2026-10-15。节点目录仍为 94 个，已有节点 ID 和端口保持可连接。

## 实际后端

| 能力 | 实现 |
|---|---|
| 灰度、通道、滤波、阈值、形态学、裁剪及重采样 | JLVision 原生图像／区域算子 |
| 轮廓、外接几何、亚像素边缘、卡尺、直线／圆／椭圆拟合 | JLVision 区域、XLD 和测量算子 |
| 灰度模板匹配 | JLVision NCC 金字塔模型；平方差模式对 NCC 候选进行原生 SSD 复核 |
| 形状定位 | JLVision 原生形状模型及亚像素位姿；固定尺度 |
| 内参标定／去畸变 | JLVision 原生标定数据模型与图像映射 |
| 四类 ONNX 推理 | ONNX Runtime；继续读取既有 ONNX 模型及 NCHW 输入契约 |
| 条码／OCR | ZXing.Net／Tesseract；图像预处理使用 JLVision |
| 数值、坐标系、流程控制、二维手眼数学求解 | 既有 C# 实现 |

依赖固定为 `JLVisionLib 1.0.0`、`Microsoft.ML.OnnxRuntime 1.31.0`。本机原生版本诊断为 **26.05**。JLVision 只支持 Windows x64；启动时检查真实原生算子并记录版本，加载失败会明确报错，算法节点不回退至 OpenCV。NuGet 1.0 托管门面未包含的标定、FFT 等算子通过其公开 HLI 接口调用，调用前核对算子名称与已固定的原生 ABI。

OpenCvSharp 仍用于相机、旧插件、图像快照的 Mat 缓冲，以及文件编码／解码和测试图绘制。传统算法执行与 ONNX 推理已替换；本次没有改写相机或旧插件的图像传输协议。静态检查禁止节点重新调用 OpenCV 算法或 DNN。

## 参数与数据兼容

- `match.template.pyramidLevels=0` 按模板尺寸选取金字塔层数；1 表示单层，最多 8 层。细小或周期纹理可手动降低层数。模型按模板像素、尺寸、角度配置和层数缓存，内容变化后重新建立。
- `method=ccoeff-normed` 使用原生 NCC；`sqdiff-normed` 使用 NCC 候选加 SSD 判定，不是旧版全图穷举 SSD。更换后应复验匹配阈值。旋转和位移现在可输出亚像素结果。
- `match.shape` 已不使用半径签名；旧 `signatureSamples` 参数不再参与计算，前端不再提供该无效控件。新实现支持原生边缘形状模型，仍存在旋转对称目标的方向歧义。
- `feature.line`／`feature.circle` 使用轮廓分段和拟合，不再是霍夫搜索；旧证据阈值现在用于筛选轮廓支持程度。现场素材应复验阈值。
- 显式高斯 sigma 使用原生 FFT 高斯过滤和反射边界；默认小核使用原生 GaussImage。与旧离散卷积的边界及灰度细节可能不同。
- 新标定 JSON 带 `NativeCameraType` 和 `NativeCameraParameters`，原生参数是畸变模型的依据。`CameraMatrix` 提供像素内参；新原生模型中的 `DistCoeffs` 为保留字段，不能交给 OpenCV 当作原生模型的畸变系数使用。去畸变节点仍能读取旧 Brown-Conrady JSON，并用 JLVision 对其坐标映射重采样。
- NCC／形状模型和 ONNX 会话使用最多 16 个条目的缓存；使用中的对象不会被回收，宿主停止时清理。ONNX 会话按文件路径、大小及修改时间区分，输入和取消状态按调用隔离。
- 执行报告增加 `algorithmBackend`，匹配结果包含 `modelCacheHit`，用于确认实际运行后端。

## 可复现的速度与精度对照

命令：`dotnet run --project backend/tools/VisionStudio.AlgorithmBenchmark --configuration Release`。

基准包含迁移前的完整 OpenCV 模板匹配节点；两侧均统计实际预处理、搜索和结果构建。下表是本机 x64、16 个逻辑处理器、48×48 合成模板、100 次热态运行的中位数。JLVision 复用已建立的模型。

| 图像／搜索范围 | 旧 OpenCV 节点 | JLVision 节点 |
|---|---:|---:|
| 640×480，无旋转 | 3.9061 ms | 0.5037 ms |
| 640×480，±45°，5° 步长 | 70.6534 ms | 1.6657 ms |
| 1280×960，无旋转 | 14.0425 ms | 1.0316 ms |
| 1280×960，±45°，5° 步长 | 286.4668 ms | 5.2969 ms |

合成形状位姿真值为 `(319.4, 243.2, 33.7°)`；原生形状定位误差约 **0.4173 px／0.00314°**。单个合成样例不是精度承诺。首次建模有额外成本；模板纹理、搜索范围、金字塔层数、硬件和业务素材都会影响结果。

初次使用库的自动 NCC 层数时，该素材实际退化为慢速单层搜索；经原生层数对照后采用尺寸自适应金字塔。不能只更换库名就认定所有节点都会加速，也不能用热态结果代表冷启动。

## 完成验证

- Release 后端非 Soak 测试 **632/632**（Engine 294、API 338），包含真实原生算子及四类 ONNX 推理。
- 前端测试 **39/39**，TypeScript／Vite 生产构建通过。
- 94 节点目录一致性、静态审查及编译警告基线通过，无新增编译警告。
- 本地 Release 发布产物已验证包含 `JLVisionCore.dll` 与 Windows x64 的 `onnxruntime.dll`；未执行线上部署。
- 补充验证覆盖图像通道与所有权、灰度形态学、FFT 边界、匹配缓存及失效、SSD 亮度判定和嵌套轮廓模式。
