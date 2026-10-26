import { nodeCategory } from '../toolboxModel';

type NodeIconName =
  | 'camera' | 'image' | 'sparkles' | 'layers' | 'sliders' | 'threshold' | 'histogram' | 'blur'
  | 'crop' | 'resize' | 'rotate' | 'perspective' | 'lens' | 'mask' | 'difference' | 'light'
  | 'palette' | 'sharpen' | 'edge' | 'circle' | 'line' | 'ruler' | 'caliper' | 'rings' | 'target'
  | 'ellipse' | 'corner' | 'crosshair' | 'compare' | 'barcode' | 'text' | 'inference' | 'contour'
  | 'region' | 'hull' | 'boundary' | 'skeleton' | 'detections' | 'grid' | 'axes' | 'transform'
  | 'branch' | 'switch' | 'merge' | 'parallel' | 'loop' | 'delay' | 'retry' | 'check' | 'number'
  | 'device' | 'deviceRead' | 'deviceWrite' | 'robot' | 'robotPose' | 'robotMove' | 'puzzle' | 'file' | 'surface'
  | 'log' | 'stop';

const glyphs: Record<NodeIconName, string[]> = {
  camera: ['M3 7h4l2-3h6l2 3h4v13H3z', 'M16 13a4 4 0 1 1-8 0 4 4 0 0 1 8 0'],
  image: ['M4 4h16v16H4z', 'm5 16 5-5 3 3 2-2 5 5', 'M8 8h.01'],
  sparkles: ['M3 5h14v14H3z', 'm4 16 4-4 3 3 2-2 3 3', 'M19 3v5M16.5 5.5h5M20 11v4M18 13h4'],
  layers: ['m12 3 9 5-9 5-9-5 9-5z', 'm3 12 9 5 9-5', 'm3 16 9 5 9-5'],
  sliders: ['M4 6h16M4 12h16M4 18h16', 'M8 3v6M16 9v6M10 15v6'],
  threshold: ['M3 4h18v16H3z', 'M5 15h3v3H5zM10 11h3v7h-3zM15 7h3v11h-3z'],
  histogram: ['M4 20V13h4v7M10 20V6h4v14M16 20V3h4v17', 'M3 21h18'],
  blur: ['M12 3c-3 4-6 7-6 11a6 6 0 0 0 12 0c0-4-3-7-6-11z', 'M9 15a3 3 0 0 0 3 3'],
  crop: ['M6 3v15h15M3 6h15v15'],
  resize: ['M4 9V4h5M20 15v5h-5M4 4l6 6M20 20l-6-6', 'M4 15v5h5M20 9V4h-5'],
  rotate: ['M20 7v5h-5', 'M20 12a8 8 0 1 1-2.3-5.7L20 12z'],
  perspective: ['M5 5h14v14H5z', 'm5 5 3 3m11-3-3 3m-8 8-3 3m11-3 3 3'],
  lens: ['M3 6h18v12H3z', 'M7 6a5 5 0 0 0 0 12M17 6a5 5 0 0 1 0 12'],
  mask: ['M4 5h16v14H4z', 'M6 7h5v5H6zM13 7h5v2h-5zM13 12h5v5h-5zM6 14h5v3H6z'],
  difference: ['M4 4h10v10H4zM10 10h10v10H10z', 'M7 17h.01M17 7h.01'],
  light: ['M9 18h6M10 21h4', 'M8.5 15a7 7 0 1 1 7 0c-.8.6-1.2 1.3-1.4 2h-4.2c-.2-.7-.6-1.4-1.4-2z', 'M12 2v2M4.9 4.9l1.4 1.4M19.1 4.9l-1.4 1.4'],
  palette: ['M12 3a9 9 0 1 0 0 18h1a2 2 0 0 0 1.4-3.4 2 2 0 0 1 1.4-3.4H18a3 3 0 0 0 3-3 8.2 8.2 0 0 0-9-8.2z', 'M7.5 11h.01M10 7.5h.01M15 7.5h.01M17 11h.01'],
  sharpen: ['M12 3l2.2 6.8L21 12l-6.8 2.2L12 21l-2.2-6.8L3 12l6.8-2.2L12 3z', 'M19 2v4M17 4h4'],
  edge: ['M3 19h5l3-6 3 3 4-9 3 3', 'M4 5h.01M20 19h.01'],
  circle: ['M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z', 'M12 12h.01'],
  line: ['M4 19 19 4', 'M3 16v5h5M16 3h5v5'],
  ruler: ['M3 7h18v10H3z', 'M7 7v5M11 7v3M15 7v5M19 7v3'],
  caliper: ['M5 4v16M19 4v16M5 8h9a3 3 0 0 1 3 3v2a3 3 0 0 1-3 3H5', 'M19 8h-2M19 16h-2'],
  rings: ['M12 12m-8 0a8 8 0 1 0 16 0a8 8 0 1 0-16 0', 'M12 12m-4 0a4 4 0 1 0 8 0a4 4 0 1 0-8 0', 'M12 12h.01'],
  target: ['M12 3v4M12 17v4M3 12h4M17 12h4', 'M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10z', 'M12 12h.01'],
  ellipse: ['M12 4a8 4 0 1 0 0 16 8 4 0 0 0 0-16z', 'M12 4v16'],
  corner: ['M5 19V5h14', 'm8 16 8-8'],
  crosshair: ['M3 12h18M12 3v18', 'M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10z'],
  compare: ['M4 5h7v14H4zM13 5h7v14h-7z', 'M7 8v8M16 8v8'],
  barcode: ['M4 5v14M7 5v14M11 5v14M14 5v14M17 5v14M20 5v14'],
  text: ['M4 5h16M12 5v14M8 19h8', 'M6 9h3M15 9h3'],
  inference: ['M8 3v3M16 3v3M8 18v3M16 18v3M3 8h3M3 16h3M18 8h3M18 16h3', 'M7 6h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2z', 'M9 10h2v4H9zM14 10h2v4h-2z'],
  contour: ['M4 7c2-3 4 1 6-1s4-3 5 0 4 0 5 3-2 4-1 6-3 5-5 3-4 2-6 0-5-2-4-5-2-4 0-6z', 'M8 12h.01'],
  region: ['M4 5h16v14H4z', 'M7 8h10v8H7z'],
  hull: ['M4 18 6 7l6-3 8 6-2 9-9 2z', 'M6 7l12 12M12 4l-3 17'],
  boundary: ['M5 5h14v14H5z', 'M8 8h8v8H8z'],
  skeleton: ['M4 5h16v14H4z', 'm5 18 5-5 3 2 6-8'],
  detections: ['M4 5h6v6H4zM14 5h6v6h-6zM4 15h6v5H4zM14 15h6v5h-6z'],
  grid: ['M3 3h18v18H3zM3 9h18M3 15h18M9 3v18M15 3v18'],
  axes: ['M5 4v15h15M5 19l6-6 4 2 6-8', 'M3 6l2-2 2 2M18 19l2 0-1-2'],
  transform: ['M4 6h7v7H4zM13 11h7v7h-7z', 'M11 9l3 3M9 13l-3 3'],
  branch: ['M12 3v6M12 9l-7 5v7M12 9l7 5v7', 'M2 21h6M16 21h6'],
  switch: ['M5 4v4c0 2 2 4 4 4h6M5 20v-4c0-2 2-4 4-4', 'm15 5 4 3-4 3M15 16l4 4-4 3'],
  merge: ['M5 4v4c0 2 2 4 4 4h6M5 20v-4c0-2 2-4 4-4M15 8l4 4-4 4', 'M5 4h.01M5 20h.01'],
  parallel: ['M5 5h5v5H5zM14 5h5v5h-5zM9.5 15h5v5h-5z', 'M7.5 10v2h9v-2M12 12v3'],
  loop: ['M5 8a8 8 0 0 1 13-2l2 2', 'M20 3v5h-5', 'M19 16a8 8 0 0 1-13 2l-2-2', 'M4 21v-5h5'],
  delay: ['M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z', 'M12 7v5l3 2'],
  retry: ['M20 7v5h-5', 'M20 12a8 8 0 1 0 1.2 4'],
  check: ['M5 12h4l3-7 3 14 2-7h2', 'M4 3h16v18H4z'],
  number: ['M5 8h14M5 16h14', 'M8 4 6 20M18 4l-2 16'],
  device: ['M5 4h14v16H5z', 'M9 8h6v8H9z', 'M2 8h3M2 12h3M2 16h3M19 8h3M19 12h3M19 16h3'],
  deviceRead: ['M5 4h14v16H5z', 'M9 8h6M9 12h3M12 17l-3-3 3-3M3 17h6'],
  deviceWrite: ['M5 4h14v16H5z', 'M9 8h6M9 12h3M12 11l3 3-3 3M12 14H7'],
  robot: ['M5 21h14M8 21v-5l6-5-5-5M5 6a2 2 0 1 0 4 0 2 2 0 0 0-4 0M14 11l4-6 3 2-2 4'],
  robotPose: ['M12 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6z', 'M6 21v-3a6 6 0 0 1 12 0v3', 'M2 12h3M19 12h3'],
  robotMove: ['M5 19h14M7 19v-5l5-4 3-6 4 2-2 5-5 3', 'M3 5h5M5 3v5'],
  puzzle: ['M9 3H3v6h3a3 3 0 0 1 0 6H3v6h6v-3a3 3 0 0 1 6 0v3h6v-6h-3a3 3 0 0 1 0-6h3V3h-6v3a3 3 0 0 1-6 0z'],
  log: ['M4 5h16v14H4z', 'M7 9h10M7 13h10M7 17h6'],
  stop: ['M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18', 'm8.5 8.5 7 7m0-7-7 7'],
  file: ['M6 3h8l4 4v14H6z', 'M14 3v5h5', 'M9 13l2 2 4-4'],
  surface: ['M3 18 9 12l6 3 6-8', 'M3 18h18M9 12v6M15 15v4', 'M3 6l6 3M15 3l6 2']
};

const exactIcons: Record<string, NodeIconName> = {
  'image.filter': 'blur', 'text.validate': 'text',
  'image.acquire': 'camera', 'camera.syncCapture': 'camera', 'image.synthetic': 'sparkles', 'frameset.image': 'layers',
  'image.grayscale': 'image', 'image.channel': 'sliders', 'image.morphology': 'region',
  'image.threshold': 'threshold', 'image.threshold.adaptive': 'threshold', 'image.threshold.otsu': 'histogram',
  'image.blur.gaussian': 'blur', 'image.blur.median': 'blur', 'image.crop': 'crop', 'image.resize': 'resize',
  'image.rotate': 'rotate', 'image.perspective': 'perspective', 'image.register': 'target', 'image.warpAffine': 'transform',
  'image.undistort': 'lens', 'image.reduceDomain': 'mask', 'image.difference': 'difference', 'image.flatFieldCorrect': 'light',
  'image.dynamicThreshold': 'threshold', 'image.colorConvert': 'palette', 'image.contrastNormalize': 'histogram',
  'image.illuminationNormalize': 'light', 'image.sharpen': 'sharpen', 'image.topHat': 'sparkles',
  'image.bottomHat': 'blur', 'image.morphGradient': 'edge',
  'region.fromRange': 'palette', 'region.fromThreshold': 'threshold', 'region.fromColorRange': 'palette', 'region.fromRoi': 'region',
  'region.connectedComponents': 'detections', 'region.filter': 'sliders', 'region.boolean': 'merge', 'region.fillHoles': 'region',
  'region.morphology': 'region', 'region.properties': 'histogram', 'region.toDetections': 'detections',
  'region.transform': 'transform', 'region.convexHull': 'hull', 'region.boundary': 'boundary', 'region.skeleton': 'skeleton',
  'contour.extract': 'contour', 'contour.fromPoints': 'line', 'contour.filter': 'sliders', 'contour.properties': 'histogram',
  'feature.edge': 'edge', 'feature.edgeSubpixel': 'edge', 'feature.circle': 'circle', 'feature.line': 'line',
  'match.template': 'target', 'match.shape': 'target', 'roi.transform': 'transform',
  'measure.blob': 'detections', 'measure.blobs': 'detections', 'measure.caliper': 'caliper',
  'measure.caliperWidth': 'caliper', 'measure.caliperLine': 'caliper', 'measure.caliperCircle': 'caliper', 'measure.pointLineDistance': 'ruler',
  'measure.circleSpacing': 'rings', 'measure.lineSpacing': 'ruler', 'measure.fitLine': 'line', 'measure.fitCircle': 'circle',
  'measure.fitEllipse': 'ellipse', 'measure.rotatedRect': 'region', 'measure.wallThickness': 'rings',
  'measure.contourThickness': 'rings', 'measure.concentricity': 'target', 'measure.roundness': 'circle', 'measure.straightness': 'line',
  'measure.contourDeviation': 'compare', 'measure.parallelism': 'parallel', 'measure.perpendicularity': 'corner',
  'measure.colorDifference': 'palette', 'measure.regionIntensity': 'histogram', 'measure.grayHistogram': 'histogram',
  'geometry.angle': 'corner', 'geometry.intersection': 'crosshair', 'geometry.distance': 'ruler',
  'calibration.planar': 'grid', 'calibration.cameraIntrinsics': 'lens', 'calibration.handEye2D': 'robotPose', 'calibration.handEye3D': 'robotPose',
  'calibration.detectBoard': 'grid', 'coordinate.transformCompose': 'transform', 'coordinate.transformConstant': 'transform',
  'coordinate.distance': 'ruler', 'coordinate.point': 'crosshair', 'coordinate.point3D': 'crosshair', 'coordinate.transformPoint3D': 'transform', 'coordinate.pose': 'robotPose', 'coordinate.frameTree': 'axes',
  'coordinate.transformInverse': 'transform', 'coordinate.poseToTransform': 'transform', 'coordinate.frameResolve': 'axes',
  'coordinate.transformCoordinatePoint': 'crosshair', 'coordinate.transformCoordinatePose': 'robotPose',
  'coordinate.transformLine': 'line', 'coordinate.transformPoint': 'crosshair', 'coordinate.transformPose': 'robotPose',
  'device.readTag': 'deviceRead', 'device.waitTag': 'delay', 'device.writeTag': 'deviceWrite', 'device.writeVisionResult': 'deviceWrite',
  'robot.executeTarget': 'robotMove', 'robot.currentPose': 'robotPose', 'robot.guidance2d': 'target',
  'robot.j4TcpCompensation': 'robot', 'flow.if': 'branch', 'flow.ifBoolean': 'branch', 'flow.switch': 'switch',
  'flow.join': 'merge', 'flow.merge': 'merge', 'flow.parallel': 'parallel', 'flow.forEach': 'loop',
  'flow.loopEnd': 'loop', 'flow.while': 'loop', 'flow.repeat': 'loop', 'flow.break': 'branch',
  'flow.delay': 'delay', 'flow.try': 'retry', 'flow.result': 'check',
  'flow.log': 'log', 'flow.fail': 'stop',
  'value.number': 'number', 'value.arithmetic': 'number', 'value.range': 'check', 'value.string': 'text', 'logic.boolean': 'branch',
  'collection.detections.filter': 'sliders', 'collection.detections.sort': 'detections',
  'collection.detections.at': 'crosshair', 'collection.detections.count': 'histogram',
  'recognition.barcode': 'barcode', 'recognition.ocr': 'text', 'text.match': 'text', 'text.regex': 'text',
  'inference.classification': 'inference', 'inference.detection': 'detections', 'inference.segmentation': 'region', 'inference.anomaly': 'target',
  'output.saveImage': 'image', 'output.exportResult': 'histogram', 'output.httpRequest': 'deviceWrite', 'module.call': 'layers',
  'jlvision.worldPlane': 'perspective', 'jlvision.contourOp': 'contour', 'jlvision.dxf': 'file',
  'jlvision.featureExtract': 'edge', 'defect.referenceCompare': 'compare',
  'jlvision.geometry': 'corner', 'jlvision.homMat': 'transform', 'jlvision.matrixOp': 'grid',
  'jlvision.measure1d': 'caliper', 'jlvision.contourFeature': 'contour', 'jlvision.regionFeature': 'histogram',
  'jlvision.imageOp': 'sliders', 'jlvision.threshold': 'threshold', 'jlvision.restoration': 'sharpen',
  'jlvision.surface': 'surface', 'jlvision.regionOp': 'region', 'jlvision.regionAnalysis': 'detections',
  'jlvision.segmentation': 'mask'
};

const normalizedExactIcons = Object.fromEntries(
  Object.entries(exactIcons).map(([type, icon]) => [type.toLowerCase(), icon])
) as Record<string, NodeIconName>;

const prefixIcons: Array<[string, NodeIconName]> = [
  ['device.', 'device'], ['camera.', 'camera'], ['robot.', 'robot'], ['calibration.', 'grid'], ['coordinate.', 'axes'], ['geometry.', 'corner'],
  ['measure.', 'ruler'], ['region.', 'region'], ['contour.', 'contour'], ['feature.', 'edge'], ['match.', 'target'],
  ['recognition.', 'text'], ['inference.', 'inference'], ['collection.', 'detections'], ['flow.', 'branch'],
  ['logic.', 'branch'], ['value.', 'number'], ['output.', 'image'], ['image.', 'image']
];

const categoryIcons: Record<string, NodeIconName> = {
  acquisition: 'camera', preprocess: 'sliders', feature: 'edge', measurement: 'ruler', geometry: 'corner',
  calibration: 'grid', coordinate: 'axes', flow: 'branch', device: 'device', robot: 'robot', plugin: 'puzzle'
};

export function iconForNodeType(type: string): NodeIconName {
  const normalized = type.trim().toLowerCase();
  const exact = normalizedExactIcons[normalized];
  if (exact) return exact;
  const prefix = prefixIcons.find(([value]) => normalized.startsWith(value));
  return prefix?.[1] ?? categoryIcons[nodeCategory(normalized)];
}

export default function NodeCategoryIcon({ type }: { type: string }) {
  const icon = iconForNodeType(type);
  return <span className="node-icon-badge" aria-hidden="true">
    <svg className="node-category-icon" data-icon={icon} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      {glyphs[icon].map((path, index) => <path key={index} d={path} />)}
    </svg>
  </span>;
}
