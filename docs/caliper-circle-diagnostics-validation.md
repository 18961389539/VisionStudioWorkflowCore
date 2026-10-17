# 单卡尺诊断验证记录

日期：2026-10-17。

## 验证范围

本轮使用仓库的 4 张 PNG 和可控生成的灰度 PNG，验证文件读取、原生边缘测量、拟合、运行工件缓存、HTTP 诊断接口、图像覆盖层以及配置窗口联动。

没有现场相机原图。本记录证明所列场景的真实像素与原生算法链路通过，不构成现场检出率、绝对测量精度或生产验收结论。

## 自动化结果

新增 **55 项**诊断相关用例；运行相关回归共 **465 项通过，0 失败、0 跳过**。

| 范围 | 通过数 | 主要覆盖 |
| --- | ---: | --- |
| 前端 8 个测试文件 | 317 | 请求取消、快速选尺、迟到成功/错误响应、运行与执行序列一致性、超时、缓存淘汰、过期重试、配置窗口只读图像与几何联动、参数过期提示、ROI 曲线断点、异常导航 |
| 后端引擎 | 125 | 捕获生命周期、失败重执行清除旧数据、生产默认关闭、并发捕获隔离、每次运行的缓存预算，以及既有卡尺/亚像素/逐节点图像回归 |
| 后端 API / RunStore | 23 | 原生 PNG 测量、诊断与图像接口、选尺越界、缓存过期、诊断内存预算与替换记账，以及既有图像接口回归 |

### 像素及原生算法用例

| 输入或操作 | 核对内容 |
| --- | --- |
| 仓库 `samples/camera_frames/frame001.png` 至 `frame004.png` | 从 PNG 解码、测量、拟合到诊断 HTTP；选中边缘与实际覆盖层一致 |
| 灰度圆，背景 30、前景 220，抗锯齿和高斯模糊 | 在 0°、投影宽度 1 px 时逐点比较原图像素与诊断灰度；比较实际候选位置、带符号响应、选中强度与拟合输出 |
| 弱边缘 | 高阈值无候选；降低阈值并选择正确极性后找到边缘；错误极性仍未命中 |
| 竞争外边缘 | 由外向内选中干扰边缘；拟合将其标为离群点；启用半径偏差限制后标为半径剔除；均核对实际点覆盖层 |
| Brush ROI 擦除圆边缘附近像素 | 无效采样通过 JSON `null` 返回；不中断 JSON 序列化；不跨越 ROI 缺口制造边缘；其他卡尺仍可拟合 |
| 刚性平移与 +90° 旋转 | 基准圆心映射到实际输入坐标，角度、剖面位置、原图采样和点覆盖层一致 |

回归中的半径容差用于识别实现回归，不代表测量精度指标。配置窗口中的相邻灰度差是参考曲线；算法实际候选响应独立来自原生测量结果。

## 浏览器中的完整链路

在独立的本地验证宿主和数据目录中，配置窗口使用真实 HTTP 接口读取 PNG、执行原生算法并查询诊断。验证页没有替换 `fetch` 或图像加载器，没有手工提供诊断数值。

1. 读取带外部干扰块的 PNG 并运行节点。
2. 点选图像中的正常卡尺，再点选干扰边缘卡尺；序号、图像高亮、灰度与候选响应同步切换。
3. “下一异常”定位到第 37 条卡尺。实际记录：3 个合格候选，选中半径 111.5 px，状态为拟合离群点。
4. 将最小对比度从 15 改为 255：原诊断保持原快照，并提示过期；修改本身不执行节点。
5. 显式运行后切换至新 runId；第 37 条卡尺候选数变为 0，状态变为无符合阈值和极性的边缘，整体未找到圆；旧边缘点不再显示。
6. 浏览器错误日志为空。临时验证页、配置文件和服务已清理。

证据位于忽略的 `artifacts/caliper-diagnostic-validation/`：PNG、原始运行报告、诊断 JSON、浏览器重跑前后快照、TRX 和前端 JSON 测试报告。截图位于 `artifacts/ui/caliper-circle-native-validation.png` 与 `artifacts/ui/caliper-circle-native-threshold-validation.png`。

## 重复运行

在仓库根目录的 PowerShell 中执行。后端使用独立构建目录；结果写到 artifacts，避免占用现有运行实例的输出目录。

```powershell
$env:VISIONSTUDIO_CALIPER_EVIDENCE = Join-Path (Get-Location) 'artifacts/caliper-diagnostic-validation'
dotnet test backend/tests/VisionStudio.Api.Tests/VisionStudio.Api.Tests.csproj --artifacts-path artifacts/caliper-diagnostic-tests --filter 'FullyQualifiedName~CaliperCircleDiagnosticsApiTests|FullyQualifiedName~RunNodeImageApiTests|FullyQualifiedName~RunStoreTests' --results-directory artifacts/caliper-diagnostic-validation --logger 'trx;LogFileName=caliper-diagnostics-api.trx'
dotnet test backend/tests/VisionStudio.Engine.Tests/VisionStudio.Engine.Tests.csproj --artifacts-path artifacts/caliper-diagnostic-engine-tests --filter 'FullyQualifiedName~CaliperCircleDiagnosticsLifecycleTests|FullyQualifiedName~Batch5CaliperRegressionTests|FullyQualifiedName~Batch5PrecisionNodeTests|FullyQualifiedName~RunNodeImageCaptureTests' --results-directory artifacts/caliper-diagnostic-validation --logger 'trx;LogFileName=caliper-diagnostics-engine.trx'
npm --prefix frontend test -- --run src/__tests__/caliperCircleDiagnostics.test.tsx src/__tests__/caliperCircleDiagnosticsPanel.test.tsx src/__tests__/caliperCircleDiagnosticWorkspace.test.tsx src/__tests__/imageViewerMeasurementGeometry.test.tsx src/__tests__/nodeConfigurationDialog.test.tsx src/__tests__/caliperCircleQuickSettings.test.tsx src/__tests__/circleConstruction.test.ts src/__tests__/nodeConfigurationState.test.ts
npm --prefix frontend run build
```

后续拿到现场图像后，还需按工件、光照和缺陷类别建立独立验收集，评估误检、漏检、重复性、偏差及长时间运行表现。
