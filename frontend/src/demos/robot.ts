import { control, data, node, type DemoGraph } from './shared';

/** 机器人与设备类示例：手眼标定引导、PLC/TCP 握手。 */

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
