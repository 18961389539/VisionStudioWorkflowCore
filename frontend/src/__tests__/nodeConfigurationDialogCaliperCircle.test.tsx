/**
 * 卡尺几何写回/校验域（下） —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

describe('caliper geometry input guidance', () => {
  const mountCropFrameMeasurement = (sourceType: string, targetType: string, outputPort: string, inputPort: string, separateGeometryFrame: boolean) => {
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '上游几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const measurementCrop: Node = { id: 'measurement-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '测量图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const geometryCrop: Node = { id: 'geometry-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '几何图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '源图像' } };
    const geometryFrame = separateGeometryFrame ? geometryCrop : measurementCrop;
    const edges = [
      { id: 'measurement-crop-image', source: sourceImage.id, sourceHandle: 'image', target: measurementCrop.id, targetHandle: 'image' },
      ...(separateGeometryFrame ? [{ id: 'geometry-crop-image', source: sourceImage.id, sourceHandle: 'image', target: geometryCrop.id, targetHandle: 'image' }] : []),
      { id: 'geometry-image', source: geometryFrame.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: measurementCrop.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort }
    ];
    return mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, measurementCrop, ...(separateGeometryFrame ? [geometryCrop] : []), sourceImage], edges });
  };
  const mountCropTransformSourceMeasurement = (sourceType: string, targetType: string, outputPort: string, inputPort: string, separateGeometrySource: boolean) => {
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '上游几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪输入图像' } };
    const otherImage: Node = { id: 'other-image', position: { x: 0, y: 0 }, data: { label: '另一张图像' } };
    const geometryImage = separateGeometrySource ? otherImage : sourceImage;
    const edges = [
      { id: 'crop-image', source: sourceImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'geometry-image', source: geometryImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    return mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, cropNode, sourceImage, ...(separateGeometrySource ? [otherImage] : [])], edges });
  };



  it('offers image-based local ROI editing for the single caliper', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺测量',
      inputs: caliperCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 2, y: 3, width: 20, height: 15 } } } };
    const imageNode: Node = { id: 'caliper-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'IMAGE' }];
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));

    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: report });

    expect(caliperCatalog.capabilities?.supportsRoi).toBe(true);
    await waitFor(() => expect(dialog.getByText('ROI 工具')).toBeTruthy());
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 可编辑');
    expect(dialog.getByTestId('roi-target').textContent).toBe('输入图像 · 场景图像');
    expect(dialog.getByRole('button', { name: '在图像中编辑' })).toBeTruthy();
  });

  it('keeps ROI drawing read-only when the selected input image is missing and the viewer falls back to final preview', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺测量',
      inputs: caliperCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 2, y: 3, width: 20, height: 15 } } } };
    const imageNode: Node = { id: 'missing-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];

    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: report });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/preview'));
    expect(dialog.getByTestId('roi-enabled').textContent).toBe('ROI 只读');
    expect(dialog.queryByText('ROI 工具')).toBeNull();
    expect(dialog.queryByRole('button', { name: '在图像中编辑' })).toBeNull();
  });

  it('uses the caliper pass-through output for geometry editing when the run did not save its input image', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺找线',
      inputs: caliperCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'missing-input', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));

    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: report });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper/image'));
    expect(dialog.getByTestId('image-url').dataset).toMatchObject({ geometry: 'line', geometryEditable: 'true' });
    expect(await dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper/image');
  });

  describe('caliper image-port provenance', () => {
    function cropReference(includeReference = true) {
      const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
      const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
      const line: Node = { id: 'port-line', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type,
        inputs: lineCatalog.inputs, parameters: { startX: 30, startY: 35, endX: 80, endY: 35 } } };
      const crop: Node = { id: 'port-crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type,
        inputs: cropCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 50, height: 50 } } } };
      const source: Node = { id: 'multi-image', position: { x: 0, y: 0 }, data: { outputs: [
        { name: 'image', dataType: 'Image', label: '其它图像端口' },
        { name: 'roiMask', dataType: 'Image', label: '基准图像端口' },
      ] } };
      const edges = [
        { id: 'reference', source: source.id, sourceHandle: 'RoImAsK', target: crop.id, targetHandle: 'image' },
        { id: 'image', source: crop.id, sourceHandle: 'image', target: line.id, targetHandle: 'image' },
        { id: 'transform', source: crop.id, sourceHandle: 'transform', target: line.id, targetHandle: 'transform' },
      ];
      vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
        { nodeId: source.id, portName: 'image', width: 1000, height: 800 },
        ...(includeReference ? [{ nodeId: source.id, portName: 'roiMask', width: 100, height: 100 }] : []),
        { nodeId: crop.id, portName: 'image', width: 50, height: 50 },
        { nodeId: line.id, portName: 'image', width: 50, height: 50 },
      ] }) })));
      const result: RunResult = { ...report, nodeReports: [{ nodeId: line.id, nodeType: lineCatalog.type,
        success: true, durationMs: 1, summary: {}, inputs: { transform: { type: 'Transform2D', display: 'crop', value: {
          h00: 1, h01: 0, h02: -20, h10: 0, h11: 1, h12: -20, h20: 0, h21: 0, h22: 1,
          sourceUnit: 'px', targetUnit: 'px',
        } } } }] };
      return mount({ node: line, catalogItem: lineCatalog, nodes: [line, crop, source], edges, result });
    }

    it('uses the exact crop input port as the editable reference and preserves edits', async () => {
      const view = cropReference();
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('/nodes/multi-image/image?portName=roiMask'));
      expect(view.dialog.getByTestId('image-url').dataset).toMatchObject({ geometry: 'line', geometryEditable: 'true' });
      fireEvent.click(view.dialog.getByRole('button', { name: '模拟拖动搜索线' }));
      fireEvent.click(view.dialog.getByRole('button', { name: /应\s*用/ }));
      expect(view.props.onApply).toHaveBeenCalledWith(expect.objectContaining({ startX: 23, startY: 31, endX: 91, endY: 47 }));
    });

    it('does not substitute a different output when the reference port was not captured', async () => {
      const view = cropReference(false);
      expect(await view.dialog.findByText('当前图像不能用于测量几何编辑')).toBeTruthy();
      expect(view.dialog.getByTestId('image-url').dataset.geometry).toBeUndefined();
      expect(view.dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
      expect(view.dialog.queryByRole('button', { name: '适配几何到图像' })).toBeNull();
    });

    it('fits geometry to the connected reference port dimensions rather than another output', async () => {
      const view = cropReference();
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      fireEvent.click(view.dialog.getByRole('button', { name: '适配几何到图像' }));
      expect(view.dialog.getByTestId('line-geometry').dataset).toMatchObject({
        startX: '18.75', startY: '50', endX: '81.25', endY: '50',
      });
    });

    it('restores the connected reference port after inspecting the read-only current image', async () => {
      const view = cropReference();
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      fireEvent.click(view.dialog.getByRole('radio', { name: '当前测量图像' }));
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('/nodes/port-crop/image?portName=image'));
      expect(view.dialog.getByTestId('line-geometry').dataset).toMatchObject({
        startX: '10', startY: '15', endX: '60', endY: '15', editable: 'false',
      });
      fireEvent.click(view.dialog.getByRole('radio', { name: '基准图像' }));
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      expect(view.dialog.getByTestId('image-url').dataset.geometryEditable).toBe('true');
    });

    it('keeps the connected reference port after a new run resets output selection', async () => {
      const view = cropReference();
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      view.rerender(<NodeConfigurationDialog {...view.props} result={{ ...view.props.result!, runId: 'next-run' }} />);
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent)
        .toBe('/api/runs/next-run/nodes/multi-image/image?portName=roiMask'));
      expect(view.dialog.getByTestId('image-url').dataset.geometryEditable).toBe('true');
    });

    it('keeps alternate output inspection read-only and restores editing on the connected port', async () => {
      const view = cropReference();
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      fireEvent.mouseDown(view.dialog.getByRole('combobox', { name: '输出图像端口' }));
      fireEvent.click((await screen.findAllByText('其它图像端口')).at(-1)!);
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=image'));
      expect(view.dialog.getByTestId('image-url').dataset.geometry).toBeUndefined();
      expect(view.dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
      expect(view.dialog.getByText('当前图像不能用于测量几何编辑')).toBeTruthy();
      fireEvent.mouseDown(view.dialog.getByRole('combobox', { name: '输出图像端口' }));
      fireEvent.click((await screen.findAllByText('基准图像端口')).at(-1)!);
      await waitFor(() => expect(view.dialog.getByTestId('image-url').dataset.geometryEditable).toBe('true'));
      expect(view.dialog.getByTestId('image-url').dataset.geometry).toBe('line');
      expect(view.dialog.queryByText('当前图像不能用于测量几何编辑')).toBeNull();
    });

    it('does not use an arbitrary caliper output as its pass-through image', async () => {
      const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
      const line: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type,
        inputs: lineCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
      vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
        { nodeId: line.id, portName: 'roiMask', width: 40, height: 40 },
      ] }) })));
      const result: RunResult = { ...report, overlays: [
        { id: 'main-image-measurement', nodeId: line.id, type: 'Point', x: 64, y: 32 },
      ] };
      const view = mount({ node: line, catalogItem: lineCatalog, nodes: [line], result });
      await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('portName=roiMask'));
      expect(view.dialog.getByTestId('image-url').dataset.geometry).toBeUndefined();
      expect(view.dialog.getByTestId('overlay-ids').textContent).not.toContain('main-image-measurement');
      expect(view.dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
      expect(view.dialog.getByText('当前图像不能用于测量几何编辑')).toBeTruthy();
    });
  });

  it.each(['measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperCircle', 'feature.edgeSubpixel'] as const)(
    'shows %s result overlays until its measurement geometry changes', async (type) => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === type)!;
    const parameters = type === 'measure.caliperCircle'
      ? { centerX: 50, centerY: 50, radius: 20 }
      : { startX: 10, startY: 20, endX: 80, endY: 20 };
    const labels: Record<string, string> = { 'measure.caliper': '卡尺测量', 'measure.caliperWidth': '双边缘卡尺测宽', 'measure.caliperLine': '卡尺找线',
      'measure.caliperCircle': '卡尺找圆', 'feature.edgeSubpixel': '亚像素找边' };
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: labels[type],
      inputs: caliperCatalog.inputs, parameters } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const resultWithCandidate: RunResult = { ...report, runId: 'caliper-candidate-run', overlays: [
      { id: `${type}-result-point`, nodeId: caliperNode.id, type: 'Point', x: 64, y: 32, stroke: '#faad14', strokeWidth: 2.5 }
    ], nodeReports: [{ nodeId: caliperNode.id, nodeType: caliperCatalog.type, success: true, durationMs: 1,
      summary: { found: false, reason: 'too-few-edge-points' } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: resultWithCandidate });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain(`${type}-result-point`);
    fireEvent.click(dialog.getByRole('button', { name: type === 'measure.caliperCircle' ? '模拟调整标称圆' : '模拟拖动搜索线' }));
    await waitFor(() => expect(dialog.getByTestId('overlay-ids').textContent).not.toContain(`${type}-result-point`));
  });

  it('hides stale measurement overlays when geometry editing uses the node output image', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺找线',
      inputs: caliperCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'missing-input', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const resultWithCandidate: RunResult = { ...report, runId: 'caliper-output-overlay-run', overlays: [
      { id: 'stale-output-edge-point', nodeId: caliperNode.id, type: 'Point', x: 64, y: 32, stroke: '#faad14', strokeWidth: 2.5 }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: resultWithCandidate });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('stale-output-edge-point');
    fireEvent.click(dialog.getByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => expect(dialog.getByTestId('overlay-ids').textContent).not.toContain('stale-output-edge-point'));
  });

  it('hides local search-line coordinates when a searchLine input is connected', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线',
      inputs: lineCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const geometryNode: Node = { id: 'fitted-line', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'line-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: 'line-image', sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-geometry-input', source: geometryNode.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, geometryNode, imageNode], edges });

    expect(dialog.getByText('搜索线来自上游：上游拟合线')).toBeTruthy();
    for (const name of ['startX', 'startY', 'endX', 'endY'])
      expect(dialog.queryByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === name)!.label })).toBeNull();
    expect(dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'caliperCount')!.label })).toBeTruthy();
  });

  it('shows the actual connected search line as a read-only image overlay', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线',
      inputs: lineCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20, caliperCount: 9 } } };
    const geometryNode: Node = { id: 'fitted-line', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'line-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-geometry-input', source: geometryNode.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'SEARCHLINE' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { SEARCHLINE: { type: 'Line2D', display: '[(14, 22), (88, 46)]',
        value: { start: { x: 14, y: 22 }, end: { x: 88, y: 46 } } } } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: lineNode.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, geometryNode, imageNode], edges, result: lineResult });

    expect(dialog.getByText(/蓝色线显示本次运行实际使用的上游搜索线/)).toBeTruthy();
    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('14');
    expect(geometry.getAttribute('data-start-y')).toBe('22');
    expect(geometry.getAttribute('data-end-x')).toBe('88');
    expect(geometry.getAttribute('data-end-y')).toBe('46');
    expect(geometry.getAttribute('data-editable')).toBe('false');
    expect(dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
  });

  it('warns when the automatic caliper span leaves too few profiles inside the image', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线',
      inputs: lineCatalog.inputs, parameters: { startX: -200, startY: 64, endX: 328, endY: 64,
        caliperCount: 9, minValidCalipers: 3, sampleWidth: 1 } } };
    const imageNode: Node = { id: 'line-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const lineResult: RunResult = { ...report, runId: 'caliper-span-run', nodeReports: [
      { nodeId: lineNode.id, nodeType: lineCatalog.type, success: true, durationMs: 1, summary: {} }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: lineNode.id, portName: 'image', width: 128, height: 128 },
      { nodeId: imageNode.id, portName: 'image', width: 128, height: 128 }
    ] }) })));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: lineResult });

    expect(await dialog.findByText('图像边界可能导致卡尺数量不足')).toBeTruthy();
    expect(dialog.getByText(/8\/9 条剖面完全位于图像外/)).toBeTruthy();
    expect(dialog.getByText(/最多剩余 1 条可采样，当前拟合至少需要 3 条有效卡尺/)).toBeTruthy();

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整采样跨度' }));
    await waitFor(() => expect(dialog.queryByText('图像边界可能导致卡尺数量不足')).toBeNull());
  });

  it('warns when a single caliper search line is fully outside the image', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'single-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '单点卡尺',
      inputs: caliperCatalog.inputs, parameters: { startX: -200, startY: 64, endX: -100, endY: 64 } } };
    const imageNode: Node = { id: 'caliper-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const caliperResult: RunResult = { ...report, runId: 'single-caliper-bounds-run', nodeReports: [
      { nodeId: caliperNode.id, nodeType: caliperCatalog.type, success: true, durationMs: 1, summary: {} }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 128, height: 128 },
      { nodeId: imageNode.id, portName: 'image', width: 128, height: 128 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, imageNode], edges, result: caliperResult });

    expect(await dialog.findByText('搜索线完全位于图像外')).toBeTruthy();
    expect(dialog.getByText(/当前卡尺不会采到图像像素/)).toBeTruthy();

    fireEvent.click(dialog.getByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => expect(dialog.queryByText('搜索线完全位于图像外')).toBeNull());
  });

  it('warns before the first run when a transform source uses engineering units', () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'engineering-unit-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type,
      label: '单点卡尺', inputs: caliperCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 90, endY: 20 } } };
    const transformNode: Node = { id: 'constant-transform', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformConstant',
      label: '固定坐标变换', parameters: { unit: 'mm' } } };
    const edges = [{ id: 'transform-input', source: transformNode.id, sourceHandle: 'transform',
      target: caliperNode.id, targetHandle: 'transform' }];
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, transformNode], edges });

    expect(dialog.getByText('transform 来源可能不适用于卡尺')).toBeTruthy();
    expect(dialog.getByText(/当前配置的单位不是 px/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('does not show a unit warning for a pixel constant transform', () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'pixel-unit-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type,
      label: '单点卡尺', inputs: caliperCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 90, endY: 20 } } };
    const transformNode: Node = { id: 'pixel-transform', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformConstant',
      label: '像素刚性变换', parameters: { unit: 'px' } } };
    const edges = [{ id: 'transform-input', source: transformNode.id, sourceHandle: 'transform',
      target: caliperNode.id, targetHandle: 'transform' }];
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, transformNode], edges });

    expect(dialog.queryByText('transform 来源可能不适用于卡尺')).toBeNull();
  });

  it('explains when the latest caliper input transform is not rigid pixel-to-pixel', () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'invalid-transform-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type,
      label: '单点卡尺', inputs: caliperCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 90, endY: 20 } } };
    const transformNode: Node = { id: 'affine-transform', position: { x: 0, y: 0 }, data: { typeKey: 'calibration.planar', label: '平面标定' } };
    const edges = [{ id: 'transform-input', source: transformNode.id, sourceHandle: 'transform',
      target: caliperNode.id, targetHandle: 'transform' }];
    const resultWithAffineTransform: RunResult = { ...report, runId: 'non-rigid-caliper-run', nodeReports: [
      { nodeId: caliperNode.id, nodeType: caliperCatalog.type, success: false, durationMs: 1,
        summary: {}, error: 'Measurement transform must be a finite, orientation-preserving rigid px-to-px transform (translation and rotation only).',
        inputs: { transform: { type: 'Transform2D', display: 'affine', value: { sourceUnit: 'px', targetUnit: 'px',
          h00: 1.2, h01: 0, h02: 5, h10: 0, h11: 1, h12: 0, h20: 0, h21: 0, h22: 1 } } } }
    ] };
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, transformNode], edges,
      result: resultWithAffineTransform });

    expect(dialog.getByText('当前 transform 不符合卡尺测量要求')).toBeTruthy();
    expect(dialog.getByText(/最近一次运行报告中的实际输入/)).toBeTruthy();
    expect(dialog.queryByText('暂时无法预估变换后的图像边界')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('checks a transformed single-caliper line against the current measurement image', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'transformed-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '单点卡尺',
      inputs: caliperCatalog.inputs, parameters: { startX: -200, startY: 64, endX: -100, endY: 64 } } };
    const transformNode: Node = { id: 'pose-transform', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 256, referenceHeight: 128 } } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: caliperNode.id, targetHandle: 'TRANSFORM' }
    ];
    const transformedResult: RunResult = { ...report, runId: 'transformed-caliper-bounds-run', nodeReports: [
      { nodeId: caliperNode.id, nodeType: caliperCatalog.type, success: true, durationMs: 1, summary: {}, inputs: {
        TRANSFORM: { type: 'Transform2D', display: 'translation', value: { sourceUnit: 'px', targetUnit: 'px',
          h00: 1, h01: 0, h02: 50, h10: 0, h11: 1, h12: 0, h20: 0, h21: 0, h22: 1 } }
      } }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 128, height: 128 },
      { nodeId: imageNode.id, portName: 'image', width: 128, height: 128 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, transformNode, imageNode], edges, result: transformedResult });

    expect(await dialog.findByText('搜索线完全位于图像外')).toBeTruthy();
    expect(dialog.getByText(/已按本次有效运行的刚性 transform 映射到测量输入图像后估算/)).toBeTruthy();
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('-200');
    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
  });

  it('does not guess transformed bounds from the reference image when no current transform is available', () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'pending-transform-caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '单点卡尺',
      inputs: caliperCatalog.inputs, parameters: { startX: -200, startY: 64, endX: -100, endY: 64 } } };
    const transformNode: Node = { id: 'pose-transform', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 256, referenceHeight: 128 } } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: caliperNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, transformNode, imageNode], edges });

    expect(dialog.getByText('暂时无法预估变换后的图像边界')).toBeTruthy();
    expect(dialog.queryByText('搜索线完全位于图像外')).toBeNull();
  });

  it('checks transformed circle calipers against the current measurement image', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'transformed-circle', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 80, centerY: 64, radius: 20, radialBand: 8,
        angleStartDeg: -180, angleEndDeg: 180, samplingMode: 'count', caliperCount: 72, minValidCalipers: 3 } } };
    const transformNode: Node = { id: 'pose-transform', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 256, referenceHeight: 128 } } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const transformedResult: RunResult = { ...report, runId: 'transformed-circle-bounds-run', nodeReports: [
      { nodeId: circleNode.id, nodeType: circleCatalog.type, success: true, durationMs: 1, summary: {}, inputs: {
        transform: { type: 'Transform2D', display: 'translation', value: { sourceUnit: 'px', targetUnit: 'px',
          h00: 1, h01: 0, h02: 50, h10: 0, h11: 1, h12: 0, h20: 0, h21: 0, h22: 1 } }
      } }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: circleNode.id, portName: 'image', width: 128, height: 128 },
      { nodeId: imageNode.id, portName: 'image', width: 128, height: 128 }
    ] }) })));
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, transformNode, imageNode], edges, result: transformedResult });

    expect(await dialog.findByText('部分圆卡尺位于图像边界外')).toBeTruthy();
    expect(dialog.getByText(/已按本次有效运行的刚性 transform 映射到测量输入图像后估算/)).toBeTruthy();
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('80');
    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
  });

  it('warns when the nominal circle leaves too few radial calipers inside the image', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 200, centerY: 200, radius: 20, radialBand: 8,
        angleStartDeg: -180, angleEndDeg: 180, samplingMode: 'count', caliperCount: 72, minValidCalipers: 3 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const circleResult: RunResult = { ...report, runId: 'caliper-circle-bounds-run', nodeReports: [
      { nodeId: circleNode.id, nodeType: circleCatalog.type, success: true, durationMs: 1, summary: {} }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: circleNode.id, portName: 'image', width: 128, height: 128 },
      { nodeId: imageNode.id, portName: 'image', width: 128, height: 128 }
    ] }) })));
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: circleResult });

    expect(await dialog.findByText('图像边界可能导致圆卡尺不足')).toBeTruthy();
    expect(dialog.getByText(/72\/72 条径向剖面完全位于图像外/)).toBeTruthy();
    expect(dialog.getByText(/最多剩余 0 条可采样，当前拟合至少需要 3 条有效卡尺/)).toBeTruthy();

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整标称圆' }));
    await waitFor(() => expect(dialog.queryByText('图像边界可能导致圆卡尺不足')).toBeNull());
  });

  it('warns against applying a pose transform twice to an already corrected circle', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 20, centerY: 30, radius: 10 } } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-geometry-input', source: roiNode.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' },
      { id: 'circle-transform-input', source: roiNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const correctedCircleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { circle: { type: 'Circle', display: '(20, 30, r=10)', value: { x: 20, y: 30, radius: 10 } } } }] };
    const { dialog, props } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, roiNode, imageNode], edges, result: correctedCircleResult });

    expect(dialog.getByText('定位修正圆不能再次应用 transform')).toBeTruthy();
    expect(dialog.getByText(/圆已经是当前图像坐标，请断开这个 transform 输入/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    expect(props.onPreview).not.toHaveBeenCalled();
    for (const name of ['centerX', 'centerY', 'radius'])
      expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === name)!.label })).toBeNull();
    expect(dialog.queryByTestId('circle-geometry')).toBeNull();
    expect(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'caliperCount')!.label })).toBeTruthy();
  });

  it.each([
    ['Rectangle', true],
    ['Circle', false]
  ])('explains whether a %s ROI transform supplies a bounding circle to the circle caliper', (roiType, isBoundingApproximation) => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 20, centerY: 30, radius: 10 } } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { roi: roiType === 'Circle' ? { type: 'Circle', x: 30, y: 25, radius: 25 }
        : { type: 'Rectangle', x: 10, y: 10, width: 40, height: 30 } } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-geometry-input', source: roiNode.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' }
    ];
    const resultWithBoundingCircle: RunResult = { ...report, nodeReports: [
      { nodeId: roiNode.id, nodeType: 'roi.transform', success: true, durationMs: 1,
        summary: { roiType, boundingRadius: 25 } },
      { nodeId: circleNode.id, nodeType: circleCatalog.type, success: true, durationMs: 1, summary: {},
        inputs: { circle: { type: 'Circle', display: '(30, 25, r=25)', value: { x: 30, y: 25, radius: 25 } } } }
    ] };
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, roiNode, imageNode], edges,
      result: resultWithBoundingCircle });

    if (isBoundingApproximation) {
      expect(dialog.getByText('上游提供的是 ROI 包围圆')).toBeTruthy();
      expect(dialog.getByText(/ROI 类型为 Rectangle；其 circle 端口输出包围圆，不是圆拟合结果/)).toBeTruthy();
    } else {
      expect(dialog.queryByText('上游提供的是 ROI 包围圆')).toBeNull();
    }
  });

  it('detects a repeated circle correction inside a composed transform chain', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 20, centerY: 30, radius: 10 } } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI' } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: 'image.crop', label: '裁剪变换' } };
    const composeNode: Node = { id: 'compose', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformCompose', label: '组合变换' } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'corrected-circle', source: roiNode.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformA' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformB' },
      { id: 'composed-transform', source: composeNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];

    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, roiNode, cropNode, composeNode, imageNode], edges });

    expect(dialog.getByText('定位修正圆不能再次应用 transform')).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

