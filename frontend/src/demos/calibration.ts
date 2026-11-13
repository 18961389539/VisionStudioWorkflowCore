import { control, data, node, type DemoGraph } from './shared';

/** 标定类示例：标定板像素→毫米坐标变换。 */

export const calibrationDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 20, 170, { centerX: 320, centerY: 240, radius: 80 }),
    node('threshold-1', 'image.threshold', 280, 170, { threshold: 128 }),
    node('blob-1', 'measure.blobs', 540, 170, { mode: 'largest' }),
    node('calibration-1', 'calibration.planar', 810, 170),
    node('transform-point-1', 'coordinate.transformPoint', 1100, 110),
    node('reference-1', 'coordinate.point', 1100, 330, { x: 20, y: 10, frame: 'Workpiece', unit: 'mm' }),
    node('world-distance-1', 'coordinate.distance', 1410, 210)
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image'),
    control('c3', 'blob-1', 'next', 'calibration-1', 'exec'),
    control('c4', 'calibration-1', 'next', 'transform-point-1', 'exec'),
    data('d3', 'blob-1', 'center', 'transform-point-1', 'point'),
    data('d4', 'calibration-1', 'transform', 'transform-point-1', 'transform'),
    control('c5', 'transform-point-1', 'next', 'reference-1', 'exec'),
    control('c6', 'reference-1', 'next', 'world-distance-1', 'exec'),
    data('d5', 'transform-point-1', 'point', 'world-distance-1', 'pointA'),
    data('d6', 'reference-1', 'point', 'world-distance-1', 'pointB')
  ]
});
