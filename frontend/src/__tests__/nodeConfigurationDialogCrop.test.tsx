/**
 * 裁剪工作区/上游 ROI 域 —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

describe('crop workspace with a connected ROI', () => {
  const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
  const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', label: '动态裁剪',
    inputs: cropCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 1, y: 2, width: 3, height: 4 } } } };
  const roiCatalog = fallbackCatalog.find(item => item.type === 'roi.transform')!;
  const roiNode: Node = { id: 'roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI', inputs: roiCatalog.inputs, parameters: {} } };
  const imageNode: Node = { id: 'source', position: { x: 0, y: 0 }, data: { typeKey: 'image.synthetic', label: '场景图像' } };
  const overlays: VisionOverlay[] = [{ id: 'roi-transform-corrected-shape', nodeId: 'roi', type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 20, y: 10 }, { x: 20, y: 20 }] }];
  const result: RunResult = { ...report, runId: 'crop-run', overlays };

  it('shows the upstream ROI owner, disables local editing, and displays its overlay in the shared image frame', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode], edges, result });

    expect(await dialog.findByText('裁剪区域来自上游：定位修正 ROI')).toBeTruthy();
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByText('纯图像预览')).toBeTruthy();
    expect(dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect(dialog.queryByRole('button', { name: '清除选区（使用整图）' })).toBeNull();
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it('shows the actual connected cropped ROI on the matching input image even without an overlay', async () => {
    const upstreamCrop: Node = { id: 'upstream-crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', label: '前级裁剪',
      inputs: cropCatalog.inputs, parameters: {} } };
    const targetCrop: Node = { ...cropNode, id: 'target-crop', data: { ...cropNode.data, label: '后级裁剪' } };
    const edges = [
      { id: 'upstream-image', source: 'source', sourceHandle: 'image', target: 'upstream-crop', targetHandle: 'image' },
      { id: 'target-image', source: 'upstream-crop', sourceHandle: 'image', target: 'target-crop', targetHandle: 'image' },
      { id: 'cropped-roi', source: 'upstream-crop', sourceHandle: 'croppedRoi', target: 'target-crop', targetHandle: 'roi' }
    ];
    const runResult: RunResult = { ...report, runId: 'connected-cropped-roi-run', nodeReports: [
      { nodeId: 'target-crop', nodeType: 'image.crop', success: true, durationMs: 1, summary: {}, inputs: {
        roi: { type: 'Roi', display: 'Circle (30, 24, r=12)', value: { type: 'Circle', x: 30, y: 24, radius: 12 } }
      } }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'target-crop', portName: 'image', width: 30, height: 30 },
      { nodeId: 'upstream-crop', portName: 'image', width: 60, height: 50 },
      { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: targetCrop, catalogItem: cropCatalog, nodes: [targetCrop, upstreamCrop, imageNode], edges, result: runResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/upstream-crop/image'));
    expect(dialog.getByTestId('roi-preview-shape').getAttribute('data-type')).toBe('Circle');
    expect(dialog.getByTestId('roi-preview-shape').getAttribute('data-editable')).toBe('false');
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 只读');
    expect(dialog.getByTestId('overlay-ids').textContent).toBe('');
  });

  it('hides a stale upstream corrected ROI overlay on the crop input image', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode], edges, result, resultStale: true });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByText('上次运行图像 · 当前参数待验证')).toBeTruthy();
    expect(dialog.getByTestId('overlay-ids').textContent).not.toContain('roi-transform-corrected-shape');
  });

  it('keeps the current upstream ROI overlay when a local crop test fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const onPreview = vi.fn(async () => { throw new Error('local preview failed'); });
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode], edges, result, onPreview });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
    await waitFor(() => expect(dialog.getByText('上次运行图像 · 当前参数待验证')).toBeTruthy());
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it('shows ROI overlays from a fresh crop preview even when the previous workflow result was stale', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 30, height: 40 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode], edges,
      result, resultStale: true, onPreview: vi.fn(async () => result) });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).not.toContain('roi-transform-corrected-shape');
    fireEvent.click(dialog.getByRole('button', { name: '预览裁剪结果' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/crop/image'));
    fireEvent.click(dialog.getByRole('radio', { name: '输入图像' }));
    await waitFor(() => expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape'));
  });

  it('keeps the corrected ROI overlay visible through pixel-coordinate-preserving preprocessing', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 },
      { nodeId: 'gray', portName: 'image', width: 100, height: 100 },
      { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const grayscaleNode: Node = { id: 'gray', position: { x: 0, y: 0 }, data: { typeKey: 'image.channel', label: '灰度转换' } };
    const edges = [
      { id: 'roi-gray', source: 'roi', sourceHandle: 'image', target: 'gray', targetHandle: 'image' },
      { id: 'gray-crop', source: 'gray', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode, grayscaleNode], edges, result });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/gray/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it.each(['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle',
    'feature.line', 'feature.circle', 'measure.blobs', 'region.fromRoi'])(
    'keeps the corrected ROI overlay visible when the crop input passes through %s', async (imageNodeType) => {
      vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
        { nodeId: 'crop', portName: 'image', width: 100, height: 100 },
        { nodeId: 'measurement', portName: 'image', width: 100, height: 100 },
        { nodeId: 'source', portName: 'image', width: 100, height: 100 }
      ] }) })));
      const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: { typeKey: imageNodeType, label: '图像节点' } };
      const edges = [
        { id: 'measurement-source', source: 'source', sourceHandle: 'image', target: 'measurement', targetHandle: 'image' },
        { id: 'measurement-crop', source: 'measurement', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
        { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
        { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
      ];
      const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog,
        nodes: [cropNode, roiNode, imageNode, measurementNode], edges, result });

      await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/measurement/image'));
      expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
    }
  );

  it('uses runtime image ports to preserve ROI overlays through plugin image pass-through nodes', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 },
      { nodeId: 'plugin', portName: 'image', width: 100, height: 100 },
      { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const pluginCatalog: NodeCatalogItem = { type: 'plugin.imagePassThrough', displayName: 'Image pass-through', category: 'Plugin',
      inputs: [{ name: 'image', label: 'Image', dataType: 'Image', required: true }],
      outputs: [{ name: 'image', label: 'Image', dataType: 'Image', required: true }], parameters: [] };
    const pluginNode: Node = { id: 'plugin', position: { x: 0, y: 0 }, data: { typeKey: pluginCatalog.type, label: pluginCatalog.displayName } };
    const edges = [
      { id: 'plugin-source', source: 'source', sourceHandle: 'image', target: 'plugin', targetHandle: 'image' },
      { id: 'plugin-crop', source: 'plugin', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog,
      catalogItems: [...fallbackCatalog, pluginCatalog], nodes: [cropNode, roiNode, imageNode, pluginNode], edges, result });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/plugin/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it('shows the corrected ROI overlay when crop and reference image use sibling pixel-preserving branches', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 },
      { nodeId: 'gray', portName: 'image', width: 100, height: 100 },
      { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const grayscaleNode: Node = { id: 'gray', position: { x: 0, y: 0 }, data: { typeKey: 'image.channel', label: '灰度转换' } };
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'source-gray', source: 'source', sourceHandle: 'image', target: 'gray', targetHandle: 'image' },
      { id: 'gray-roi-reference', source: 'gray', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode, grayscaleNode], edges, result });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it('allows preview when a malformed local ROI is dormant behind the connected ROI input', async () => {
    const invalidCropNode: Node = { ...cropNode, data: { ...cropNode.data,
      parameters: { roi: { type: 'Rectangle', x: 0, y: 0, width: 0, height: 10 } } } };
    const edges = [
      { id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const onPreview = vi.fn(async () => result);
    const { dialog } = mount({ node: invalidCropNode, catalogItem: cropCatalog, nodes: [invalidCropNode, roiNode, imageNode], edges, result, onPreview });

    expect(dialog.queryByText(/请修正参数后再运行或应用/)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '预览裁剪结果' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
  });

  it('blocks a local crop ROI that misses the known input image before execution', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const outsideCropNode: Node = { ...cropNode, data: { ...cropNode.data,
      parameters: { roi: { type: 'Rectangle', x: 120, y: 12, width: 20, height: 30 } } } };
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const onPreview = vi.fn(async () => result);
    const { dialog } = mount({ node: outsideCropNode, catalogItem: cropCatalog, nodes: [outsideCropNode, imageNode], edges, result, onPreview });

    expect(await dialog.findByText(/裁剪区域与当前输入图像（100×100）没有像素交集/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(true);
    expect(onPreview).not.toHaveBeenCalled();
  });

  it('shows, clears, and restores a partial legacy crop rectangle on the input image', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 200, height: 100 }, { nodeId: 'source', portName: 'image', width: 200, height: 100 }
    ] }) })));
    const legacyCropNode: Node = { ...cropNode, data: { ...cropNode.data,
      parameters: { x: '12', y: 8, width: 80 } } };
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const { dialog } = mount({ node: legacyCropNode, catalogItem: cropCatalog, nodes: [legacyCropNode, imageNode], edges, result });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    const previewRoi = dialog.getByTestId('roi-preview-shape');
    expect(previewRoi.getAttribute('data-type')).toBe('Rectangle');
    expect(previewRoi.getAttribute('data-editable')).toBe('true');
    expect(previewRoi.getAttribute('data-x')).toBe('12');
    expect(previewRoi.getAttribute('data-y')).toBe('8');
    expect(previewRoi.getAttribute('data-width')).toBe('80');
    expect(previewRoi.getAttribute('data-height')).toBe('100');
    expect(dialog.getByText(/正在使用旧版裁剪坐标/)).toBeTruthy();

    fireEvent.click(dialog.getByRole('button', { name: '清除选区（使用整图）' }));
    expect(dialog.getByTestId('roi-preview-shape').getAttribute('data-type')).toBe('none');
    expect(dialog.getByRole('button', { name: '撤销清除选区' })).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: '撤销清除选区' }));
    expect(dialog.getByTestId('roi-preview-shape').getAttribute('data-height')).toBe('100');
  });

  it('blocks a partial legacy crop rectangle that misses the known input image', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const legacyCropNode: Node = { ...cropNode, data: { ...cropNode.data,
      parameters: { x: 120, y: 12, width: 20 } } };
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const onPreview = vi.fn(async () => result);
    const { dialog } = mount({ node: legacyCropNode, catalogItem: cropCatalog, nodes: [legacyCropNode, imageNode], edges, result, onPreview });

    expect(await dialog.findByText(/裁剪区域与当前输入图像（100×100）没有像素交集/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(true);
    expect(onPreview).not.toHaveBeenCalled();
  });

  it('blocks a brush crop erased along the exact same raster path before execution', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const erasedBrushCropNode: Node = { ...cropNode, data: { ...cropNode.data, parameters: { roi: { type: 'Brush', strokes: [
      { mode: 'Add', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] },
      { mode: 'Erase', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] }
    ] } } } };
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const onPreview = vi.fn(async () => result);
    const { dialog } = mount({ node: erasedBrushCropNode, catalogItem: cropCatalog, nodes: [erasedBrushCropNode, imageNode], edges, result, onPreview });

    expect(await dialog.findByText(/笔刷裁剪区域被同轨迹、同笔宽的擦除笔迹完全覆盖/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(true);
    expect(onPreview).not.toHaveBeenCalled();
  });

  it('keeps preview enabled when an add stroke restores the same path after erasing', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const restoredBrushCropNode: Node = { ...cropNode, data: { ...cropNode.data, parameters: { roi: { type: 'Brush', strokes: [
      { mode: 'Erase', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] },
      { mode: 'Add', width: 4, points: [{ x: 5, y: 5 }, { x: 9, y: 5 }] }
    ] } } } };
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const onPreview = vi.fn(async () => result);
    const { dialog } = mount({ node: restoredBrushCropNode, catalogItem: cropCatalog, nodes: [restoredBrushCropNode, imageNode], edges, result, onPreview });

    await waitFor(() => expect(dialog.queryByText(/笔刷裁剪区域被同轨迹、同笔宽的擦除笔迹完全覆盖/)).toBeNull());
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('does not project an upstream ROI overlay onto a different image coordinate frame', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'processed', portName: 'image', width: 80, height: 80 }
    ] }) })));
    const processedNode: Node = { id: 'processed', position: { x: 0, y: 0 }, data: { typeKey: 'image.resize', label: '缩放图像' } };
    const edges = [
      { id: 'processed-source', source: 'source', sourceHandle: 'image', target: 'processed', targetHandle: 'image' },
      { id: 'crop-image', source: 'processed', sourceHandle: 'image', target: 'crop', targetHandle: 'image' },
      { id: 'corrected-roi', source: 'roi', sourceHandle: 'roi', target: 'crop', targetHandle: 'roi' },
      { id: 'roi-debug-image', source: 'source', sourceHandle: 'image', target: 'roi', targetHandle: 'debugImage' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, imageNode, processedNode], edges, result });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/processed/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).not.toContain('roi-transform-corrected-shape');
  });

  it('shows the crop output after preview and keeps the input one click away for further editing', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 30, height: 40 }, { nodeId: 'source', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const edges = [{ id: 'crop-image', source: 'source', sourceHandle: 'image', target: 'crop', targetHandle: 'image' }];
    const cropResult: RunResult = { ...report, runId: 'crop-preview', nodeReports: [
      { ...report.nodeReports[0], nodeId: 'crop', nodeType: 'image.crop', summary: { width: 30, height: 40 } }
    ] };
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, imageNode], edges,
      result: cropResult, onPreview: vi.fn(async () => cropResult) });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    fireEvent.click(dialog.getByRole('button', { name: '预览裁剪结果' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/crop/image'));
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 只读');

    fireEvent.click(dialog.getByRole('radio', { name: '输入图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 可编辑');

    fireEvent.click(dialog.getByRole('radio', { name: '裁剪输出' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/crop/image'));
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 只读');
    fireEvent.click(dialog.getByRole('button', { name: '清除选区（使用整图）' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 可编辑');
    expect(dialog.getByText('未绘制 · 使用旧版坐标或整幅图像')).toBeTruthy();
    expect(dialog.getByRole('button', { name: '撤销清除选区' })).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: '撤销清除选区' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source/image'));
    expect(dialog.queryByRole('button', { name: '撤销清除选区' })).toBeNull();
    expect(dialog.getByRole('button', { name: '清除选区（使用整图）' })).toBeTruthy();
  });
});

