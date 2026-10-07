/**
 * Catalog and runtime-status localization.
 *
 * The backend catalog is authored in English (node display names, categories, parameter
 * labels, options, descriptions, status words). Chinese labels live here as mapping tables
 * keyed by that English text.
 *
 * English mode therefore needs no table at all: the backend text is already the English UI
 * text, so `localizeCatalog(..., 'en')` and `localizeStatus(..., 'en')` pass it through
 * unchanged. Chinese mode overlays the tables, falling back to the English original for
 * anything not translated yet (e.g. plugin-provided nodes).
 */
import type { NodeCatalogItem } from '../types';
import type { Lang } from './lang';

const nodeNames: Record<string, string> = {
  'image.acquire': '采集图像', 'frameset.image': '帧组图像', 'camera.syncCapture': '同步采集', 'image.synthetic': '生成测试图像',
  'calibration.planar': '平面标定', 'coordinate.transformCompose': '组合坐标变换', 'coordinate.transformConstant': '固定坐标变换',
  'coordinate.distance': '坐标点距离', 'coordinate.point': '坐标点', 'coordinate.pose': '坐标位姿', 'coordinate.frameTree': '坐标系树',
  'coordinate.transformInverse': '逆变换', 'coordinate.poseToTransform': '位姿转变换', 'coordinate.frameResolve': '解析坐标变换',
  'coordinate.transformCoordinatePoint': '变换坐标点', 'coordinate.transformCoordinatePose': '变换坐标位姿',
  'coordinate.transformLine': '变换直线', 'coordinate.transformPoint': '变换点', 'coordinate.transformPose': '变换位姿',
  'device.readTag': '读取设备标签', 'device.waitTag': '等待设备标签', 'device.writeTag': '写入设备标签', 'device.writeVisionResult': '写入视觉结果',
  'feature.circle': '圆检测', 'feature.edge': '边缘检测 / Canny', 'feature.line': '直线检测',
  'flow.if': '条件分支', 'flow.join': '分支汇合', 'flow.parallel': '并行执行', 'flow.result': '检测结果 / 判定',
  'geometry.angle': '直线夹角', 'geometry.intersection': '直线交点', 'geometry.distance': '点间距离',
  'measure.caliper': '卡尺测量', 'measure.blob': '最大连通区域', 'measure.rotatedRect': '旋转矩形',
  'image.threshold': '图像阈值分割', 'robot.executeTarget': '执行机器人目标', 'robot.j4TcpCompensation': 'J4 / TCP 补偿',
  'robot.currentPose': '读取机器人当前位姿', 'robot.guidance2d': '二维机器人引导'
};

const categories: Record<string, string> = {
  Acquisition: '图像采集', Calibration: '标定', Coordinate: '坐标变换', Device: '设备通信', Feature: '特征检测',
  Flow: '流程控制', Geometry: '几何计算', Measurement: '测量', Preprocess: '图像处理', Robot: '机器人'
};

const parameterNames: Record<string, string> = {
  Action: '操作', 'Angle Offset': '角度偏移', 'Auto Ack': '自动确认', 'Auto Connect': '自动连接', 'Auto Start': '自动启动',
  'Calibration Asset ID': '标定资产 ID', 'Calibration Asset Version': '标定资产版本', 'Calibration Pairs JSON': '标定点 JSON',
  'Camera ID': '相机 ID', 'Center Threshold': '中心阈值', 'Circle X': '圆心 X', 'Circle Y': '圆心 Y', 'Compare Value': '比较值',
  Condition: '条件', 'Device ID': '设备 ID', 'Edge Threshold': '边缘阈值', 'End X': '终点 X', 'End Y': '终点 Y',
  'Expected Frame': '目标坐标系', 'Fallback Type': '备用值类型', 'Fallback Value': '备用值', Frame: '坐标系', 'Frame Mode': '取帧模式',
  'Frame Timeout': '帧等待超时', 'Fresh Read': '强制读取最新值', 'Guidance Mode': '引导模式', Height: '高度',
  'High Threshold': '高阈值', 'Hough Threshold': '霍夫阈值', 'J4 Angle Zero Offset': 'J4 角度零点偏移',
  'J4 Pivot Offset X': 'J4 旋转中心偏移 X', 'J4 Pivot Offset Y': 'J4 旋转中心偏移 Y', 'Low Threshold': '低阈值',
  'Max Gap': '最大间隔', 'Max Radius': '最大半径', 'Max Retries': '最大重试次数', 'Min Area': '最小面积',
  'Min Center Distance': '最小圆心间距', 'Min Length': '最小长度', 'Min Radius': '最小半径', Operator: '运算符',
  'Pick Offset X': '取料偏移 X', 'Pick Offset Y': '取料偏移 Y', Polarity: '边缘极性', 'Poll Interval': '轮询间隔',
  'R Tag': 'R 标签', Radius: '半径', 'Result OK Tag': '结果 OK 标签', 'Result Ready Tag': '结果就绪标签',
  'Retry Delay': '重试间隔', Robot: '机器人', 'Robot ID': '机器人 ID', Rotation: '旋转角度', 'Sample Width': '采样宽度',
  'Scheduled Action': '定时触发', 'Scheduled Lead Time': '定时触发提前量', 'Source Frame': '源坐标系', 'Start X': '起点 X',
  'Start Y': '起点 Y', 'Synchronization Group': '同步组', 'Tag ID': '标签 ID', 'Target Frame': '目标坐标系',
  'Target Unit': '目标单位', 'TCP Offset X': 'TCP 偏移 X', 'TCP Offset Y': 'TCP 偏移 Y', Theta: '角度 θ', Threshold: '阈值',
  Timeout: '超时时间', 'Timeout (ms)': '超时时间（毫秒）', 'Transforms JSON': '坐标变换 JSON', 'Trigger Before Grab': '采集前触发',
  Unit: '单位', 'Wait For Complete': '等待执行完成', Width: '宽度', X: 'X 坐标', 'X Tag': 'X 标签', 'X Translation': 'X 平移量',
  Y: 'Y 坐标', 'Y Tag': 'Y 标签', 'Y Translation': 'Y 平移量'
};

const optionNames: Record<string, string> = {
  Latest: '最新帧', Next: '下一帧', Boolean: '布尔值', Integer: '整数', Number: '数值', String: '文本',
  'Industrial Handshake': '工业握手', Move: '移动', 'Eye-to-Hand': '眼在手外', 'Eye-in-Hand': '眼在手上', Manual: '手动',
  True: '是', False: '否', Any: '任意', Rising: '上升沿', Falling: '下降沿', Positive: '正向', Negative: '负向',
  Both: '双向', DarkToLight: '由暗到亮', LightToDark: '由亮到暗'
};

/** 端口显示名（zh）：覆盖内置目录的全部端口。Handle id 仍用英文 name，映射只影响显示；
 *  en 模式 localizeCatalog 直通原文，节点回落英文端口名。 */
export const portNames: Record<string, string> = {
  image: '图像', exec: '执行', next: '下一步', true: '真分支', false: '假分支', branch1: '分支 1', branch2: '分支 2',
  center: '中心点', radius: '半径', area: '面积', circle: '圆', rectangle: '矩形', angle: '角度', distance: '距离',
  line: '直线', lineA: '直线 A', lineB: '直线 B', point: '点', pointA: '点 A', pointB: '点 B',
  pose: '位姿', currentPose: '当前位姿', transform: '变换', transformA: '变换 A', transformB: '变换 B', tree: '坐标系树',
  frameSet: '帧组', condition: '条件', pass: '判定通过', disposition: '判定', quality: '质量', error: '错误', state: '状态',
  timestamp: '时间戳', timestampBasis: '时间基准', deviceTimestampNs: '设备时间戳', triggerId: '触发号', sequence: '序号',
  strength: '梯度强度', searchLine: '搜索线', measurement: '测量', length: '长度', lengthValue: '长度', maxError: '最大误差',
  rmse: 'RMSE', r: '旋转角', x: 'X 坐标', y: 'Y 坐标', value: '数值', numberValue: '数值', boolValue: '布尔值', textValue: '文本值',
  cameraId: '相机 ID', cameraCount: '相机数', path: '路径', connection: '连接', written: '已写入', matched: '已匹配',
  withinTolerance: '在公差内', inPosition: '已到位', targetReady: '目标就绪', busy: '忙碌', complete: '已完成',
  execute: '执行', commandId: '指令号', attempts: '尝试次数', handshakeError: '握手异常', ack: '已应答',
  target: '机器人目标', edgeCount: '边缘数', waitMs: '等待时长', skewUs: '触发偏差', traceId: '追溯号'
};

const descriptions: Record<string, string> = {
  'image.acquire': '从连续采集的相机帧流中获取一帧。选择“最新帧”会复用最新图像；选择“下一帧”会等待新图像。',
  'frameset.image': '从同步帧组中选取一台相机的图像，不复制底层图像数据。',
  'camera.syncCapture': '向同步组发送一次触发命令，收集每台成员相机的新图像，并输出包含实测时差的帧组。',
  'image.synthetic': '生成确定性的测试图像，用于验证基础流程。',
  'calibration.planar': '通过最小二乘平面单应性，将像素坐标转换到指定的目标坐标系。',
  'coordinate.transformCompose': '组合 A：S→M 与 B：M→T，生成 S→T，并检查坐标系和单位是否兼容。',
  'coordinate.transformConstant': '在指定的工程坐标系之间创建二维刚性变换。',
  'coordinate.distance': '仅当坐标系和单位一致时，计算两个坐标点之间的距离。',
  'coordinate.point': '创建固定的坐标系参考点。', 'coordinate.pose': '创建固定的二维坐标系位姿。',
  'coordinate.frameTree': '构建双向坐标系图，并按命名路径解析坐标变换。',
  'coordinate.transformInverse': '求二维变换的逆，并交换源坐标系、目标坐标系及其单位。',
  'coordinate.poseToTransform': '将相对父坐标系的位姿解释为从源坐标系到父坐标系的刚性变换。可用于动态工具坐标系变换。',
  'coordinate.frameResolve': '解析坐标系树中的最短变换路径，必要时自动使用逆向变换。',
  'coordinate.transformCoordinatePoint': '使用工程坐标系变换点，并验证坐标系和单位。',
  'coordinate.transformCoordinatePose': '使用工程坐标系变换位姿，并验证坐标系和单位。',
  'coordinate.transformLine': '将像素空间直线的两个端点转换为带坐标系信息的直线。',
  'coordinate.transformPoint': '将图像空间二维点转换为带坐标系和单位信息的坐标点。',
  'coordinate.transformPose': '将 X、Y 和局部方向转换到指定目标坐标系。',
  'device.readTag': '通过设备管理器读取指定类型的设备标签，返回数值、质量状态和时间戳。',
  'device.waitTag': '等待设备标签满足条件或超时。适用于 PLC 触发和就绪握手。',
  'device.writeTag': '写入通用设备标签。若已连接输入，则优先使用输入；否则使用指定类型的备用值。',
  'device.writeVisionResult': '按顺序向 PLC 写入 OK/NG 和可选的 X/Y/R 结果：就绪位清零、写入数据、就绪位置位。',
  'feature.circle': '使用霍夫圆检测，返回圆和二维中心点。',
  'feature.edge': '提取 Canny 边缘。支持图像感兴趣区域，并返回二值边缘图。',
  'feature.line': '查找最长的霍夫直线，并返回直线数据。', 'flow.if': '流程条件分支。',
  'flow.join': '结构化分支的汇合节点。', 'flow.parallel': '启动两条并行流程分支。',
  'flow.result': '设置用于追溯的自动检测判定（OK/NG）。', 'geometry.angle': '计算两条二维直线之间的锐角，单位为度。',
  'geometry.intersection': '计算两条无限延长直线的交点，并返回二维点。',
  'geometry.distance': '计算两个二维点之间的欧氏距离。',
  'measure.caliper': '沿可配置搜索线进行一维灰度卡尺测量，并返回响应最强的边缘点。',
  'measure.blob': '查找最大轮廓并返回几何数据。', 'measure.rotatedRect': '计算符合面积条件的最大轮廓的最小外接旋转矩形。',
  'image.threshold': '使用 OpenCvSharp 进行二值阈值处理。',
  'robot.executeTarget': '通过通用二维机器人目标执行移动，支持标准工业握手、超时和重试策略。',
  'robot.j4TcpCompensation': '补偿 J4 旋转中心或 TCP 偏心，输出补偿后的机器人目标，不会直接向硬件发送指令。',
  'robot.currentPose': '从机器人管理器读取当前平面位姿和通用握手状态。',
  'robot.guidance2d': '根据已转换到 RobotBase（或其他机器人坐标系）的位姿创建通用二维机器人目标。'
};

/** 参数级中文描述（key = `${节点类型}.${参数名}`）。
 *  后端下发的是面向实现的英文说明；这里优先替换为面向操作的中文说明，
 *  并尽量给出“调大会怎样 / 什么时候调”的具体例子。未命中的参数回落后端原文。 */
const parameterDescriptions: Record<string, string> = {
  'image.threshold.threshold': '二值化阈值（0–255）：越高，保留的亮区域越少；目标偏暗时调低，偏亮时调高。',
  'image.acquire.cameraId': '要取图的相机 ID（如 virtual-1），可在“设备 → 相机”面板查看可用相机。',
  'image.acquire.frameMode': '取图模式：最新帧直接复用最近一帧；下一帧会等待新图像，适合触发采集。',
  'image.acquire.timeoutMs': '取图超时（毫秒）：超过该时间仍未取到图像，本节点判定失败。',
  'image.acquire.autoStart': '运行前自动启动相机采集，无需手动开始。',
  'image.acquire.triggerBeforeGrab': '取图前先发触发信号：相机为外触发模式时开启。',
  'measure.blob.minArea': '最小面积（像素）：小于该面积的连通域会被过滤；噪声多时调大，小目标漏检时调小。',
  'measure.rotatedRect.minArea': '最小面积（像素）：小于该面积的轮廓不参与最小外接矩形计算。',
  'feature.edge.lowThreshold': '边缘低阈值：梯度低于它的像素直接丢弃；与高阈值配合构成滞后阈值。',
  'feature.edge.highThreshold': '边缘高阈值：梯度高于它的才算强边缘；调高可抑制噪声，但可能丢失弱边缘。',
  'feature.line.houghThreshold': '霍夫投票阈值：越高，检出的直线越少但越可靠；漏检时调低。',
  'feature.line.minLength': '最短线段长度（像素）：短于它的线段被丢弃。',
  'feature.line.maxGap': '线段最大断缝（像素）：断缝超过该值的同一直线不会被合并成一条。',
  'feature.circle.minRadius': '圆半径下限（像素）：先按目标尺寸框定范围可显著减少误检。',
  'feature.circle.maxRadius': '圆半径上限（像素）：先按目标尺寸框定范围可显著减少误检。',
  'feature.circle.minDist': '相邻圆心最小间距（像素）：防止同一个圆被重复检出。',
  'feature.circle.edgeThreshold': 'Canny 边缘阈值：越高，弱边缘越少，检出越稳定但可能漏检。',
  'feature.circle.centerThreshold': '霍夫圆心累加器阈值：越高，圆心判定越严格。',
  'measure.caliper.sampleWidth': '卡尺采样宽度（像素）：垂直于扫描方向的取样带宽；反光强烈时可调小。',
  'measure.caliper.edgeThreshold': '卡尺边缘强度阈值：越高，只保留对比度更强的边缘。',
  'measure.caliper.polarity': '边缘极性：由暗到亮 / 由亮到暗 / 任意。明确方向时可排除反方向干扰。',
  'flow.if.operator': '比较条件（大于 / 小于 / 等于等）：决定 true / false 分支的走向。',
  'flow.if.threshold': '比较阈值：上游数值与该值按比较条件判断后进入对应分支。',
  'device.waitTag.timeoutMs': '等待信号超时（毫秒）：超时后节点失败，产线节拍受它约束。',
  'device.waitTag.pollMs': '轮询间隔（毫秒）：查询 PLC 标签值的频率；越小响应越快、负载越高。',
  'robot.executeTarget.timeoutMs': '动作超时（毫秒）：等待机器人到位信号的最长时间。',
  'robot.executeTarget.maxRetries': '失败重试次数：超时或未到位时最多重试几次。',
  'robot.executeTarget.retryDelayMs': '重试间隔（毫秒）：两次重试之间的等待时间。',
  'robot.executeTarget.waitForInPosition': '等待机器人到位信号后才继续下一步；关闭则发出指令即通过。',
  'robot.executeTarget.autoAck': '自动应答握手确认：关闭时需要 PLC 侧参与确认握手。'
};

const statusNames: Record<string, string> = {
  Running: '运行中', Succeeded: '成功', Success: '成功', Failed: '失败', Error: '错误', Idle: '空闲',
  Complete: '已完成', Completed: '已完成', Stopped: '已停止', Breakpoint: '断点暂停', Queued: '排队中',
  Cancelled: '已取消', PENDING: '未完成',
  Healthy: '正常', Warning: '警告', Critical: '严重', Disconnected: '未连接', Connected: '已连接',
  Connecting: '连接中', Faulted: '故障', Ready: '就绪', Busy: '忙碌',
  Administrator: '管理员', Engineer: '工程师', Operator: '操作员',
  OK: '合格', NG: '不合格', Unknown: '未知', Native: '原生', Fallback: '备用',
  Insufficient: '数据不足', Slow: '较慢', VerySlow: '很慢', Same: '无变化', Added: '新增', Missing: '缺失',
  TypeChanged: '类型已更改', StatusChanged: '状态已更改', OutputChanged: '输出已更改',
  PerformanceRegression: '性能下降', Within: '符合范围', Out: '超出范围'
};

/** Localize a backend catalog for the active language. English is a pass-through.
 *  `lang` defaults to Chinese so pre-existing single-argument call sites keep compiling. */
export function localizeCatalog(items: NodeCatalogItem[], lang: Lang = 'zh'): NodeCatalogItem[] {
  if (lang === 'en') return items;
  return items.map((item) => ({
    ...item,
    displayName: nodeNames[item.type] ?? item.displayName,
    category: categories[item.category] ?? item.category,
    description: descriptions[item.type] ?? item.description,
    inputs: item.inputs.map((port) => ({ ...port, label: portNames[port.name] ?? port.name })),
    outputs: item.outputs.map((port) => ({ ...port, label: portNames[port.name] ?? port.name })),
    parameters: item.parameters.map((parameter) => ({
      ...parameter,
      label: parameterNames[parameter.label] ?? parameter.label,
      description: parameterDescriptions[`${item.type}.${parameter.name}`] ?? parameter.description,
      options: parameter.options?.map((option) => ({ ...option, label: optionNames[option.label] ?? option.label }))
    }))
  }));
}

/** Localize a backend status word (role, health, lifecycle, diff kind, ...). English is a pass-through.
 *  `lang` defaults to Chinese so pre-existing single-argument call sites keep compiling. */
export const localizeStatus = (value: string | null | undefined, lang: Lang = 'zh') => {
  if (!value) return value;
  if (lang === 'en') return value;
  return statusNames[value] ?? value;
};
