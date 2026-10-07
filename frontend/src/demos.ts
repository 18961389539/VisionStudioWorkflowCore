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
    node('blob-1', 'measure.blob', 680, 150)
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
    node('blob-1', 'measure.blob', 570, 210),
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
    node('blob-1', 'measure.blob', 1210, 210)
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
    node('blob-1', 'measure.blob', 680, 150, { minArea: 1000 })
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
    node('blob-1', 'measure.blob', 700, 150, { minArea: 1000 })
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
    node('blob-1', 'measure.blob', 710, 150, { minArea: 1000 })
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
    node('blob-1', 'measure.blob', 540, 170),
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
    node('blob-1', 'measure.blob', 860, 210, { minArea: 1000 }),
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
    node('blob-1', 'measure.blob', 500, 260, { minArea: 1000 }),
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

export const demos = {
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
