import { control, data, node, type DemoGraph } from './shared';

/** 采集类示例：虚拟相机、触发式取图与多相机同步帧组。 */

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
