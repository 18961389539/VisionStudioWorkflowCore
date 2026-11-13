import { control, data, node, type DemoGraph } from './shared';

/** 几何测量类示例：找圆/卡尺/精度定位/区域处理。 */

export const measurementDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 20, 250),
    node('edge-1', 'feature.edge', 260, 430, { lowThreshold: 45, highThreshold: 130, roi: { type: 'Rectangle', x: 20, y: 340, width: 600, height: 120 } }),
    node('line-a', 'feature.line', 300, 80, { houghThreshold: 25, minLength: 60, maxGap: 15, roi: { type: 'Rectangle', x: 20, y: 340, width: 600, height: 120 } }),
    node('line-b', 'feature.line', 300, 260, { houghThreshold: 20, minLength: 50, maxGap: 8, roi: { type: 'Rectangle', x: 55, y: 50, width: 150, height: 105 } }),
    node('intersection-1', 'geometry.intersection', 610, 120),
    node('angle-1', 'geometry.angle', 610, 300),
    node('circle-1', 'feature.circle', 900, 80, { minRadius: 55, maxRadius: 110, minDist: 40, edgeThreshold: 100, centerThreshold: 18 }),
    node('distance-1', 'geometry.distance', 1210, 120),
    node('caliper-1', 'measure.caliper', 900, 320, { startX: 120, startY: 240, endX: 520, endY: 240, sampleWidth: 7, edgeThreshold: 10, polarity: 'Any' }),
    node('threshold-1', 'image.threshold', 1210, 320, { threshold: 80 }),
    node('rect-1', 'measure.rotatedRect', 1510, 320, { minArea: 2000 })
  ],
  edges: [
    control('c0', 'source-1', 'next', 'edge-1', 'exec'),
    data('d0', 'source-1', 'image', 'edge-1', 'image'),
    control('c1', 'edge-1', 'next', 'line-a', 'exec'),
    data('d1', 'edge-1', 'image', 'line-a', 'image'),
    control('c2', 'line-a', 'next', 'line-b', 'exec'),
    data('d2', 'source-1', 'image', 'line-b', 'image'),
    control('c3', 'line-b', 'next', 'intersection-1', 'exec'),
    data('d3', 'line-a', 'line', 'intersection-1', 'lineA'),
    data('d4', 'line-b', 'line', 'intersection-1', 'lineB'),
    control('c4', 'intersection-1', 'next', 'angle-1', 'exec'),
    data('d5', 'line-a', 'line', 'angle-1', 'lineA'),
    data('d6', 'line-b', 'line', 'angle-1', 'lineB'),
    control('c5', 'angle-1', 'next', 'circle-1', 'exec'),
    data('d7', 'source-1', 'image', 'circle-1', 'image'),
    control('c6', 'circle-1', 'next', 'distance-1', 'exec'),
    data('d8', 'intersection-1', 'point', 'distance-1', 'pointA'),
    data('d9', 'circle-1', 'center', 'distance-1', 'pointB'),
    control('c7', 'distance-1', 'next', 'caliper-1', 'exec'),
    data('d10', 'source-1', 'image', 'caliper-1', 'image'),
    control('c8', 'caliper-1', 'next', 'threshold-1', 'exec'),
    data('d11', 'source-1', 'image', 'threshold-1', 'image'),
    control('c9', 'threshold-1', 'next', 'rect-1', 'exec'),
    data('d12', 'threshold-1', 'image', 'rect-1', 'image')
  ]
});

export const contourThicknessDemo = (): DemoGraph => ({
  nodes: [
    node('outer', 'region.fromRoi', 20, 80, {"canvasWidth":400,"canvasHeight":400,"roi":{"type":"Rectangle","x":100,"y":100,"width":200,"height":200}}),
    node('notch', 'region.fromRoi', 290, -160, {"canvasWidth":400,"canvasHeight":400,"roi":{"type":"Rectangle","x":275,"y":180,"width":25,"height":40}}),
    node('cut', 'region.boolean', 560, -160, {"operation":"difference"}),
    node('inner', 'region.fromRoi', 290, 80, {"canvasWidth":400,"canvasHeight":400,"roi":{"type":"Rectangle","x":140,"y":140,"width":120,"height":120}}),
    node('ring', 'region.boolean', 560, 80, {"operation":"difference"}),
    node('extract', 'contour.extract', 830, 80, {"mode":"all","binarize":"direct","maxPoints":2000}),
    node('outerContour', 'contour.filter', 1100, 80, {"hierarchy":"external"}),
    node('innerContour', 'contour.filter', 1370, 80, {"hierarchy":"holes"}),
    node('thickness', 'measure.contourThickness', 1640, 80, {"measurementMode":"shortest","sampleStep":4,"minThickness":20}),
    node('found', 'flow.ifBoolean', 1910, 80, {}),
    node('result', 'flow.result', 2180, -40, {}),
    node('missing', 'flow.result', 2180, 240, {}),
    node('join', 'flow.join', 2450, 80, {})
  ],
  edges: [
    control('c1', 'inner', 'next', 'ring', 'exec'),
    control('c2', 'ring', 'next', 'extract', 'exec'),
    control('c3', 'extract', 'next', 'outerContour', 'exec'),
    control('c4', 'outerContour', 'next', 'innerContour', 'exec'),
    control('c5', 'innerContour', 'next', 'thickness', 'exec'),
    control('c6', 'thickness', 'next', 'found', 'exec'),
    control('c7', 'found', 'true', 'result', 'exec'),
    control('c8', 'result', 'next', 'join', 'branch1'),
    control('c9', 'found', 'false', 'missing', 'exec'),
    control('c10', 'missing', 'next', 'join', 'branch2'),
    data('d0', 'cut', 'region', 'ring', 'a'),
    data('d1', 'inner', 'region', 'ring', 'b'),
    data('d2', 'ring', 'image', 'extract', 'image'),
    data('d3', 'extract', 'contours', 'outerContour', 'contours'),
    data('d4', 'extract', 'contours', 'innerContour', 'contours'),
    data('d5', 'outerContour', 'contours', 'thickness', 'outerContours'),
    data('d6', 'innerContour', 'contours', 'thickness', 'innerContours'),
    data('d7', 'ring', 'image', 'thickness', 'image'),
    data('d8', 'thickness', 'found', 'found', 'condition'),
    data('d9', 'thickness', 'withinTolerance', 'result', 'pass'),
    data('d10', 'found', 'condition', 'missing', 'pass'),
    control('c-notch', 'outer', 'next', 'notch', 'exec'),
    control('c-cut', 'notch', 'next', 'cut', 'exec'),
    control('c-inner', 'cut', 'next', 'inner', 'exec'),
    data('d-cut-a', 'outer', 'region', 'cut', 'a'),
    data('d-cut-b', 'notch', 'region', 'cut', 'b')
  ]
});

export const caliperWidthDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 180, { width: 640, height: 480, centerX: 320, centerY: 240, radius: 80 }),
    node('width', 'measure.caliperWidth', 300, 180, { startX: 10, startY: 100, endX: 220, endY: 100,
      polarity: 'bright', edgeThreshold: 8, sampleWidth: 7, minWidth: 100, maxWidth: 130,
      direction: 'closestwidth', nominalWidth: 115 }),
    node('found', 'flow.ifBoolean', 580, 180),
    node('tolerance', 'value.range', 850, 80, { min: 114, max: 116 }),
    node('result', 'flow.result', 1120, 80),
    node('missing', 'flow.result', 850, 340),
    node('join', 'flow.join', 1390, 180)
  ],
  edges: [
    control('c1', 'source', 'next', 'width', 'exec'), data('d1', 'source', 'image', 'width', 'image'),
    control('c2', 'width', 'next', 'found', 'exec'), data('d2', 'width', 'found', 'found', 'condition'),
    control('c3', 'found', 'true', 'tolerance', 'exec'), data('d3', 'width', 'width', 'tolerance', 'value'),
    control('c4', 'tolerance', 'next', 'result', 'exec'), data('d4', 'tolerance', 'pass', 'result', 'pass'),
    control('c5', 'result', 'next', 'join', 'branch1'),
    control('c6', 'found', 'false', 'missing', 'exec'), data('d5', 'found', 'condition', 'missing', 'pass'),
    control('c7', 'missing', 'next', 'join', 'branch2')
  ]
});

export const precisionDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 240, { width: 640, height: 480, centerX: 320, centerY: 240, radius: 80 }),
    node('template', 'image.crop', 260, 420, { x: 220, y: 140, width: 200, height: 200 }),
    node('locate', 'match.shape', 520, 180, { minScore: 0.5, angleRangeDeg: 180, angleStepDeg: 3, minArea: 400, signatureSamples: 360 }),
    node('locate-found', 'flow.ifBoolean', 700, 180),
    node('roi', 'roi.transform', 800, 180, { roi: { type: 'Circle', x: 100, y: 100, radius: 80 }, baseX: 100, baseY: 100, baseThetaDeg: 0 }),
    node('measure', 'measure.caliperCircle', 1080, 180, { centerX: 100, centerY: 100, radius: 80, radialBand: 20, angleStepDeg: 5, minContrast: 8 }),
    node('found', 'flow.ifBoolean', 1280, 180),
    node('tol', 'value.range', 1510, 100, { min: 78, max: 82 }),
    node('result', 'flow.result', 1760, 100),
    node('export', 'output.exportResult', 2010, 100, { path: 'artifacts/shape-locate-measure-result.json', format: 'json', overwrite: true }),
    node('not-found-result', 'flow.result', 1760, 330),
    node('not-found-export', 'output.exportResult', 2010, 330, { path: 'artifacts/shape-locate-measure-result.json', format: 'json', overwrite: true }),
    node('join', 'flow.join', 2260, 210),
    node('locate-not-found-result', 'flow.result', 1760, 560),
    node('locate-not-found-export', 'output.exportResult', 2010, 560, { path: 'artifacts/shape-locate-measure-result.json', format: 'json', overwrite: true }),
    node('locate-join', 'flow.join', 2500, 300)
  ],
  edges: [
    control('c0', 'source', 'next', 'template', 'exec'), data('d0', 'source', 'image', 'template', 'image'),
    control('c1', 'template', 'next', 'locate', 'exec'), data('d1', 'source', 'image', 'locate', 'image'),
    data('d2', 'template', 'image', 'locate', 'template'), control('c2', 'locate', 'next', 'locate-found', 'exec'),
    data('d_locate_found', 'locate', 'found', 'locate-found', 'condition'),
    control('c_locate_true', 'locate-found', 'true', 'roi', 'exec'),
    control('c_locate_false', 'locate-found', 'false', 'locate-not-found-result', 'exec'),
    data('d3', 'locate', 'pose', 'roi', 'currentPose'), data('d_roi_image', 'source', 'image', 'roi', 'debugImage'),
    control('c3', 'roi', 'next', 'measure', 'exec'),
    data('d4', 'locate', 'image', 'measure', 'image'), data('d5', 'roi', 'transform', 'measure', 'transform'),
    control('c4', 'measure', 'next', 'found', 'exec'), data('d6', 'measure', 'found', 'found', 'condition'),
    control('c5', 'found', 'true', 'tol', 'exec'), control('c6', 'found', 'false', 'not-found-result', 'exec'),
    data('d7', 'measure', 'radius', 'tol', 'value'),
    control('c7', 'tol', 'next', 'result', 'exec'), data('d8', 'tol', 'pass', 'result', 'pass'),
    control('c8', 'result', 'next', 'export', 'exec'), data('d9', 'result', 'pass', 'export', 'value'),
    data('d10', 'measure', 'found', 'not-found-result', 'pass'),
    control('c9', 'not-found-result', 'next', 'not-found-export', 'exec'),
    data('d11', 'not-found-result', 'pass', 'not-found-export', 'value'),
    control('c10', 'export', 'next', 'join', 'branch1'),
    control('c11', 'not-found-export', 'next', 'join', 'branch2'),
    data('d_locate_ng', 'locate', 'found', 'locate-not-found-result', 'pass'),
    control('c_locate_ng_export', 'locate-not-found-result', 'next', 'locate-not-found-export', 'exec'),
    data('d_locate_ng_export', 'locate-not-found-result', 'pass', 'locate-not-found-export', 'value'),
    control('c_locate_join_success', 'join', 'next', 'locate-join', 'branch1'),
    control('c_locate_join_ng', 'locate-not-found-export', 'next', 'locate-join', 'branch2')
  ]
});

export const regionDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 180, {"width":640,"height":480,"centerX":320,"centerY":240,"radius":80}),
    node('threshold', 'region.fromRange', 300, 180, {"mode":"gray","minGray":200,"maxGray":255}),
    node('connected', 'region.connectedComponents', 580, 180, {}),
    node('filter', 'region.filter', 860, 180, {"feature":"area","min":1000,"max":50000}),
    node('fill', 'region.fillHoles', 1140, 180, {}),
    node('domain', 'image.reduceDomain', 1420, 180, {}),
    node('selected', 'region.fromRange', 1700, 180, {"mode":"gray","minGray":200,"maxGray":255}),
    node('boolean', 'region.boolean', 1980, 180, {"operation":"intersection"}),
    node('range', 'value.range', 2260, 180, {"min":1,"max":1}),
    node('result', 'flow.result', 2540, 180, {})
  ],
  edges: [
    control('c0', 'source', 'next', 'threshold', 'exec'),
    control('c1', 'threshold', 'next', 'connected', 'exec'),
    control('c2', 'connected', 'next', 'filter', 'exec'),
    control('c3', 'filter', 'next', 'fill', 'exec'),
    control('c4', 'fill', 'next', 'domain', 'exec'),
    control('c5', 'domain', 'next', 'selected', 'exec'),
    control('c6', 'selected', 'next', 'boolean', 'exec'),
    control('c7', 'boolean', 'next', 'range', 'exec'),
    control('c8', 'range', 'next', 'result', 'exec'),
    data('d0', 'source', 'image', 'threshold', 'image'),
    data('d1', 'threshold', 'region', 'connected', 'region'),
    data('d2', 'connected', 'region', 'filter', 'region'),
    data('d3', 'filter', 'region', 'fill', 'region'),
    data('d4', 'source', 'image', 'domain', 'image'),
    data('d5', 'fill', 'region', 'domain', 'region'),
    data('d6', 'domain', 'domainImage', 'selected', 'domainImage'),
    data('d7', 'fill', 'region', 'boolean', 'a'),
    data('d8', 'selected', 'region', 'boolean', 'b'),
    data('d9', 'boolean', 'count', 'range', 'value'),
    data('d10', 'range', 'pass', 'result', 'pass')
  ]
});
