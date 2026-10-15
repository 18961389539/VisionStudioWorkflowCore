/** 摘要标量压缩：节点 footer 与运行结果面板共用同一套“标量内联”规则（数组/对象不入内联行）。 */

export const isScalar = (value: unknown): value is string | number | boolean | null =>
  value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';

export function formatScalar(value: string | number | boolean | null) {
  if (value === null) return '—';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(3)));
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return value;
}

/** 各节点类型的“关键结果”字段顺序：先展示用户真正关心的测量量，而不是摘要里的前 N 个键。
 *  键取自后端各节点 Summary（backend/src/VisionStudio.Engine/Nodes/*.cs）；未列出的类型走通用排序。 */
const PRIMARY_SUMMARY_KEYS: Record<string, string[]> = {
  'image.filter': ['method', 'kernelSize', 'sigmaX', 'sigmaY'], 'image.morphology': ['operation', 'kernelSize', 'kernelWidth', 'kernelHeight'],
  'image.topHat': ['kernelWidth', 'kernelHeight'], 'image.bottomHat': ['kernelWidth', 'kernelHeight'],
  'image.morphGradient': ['kernelWidth', 'kernelHeight'], 'measure.grayHistogram': ['found', 'pixelCount', 'entropy'],
  'region.fromRoi': ['found', 'area', 'count'], 'region.convexHull': ['area', 'count'],
  'region.boundary': ['area', 'count'], 'region.skeleton': ['area', 'count'],
  'contour.fromPoints': ['found', 'sampleCount', 'closed'], 'measure.wallThickness': ['found', 'minThickness', 'maxThickness'],
  'measure.concentricity': ['centerDistance', 'diametralOffset'], 'measure.roundness': ['found', 'deviation', 'rms'],
  'measure.straightness': ['found', 'deviation', 'rms'], 'measure.contourDeviation': ['found', 'deviation', 'mean'],
  'measure.parallelism': ['angleError', 'deviation', 'measuredLength'], 'measure.perpendicularity': ['angleError', 'deviation', 'measuredLength'],
  'image.colorConvert': ['space', 'channelNames'], 'region.fromRange': ['found', 'area', 'count'], 'region.fromColorRange': ['found', 'area', 'count'],
  'measure.colorDifference': ['found', 'deltaE', 'maxDeltaE'], 'image.contrastNormalize': ['inputMin', 'inputMax', 'rangeCollapsed'],
  'image.illuminationNormalize': ['windowSize', 'targetMean'], 'image.sharpen': ['amount', 'windowSize'],
  'text.validate': ['pass', 'mode', 'textLength', 'groupCount'], 'text.match': ['pass', 'mode', 'textLength'], 'text.regex': ['pass', 'groupCount', 'textLength'],
  'image.register': ['found', 'score'], 'image.warpAffine': ['width', 'height', 'interpolation'],
  'region.morphology': ['count', 'area'], 'region.transform': ['count', 'area'],
  'region.properties': ['found', 'area', 'orientation'], 'region.toDetections': ['count'],
  'measure.lineSpacing': ['found', 'distance', 'sampleCount'], 'calibration.detectBoard': ['found', 'count', 'views'],
  'image.flatFieldCorrect': ['targetMean', 'minGain', 'outputType'],
  'image.dynamicThreshold': ['found', 'area', 'offset'], 'image.difference': ['meanDifference', 'minDifference', 'maxDifference'],
  'defect.referenceCompare': ['found', 'count', 'area'], 'measure.regionIntensity': ['found', 'mean', 'stdDev'],
  'region.fromThreshold': ['found', 'area', 'count'], 'region.connectedComponents': ['count', 'area'],
  'region.filter': ['count', 'area'], 'region.boolean': ['area', 'count'],
  'region.fillHoles': ['area', 'count'], 'image.reduceDomain': ['area', 'width', 'height'],
  'contour.extract': ['found', 'count', 'holes'], 'contour.filter': ['count', 'dropped'],
  'contour.properties': ['area', 'circularity', 'selectedIndex'],
  'feature.edgeSubpixel': ['found', 'edgeX', 'edgeY'], 'measure.caliperLine': ['found', 'fitError', 'sampleCount'],
  'measure.caliperCircle': ['found', 'fittedRadius', 'fitError'], 'measure.fitEllipse': ['width', 'height', 'angleDeg'],
  'match.shape': ['found', 'bestScore', 'matchedAngle'], 'roi.transform': ['roiType', 'thetaDeg'],
  'measure.blobs': ['mode', 'count', 'totalArea'], 'match.template': ['found', 'score', 'angle'],
  'value.arithmetic': ['result'], 'value.range': ['pass', 'value'], 'logic.boolean': ['result'],
  'flow.ifBoolean': ['condition'], 'flow.switch': ['selected'],
  'output.saveImage': ['path', 'bytesWritten'], 'output.exportResult': ['path', 'bytesWritten'],
  'recognition.barcode': ['count'], 'recognition.ocr': ['text', 'confidence'],
  'image.acquire': ['size', 'camera', 'sequence'],
  'frameset.image': ['cameraId', 'sequence', 'triggerSkewUs'],
  'camera.syncCapture': ['withinTolerance', 'triggerSkewUs', 'cameraCount'],
  'image.synthetic': ['size', 'circle'],
  'image.threshold': ['threshold'],
  'image.channel': ['channel'],
  'measure.blob': ['area', 'radius', 'x', 'y'],
  'measure.caliper': ['x', 'y', 'strength'],
  'measure.rotatedRect': ['width', 'height', 'angleDeg'],
  'feature.edge': ['edgePixels'],
  'feature.line': ['length', 'angleDeg'],
  'feature.circle': ['radius', 'x', 'y'],
  'geometry.distance': ['distance'],
  'geometry.angle': ['angleDeg'],
  'geometry.intersection': ['x', 'y'],
  'flow.if': ['condition', 'value'],
  'flow.parallel': ['branches'],
  'flow.join': ['joined', 'parallelDisposition'],
  'flow.result': ['disposition', 'pass'],
  'device.readTag': ['value', 'quality'],
  'device.waitTag': ['matched', 'waitMs'],
  'device.writeTag': ['writtenValue'],
  'device.writeVisionResult': ['pass', 'x', 'y'],
  'coordinate.pose': ['pose', 'frame'],
  'robot.currentPose': ['pose', 'state', 'connection'],
  'robot.guidance2d': ['target', 'robot', 'mode'],
  'robot.j4TcpCompensation': ['commandPivot', 'eccentricity'],
  'robot.executeTarget': ['state', 'inPosition', 'commandId']
};

/** 低信息量的元数据键（单位 / 类型标注 / 时间戳 …）：没有主字段命中时才兜底展示。 */
const META_SUMMARY_KEYS = new Set([
  'algorithmBackend', 'modelCacheHit', 'roi', 'unit', 'source', 'frame', 'pixelFormat', 'frameMode', 'timestamp', 'timestampBasis',
  'centerType', 'circleType', 'resultReadyTag', 'tagId', 'dataType', 'writeOrder', 'replay'
]);

const isMetaKey = (key: string) => META_SUMMARY_KEYS.has(key) || key.endsWith('Type');

/** 按“主字段 → 普通字段 → 元数据字段”排出的摘要键顺序。 */
export function pickSummaryKeys(
  summary: Record<string, unknown> | undefined | null,
  typeKey?: string,
  max = 3,
): string[] {
  const keys = Object.entries(summary ?? {}).filter(([, value]) => isScalar(value)).map(([key]) => key);
  if (keys.length === 0) return [];
  const primary = (typeKey ? PRIMARY_SUMMARY_KEYS[typeKey] ?? [] : []).filter((key) => keys.includes(key));
  const rest = keys.filter((key) => !primary.includes(key));
  return [...primary, ...rest.filter((key) => !isMetaKey(key)), ...rest.filter(isMetaKey)].slice(0, max);
}

/** 取摘要中前 max 个标量键值对拼成一行；keyLabel 允许调用方本地化键名（如端口名中文映射）。
 *  typeKey 给定时按该节点类型的主结果字段优先排序（measure.blob 先看面积而不是 x/y）。 */
export function summarizeScalars(
  summary: Record<string, unknown> | undefined | null,
  max = 3,
  keyLabel?: (key: string) => string,
  typeKey?: string,
): string {
  return pickSummaryKeys(summary, typeKey, max)
    .map((key) => `${keyLabel ? keyLabel(key) : key}=${formatScalar(summary?.[key] as string | number | boolean | null)}`)
    .join(' · ');
}
