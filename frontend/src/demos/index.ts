// 演示工作流按主题分文件存放；本文件只做汇总与注册，对外 API 与原单文件 demos.ts 一致，
// 因此调用方（App / 模板库 / 编辑器）无需改动导入路径。
export type { DemoGraph } from './shared';
export * from './flow';
export * from './camera';
export * from './measurement';
export * from './defect';
export * from './robot';
export * from './calibration';

import { linearDemo, ifDemo, parallelDemo, roiDemo, nestedDemo, inspectionDemo, forEachDemo } from './flow';
import { cameraDemo, triggeredCameraDemo, frameSetDemo } from './camera';
import { measurementDemo, contourThicknessDemo, caliperWidthDemo, precisionDemo, regionDemo } from './measurement';
import { defectDemo, alignmentRegionDemo, colorEnhancementDemo, textValidationDemo, circularToleranceDemo, edgeToleranceDemo, smallFeatureDemo, regionShapeDemo } from './defect';
import { robotEyeToHandDemo, robotEyeInHandDemo, robotRuntimeDemo, robotPlcHandshakeDemo, robotTcpHandshakeDemo, devicePlcDemo } from './robot';
import { calibrationDemo } from './calibration';

export const demos = {
  contourThickness: contourThicknessDemo,
  caliperWidth: caliperWidthDemo,
  smallFeatures: smallFeatureDemo,
  regionShapes: regionShapeDemo,
  circularTolerance: circularToleranceDemo,
  edgeTolerance: edgeToleranceDemo,
  colors: colorEnhancementDemo,
  textValidation: textValidationDemo,
  alignment: alignmentRegionDemo,
  defects: defectDemo,
  regions: regionDemo,
  precision: precisionDemo,
  forEach: forEachDemo,
  inspection: inspectionDemo,
  camera: cameraDemo,
  cameraTrigger: triggeredCameraDemo,
  frameSet: frameSetDemo,
  measurement: measurementDemo,
  calibration: calibrationDemo,
  robotEyeToHand: robotEyeToHandDemo,
  robotEyeInHand: robotEyeInHandDemo,
  robotRuntime: robotRuntimeDemo,
  robotPlc: robotPlcHandshakeDemo,
  robotTcp: robotTcpHandshakeDemo,
  devicePlc: devicePlcDemo,
  linear: linearDemo,
  roi: roiDemo,
  if: ifDemo,
  parallel: parallelDemo,
  nested: nestedDemo
};

export type DemoKey = keyof typeof demos;

/** 每个示例的一句话用途说明：让用户在打开前就知道“这个示例是干什么的、适合谁” */
export const demoDescriptions: Record<DemoKey, string> = {
  contourThickness: '生成带局部凹陷的环形图像 → 提取真实内外轮廓 → 壁厚分布与最薄处 → 公差判定；默认检出约 14 px 薄区并判 NG，降低下限可验证 OK。',
  caliperWidth: '合成亮条 → 双边缘卡尺测宽 → 宽度公差判定；演示 115 px 标称宽度筛选、中心与两端点输出，找不到边缘对时走 NG。',
  smallFeatures: '生成带亮细条与暗缝的区域测试图，分别做顶帽、底帽和梯度；统计暗缝底帽响应，空检测域走 NG。',
  regionShapes: 'ROI 多边形生成区域，计算凸包与凹陷差，展示边界和骨架，并按凹陷像素面积判定。',
  circularTolerance: '点列生成内外轮廓 → 圆度拟合 → 包含检查与壁厚 → 同心偏差 → 最小壁厚判定；缺失轮廓和内圆不包含时走 NG。',
  edgeTolerance: '点列轮廓 → 直线度与拟合 → 平行度、垂直度和轮廓偏差 → 公差判定；空输入或缺失测量走 NG。',
  colors: '演示灰度增强，并从原图提取颜色范围、测量区域内色差，再做公差判定；灰度测试图按 R=G=B，彩色业务保留原图接线。',
  textValidation: '校验批号前缀和完整格式，提取日期与批次分组，再合并两项判定；可将固定文本替换为 OCR 或条码输出。',
  alignment: '旋转模拟工件偏移 → 配准 → 找到后变换图像与区域 → 逐对象属性与目标列表 → 数量判定；未找到走 NG 分支。',
  defects: '明暗场校正 → 参考图差分与动态阈值 → 缺陷比对 → 区域灰度统计 → OK/NG；默认边界差异触发 NG，将参考图腐蚀核设为 1 可验证 OK。',
  regions: '阈值区域 → 连通域 → 属性筛选 → 填孔 → 限定处理域 → 区域交集 → 数量判定，保留精确像素区域。',
  precision: '形状模板匹配 → 定位修正 ROI → 卡尺找圆 → 公差判定；未找到目标时直接判 NG，避免读取不存在的半径。',
  forEach: '检测多个目标后逐个读取中心 X 坐标，循环结束继续计数，演示有界顺序迭代。',
  inspection: '灰度 → 滤波 → 自动阈值 → 形态学 → 多目标计数 → 范围判定，演示新增节点的完整检测流程。',
  camera: '从虚拟相机取图 → 阈值分割 → 斑点计数，演示最基本的采集-处理链路。',
  cameraTrigger: 'PLC 触发信号到来才取图，演示触发式采集与取图超时控制。',
  frameSet: '同步帧组一次采集多台相机图像并分别处理，适用于多面同步检测。',
  measurement: '找圆、卡尺测边、求交点夹角，演示几何测量组合（如检测圆形零件尺寸）。',
  calibration: '用标定板特征求平面坐标变换，把像素坐标换算成工件毫米坐标。',
  robotEyeToHand: '眼在手外：相机固定、机器人移动，视觉引导机器人到达目标位姿。',
  robotEyeInHand: '眼在手上：相机装在机械臂末端，随动补偿后引导机器人到位。',
  robotRuntime: '手动下发目标位姿并回读当前位姿，用于调试机器人运行时连接。',
  robotPlc: '机器人与 PLC 握手应答，演示设备间握手交互链路。',
  robotTcp: '通过 TCP 模拟器收发机器人指令，无需真实机械臂即可验证协议。',
  devicePlc: 'PLC 等触发 → 取图检测 → 分支写回 PLC 结果，完整产线交互示例。',
  linear: '最简单的三节点直线流程：生成图像 → 阈值 → 斑点测量，适合首次上手。',
  roi: '在阈值节点上框选感兴趣区域（ROI），只处理画面中指定范围。',
  if: '按斑点面积走条件分支，两条路径各自处理后汇合给出判定。',
  parallel: '两条分支并行做不同阈值的处理，汇合后判定，演示并行流程。',
  nested: '条件分支内再嵌条件分支，演示嵌套流程的编译与执行。'
};
