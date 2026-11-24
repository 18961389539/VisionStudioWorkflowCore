import type { NodeCatalogItem, PortDescriptor } from './types';
import { portsCompatible } from './validation';

export type NodeCategory = 'acquisition' | 'preprocess' | 'feature' | 'measurement' | 'geometry' | 'calibration' | 'coordinate' | 'flow' | 'device' | 'robot' | 'script' | 'plugin';
export type PortRecommendation = { dataType: string; direction: 'source' | 'target' };
const categories: Record<NodeCategory, { order: number; color: string }> = {
  acquisition: { order: 0, color: '#52a9dc' }, preprocess: { order: 1, color: '#54b6ad' },
  feature: { order: 2, color: '#7896e8' }, measurement: { order: 3, color: '#c49a54' },
  geometry: { order: 4, color: '#c49a54' }, calibration: { order: 5, color: '#b48ad7' },
  coordinate: { order: 6, color: '#b48ad7' }, flow: { order: 7, color: '#9e8cde' },
  device: { order: 8, color: '#cd8d73' }, robot: { order: 9, color: '#cd8d73' },
  script: { order: 10, color: '#c9a86a' }, plugin: { order: 11, color: '#8e9baa' }
};
export function nodeCategory(type: string): NodeCategory {
  const normalized = type.trim().toLowerCase();
  if (['image.acquire', 'image.synthetic', 'camera.syncCapture', 'frameset.image'].includes(normalized)) return 'acquisition';
  if (normalized === 'jlvision.worldplane') return 'calibration';
  if (['jlvision.geometry', 'jlvision.hommat', 'jlvision.matrixop'].includes(normalized)) return 'geometry';
  if (['jlvision.measure1d', 'jlvision.contourfeature', 'jlvision.regionfeature'].includes(normalized)) return 'measurement';
  if (['jlvision.imageop', 'jlvision.threshold', 'jlvision.restoration', 'jlvision.surface'].includes(normalized)) return 'preprocess';
  if (normalized.startsWith('jlvision.')) return 'feature';
  if (normalized.startsWith('defect.')) return 'measurement';
  const prefix = normalized.split('.')[0];
  return ({ image: 'preprocess', color: 'preprocess', feature: 'feature', measure: 'measurement', geometry: 'geometry',
    calibration: 'calibration', coordinate: 'coordinate', flow: 'flow', device: 'device', robot: 'robot', script: 'script',
    match: 'feature', value: 'flow', logic: 'flow', output: 'device', http: 'device',
    recognition: 'feature', text: 'feature', ai: 'feature', inference: 'feature', contour: 'feature', region: 'feature', roi: 'measurement', collection: 'flow' } as Record<string, NodeCategory>)[prefix] ?? 'plugin';
}
export function nodePresentation(type: string) {
  const category = nodeCategory(type);
  return { category, ...categories[category] };
}
const aliases: Record<string, string[]> = {
  'image.acquire': ['相机', '拍照', '取图', 'camera', 'capture'], 'image.synthetic': ['测试图', '模拟图', 'synthetic'],
  'image.filter': ['图像滤波', '高斯滤波', '中值滤波', 'gaussian', 'median', 'blur'],
  'image.morphology': ['形态学', '开运算', '闭运算', '顶帽', '底帽', '形态学梯度'],
  'text.validate': ['文本校验', '字符串校验', '正则', 'OCR 校验', 'barcode validation'],
  'image.threshold': ['二值化', '阈值', '分割', '固定阈值', '自适应阈值', 'otsu', 'binarize', 'binary', 'threshold'],
  'image.channel': ['灰度', '灰度转换', '通道提取', 'channel', 'grayscale'],
  'region.fromRange': ['灰度范围', '颜色范围', '区域范围分割', '阈值区域', '颜色分割', 'range segmentation'],
  'measure.blobs': ['斑点', '连通域', '最大斑点', 'blob', 'largest blob'],
  'feature.edge': ['边缘', '找边', 'canny'], 'feature.circle': ['找圆', '圆检测', 'circle'],
  'feature.line': ['找线', '直线', 'line'], 'measure.blob': ['斑点', '连通域', 'blob'],
  'measure.contourThickness': ['壁厚', '最薄', '凹陷', 'thickness', 'wall'],
  'measure.caliper': ['卡尺', 'caliper'], 'measure.caliperWidth': ['卡尺', '测宽', '线宽', '槽宽', '间隙', 'width', 'gap'], 'geometry.distance': ['测距', '距离', 'distance'],
  'flow.if': ['条件', '分支', 'if'], 'flow.parallel': ['并行', 'parallel'],
  'flow.join': ['汇合', '合并', 'join'], 'flow.result': ['判定', '结果', 'ok', 'ng'],
  'device.readTag': ['plc', '读取', 'read'], 'device.writeTag': ['plc', '写入', 'write'],
  'camera.syncCapture': ['多相机', '同步', 'sync'], 'calibration.planar': ['标定', 'calibrate'],
  'color.classify': ['颜色', '色彩', '聚类', '分类', 'color', 'kmeans'],
  'image.denoise': ['降噪', '去噪', 'denoise', 'nlm'],
  'image.stitch': ['拼接', '全景', 'stitch', 'panorama'],
  'recognition.aruco': ['aruco', '标记', '定位标记', 'apriltag'],
  'contour.convexity': ['凸度', '凸性', '实心度', 'solidity'],
  'contour.toPoints': ['轮廓转点列', '轮廓转点集', '边界采样'],
  'contour.matchXld': ['轮廓匹配', 'xld 匹配', '轮廓定位'],
  'coordinate.transformAffine': ['仿射坐标', '仿射矩阵', 'affine transform'],
  'feature.superpixel': ['超像素', 'slic', 'seeds'],
  'geometry.lineConstruct': ['两点画线', '两点构线', '生成直线'],
  'measure.arcFit': ['圆弧拟合', '拟合圆弧', 'arc fit'],
  'measure.curvature': ['曲率', '轮廓弯曲', 'curvature'],
  'measure.contourDistance': ['轮廓间距', '轮廓测距', '豪斯多夫距离', 'hausdorff'],
  'tolerance.drf': ['基准交点', '基准框架', 'datum reference frame'],
  'measure.mzCircle': ['最小区域圆度', '最小区域法', 'minimum zone circle'],
  'measure.profileFilter': ['轮廓滤波', '波纹度滤波', '高斯轮廓滤波'],
  'measure.roughness': ['粗糙度', 'ra rq rz', 'roughness'],
  'measure.gear': ['齿轮测量', '直齿轮', '节圆直径', 'spur gear'],
  'measure.thread': ['螺距', '估算中径', '螺纹中径', '牙型角', 'thread measurement'],
  'tolerance.stackup': ['公差叠加', '尺寸链', 'rss', 'tolerance stack-up'],
  'image.bilateral': ['双边滤波', '保边降噪', 'bilateral'],
  'image.channelMix': ['通道混合', '通道加权', 'rgb 加权'],
  'image.clahe': ['局部直方图均衡', '局部对比度增强', 'clahe'],
  'image.depthScale': ['位深转换', '16 位转 8 位', 'depth scale'],
  'image.qualityMetrics': ['图像质量', '清晰度检测', '模糊检测', 'image quality'],
  'image.polarUnwrap': ['极坐标展开', '环形展开', '圆环展开', 'polar unwrap'],
  'image.whiteBalance': ['色温校正', '白平衡', 'white balance'],
  'region.fromContour': ['轮廓转区域', '轮廓栅格化', '轮廓填充'],
  'region.toMask': ['区域转掩膜', '区域转掩码', '生成蒙版'],
  'region.fromMask': ['掩膜转区域', '概率图转区域'],
  'contour.fromMask': ['掩膜转轮廓', '概率图转轮廓'],
  'roi.fromDetection': ['检测框转 roi', '目标框转选区', '目标裁剪区域'],
  'roi.fromGeometry': ['几何转 roi', '图形转选区', '几何生成选区'],
  'script.csharp': ['脚本', 'script', 'c#', '代码', 'code']
};
const mergedLegacyTypes = new Set([
  'image.blur.gaussian', 'image.blur.median', 'image.topHat', 'image.bottomHat', 'image.morphGradient',
  'text.match', 'text.regex',
  'image.threshold.adaptive', 'image.threshold.otsu', 'image.grayscale',
  'region.fromThreshold', 'region.fromColorRange', 'measure.blob'
]);
function score(item: NodeCatalogItem, keyword: string): number {
  const name = item.displayName.toLowerCase();
  const type = item.type.toLowerCase();
  if (name === keyword) return 100;
  if (name.startsWith(keyword)) return 90;
  if (name.includes(keyword)) return 80;
  if ((aliases[item.type] ?? []).some((alias) => alias === keyword)) return 70;
  if ((aliases[item.type] ?? []).some((alias) => alias.includes(keyword))) return 60;
  if (type === keyword) return 55;
  if (type.includes(keyword)) return 50;
  if (item.category.toLowerCase().includes(keyword)) return 30;
  return (item.description ?? '').toLowerCase().includes(keyword) ? 20 : 0;
}
export function searchTools(catalog: NodeCatalogItem[], query: string): NodeCatalogItem[] {
  const keyword = query.trim().toLowerCase();
  return catalog.map((item, index) => ({ item, index, score: keyword ? score(item, keyword) : 1 }))
    .filter((entry) => entry.score > 0 && !mergedLegacyTypes.has(entry.item.type))
    .sort((a, b) => b.score - a.score || nodePresentation(a.item.type).order - nodePresentation(b.item.type).order || a.index - b.index)
    .map((entry) => entry.item);
}
export function compatiblePorts(item: NodeCatalogItem, connection: PortRecommendation): PortDescriptor[] {
  const ports = connection.direction === 'source' ? item.inputs : item.outputs;
  // Control ports must never be suggested for an Any data port.
  return ports.filter((port) => (port.dataType === 'Control') === (connection.dataType === 'Control') &&
    (connection.direction === 'source' ? portsCompatible(connection.dataType, port.dataType) : portsCompatible(port.dataType, connection.dataType)));
}
