import { control, data, node, type DemoGraph } from './shared';

/** 流程控制类示例：直线、分支、并行、ROI、嵌套、遍历与基础检测。 */

export const linearDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 60, 150),
    node('threshold-1', 'image.threshold', 360, 150),
    node('blob-1', 'measure.blobs', 680, 150, { mode: 'largest' })
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image')
  ]
});

export const ifDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 30, 210),
    node('threshold-1', 'image.threshold', 300, 210, { threshold: 128 }),
    node('blob-1', 'measure.blobs', 570, 210, { mode: 'largest' }),
    node('if-1', 'flow.if', 840, 210, { operator: '>', threshold: 15000 }),
    node('true-threshold', 'image.threshold', 1120, 70, { threshold: 180 }),
    node('false-threshold', 'image.threshold', 1120, 350, { threshold: 80 }),
    node('join-1', 'flow.join', 1420, 210),
    node('result-1', 'flow.result', 1690, 210)
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image'),
    control('c3', 'blob-1', 'next', 'if-1', 'exec'),
    data('d3', 'blob-1', 'area', 'if-1', 'value'),
    control('c4', 'if-1', 'true', 'true-threshold', 'exec'),
    control('c5', 'if-1', 'false', 'false-threshold', 'exec'),
    data('d4', 'source-1', 'image', 'true-threshold', 'image'),
    data('d5', 'source-1', 'image', 'false-threshold', 'image'),
    control('c6', 'true-threshold', 'next', 'join-1', 'branch1'),
    control('c7', 'false-threshold', 'next', 'join-1', 'branch2'),
    control('c8', 'join-1', 'next', 'result-1', 'exec'),
    data('d6', 'if-1', 'condition', 'result-1', 'pass')
  ]
});

export const parallelDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 30, 210),
    node('parallel-1', 'flow.parallel', 320, 210),
    node('branch-a-threshold', 'image.threshold', 620, 70, { threshold: 160 }),
    node('branch-b-threshold', 'image.threshold', 620, 350, { threshold: 95 }),
    node('join-1', 'flow.join', 930, 210),
    node('blob-1', 'measure.blobs', 1210, 210, { mode: 'largest' })
  ],
  edges: [
    control('c1', 'source-1', 'next', 'parallel-1', 'exec'),
    control('c2', 'parallel-1', 'branch1', 'branch-a-threshold', 'exec'),
    control('c3', 'parallel-1', 'branch2', 'branch-b-threshold', 'exec'),
    data('d1', 'source-1', 'image', 'branch-a-threshold', 'image'),
    data('d2', 'source-1', 'image', 'branch-b-threshold', 'image'),
    control('c4', 'branch-a-threshold', 'next', 'join-1', 'branch1'),
    control('c5', 'branch-b-threshold', 'next', 'join-1', 'branch2'),
    control('c6', 'join-1', 'next', 'blob-1', 'exec'),
    data('d3', 'branch-a-threshold', 'image', 'blob-1', 'image')
  ]
});

export const roiDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 60, 150),
    node('threshold-1', 'image.threshold', 360, 150, {
      threshold: 128,
      roi: { type: 'Rectangle', x: 210, y: 120, width: 230, height: 240 }
    }),
    node('blob-1', 'measure.blobs', 680, 150, { mode: 'largest', minArea: 1000 })
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image')
  ]
});

export const nestedDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 20, 260),
    node('threshold-1', 'image.threshold', 260, 260, { threshold: 128 }),
    node('blob-1', 'measure.blobs', 500, 260, { mode: 'largest', minArea: 1000 }),
    node('outer-if', 'flow.if', 740, 260, { operator: '>', threshold: 15000 }),
    node('inner-if', 'flow.if', 1010, 110, { operator: '>', threshold: 18000 }),
    node('inner-true', 'image.threshold', 1290, 20, { threshold: 190 }),
    node('inner-false', 'image.threshold', 1290, 170, { threshold: 145 }),
    node('inner-join', 'flow.join', 1570, 100),
    node('outer-true-tail', 'image.threshold', 1810, 100, { threshold: 170 }),
    node('outer-false', 'image.threshold', 1160, 410, { threshold: 80 }),
    node('outer-join', 'flow.join', 2070, 260),
    node('result-1', 'flow.result', 2330, 260)
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image'),
    control('c3', 'blob-1', 'next', 'outer-if', 'exec'),
    data('d3', 'blob-1', 'area', 'outer-if', 'value'),
    control('c4', 'outer-if', 'true', 'inner-if', 'exec'),
    control('c5', 'outer-if', 'false', 'outer-false', 'exec'),
    data('d4', 'blob-1', 'area', 'inner-if', 'value'),
    control('c6', 'inner-if', 'true', 'inner-true', 'exec'),
    control('c7', 'inner-if', 'false', 'inner-false', 'exec'),
    data('d5', 'source-1', 'image', 'inner-true', 'image'),
    data('d6', 'source-1', 'image', 'inner-false', 'image'),
    control('c8', 'inner-true', 'next', 'inner-join', 'branch1'),
    control('c9', 'inner-false', 'next', 'inner-join', 'branch2'),
    control('c10', 'inner-join', 'next', 'outer-true-tail', 'exec'),
    data('d7', 'source-1', 'image', 'outer-true-tail', 'image'),
    control('c11', 'outer-true-tail', 'next', 'outer-join', 'branch1'),
    data('d8', 'source-1', 'image', 'outer-false', 'image'),
    control('c12', 'outer-false', 'next', 'outer-join', 'branch2'),
    control('c13', 'outer-join', 'next', 'result-1', 'exec'),
    data('d9', 'outer-if', 'condition', 'result-1', 'pass')
  ]
});

export const inspectionDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 30, 150),
    node('gray', 'image.channel', 300, 150, { channel: 'gray' }),
    node('blur', 'image.filter', 570, 150, { method: 'gaussian', kernelSize: 5 }),
    node('binary', 'image.threshold', 840, 150, { mode: 'otsu' }),
    node('clean', 'image.morphology', 1110, 150, { operation: 'open', kernelSize: 3 }),
    node('objects', 'measure.blobs', 1380, 150, { minArea: 1000 }),
    node('check', 'value.range', 1650, 150, { min: 1, max: 5 }),
    node('result', 'flow.result', 1920, 150)
  ],
  edges: [
    control('c1', 'source', 'next', 'gray', 'exec'), data('d1', 'source', 'image', 'gray', 'image'),
    control('c2', 'gray', 'next', 'blur', 'exec'), data('d2', 'gray', 'image', 'blur', 'image'),
    control('c3', 'blur', 'next', 'binary', 'exec'), data('d3', 'blur', 'image', 'binary', 'image'),
    control('c4', 'binary', 'next', 'clean', 'exec'), data('d4', 'binary', 'image', 'clean', 'image'),
    control('c5', 'clean', 'next', 'objects', 'exec'), data('d5', 'clean', 'image', 'objects', 'image'),
    control('c6', 'objects', 'next', 'check', 'exec'), data('d6', 'objects', 'count', 'check', 'value'),
    control('c7', 'check', 'next', 'result', 'exec'), data('d7', 'check', 'pass', 'result', 'pass')
  ]
});

export const forEachDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 30, 180),
    node('binary', 'image.threshold', 300, 180, { threshold: 80 }),
    node('objects', 'measure.blobs', 570, 180, { minArea: 100 }),
    node('offset', 'value.number', 840, 180, { value: 0 }),
    node('loop', 'flow.forEach', 1110, 180, { maxItems: 100 }),
    node('position', 'value.arithmetic', 1410, 30, { op: 'add' }),
    node('end', 'flow.loopEnd', 1700, 30),
    node('count', 'collection.detections.count', 1410, 420)
  ],
  edges: [
    control('c1', 'source', 'next', 'binary', 'exec'), data('d1', 'source', 'image', 'binary', 'image'),
    control('c2', 'binary', 'next', 'objects', 'exec'), data('d2', 'binary', 'image', 'objects', 'image'),
    control('c3', 'objects', 'next', 'offset', 'exec'),
    control('c4', 'offset', 'next', 'loop', 'exec'), data('d3', 'objects', 'detections', 'loop', 'detections'),
    control('c5', 'loop', 'body', 'position', 'exec'), data('d4', 'loop', 'x', 'position', 'a'),
    data('d5', 'offset', 'value', 'position', 'b'), control('c6', 'position', 'next', 'end', 'exec'),
    control('c7', 'end', 'repeat', 'loop', 'repeat'),
    control('c8', 'loop', 'done', 'count', 'exec'), data('d6', 'loop', 'detections', 'count', 'detections')
  ]
});
