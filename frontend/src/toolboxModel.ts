import type { NodeCatalogItem, PortDescriptor } from './types';
import { portsCompatible } from './validation';

export type NodeCategory = 'acquisition' | 'preprocess' | 'feature' | 'measurement' | 'geometry' | 'calibration' | 'coordinate' | 'flow' | 'device' | 'robot' | 'plugin';
export type PortRecommendation = { dataType: string; direction: 'source' | 'target' };
const categories: Record<NodeCategory, { order: number; color: string }> = {
  acquisition: { order: 0, color: '#52a9dc' }, preprocess: { order: 1, color: '#54b6ad' },
  feature: { order: 2, color: '#7896e8' }, measurement: { order: 3, color: '#c49a54' },
  geometry: { order: 4, color: '#c49a54' }, calibration: { order: 5, color: '#b48ad7' },
  coordinate: { order: 6, color: '#b48ad7' }, flow: { order: 7, color: '#9e8cde' },
  device: { order: 8, color: '#cd8d73' }, robot: { order: 9, color: '#cd8d73' },
  plugin: { order: 10, color: '#8e9baa' }
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
  return ({ image: 'preprocess', feature: 'feature', measure: 'measurement', geometry: 'geometry',
    calibration: 'calibration', coordinate: 'coordinate', flow: 'flow', device: 'device', robot: 'robot',
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
  'camera.syncCapture': ['多相机', '同步', 'sync'], 'calibration.planar': ['标定', 'calibrate']
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
