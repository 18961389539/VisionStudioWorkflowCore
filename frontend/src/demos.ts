import type { Edge, Node } from '@xyflow/react';

const node = (
  id: string,
  type: string,
  x: number,
  y: number,
  parameters: Record<string, unknown> = {}
): Node => ({
  id,
  type: 'vision',
  position: { x, y },
  data: {
    typeKey: type,
    label: type,
    parameters,
    inputs: [],
    outputs: [],
    status: 'idle'
  }
});

const control = (
  id: string,
  source: string,
  sourceHandle: string,
  target: string,
  targetHandle: string
): Edge => ({
  id,
  source,
  sourceHandle,
  target,
  targetHandle,
  animated: true,
  data: { kind: 'control' },
  style: { strokeDasharray: '7 5' }
});

const data = (
  id: string,
  source: string,
  sourceHandle: string,
  target: string,
  targetHandle: string
): Edge => ({ id, source, sourceHandle, target, targetHandle, data: { kind: 'data' } });

export type DemoGraph = { nodes: Node[]; edges: Edge[] };

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


export const cameraDemo = (): DemoGraph => ({
  nodes: [
    node('acquire-1', 'image.acquire', 60, 150, { cameraId: 'virtual-1', frameMode: 'Latest', timeoutMs: 1500, autoStart: true }),
    node('threshold-1', 'image.threshold', 380, 150, { threshold: 150 }),
    node('blob-1', 'measure.blobs', 700, 150, { mode: 'largest', minArea: 1000 })
  ],
  edges: [
    control('c1', 'acquire-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'acquire-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image')
  ]
});


export const triggeredCameraDemo = (): DemoGraph => ({
  nodes: [
    node('acquire-1', 'image.acquire', 60, 150, { cameraId: 'virtual-1', frameMode: 'Next', timeoutMs: 1800, autoStart: true, triggerBeforeGrab: true }),
    node('threshold-1', 'image.threshold', 390, 150, { threshold: 150 }),
    node('blob-1', 'measure.blobs', 710, 150, { mode: 'largest', minArea: 1000 })
  ],
  edges: [
    control('c1', 'acquire-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'acquire-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image')
  ]
});


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

export const robotEyeToHandDemo = (): DemoGraph => ({
  nodes: [
    node('source-1', 'image.synthetic', 20, 190, { centerX: 330, centerY: 235, radius: 78 }),
    node('threshold-1', 'image.threshold', 260, 190, { threshold: 110 }),
    node('rect-1', 'measure.rotatedRect', 500, 190, { minArea: 1500 }),
    node('calibration-1', 'calibration.planar', 760, 190),
    node('pixel-to-workpiece-1', 'coordinate.transformPose', 1030, 120),
    node('frame-tree-1', 'coordinate.frameTree', 1030, 340, {
      transformsJson: '[{"sourceFrame":"Workpiece","targetFrame":"Fixture","unit":"mm","x":100,"y":50,"angleDeg":5},{"sourceFrame":"Fixture","targetFrame":"RobotBase","unit":"mm","x":500,"y":200,"angleDeg":0}]'
    }),
    node('frame-resolve-1', 'coordinate.frameResolve', 1310, 340, { sourceFrame: 'Workpiece', targetFrame: 'RobotBase' }),
    node('to-robot-1', 'coordinate.transformCoordinatePose', 1580, 190),
    node('guidance-1', 'robot.guidance2d', 1850, 190, { robot: 'ABB', guidanceMode: 'EyeToHand', expectedFrame: 'RobotBase', offsetX: 0, offsetY: 0, angleOffsetDeg: 0 }),
    node('compensation-1', 'robot.j4TcpCompensation', 2140, 190, { tcpOffsetX: 12, tcpOffsetY: 4, j4PivotOffsetX: 0, j4PivotOffsetY: 0, angleZeroOffsetDeg: 0 }),
    node('robot-execute-1', 'robot.executeTarget', 2430, 190, { robotId: 'virtual-abb-1', action: 'Handshake', autoConnect: true, waitForInPosition: true, timeoutMs: 10000, maxRetries: 1, retryDelayMs: 100, autoAck: true })
  ],
  edges: [
    control('c1', 'source-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'source-1', 'image', 'threshold-1', 'image'),
    control('c2', 'threshold-1', 'next', 'rect-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'rect-1', 'image'),
    control('c3', 'rect-1', 'next', 'calibration-1', 'exec'),
    control('c4', 'calibration-1', 'next', 'pixel-to-workpiece-1', 'exec'),
    data('d3', 'rect-1', 'pose', 'pixel-to-workpiece-1', 'pose'),
    data('d4', 'calibration-1', 'transform', 'pixel-to-workpiece-1', 'transform'),
    control('c5', 'pixel-to-workpiece-1', 'next', 'frame-tree-1', 'exec'),
    control('c6', 'frame-tree-1', 'next', 'frame-resolve-1', 'exec'),
    data('d5', 'frame-tree-1', 'tree', 'frame-resolve-1', 'tree'),
    control('c7', 'frame-resolve-1', 'next', 'to-robot-1', 'exec'),
    data('d6', 'pixel-to-workpiece-1', 'pose', 'to-robot-1', 'pose'),
    data('d7', 'frame-resolve-1', 'transform', 'to-robot-1', 'transform'),
    control('c8', 'to-robot-1', 'next', 'guidance-1', 'exec'),
    data('d8', 'to-robot-1', 'pose', 'guidance-1', 'pose'),
    control('c9', 'guidance-1', 'next', 'compensation-1', 'exec'),
    data('d9', 'guidance-1', 'target', 'compensation-1', 'target'),
    control('c10', 'compensation-1', 'next', 'robot-execute-1', 'exec'),
    data('d10', 'compensation-1', 'target', 'robot-execute-1', 'target')
  ]
});

export const robotEyeInHandDemo = (): DemoGraph => ({
  nodes: [
    node('observed-pose-1', 'coordinate.pose', 20, 100, { x: 18, y: 7, thetaDeg: 4, frame: 'CameraPlane', unit: 'mm' }),
    node('camera-tool-1', 'coordinate.transformConstant', 300, 100, { sourceFrame: 'CameraPlane', targetFrame: 'Tool', unit: 'mm', x: 35, y: -8, angleDeg: 1.5 }),
    node('robot-current-1', 'robot.currentPose', 300, 330, { robotId: 'virtual-abb-1', autoConnect: true }),
    node('tool-base-1', 'coordinate.poseToTransform', 590, 330, { sourceFrame: 'Tool' }),
    node('compose-1', 'coordinate.transformCompose', 870, 210),
    node('to-robot-1', 'coordinate.transformCoordinatePose', 1160, 210),
    node('guidance-1', 'robot.guidance2d', 1450, 210, { robot: 'ABB', guidanceMode: 'EyeInHand', expectedFrame: 'RobotBase', offsetX: 0, offsetY: 0, angleOffsetDeg: 0 }),
    node('compensation-1', 'robot.j4TcpCompensation', 1740, 210, { tcpOffsetX: 12, tcpOffsetY: 4, j4PivotOffsetX: 0, j4PivotOffsetY: 0, angleZeroOffsetDeg: 0 }),
    node('robot-execute-1', 'robot.executeTarget', 2030, 210, { robotId: 'virtual-abb-1', action: 'Handshake', autoConnect: true, waitForInPosition: true, timeoutMs: 10000, maxRetries: 1, retryDelayMs: 100, autoAck: true })
  ],
  edges: [
    control('c1', 'observed-pose-1', 'next', 'camera-tool-1', 'exec'),
    control('c2', 'camera-tool-1', 'next', 'robot-current-1', 'exec'),
    control('c3', 'robot-current-1', 'next', 'tool-base-1', 'exec'),
    data('d1', 'robot-current-1', 'pose', 'tool-base-1', 'pose'),
    control('c4', 'tool-base-1', 'next', 'compose-1', 'exec'),
    data('d2', 'camera-tool-1', 'transform', 'compose-1', 'transformA'),
    data('d3', 'tool-base-1', 'transform', 'compose-1', 'transformB'),
    control('c5', 'compose-1', 'next', 'to-robot-1', 'exec'),
    data('d4', 'observed-pose-1', 'pose', 'to-robot-1', 'pose'),
    data('d5', 'compose-1', 'transform', 'to-robot-1', 'transform'),
    control('c6', 'to-robot-1', 'next', 'guidance-1', 'exec'),
    data('d6', 'to-robot-1', 'pose', 'guidance-1', 'pose'),
    control('c7', 'guidance-1', 'next', 'compensation-1', 'exec'),
    data('d7', 'guidance-1', 'target', 'compensation-1', 'target'),
    control('c8', 'compensation-1', 'next', 'robot-execute-1', 'exec'),
    data('d8', 'compensation-1', 'target', 'robot-execute-1', 'target')
  ]
});

export const robotRuntimeDemo = (): DemoGraph => ({
  nodes: [
    node('pose-1', 'coordinate.pose', 40, 180, { x: 580, y: 280, thetaDeg: 35, frame: 'RobotBase', unit: 'mm' }),
    node('guidance-1', 'robot.guidance2d', 330, 180, { robot: 'ABB', guidanceMode: 'Manual', expectedFrame: 'RobotBase' }),
    node('execute-1', 'robot.executeTarget', 640, 180, { robotId: 'virtual-abb-1', action: 'Handshake', autoConnect: true, waitForInPosition: true, timeoutMs: 10000, maxRetries: 1, retryDelayMs: 100, autoAck: true }),
    node('readback-1', 'robot.currentPose', 950, 180, { robotId: 'virtual-abb-1', autoConnect: true })
  ],
  edges: [
    control('c1', 'pose-1', 'next', 'guidance-1', 'exec'),
    data('d1', 'pose-1', 'pose', 'guidance-1', 'pose'),
    control('c2', 'guidance-1', 'next', 'execute-1', 'exec'),
    data('d2', 'guidance-1', 'target', 'execute-1', 'target'),
    control('c3', 'execute-1', 'next', 'readback-1', 'exec')
  ]
});


export const robotPlcHandshakeDemo = (): DemoGraph => ({
  nodes: [
    node('pose-1', 'coordinate.pose', 40, 180, { x: 560, y: 260, thetaDeg: 30, frame: 'RobotBase', unit: 'mm' }),
    node('guidance-1', 'robot.guidance2d', 330, 180, { robot: 'ABB', guidanceMode: 'Manual', expectedFrame: 'RobotBase' }),
    node('execute-1', 'robot.executeTarget', 640, 180, { robotId: 'virtual-plc-1', action: 'Handshake', autoConnect: true, waitForInPosition: true, timeoutMs: 10000, maxRetries: 1, retryDelayMs: 150, autoAck: true }),
    node('readback-1', 'robot.currentPose', 950, 180, { robotId: 'virtual-plc-1', autoConnect: true })
  ],
  edges: [
    control('c1', 'pose-1', 'next', 'guidance-1', 'exec'),
    data('d1', 'pose-1', 'pose', 'guidance-1', 'pose'),
    control('c2', 'guidance-1', 'next', 'execute-1', 'exec'),
    data('d2', 'guidance-1', 'target', 'execute-1', 'target'),
    control('c3', 'execute-1', 'next', 'readback-1', 'exec')
  ]
});

export const robotTcpHandshakeDemo = (): DemoGraph => ({
  nodes: [
    node('pose-1', 'coordinate.pose', 40, 180, { x: 590, y: 250, thetaDeg: 42, frame: 'RobotBase', unit: 'mm' }),
    node('guidance-1', 'robot.guidance2d', 330, 180, { robot: 'ABB', guidanceMode: 'Manual', expectedFrame: 'RobotBase' }),
    node('execute-1', 'robot.executeTarget', 640, 180, { robotId: 'tcp-sim-1', action: 'Handshake', autoConnect: true, waitForInPosition: true, timeoutMs: 10000, maxRetries: 1, retryDelayMs: 100, autoAck: true }),
    node('readback-1', 'robot.currentPose', 950, 180, { robotId: 'tcp-sim-1', autoConnect: true })
  ],
  edges: [
    control('c1', 'pose-1', 'next', 'guidance-1', 'exec'),
    data('d1', 'pose-1', 'pose', 'guidance-1', 'pose'),
    control('c2', 'guidance-1', 'next', 'execute-1', 'exec'),
    data('d2', 'guidance-1', 'target', 'execute-1', 'target'),
    control('c3', 'execute-1', 'next', 'readback-1', 'exec')
  ]
});

export const devicePlcDemo = (): DemoGraph => ({
  nodes: [
    node('wait-trigger-1', 'device.waitTag', 20, 210, { deviceId: 'virtual-modbus-1', tagId: 'trigger', operator: 'True', compareValue: '1', timeoutMs: 10000, pollMs: 25, autoConnect: true }),
    node('acquire-1', 'image.acquire', 300, 210, { cameraId: 'virtual-1', frameMode: 'Latest', autoStart: true }),
    node('threshold-1', 'image.threshold', 580, 210, { threshold: 128 }),
    node('blob-1', 'measure.blobs', 860, 210, { mode: 'largest', minArea: 1000 }),
    node('if-1', 'flow.if', 1140, 210, { operator: '>', threshold: 15000 }),
    node('pass-text-1', 'device.writeTag', 1420, 80, { deviceId: 'virtual-modbus-1', tagId: 'statusText', valueType: 'String', fallbackValue: 'VISION PASS', autoConnect: true }),
    node('fail-text-1', 'device.writeTag', 1420, 340, { deviceId: 'virtual-modbus-1', tagId: 'statusText', valueType: 'String', fallbackValue: 'VISION FAIL', autoConnect: true }),
    node('join-1', 'flow.join', 1710, 210),
    node('result-1', 'flow.result', 1970, 210),
    node('write-result-1', 'device.writeVisionResult', 2250, 210, { deviceId: 'virtual-modbus-1', resultReadyTag: 'resultReady', resultOkTag: 'resultOk', xTag: 'resultX', yTag: 'resultY', rTag: 'resultR', autoConnect: true })
  ],
  edges: [
    control('c1', 'wait-trigger-1', 'next', 'acquire-1', 'exec'),
    control('c2', 'acquire-1', 'next', 'threshold-1', 'exec'),
    data('d1', 'acquire-1', 'image', 'threshold-1', 'image'),
    control('c3', 'threshold-1', 'next', 'blob-1', 'exec'),
    data('d2', 'threshold-1', 'image', 'blob-1', 'image'),
    control('c4', 'blob-1', 'next', 'if-1', 'exec'),
    data('d3', 'blob-1', 'area', 'if-1', 'value'),
    control('c5', 'if-1', 'true', 'pass-text-1', 'exec'),
    control('c6', 'if-1', 'false', 'fail-text-1', 'exec'),
    control('c7', 'pass-text-1', 'next', 'join-1', 'branch1'),
    control('c8', 'fail-text-1', 'next', 'join-1', 'branch2'),
    control('c9', 'join-1', 'next', 'result-1', 'exec'),
    data('d4', 'if-1', 'condition', 'result-1', 'pass'),
    control('c10', 'result-1', 'next', 'write-result-1', 'exec'),
    data('d5', 'result-1', 'pass', 'write-result-1', 'pass'),
    data('d6', 'blob-1', 'x', 'write-result-1', 'x'),
    data('d7', 'blob-1', 'y', 'write-result-1', 'y')
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


export const frameSetDemo = (): DemoGraph => ({
  nodes: [
    node('sync-capture', 'camera.syncCapture', 40, 180, { groupId: 'sync-group-1', scheduled: false, leadTimeMs: 100, frameTimeoutMs: 3000 }),
    node('top-image', 'frameset.image', 380, 70, { cameraId: 'camera-top' }),
    node('side-image', 'frameset.image', 680, 250, { cameraId: 'camera-side' }),
    node('side-threshold', 'image.threshold', 990, 250, { threshold: 128 })
  ],
  edges: [
    control('c1', 'sync-capture', 'next', 'top-image', 'exec'),
    data('d1', 'sync-capture', 'frameSet', 'top-image', 'frameSet'),
    control('c2', 'top-image', 'next', 'side-image', 'exec'),
    data('d2', 'sync-capture', 'frameSet', 'side-image', 'frameSet'),
    control('c3', 'side-image', 'next', 'side-threshold', 'exec'),
    data('d3', 'side-image', 'image', 'side-threshold', 'image')
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

export const precisionDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 240, { width: 640, height: 480, centerX: 320, centerY: 240, radius: 80 }),
    node('template', 'image.crop', 260, 420, { x: 220, y: 140, width: 200, height: 200 }),
    node('locate', 'match.shape', 520, 180, { minScore: 0.5, angleRangeDeg: 180, angleStepDeg: 3, minArea: 400, signatureSamples: 360 }),
    node('roi', 'roi.transform', 800, 180, { roi: { type: 'Circle', x: 100, y: 100, radius: 80 }, baseX: 100, baseY: 100, baseThetaDeg: 0 }),
    node('measure', 'measure.caliperCircle', 1080, 180, { radialBand: 20, angleStepDeg: 5, minContrast: 8 }),
    node('tol', 'value.range', 1350, 180, { min: 78, max: 82 }),
    node('result', 'flow.result', 1620, 180),
    node('export', 'output.exportResult', 1620, 400, { path: 'artifacts/shape-locate-measure-result.json', format: 'json', overwrite: true })
  ],
  edges: [
    control('c0', 'source', 'next', 'template', 'exec'), data('d0', 'source', 'image', 'template', 'image'),
    control('c1', 'template', 'next', 'locate', 'exec'), data('d1', 'source', 'image', 'locate', 'image'),
    data('d2', 'template', 'image', 'locate', 'template'), control('c2', 'locate', 'next', 'roi', 'exec'),
    data('d3', 'locate', 'pose', 'roi', 'currentPose'), control('c3', 'roi', 'next', 'measure', 'exec'),
    data('d4', 'locate', 'image', 'measure', 'image'), data('d5', 'roi', 'circle', 'measure', 'circle'),
    control('c4', 'measure', 'next', 'tol', 'exec'), data('d6', 'measure', 'radius', 'tol', 'value'),
    control('c5', 'tol', 'next', 'result', 'exec'), data('d7', 'tol', 'pass', 'result', 'pass'),
    control('c6', 'result', 'next', 'export', 'exec'), data('d8', 'result', 'pass', 'export', 'value')
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

export const defectDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 160, {"width":640,"height":480,"centerX":320,"centerY":240,"radius":80}),
    node('reference', 'image.morphology', 300, 160, {"operation":"erode","kernelSize":9,"iterations":1}),
    node('dark', 'image.threshold', 580, 160, {"threshold":250}),
    node('bright', 'image.threshold', 860, 160, {"threshold":0}),
    node('corrected', 'image.flatFieldCorrect', 1140, 160, {}),
    node('difference', 'image.difference', 1420, 160, {"mode":"absolute"}),
    node('dynamic', 'image.dynamicThreshold', 1420, 480, {"polarity":"both","offset":10,"windowSize":31}),
    node('compare', 'defect.referenceCompare', 1140, 480, {"polarity":"both","threshold":20,"minArea":10}),
    node('stats', 'measure.regionIntensity', 860, 480, {}),
    node('range', 'value.range', 580, 480, {"min":0,"max":0}),
    node('result', 'flow.result', 300, 480, {})
  ],
  edges: [
    control('c0', 'source', 'next', 'reference', 'exec'),
    control('c1', 'reference', 'next', 'dark', 'exec'),
    control('c2', 'dark', 'next', 'bright', 'exec'),
    control('c3', 'bright', 'next', 'corrected', 'exec'),
    control('c4', 'corrected', 'next', 'difference', 'exec'),
    control('c5', 'difference', 'next', 'dynamic', 'exec'),
    control('c6', 'dynamic', 'next', 'compare', 'exec'),
    control('c7', 'compare', 'next', 'stats', 'exec'),
    control('c8', 'stats', 'next', 'range', 'exec'),
    control('c9', 'range', 'next', 'result', 'exec'),
    data('d0', 'reference', 'image', 'dark', 'image'),
    data('d1', 'reference', 'image', 'bright', 'image'),
    data('d2', 'source', 'image', 'corrected', 'image'),
    data('d3', 'dark', 'image', 'corrected', 'dark'),
    data('d4', 'bright', 'image', 'corrected', 'bright'),
    data('d5', 'corrected', 'image', 'difference', 'image'),
    data('d6', 'reference', 'image', 'difference', 'reference'),
    data('d7', 'difference', 'image', 'dynamic', 'image'),
    data('d8', 'corrected', 'image', 'compare', 'image'),
    data('d9', 'reference', 'image', 'compare', 'reference'),
    data('d10', 'difference', 'image', 'stats', 'image'),
    data('d11', 'compare', 'region', 'stats', 'region'),
    data('d12', 'compare', 'count', 'range', 'value'),
    data('d13', 'range', 'pass', 'result', 'pass'),
    data('d14', 'source', 'image', 'reference', 'image')
  ]
});

export const alignmentRegionDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 180, {}),
    node('current', 'image.rotate', 300, 180, {"angleDeg":5,"expand":false}),
    node('threshold', 'region.fromRange', 580, 180, {"mode":"gray","minGray":200,"maxGray":255}),
    node('connected', 'region.connectedComponents', 860, 180, {}),
    node('clean', 'region.morphology', 1140, 180, {"operation":"open","kernelWidth":3,"kernelHeight":3}),
    node('register', 'image.register', 1420, 180, {"x":50,"y":40,"width":400,"height":350,"angleRangeDeg":10,"minScore":0.7}),
    node('gate', 'flow.ifBoolean', 1700, 180, {}),
    node('warp', 'image.warpAffine', 1980, 20, {}),
    node('transform', 'region.transform', 2260, 20, {}),
    node('properties', 'region.properties', 2540, 20, {}),
    node('detections', 'region.toDetections', 2820, 20, {}),
    node('range', 'value.range', 3100, 20, {"min":1,"max":1}),
    node('result', 'flow.result', 3380, 20, {}),
    node('missing', 'flow.result', 1980, 420, {}),
    node('join', 'flow.join', 3660, 180, {})
  ],
  edges: [
    control('c0', 'source', 'next', 'current', 'exec'),
    control('c1', 'current', 'next', 'threshold', 'exec'),
    control('c2', 'threshold', 'next', 'connected', 'exec'),
    control('c3', 'connected', 'next', 'clean', 'exec'),
    control('c4', 'clean', 'next', 'register', 'exec'),
    control('c5', 'register', 'next', 'gate', 'exec'),
    control('c6', 'gate', 'true', 'warp', 'exec'),
    control('c7', 'warp', 'next', 'transform', 'exec'),
    control('c8', 'transform', 'next', 'properties', 'exec'),
    control('c9', 'properties', 'next', 'detections', 'exec'),
    control('c10', 'detections', 'next', 'range', 'exec'),
    control('c11', 'range', 'next', 'result', 'exec'),
    control('c12', 'result', 'next', 'join', 'branch1'),
    control('c13', 'gate', 'false', 'missing', 'exec'),
    control('c14', 'missing', 'next', 'join', 'branch2'),
    data('d0', 'source', 'image', 'current', 'image'),
    data('d1', 'current', 'image', 'threshold', 'image'),
    data('d2', 'threshold', 'region', 'connected', 'region'),
    data('d3', 'connected', 'region', 'clean', 'region'),
    data('d4', 'current', 'image', 'register', 'image'),
    data('d5', 'source', 'image', 'register', 'reference'),
    data('d6', 'register', 'found', 'gate', 'condition'),
    data('d7', 'current', 'image', 'warp', 'image'),
    data('d8', 'register', 'transform', 'warp', 'transform'),
    data('d9', 'clean', 'region', 'transform', 'region'),
    data('d10', 'register', 'transform', 'transform', 'transform'),
    data('d11', 'transform', 'region', 'properties', 'region'),
    data('d12', 'transform', 'region', 'detections', 'region'),
    data('d13', 'detections', 'count', 'range', 'value'),
    data('d14', 'range', 'pass', 'result', 'pass'),
    data('d15', 'gate', 'condition', 'missing', 'pass')
  ]
});

export const colorEnhancementDemo = (): DemoGraph => ({
  nodes: [
    node('source', 'image.synthetic', 20, 180, {}),
    node('contrast', 'image.contrastNormalize', 300, 180, {}),
    node('illumination', 'image.illuminationNormalize', 580, 180, {"windowSize":51,"targetMean":128}),
    node('sharpen', 'image.sharpen', 860, 180, {"amount":0.5}),
    node('channels', 'image.colorConvert', 1140, 180, {"space":"hsv"}),
    node('range', 'region.fromRange', 1420, 180, {"mode":"color","space":"hsv","min1":0,"max1":360,"min2":0,"max2":1,"min3":0.6,"max3":1}),
    node('objects', 'region.connectedComponents', 1700, 180, {}),
    node('difference', 'measure.colorDifference', 1980, 180, {}),
    node('tolerance', 'value.range', 2540, 180, {"min":0,"max":100}),
    node('result', 'flow.result', 2820, 180, {}),
    node('gate', 'flow.ifBoolean', 2260, 180, {}),
    node('missing', 'flow.result', 2540, 500, {}),
    node('join', 'flow.join', 3100, 180, {})
  ],
  edges: [
    control('c0', 'source', 'next', 'contrast', 'exec'),
    control('c1', 'contrast', 'next', 'illumination', 'exec'),
    control('c2', 'illumination', 'next', 'sharpen', 'exec'),
    control('c3', 'sharpen', 'next', 'channels', 'exec'),
    control('c4', 'channels', 'next', 'range', 'exec'),
    control('c5', 'range', 'next', 'objects', 'exec'),
    control('c6', 'objects', 'next', 'difference', 'exec'),
    control('c8', 'tolerance', 'next', 'result', 'exec'),
    data('d0', 'source', 'image', 'contrast', 'image'),
    data('d1', 'contrast', 'image', 'illumination', 'image'),
    data('d2', 'illumination', 'image', 'sharpen', 'image'),
    data('d3', 'source', 'image', 'channels', 'image'),
    data('d4', 'source', 'image', 'range', 'image'),
    data('d5', 'range', 'region', 'objects', 'region'),
    data('d6', 'source', 'image', 'difference', 'image'),
    data('d8', 'range', 'region', 'difference', 'region'),
    data('d9', 'difference', 'deltaE', 'tolerance', 'value'),
    data('d10', 'tolerance', 'pass', 'result', 'pass'),
    control('c-gate', 'difference', 'next', 'gate', 'exec'),
    control('c-found', 'gate', 'true', 'tolerance', 'exec'),
    control('c-empty', 'gate', 'false', 'missing', 'exec'),
    control('c-pass-join', 'result', 'next', 'join', 'branch1'),
    control('c-empty-join', 'missing', 'next', 'join', 'branch2'),
    data('d-found', 'difference', 'found', 'gate', 'condition'),
    data('d-empty-pass', 'gate', 'condition', 'missing', 'pass')
  ]
});

export const textValidationDemo = (): DemoGraph => ({
  nodes: [
    node('match', 'text.validate', 20, 180, {"text":"LOT-20261015-A42","expected":"LOT-","mode":"startsWith"}),
    node('regex', 'text.validate', 300, 180, {"pattern":"LOT-(?<date>[0-9]{8})-(?<batch>[A-Z][0-9]{2})","mode":"regex"}),
    node('combine', 'logic.boolean', 580, 180, {"op":"and"}),
    node('result', 'flow.result', 860, 180, {})
  ],
  edges: [
    control('c0', 'match', 'next', 'regex', 'exec'),
    control('c1', 'regex', 'next', 'combine', 'exec'),
    control('c2', 'combine', 'next', 'result', 'exec'),
    data('d0', 'match', 'text', 'regex', 'text'),
    data('d1', 'match', 'pass', 'combine', 'a'),
    data('d2', 'regex', 'pass', 'combine', 'b'),
    data('d3', 'combine', 'result', 'result', 'pass')
  ]
});

export const circularToleranceDemo = (): DemoGraph => ({
  nodes: [
    node('outerPoints', 'contour.fromPoints', 20, 180, {"pointsJson":"[[400.0,240.0],[399.561752,248.362277],[398.251808,256.632935],[396.084521,264.72136],[393.083637,272.538931],[389.282032,280.0],[384.72136,287.02282],[379.451586,293.530449],[373.530449,299.451586],[367.02282,304.72136],[360.0,309.282032],[352.538931,313.083637],[344.72136,316.084521],[336.632935,318.251808],[328.362277,319.561752],[320.0,320.0],[311.637723,319.561752],[303.367065,318.251808],[295.27864,316.084521],[287.461069,313.083637],[280.0,309.282032],[272.97718,304.72136],[266.469551,299.451586],[260.548414,293.530449],[255.27864,287.02282],[250.717968,280.0],[246.916363,272.538931],[243.915479,264.72136],[241.748192,256.632935],[240.438248,248.362277],[240.0,240.0],[240.438248,231.637723],[241.748192,223.367065],[243.915479,215.27864],[246.916363,207.461069],[250.717968,200.0],[255.27864,192.97718],[260.548414,186.469551],[266.469551,180.548414],[272.97718,175.27864],[280.0,170.717968],[287.461069,166.916363],[295.27864,163.915479],[303.367065,161.748192],[311.637723,160.438248],[320.0,160.0],[328.362277,160.438248],[336.632935,161.748192],[344.72136,163.915479],[352.538931,166.916363],[360.0,170.717968],[367.02282,175.27864],[373.530449,180.548414],[379.451586,186.469551],[384.72136,192.97718],[389.282032,200.0],[393.083637,207.461069],[396.084521,215.27864],[398.251808,223.367065],[399.561752,231.637723]]","closed":true}),
    node('innerPoints', 'contour.fromPoints', 300, 180, {"pointsJson":"[[382.0,240.0],[381.671314,246.271708],[380.688856,252.474701],[379.063391,258.54102],[376.812727,264.404199],[373.961524,270.0],[370.54102,275.267115],[366.58869,280.147836],[362.147836,284.58869],[357.267115,288.54102],[352.0,291.961524],[346.404199,294.812727],[340.54102,297.063391],[334.474701,298.688856],[328.271708,299.671314],[322.0,300.0],[315.728292,299.671314],[309.525299,298.688856],[303.45898,297.063391],[297.595801,294.812727],[292.0,291.961524],[286.732885,288.54102],[281.852164,284.58869],[277.41131,280.147836],[273.45898,275.267115],[270.038476,270.0],[267.187273,264.404199],[264.936609,258.54102],[263.311144,252.474701],[262.328686,246.271708],[262.0,240.0],[262.328686,233.728292],[263.311144,227.525299],[264.936609,221.45898],[267.187273,215.595801],[270.038476,210.0],[273.45898,204.732885],[277.41131,199.852164],[281.852164,195.41131],[286.732885,191.45898],[292.0,188.038476],[297.595801,185.187273],[303.45898,182.936609],[309.525299,181.311144],[315.728292,180.328686],[322.0,180.0],[328.271708,180.328686],[334.474701,181.311144],[340.54102,182.936609],[346.404199,185.187273],[352.0,188.038476],[357.267115,191.45898],[362.147836,195.41131],[366.58869,199.852164],[370.54102,204.732885],[373.961524,210.0],[376.812727,215.595801],[379.063391,221.45898],[380.688856,227.525299],[381.671314,233.728292]]","closed":true}),
    node('outerFit', 'measure.roundness', 580, 180, {}),
    node('innerFit', 'measure.roundness', 860, 180, {}),
    node('fits', 'logic.boolean', 1140, 180, {"op":"and"}),
    node('gateFit', 'flow.ifBoolean', 1420, 180, {}),
    node('wall', 'measure.wallThickness', 1700, 20, {"sampleCount":72}),
    node('gateWall', 'flow.ifBoolean', 1980, 20, {}),
    node('offset', 'measure.concentricity', 2260, -140, {}),
    node('tolerance', 'value.range', 2540, -140, {"min":17,"max":23}),
    node('result', 'flow.result', 2820, -140, {}),
    node('missingWall', 'flow.result', 2260, 180, {}),
    node('joinWall', 'flow.join', 3100, 20, {}),
    node('missingFit', 'flow.result', 1700, 500, {}),
    node('joinFit', 'flow.join', 3380, 180, {})
  ],
  edges: [
    control('c0', 'outerPoints', 'next', 'innerPoints', 'exec'),
    control('c1', 'innerPoints', 'next', 'outerFit', 'exec'),
    control('c2', 'outerFit', 'next', 'innerFit', 'exec'),
    control('c3', 'innerFit', 'next', 'fits', 'exec'),
    control('c4', 'fits', 'next', 'gateFit', 'exec'),
    control('c5', 'gateFit', 'true', 'wall', 'exec'),
    control('c6', 'wall', 'next', 'gateWall', 'exec'),
    control('c7', 'gateWall', 'true', 'offset', 'exec'),
    control('c8', 'offset', 'next', 'tolerance', 'exec'),
    control('c9', 'tolerance', 'next', 'result', 'exec'),
    control('c10', 'result', 'next', 'joinWall', 'branch1'),
    control('c11', 'gateWall', 'false', 'missingWall', 'exec'),
    control('c12', 'missingWall', 'next', 'joinWall', 'branch2'),
    control('c13', 'joinWall', 'next', 'joinFit', 'branch1'),
    control('c14', 'gateFit', 'false', 'missingFit', 'exec'),
    control('c15', 'missingFit', 'next', 'joinFit', 'branch2'),
    data('d0', 'outerPoints', 'contours', 'outerFit', 'contours'),
    data('d1', 'innerPoints', 'contours', 'innerFit', 'contours'),
    data('d2', 'outerFit', 'found', 'fits', 'a'),
    data('d3', 'innerFit', 'found', 'fits', 'b'),
    data('d4', 'fits', 'result', 'gateFit', 'condition'),
    data('d5', 'innerFit', 'circle', 'wall', 'inner'),
    data('d6', 'outerFit', 'circle', 'wall', 'outer'),
    data('d7', 'wall', 'found', 'gateWall', 'condition'),
    data('d8', 'outerFit', 'circle', 'offset', 'circleA'),
    data('d9', 'innerFit', 'circle', 'offset', 'circleB'),
    data('d10', 'wall', 'minThickness', 'tolerance', 'value'),
    data('d11', 'tolerance', 'pass', 'result', 'pass'),
    data('d12', 'gateWall', 'condition', 'missingWall', 'pass'),
    data('d13', 'gateFit', 'condition', 'missingFit', 'pass')
  ]
});

export const edgeToleranceDemo = (): DemoGraph => ({
  nodes: [
    node('datumPoints', 'contour.fromPoints', 20, 180, {"pointsJson":"[[100,200],[150,200],[200,200],[250,200],[300,200]]","closed":false}),
    node('edgePoints', 'contour.fromPoints', 300, 180, {"pointsJson":"[[100,210],[150,210.5],[200,211],[250,211.5],[300,212]]","closed":false}),
    node('verticalPoints', 'contour.fromPoints', 580, 180, {"pointsJson":"[[320,80],[320,120],[320,180],[320,250]]","closed":false}),
    node('datumFit', 'measure.straightness', 860, 180, {}),
    node('edgeFit', 'measure.straightness', 1140, 180, {}),
    node('verticalFit', 'measure.straightness', 1420, 180, {}),
    node('fitsAB', 'logic.boolean', 1700, 180, {"op":"and"}),
    node('fits', 'logic.boolean', 1980, 180, {"op":"and"}),
    node('gateFit', 'flow.ifBoolean', 2260, 180, {}),
    node('parallel', 'measure.parallelism', 2540, 20, {}),
    node('perpendicular', 'measure.perpendicularity', 2820, 20, {}),
    node('deviation', 'measure.contourDeviation', 3100, 20, {"closed":false,"sampleStep":2,"symmetric":true}),
    node('gateDeviation', 'flow.ifBoolean', 3380, 20, {}),
    node('tolerance', 'value.range', 3660, -140, {"min":0,"max":13}),
    node('result', 'flow.result', 3940, -140, {}),
    node('missingDeviation', 'flow.result', 3660, 180, {}),
    node('joinDeviation', 'flow.join', 4220, 20, {}),
    node('missingFit', 'flow.result', 2540, 500, {}),
    node('joinFit', 'flow.join', 4500, 180, {})
  ],
  edges: [
    control('c0', 'datumPoints', 'next', 'edgePoints', 'exec'),
    control('c1', 'edgePoints', 'next', 'verticalPoints', 'exec'),
    control('c2', 'verticalPoints', 'next', 'datumFit', 'exec'),
    control('c3', 'datumFit', 'next', 'edgeFit', 'exec'),
    control('c4', 'edgeFit', 'next', 'verticalFit', 'exec'),
    control('c5', 'verticalFit', 'next', 'fitsAB', 'exec'),
    control('c6', 'fitsAB', 'next', 'fits', 'exec'),
    control('c7', 'fits', 'next', 'gateFit', 'exec'),
    control('c8', 'gateFit', 'true', 'parallel', 'exec'),
    control('c9', 'parallel', 'next', 'perpendicular', 'exec'),
    control('c10', 'perpendicular', 'next', 'deviation', 'exec'),
    control('c11', 'deviation', 'next', 'gateDeviation', 'exec'),
    control('c12', 'gateDeviation', 'true', 'tolerance', 'exec'),
    control('c13', 'tolerance', 'next', 'result', 'exec'),
    control('c14', 'result', 'next', 'joinDeviation', 'branch1'),
    control('c15', 'gateDeviation', 'false', 'missingDeviation', 'exec'),
    control('c16', 'missingDeviation', 'next', 'joinDeviation', 'branch2'),
    control('c17', 'joinDeviation', 'next', 'joinFit', 'branch1'),
    control('c18', 'gateFit', 'false', 'missingFit', 'exec'),
    control('c19', 'missingFit', 'next', 'joinFit', 'branch2'),
    data('d0', 'datumPoints', 'contours', 'datumFit', 'contours'),
    data('d1', 'edgePoints', 'contours', 'edgeFit', 'contours'),
    data('d2', 'verticalPoints', 'contours', 'verticalFit', 'contours'),
    data('d3', 'datumFit', 'found', 'fitsAB', 'a'),
    data('d4', 'edgeFit', 'found', 'fitsAB', 'b'),
    data('d5', 'fitsAB', 'result', 'fits', 'a'),
    data('d6', 'verticalFit', 'found', 'fits', 'b'),
    data('d7', 'fits', 'result', 'gateFit', 'condition'),
    data('d8', 'datumFit', 'line', 'parallel', 'a'),
    data('d9', 'edgeFit', 'line', 'parallel', 'b'),
    data('d10', 'datumFit', 'line', 'perpendicular', 'a'),
    data('d11', 'verticalFit', 'line', 'perpendicular', 'b'),
    data('d12', 'edgePoints', 'contours', 'deviation', 'contours'),
    data('d13', 'datumPoints', 'contours', 'deviation', 'referenceContours'),
    data('d14', 'deviation', 'found', 'gateDeviation', 'condition'),
    data('d15', 'deviation', 'deviation', 'tolerance', 'value'),
    data('d16', 'tolerance', 'pass', 'result', 'pass'),
    data('d17', 'gateDeviation', 'condition', 'missingDeviation', 'pass'),
    data('d18', 'gateFit', 'condition', 'missingFit', 'pass')
  ]
});

export const smallFeatureDemo = (): { nodes: Node[]; edges: Edge[] } => ({
  nodes: [
    node('board', 'region.fromRoi', 20, 40, {"canvasWidth": 128, "canvasHeight": 128, "roi": {"type": "rectangle", "x": 16, "y": 16, "width": 96, "height": 96}}),
    node('slit', 'region.fromRoi', 300, 40, {"canvasWidth": 128, "canvasHeight": 128, "roi": {"type": "rectangle", "x": 60, "y": 40, "width": 5, "height": 48}}),
    node('bright', 'region.fromRoi', 580, 40, {"canvasWidth": 128, "canvasHeight": 128, "roi": {"type": "rectangle", "x": 4, "y": 40, "width": 5, "height": 48}}),
    node('cut', 'region.boolean', 860, 40, {"operation": "difference"}),
    node('pattern', 'region.boolean', 1140, 40, {"operation": "union"}),
    node('top', 'image.morphology', 20, 300, {"operation": "whiteTopHat", "kernelWidth": 11, "kernelHeight": 11}),
    node('bottom', 'image.morphology', 300, 300, {"operation": "blackBottomHat", "kernelWidth": 11, "kernelHeight": 11}),
    node('gradient', 'image.morphology', 580, 300, {"operation": "gradient", "kernelWidth": 3, "kernelHeight": 3}),
    node('histogram', 'measure.grayHistogram', 860, 300, {"lowPercent": 5, "highPercent": 95}),
    node('gate', 'flow.ifBoolean', 1140, 300, {}),
    node('tolerance', 'value.range', 20, 560, {"min": 240, "max": 255}),
    node('result', 'flow.result', 300, 560, {}),
    node('missing', 'flow.result', 580, 560, {}),
    node('join', 'flow.join', 860, 560, {}),
  ],
  edges: [
    control('c0', 'board', 'next', 'slit', 'exec'),
    control('c1', 'slit', 'next', 'bright', 'exec'),
    control('c2', 'bright', 'next', 'cut', 'exec'),
    control('c3', 'cut', 'next', 'pattern', 'exec'),
    control('c4', 'pattern', 'next', 'top', 'exec'),
    control('c5', 'top', 'next', 'bottom', 'exec'),
    control('c6', 'bottom', 'next', 'gradient', 'exec'),
    control('c7', 'gradient', 'next', 'histogram', 'exec'),
    control('c8', 'histogram', 'next', 'gate', 'exec'),
    control('c9', 'gate', 'true', 'tolerance', 'exec'),
    control('c10', 'tolerance', 'next', 'result', 'exec'),
    control('c11', 'result', 'next', 'join', 'branch1'),
    control('c12', 'gate', 'false', 'missing', 'exec'),
    control('c13', 'missing', 'next', 'join', 'branch2'),
    data('d14', 'board', 'region', 'cut', 'a'),
    data('d15', 'slit', 'region', 'cut', 'b'),
    data('d16', 'cut', 'region', 'pattern', 'a'),
    data('d17', 'bright', 'region', 'pattern', 'b'),
    data('d18', 'pattern', 'image', 'top', 'image'),
    data('d19', 'pattern', 'image', 'bottom', 'image'),
    data('d20', 'pattern', 'image', 'gradient', 'image'),
    data('d21', 'bottom', 'image', 'histogram', 'image'),
    data('d22', 'slit', 'region', 'histogram', 'region'),
    data('d23', 'histogram', 'found', 'gate', 'condition'),
    data('d24', 'histogram', 'mean', 'tolerance', 'value'),
    data('d25', 'tolerance', 'pass', 'result', 'pass'),
    data('d26', 'gate', 'condition', 'missing', 'pass'),
  ]
});

export const regionShapeDemo = (): { nodes: Node[]; edges: Edge[] } => ({
  nodes: [
    node('source', 'region.fromRoi', 20, 40, {"canvasWidth": 128, "canvasHeight": 128, "roi": {"type": "polygon", "points": [{"x": 20, "y": 20}, {"x": 100, "y": 20}, {"x": 100, "y": 100}, {"x": 75, "y": 100}, {"x": 75, "y": 45}, {"x": 45, "y": 45}, {"x": 45, "y": 100}, {"x": 20, "y": 100}]}}),
    node('hull', 'region.convexHull', 300, 40, {}),
    node('gap', 'region.boolean', 580, 40, {"operation": "difference"}),
    node('boundary', 'region.boundary', 860, 40, {"boundaryMode": "inner"}),
    node('skeleton', 'region.skeleton', 1140, 40, {}),
    node('tolerance', 'value.range', 20, 300, {"min": 1000, "max": 2500}),
    node('result', 'flow.result', 300, 300, {}),
  ],
  edges: [
    control('c0', 'source', 'next', 'hull', 'exec'),
    control('c1', 'hull', 'next', 'gap', 'exec'),
    control('c2', 'gap', 'next', 'boundary', 'exec'),
    control('c3', 'boundary', 'next', 'skeleton', 'exec'),
    control('c4', 'skeleton', 'next', 'tolerance', 'exec'),
    control('c5', 'tolerance', 'next', 'result', 'exec'),
    data('d6', 'source', 'region', 'hull', 'region'),
    data('d7', 'hull', 'region', 'gap', 'a'),
    data('d8', 'source', 'region', 'gap', 'b'),
    data('d9', 'source', 'region', 'boundary', 'region'),
    data('d10', 'source', 'region', 'skeleton', 'region'),
    data('d11', 'gap', 'area', 'tolerance', 'value'),
    data('d12', 'tolerance', 'pass', 'result', 'pass'),
  ]
});

export const demos = {
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
  smallFeatures: '生成带亮细条与暗缝的区域测试图，分别做顶帽、底帽和梯度；统计暗缝底帽响应，空检测域走 NG。',
  regionShapes: 'ROI 多边形生成区域，计算凸包与凹陷差，展示边界和骨架，并按凹陷像素面积判定。',
  circularTolerance: '点列生成内外轮廓 → 圆度拟合 → 包含检查与壁厚 → 同心偏差 → 最小壁厚判定；缺失轮廓和内圆不包含时走 NG。',
  edgeTolerance: '点列轮廓 → 直线度与拟合 → 平行度、垂直度和轮廓偏差 → 公差判定；空输入或缺失测量走 NG。',
  colors: '演示灰度增强，并从原图提取颜色范围、测量区域内色差，再做公差判定；灰度测试图按 R=G=B，彩色业务保留原图接线。',
  textValidation: '校验批号前缀和完整格式，提取日期与批次分组，再合并两项判定；可将固定文本替换为 OCR 或条码输出。',
  alignment: '旋转模拟工件偏移 → 配准 → 找到后变换图像与区域 → 逐对象属性与目标列表 → 数量判定；未找到走 NG 分支。',
  defects: '明暗场校正 → 参考图差分与动态阈值 → 缺陷比对 → 区域灰度统计 → OK/NG；默认边界差异触发 NG，将参考图腐蚀核设为 1 可验证 OK。',
  regions: '阈值区域 → 连通域 → 属性筛选 → 填孔 → 限定处理域 → 区域交集 → 数量判定，保留精确像素区域。',
  precision: '形状定位 → 动态 ROI → 卡尺找圆 → 公差判定 → JSON 导出，无需硬件即可运行。',
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
