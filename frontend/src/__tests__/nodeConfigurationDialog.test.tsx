import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';

vi.mock('../components/ImageViewer', () => ({
  default: ({ previewUrl, showRoiTools, roiEnabled, roiTargetLabel, onRoiChange, overlays = [], measurementGeometry, measurementGeometryEditable, onMeasurementGeometryChange }: {
    previewUrl?: string; showRoiTools?: boolean; roiEnabled: boolean; onRoiChange: (roi?: VisionRoi) => void; overlays?: VisionOverlay[];
    roiTargetLabel?: string;
    measurementGeometry?: { type: 'line'; start: { x: number; y: number }; end: { x: number; y: number } }
      | { type: 'circle'; center: { x: number; y: number }; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number };
    measurementGeometryEditable?: boolean;
    onMeasurementGeometryChange?: (geometry: NonNullable<typeof measurementGeometry>) => void;
  }) =>
    <div><span data-testid="image-url">{previewUrl}</span><span data-testid="roi-enabled">{roiEnabled ? 'ROI 可编辑' : 'ROI 只读'}</span>
      <span data-testid="roi-target">{roiTargetLabel}</span><span>{showRoiTools ? 'ROI 工具' : '纯图像预览'}</span>
      <span data-testid="overlay-ids">{overlays.map(overlay => overlay.id).join(',')}</span>
      {measurementGeometryEditable && measurementGeometry?.type === 'line' && <button onClick={() => onMeasurementGeometryChange?.({ type: 'line', start: { x: 23, y: 31 }, end: { x: 91, y: 47 } })}>模拟拖动搜索线</button>}
      {measurementGeometryEditable && measurementGeometry?.type === 'circle' && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry,
        center: { x: 42, y: 51 }, radius: 17, band: 14, angleStartDeg: 350, angleEndDeg: 450 })}>模拟调整标称圆</button>}
      {showRoiTools && roiEnabled && <button onClick={() => onRoiChange(undefined)}>清除测试 ROI</button>}</div>
}));

const catalog: NodeCatalogItem = {
  type: 'text.validate', displayName: '文本校验', category: '校验', pluginId: 'builtin',
  description: '按预期内容校验文字。',
  inputs: [{ name: 'text', label: '文本', dataType: 'String', required: true }],
  outputs: [{ name: 'pass', label: '通过', dataType: 'Boolean' }],
  parameters: [{ name: 'expected', label: '预期内容', type: 'text', defaultValue: 'ABC' }]
};
const node: Node = { id: 'test', position: { x: 0, y: 0 }, data: { label: '批号检查', typeKey: 'text.validate', parameters: { expected: 'ABC' } } };
const report: RunResult = {
  runId: 'test-run', success: true, totalDurationMs: 2, previewAvailable: true, previewWidth: 100, previewHeight: 100,
  nodeReports: [{ nodeId: 'test', nodeType: 'text.validate', success: true, durationMs: 1.2, summary: { pass: true },
    inputs: { text: { type: 'String', display: 'ABC', value: 'ABC' } },
    outputs: { pass: { type: 'Boolean', display: 'true', value: true } } }]
};

function mount(overrides: Partial<Parameters<typeof NodeConfigurationDialog>[0]> = {}) {
  const props = { open: true, node, catalogItem: catalog, nodes: [node], edges: [], disabled: false, onApply: vi.fn(), onClose: vi.fn(), onPreview: vi.fn(async () => report), ...overrides };
  const view = render(<NodeConfigurationDialog {...props} />);
  return { ...view, props, dialog: within(screen.getByRole('dialog')) };
}
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() })));
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [{ nodeId: 'test', portName: 'image', width: 100, height: 100 }, { nodeId: 'other', portName: 'image', width: 100, height: 100 }] }) })));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('ROI reference workspace', () => {
  const roiCatalog = fallbackCatalog.find(item => item.type === 'roi.transform')!;
  const roiNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
    inputs: roiCatalog.inputs, parameters: { baseX: 100, baseY: 50, baseThetaDeg: 0, roi: { type: 'Circle', x: 130, y: 60, radius: 8 } } } };
  const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
  const locateNode: Node = { id: 'locate', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: {} } };
  const edges = [
    { id: 'image', source: 'other', sourceHandle: 'image', target: 'test', targetHandle: 'debugImage' },
    { id: 'pose', source: 'locate', sourceHandle: 'pose', target: 'test', targetHandle: 'currentPose' }
  ];
  const capture: RunResult = { ...report, nodeReports: [{ nodeId: 'test', nodeType: 'roi.transform', success: true, durationMs: 1,
    summary: { referenceCaptured: true, referenceImage: 'saved-png', referenceWidth: 100, referenceHeight: 100,
      referenceX: 200, referenceY: 70, referenceThetaDeg: 0 } }] };

  it('edits only saved reference pixels, saves the pose and rebased ROI on Apply, and restores them after reopening', async () => {
    const onPreview = vi.fn(async () => capture);
    const onApply = vi.fn();
    const first = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode], edges, onPreview, onApply });
    expect(first.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    fireEvent.click(first.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(first.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png'));
    expect(onPreview).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ captureReference: true }));
    expect(first.dialog.getByRole('button', { name: '清除测试 ROI' })).toBeTruthy();
    expect(first.dialog.queryByRole('spinbutton', { name: '基准 X' })).toBeNull();
    expect(first.dialog.queryByRole('spinbutton', { name: '当前 X' })).toBeNull();
    await waitFor(() => expect((first.dialog.getByRole('radio', { name: '当前输入' }) as HTMLInputElement).disabled).toBe(false));
    fireEvent.click(first.dialog.getByRole('radio', { name: '当前输入' }));
    await waitFor(() => expect(first.dialog.getByTestId('image-url').textContent).toContain('/nodes/other/image'));
    expect(first.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    fireEvent.click(first.dialog.getByRole('radio', { name: '已保存基准' }));
    expect(first.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png');
    fireEvent.click(first.dialog.getByRole('button', { name: /应\s*用/ }));
    const applied = onApply.mock.calls[0][0];
    expect(applied).toMatchObject({ baseX: 200, baseY: 70, referenceImage: 'saved-png', roi: { x: 230, y: 80, radius: 8 } });
    expect(applied.captureReference).toBeUndefined();
    expect((roiNode.data.parameters as Record<string, unknown>).referenceImage).toBeUndefined();
    first.unmount();
    const savedNode = { ...roiNode, data: { ...roiNode.data, parameters: applied } };
    const reopened = mount({ node: savedNode, catalogItem: roiCatalog, nodes: [savedNode, imageNode, locateNode], edges });
    expect(reopened.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png');
    expect(reopened.dialog.getByRole('button', { name: '清除测试 ROI' })).toBeTruthy();
  });

  it('keeps the last reference intact after a failed capture and blocks editing when the pose origin changes', async () => {
    const onPreview = vi.fn().mockResolvedValueOnce(capture).mockResolvedValueOnce({ ...report, success: false, error: '定位未找到目标', nodeReports: [] });
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode], edges, onPreview });
    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());
    fireEvent.click(view.dialog.getByRole('button', { name: '重新设为基准' }));
    await waitFor(() => expect(view.dialog.getByText('定位未找到目标')).toBeTruthy());
    expect(view.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png');
    const changedOrigin = { ...locateNode, data: { ...locateNode.data, parameters: { trainedOriginX: 35 } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, imageNode, changedOrigin]} />);
    expect(view.dialog.getByText(/模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((view.dialog.getByRole('button', { name: '重新设为基准' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('invalidates a saved ROI reference when shape-match search settings can change the connected pose', async () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const matcher: Node = { id: 'locate', position: { x: 0, y: 0 }, data: {
      typeKey: 'match.shape', label: '形状匹配', inputs: shapeCatalog.inputs,
      parameters: { modelMode: 'trained', trainedModelHash: 'model-a', searchAngleStartDeg: -180 }
    } };
    const onPreview = vi.fn(async () => capture);
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, matcher], edges, onPreview });

    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const changedMatcher = { ...matcher, data: { ...matcher.data, parameters: { ...matcher.data.parameters as object, searchAngleStartDeg: -45 } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, imageNode, changedMatcher]} />);
    expect(view.dialog.getByText(/图像或定位来源、模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((view.dialog.getByRole('button', { name: '重新设为基准' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('invalidates a saved ROI reference when an upstream image transform changes without changing dimensions', async () => {
    const rotateCatalog = fallbackCatalog.find(item => item.type === 'image.rotate')!;
    const rotateNode: Node = { id: 'rotate', position: { x: 0, y: 0 }, data: { typeKey: rotateCatalog.type, label: '旋转图像',
      inputs: rotateCatalog.inputs, parameters: { angleDeg: 0, expand: false } } };
    const graphEdges = [
      { id: 'rotate-source', source: imageNode.id, sourceHandle: 'image', target: rotateNode.id, targetHandle: 'image' },
      { id: 'debug-rotated', source: rotateNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      edges[1]
    ];
    const onPreview = vi.fn(async () => capture);
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, rotateNode, imageNode, locateNode], edges: graphEdges, onPreview });

    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const changedRotate = { ...rotateNode, data: { ...rotateNode.data, parameters: { angleDeg: 12, expand: false } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, changedRotate, imageNode, locateNode]} />);
    expect(view.dialog.getByText(/图像或定位来源、模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((view.dialog.getByRole('button', { name: '重新设为基准' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('invalidates a saved ROI reference when an upstream crop origin moves but its size stays fixed', async () => {
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const cropNode: Node = { id: 'crop-source', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪',
      inputs: cropCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 10, y: 10, width: 60, height: 60 } } } };
    const graphEdges = [
      { id: 'crop-source-image', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'debug-cropped', source: cropNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      edges[1]
    ];
    const onPreview = vi.fn(async () => capture);
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, cropNode, imageNode, locateNode], edges: graphEdges, onPreview });

    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const changedCrop = { ...cropNode, data: { ...cropNode.data, parameters: { roi: { type: 'Rectangle', x: 20, y: 10, width: 60, height: 60 } } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, changedCrop, imageNode, locateNode]} />);
    expect(view.dialog.getByText(/图像或定位来源、模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((view.dialog.getByRole('button', { name: '重新设为基准' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('invalidates a saved ROI reference when an upstream affine transform changes', async () => {
    const warpCatalog = fallbackCatalog.find(item => item.type === 'image.warpAffine')!;
    const transformCatalog = fallbackCatalog.find(item => item.type === 'coordinate.transformConstant')!;
    const warpNode: Node = { id: 'warp', position: { x: 0, y: 0 }, data: { typeKey: warpCatalog.type, label: '仿射变换',
      inputs: warpCatalog.inputs, parameters: { width: 100, height: 100 } } };
    const transformNode: Node = { id: 'matrix', position: { x: 0, y: 0 }, data: { typeKey: transformCatalog.type, label: '坐标变换',
      inputs: transformCatalog.inputs, parameters: { sourceFrame: 'px', targetFrame: 'px', unit: 'px', x: 0, y: 0, angleDeg: 0 } } };
    const graphEdges = [
      { id: 'warp-source', source: imageNode.id, sourceHandle: 'image', target: warpNode.id, targetHandle: 'image' },
      { id: 'warp-matrix', source: transformNode.id, sourceHandle: 'transform', target: warpNode.id, targetHandle: 'transform' },
      { id: 'debug-warped', source: warpNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      edges[1]
    ];
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, warpNode, transformNode, imageNode, locateNode], edges: graphEdges,
      onPreview: vi.fn(async () => capture) });
    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const changedTransform = { ...transformNode, data: { ...transformNode.data, parameters: { ...transformNode.data.parameters as object, x: 8 } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, warpNode, changedTransform, imageNode, locateNode]} />);
    expect(view.dialog.getByText(/图像或定位来源、模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('invalidates a saved ROI reference when upstream undistortion calibration changes', async () => {
    const undistortCatalog = fallbackCatalog.find(item => item.type === 'image.undistort')!;
    const calibrationCatalog = fallbackCatalog.find(item => item.type === 'calibration.cameraIntrinsics')!;
    const undistortNode: Node = { id: 'undistort', position: { x: 0, y: 0 }, data: { typeKey: undistortCatalog.type, label: '镜头校正',
      inputs: undistortCatalog.inputs, parameters: {} } };
    const calibrationNode: Node = { id: 'calibration', position: { x: 0, y: 0 }, data: { typeKey: calibrationCatalog.type, label: '相机内参',
      inputs: calibrationCatalog.inputs, parameters: { calibrationDataJson: '{"views":"model-a"}' } } };
    const graphEdges = [
      { id: 'undistort-source', source: imageNode.id, sourceHandle: 'image', target: undistortNode.id, targetHandle: 'image' },
      { id: 'undistort-calibration', source: calibrationNode.id, sourceHandle: 'calibrationJson', target: undistortNode.id, targetHandle: 'calibrationJson' },
      { id: 'debug-undistorted', source: undistortNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      edges[1]
    ];
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, undistortNode, calibrationNode, imageNode, locateNode], edges: graphEdges,
      onPreview: vi.fn(async () => capture) });
    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const changedFallback = { ...undistortNode, data: { ...undistortNode.data, parameters: { calibrationJson: '{"camera":"unused-fallback"}' } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, changedFallback, calibrationNode, imageNode, locateNode]} />);
    expect(view.dialog.queryByText(/图像或定位来源、模型原点已变化/)).toBeNull();

    const changedCalibration = { ...calibrationNode, data: { ...calibrationNode.data, parameters: { calibrationDataJson: '{"views":"model-b"}' } } };
    view.rerender(<NodeConfigurationDialog {...view.props} nodes={[roiNode, changedFallback, changedCalibration, imageNode, locateNode]} />);
    expect(view.dialog.getByText(/图像或定位来源、模型原点已变化/)).toBeTruthy();
    expect(view.dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('hides the overridden local ROI editor when an upstream ROI is connected', () => {
    const upstream: Node = { id: 'upstream-roi', position: { x: 0, y: 0 }, data: { label: '上游选区' } };
    const upstreamEdges = [...edges, { id: 'roi-override', source: upstream.id, sourceHandle: 'roi', target: roiNode.id, targetHandle: 'roi' }];
    const { dialog } = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode, upstream], edges: upstreamEdges });

    expect(dialog.getByText('当前 ROI 来自上游节点')).toBeTruthy();
    expect(dialog.queryByText('基准 ROI')?.closest('.roi-property')).toBeNull();
    expect(dialog.queryByRole('button', { name: '在图像中编辑' })).toBeNull();
    expect(dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
  });
});

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

  it('does not project an upstream ROI overlay onto a different image coordinate frame', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'crop', portName: 'image', width: 100, height: 100 }, { nodeId: 'processed', portName: 'image', width: 80, height: 80 }
    ] }) })));
    const processedNode: Node = { id: 'processed', position: { x: 0, y: 0 }, data: { typeKey: 'image.resize', label: '缩放图像' } };
    const edges = [
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
  });
});

describe('caliper geometry input guidance', () => {
  it('edits transformed search geometry over the saved ROI reference image', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'reference-image', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, imageNode], edges, result: report });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/当前显示的已保存基准图像可直接拖动或重绘/)).toBeTruthy();
    fireEvent.click(await dialog.findByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'startX')!.label }) as HTMLInputElement).value).toBe('23'));
  });

  it('writes an interactively adjusted search line back into the staged parameters', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'startX')!.label }) as HTMLInputElement).value).toBe('23');
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'endY')!.label }) as HTMLInputElement).value).toBe('47');
    });
  });

  it('writes a dragged circle and wraparound arc back into staged parameters', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 8, angleStartDeg: 350, angleEndDeg: 10 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟调整标称圆' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerX')!.label }) as HTMLInputElement).value).toBe('42');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radius')!.label }) as HTMLInputElement).value).toBe('17');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'angleStartDeg')!.label }) as HTMLInputElement).value).toBe('-10');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'angleEndDeg')!.label }) as HTMLInputElement).value).toBe('90');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radialBand')!.label }) as HTMLInputElement).value).toBe('14');
    });
  });

  it('allows preview when a connected searchLine replaces malformed local endpoints', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 'inactive', startY: 20, endX: 80, endY: null } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'feature.line', label: '上游搜索线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-override', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' }
    ];
    const onPreview = vi.fn(async () => report);
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, lineSource, imageNode], edges, result: report, onPreview });

    expect(dialog.queryByText(/请修正参数后再运行或应用/)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
  });

  it('blocks preview and identifies endpoints when the local search line is too short', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 20, startY: 30, endX: 21, endY: 30 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    expect(dialog.getByText(/搜索线长度至少需要 2 px/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows preview when a connected circle replaces malformed local center and radius', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 'inactive', centerY: 30, radius: 0 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '上游标称圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' }
    ];
    const onPreview = vi.fn(async () => report);
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges, result: report, onPreview });

    expect(dialog.queryByText(/请修正参数后再运行或应用/)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
  });

  it('makes caliper geometry read-only when the dialog becomes disabled', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const view = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    expect(await view.dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    view.rerender(<NodeConfigurationDialog {...view.props} disabled />);
    expect(view.dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
  });

  it('locks caliper geometry editing while a preview is running', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    let finishPreview!: (value: RunResult) => void;
    const onPreview = vi.fn(() => new Promise<RunResult>(resolve => { finishPreview = resolve; }));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report, onPreview });

    expect(await dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true));
    expect(dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();

    finishPreview(report);
    await waitFor(() => expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false));
    expect(dialog.getByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
  });

  it('locks a local ROI when the caliper receives an upstream current-image mask', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺测量',
      inputs: caliperCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 2, y: 3, width: 20, height: 15 } } } };
    const roiNode: Node = { id: 'corrected-mask', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI' } };
    const imageNode: Node = { id: 'caliper-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' },
      { id: 'caliper-roi-input', source: roiNode.id, sourceHandle: 'roi', target: caliperNode.id, targetHandle: 'roi' },
      { id: 'roi-debug-image', source: imageNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' }
    ];
    const run = { ...report, runId: 'caliper-roi-run', overlays: [
      { id: 'roi-transform-corrected-shape', nodeId: roiNode.id, type: 'Circle' as const, x: 32, y: 30, radius: 10 }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, roiNode, imageNode], edges, result: run });

    expect(dialog.getByText('ROI 掩膜来自上游：定位修正 ROI')).toBeTruthy();
    expect(dialog.getByText(/按当前图像坐标解释/)).toBeTruthy();
    expect(dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    fireEvent.click(await dialog.findByRole('radio', { name: '输入' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper-image/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });

  it('offers image-based local ROI editing for the single caliper', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺测量',
      inputs: caliperCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 2, y: 3, width: 20, height: 15 } } } };
    const imageNode: Node = { id: 'caliper-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
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

    expect(await dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper/image');
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

  it('warns against applying a pose transform twice to an already corrected circle', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 20, centerY: 30, radius: 10 } } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI' } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-geometry-input', source: roiNode.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' },
      { id: 'circle-transform-input', source: roiNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const { dialog, props } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, roiNode, imageNode], edges });

    expect(dialog.getByText('定位修正圆不能再次应用 transform')).toBeTruthy();
    expect(dialog.getByText(/圆已经是当前图像坐标，请断开这个 transform 输入/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    expect(props.onPreview).not.toHaveBeenCalled();
    for (const name of ['centerX', 'centerY', 'radius'])
      expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === name)!.label })).toBeNull();
    expect(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'caliperCount')!.label })).toBeTruthy();
  });
});

describe('node configuration draft', () => {
  it('keeps trained models in the staged workflow until Apply, and excludes runtime thresholds from training status', async () => {
    const shapeCatalog: NodeCatalogItem = {
      type: 'match.shape', displayName: '形状匹配', category: '匹配', pluginId: 'builtin', description: '显式训练模型',
      inputs: [{ name: 'image', dataType: 'Image', required: true }, { name: 'template', dataType: 'Image' }],
      outputs: [{ name: 'image', dataType: 'Image' }, { name: 'found', dataType: 'Boolean' }],
      capabilities: { supportsRoi: true },
      parameters: [
        { name: 'modelMode', label: '模型生命周期', type: 'select', defaultValue: 'trained', options: [{ label: '训练模型', value: 'trained' }] },
        { name: 'minScore', label: '最小匹配分数', type: 'number', defaultValue: 0.6, min: 0, max: 1, step: 0.01 }
      ]
    };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', typeKey: 'match.shape', parameters: { modelMode: 'trained', minScore: 0.6, trainedSourceImage: 'LOCKED-PNG' } } };
    const trainingResult: RunResult = {
      ...report, nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 3,
        summary: { trainingSucceeded: true, trainingModelData: 'model-bytes', trainingModelHash: 'hash', trainingConfig: 'cfg',
          trainingWidth: 20, trainingHeight: 20, trainingOriginX: 10, trainingOriginY: 10, trainingOriginImageX: 10, trainingOriginImageY: 10,
          trainingContour: [{ x: 2, y: 2 }, { x: 10, y: 2 }, { x: 10, y: 10 }] } }]
    };
    const onPreview = vi.fn(async (parameters: Record<string, unknown>) => parameters.trainingMode ? trainingResult : report);
    const onApply = vi.fn();
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview, onApply });
    fireEvent.click(dialog.getByRole('button', { name: '训练模型' }));
    await waitFor(() => expect(dialog.getByText('模型已训练，待应用到工作流')).toBeTruthy());
    expect(onPreview).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ trainingMode: true, modelMode: 'trained', trainingImageData: 'LOCKED-PNG' }));
    expect(onApply).not.toHaveBeenCalled();
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    const applied = onApply.mock.calls[0][0];
    expect(applied.trainedModelData).toBe('model-bytes');
    expect(applied.trainedSourceImage).toBe('LOCKED-PNG');
    expect(applied.trainingMode).toBe(false);
    expect(applied.trainedModelHash).toBe('hash');
  });

  it('keeps the training workspace in view after acquiring an image and training successfully', async () => {
    const scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, 'scrollIntoView', { configurable: true, value: scrollIntoView });
    const shapeCatalog: NodeCatalogItem = {
      type: 'match.shape', displayName: '形状匹配', category: '匹配', pluginId: 'builtin', description: '显式训练模型',
      inputs: [{ name: 'image', dataType: 'Image', required: true }, { name: 'template', dataType: 'Image' }],
      outputs: [{ name: 'image', dataType: 'Image' }, { name: 'found', dataType: 'Boolean' }],
      capabilities: { supportsRoi: true },
      parameters: [{ name: 'modelMode', label: '模型生命周期', type: 'select', defaultValue: 'trained', options: [{ label: '训练模型', value: 'trained' }] }]
    };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', typeKey: 'match.shape', parameters: { modelMode: 'trained' } } };
    const response = (summary: Record<string, unknown>): RunResult => ({ ...report,
      nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 2, summary }] });
    const onPreview = vi.fn(async (parameters: Record<string, unknown>) => parameters.trainingPreviewOnly
      ? response({ trainingPreviewReady: true, trainingImageData: 'LOCKED-PNG', trainingSourceWidth: 120, trainingSourceHeight: 100 })
      : response({ trainingSucceeded: true, trainingModelData: 'model-bytes', trainingModelHash: 'hash', trainingConfig: 'cfg' }));
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview });
    const toggle = dialog.getByRole('button', { name: '本节点运行结果' });
    fireEvent.click(dialog.getByRole('button', { name: /获取训练图像/ }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
    await waitFor(() => expect(dialog.getByText('ROI 工具')).toBeTruthy());
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(scrollIntoView).not.toHaveBeenCalled();

    fireEvent.click(dialog.getByRole('button', { name: '训练模型' }));
    await waitFor(() => expect(dialog.getByText('模型已训练，待应用到工作流')).toBeTruthy());
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(scrollIntoView).not.toHaveBeenCalled();
    delete (Element.prototype as Partial<Element>).scrollIntoView;
  });

  it('opens the saved training image without reacquiring and explicitly refreshes the training source', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [{ nodeId: 'shape', portName: 'image', width: 200, height: 200 }] }) })));
    const shapeCatalog: NodeCatalogItem = {
      type: 'match.shape', displayName: '形状匹配', category: '匹配', pluginId: 'builtin', description: '显式训练模型',
      inputs: [{ name: 'image', dataType: 'Image', required: true }, { name: 'template', dataType: 'Image' }],
      outputs: [{ name: 'image', dataType: 'Image' }, { name: 'found', dataType: 'Boolean' }],
      capabilities: { supportsRoi: true },
      parameters: [{ name: 'modelMode', label: '模型生命周期', type: 'select', defaultValue: 'trained', options: [{ label: '训练模型', value: 'trained' }] }]
    };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', typeKey: 'match.shape', parameters: {
      modelMode: 'trained', templatePath: 'C:\\templates\\old.png', trainedModelData: 'saved-model', trainedPreviewImage: 'OLD-SNAPSHOT',
      trainedModelSettings: { templatePath: 'C:\\templates\\old.png' }, trainedSourceWidth: 100, trainedSourceHeight: 100
    } } };
    const response: RunResult = { ...report, runId: 'fresh-source-run', previewAvailable: true, previewWidth: 200, previewHeight: 200,
      nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 2, summary: { trainingPreviewReady: true } }] };
    const onPreview = vi.fn(async () => response);
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview });
    fireEvent.click(dialog.getByRole('tab', { name: '模型训练' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('OLD-SNAPSHOT'));
    expect(onPreview).not.toHaveBeenCalled();
    fireEvent.click(dialog.getByRole('button', { name: /获取训练图像/ }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/api/runs/fresh-source-run/nodes/shape/image'));
    expect(dialog.getByTestId('image-url').textContent).not.toContain('OLD-SNAPSHOT');
  });

  it('trains from the acquired pixels and keeps them while switching pages', async () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: { modelMode: 'trained' } } };
    const response = (summary: Record<string, unknown>): RunResult => ({ ...report,
      nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 2, summary }] });
    const onPreview = vi.fn(async (p: Record<string, unknown>) => p.trainingPreviewOnly
      ? response({ trainingPreviewReady: true, trainingImageData: 'CAPTURED-PNG', trainingSourceWidth: 120, trainingSourceHeight: 100 })
      : p.searchPreviewOnly ? response({ searchPreviewReady: true })
        : response({ trainingSucceeded: true, trainingModelData: 'trained-model', trainingModelHash: 'hash' }));
    const onApply = vi.fn();
    const view = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview, onApply });
    const { dialog } = view;
    fireEvent.click(dialog.getByRole('button', { name: '获取训练图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('CAPTURED-PNG'));
    fireEvent.click(dialog.getByRole('tab', { name: '搜索推理' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(dialog.getByRole('tab', { name: '模型训练' }).getAttribute('aria-disabled')).not.toBe('true'));
    fireEvent.click(dialog.getByRole('tab', { name: '模型训练' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('CAPTURED-PNG'));
    expect(onPreview).toHaveBeenCalledTimes(2);
    await waitFor(() => expect((dialog.getByRole('button', { name: '训练模型' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(dialog.getByRole('button', { name: '训练模型' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(3));
    expect(onPreview.mock.calls[2][0]).toMatchObject({ trainingMode: true, trainingImageData: 'CAPTURED-PNG' });
    await waitFor(() => expect(dialog.getByText('模型已训练，待应用到工作流')).toBeTruthy());
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    expect(onApply).toHaveBeenCalledOnce();
    expect(onApply.mock.calls[0][0]).not.toHaveProperty('trainingImageData');
    expect(onApply.mock.calls[0][0].trainedSourceImage).toBe('CAPTURED-PNG');

    view.unmount();
    const savedNode: Node = { ...shapeNode, data: { ...shapeNode.data, parameters: onApply.mock.calls[0][0] } };
    const reopenedPreview = vi.fn(async () => report);
    const reopened = mount({ node: savedNode, nodes: [savedNode], catalogItem: shapeCatalog, onPreview: reopenedPreview });
    fireEvent.click(reopened.dialog.getByRole('tab', { name: '模型训练' }));
    await waitFor(() => expect(reopened.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,CAPTURED-PNG'));
    expect(reopenedPreview).not.toHaveBeenCalled();
  });

  it('collects aligned training samples and applies the composite model returned by the engine', async () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: { modelMode: 'trained',
      trainedSourceImage: 'SAMPLE-PNG-1', trainedSourceImages: [{ data: 'SAMPLE-PNG-1', width: 120, height: 100 }] } } };
    const response = (summary: Record<string, unknown>): RunResult => ({ ...report,
      nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 2, summary }] });
    const onPreview = vi.fn(async (parameters: Record<string, unknown>) => {
      if (parameters.trainingPreviewOnly) {
        return response({ trainingPreviewReady: true, trainingImageData: 'SAMPLE-PNG-2', trainingSourceWidth: 120, trainingSourceHeight: 100 });
      }
      if (parameters.trainingCombineModelData) {
        return response({ trainingSucceeded: true, trainingModelData: 'COMPOSITE-MODEL', trainingModelHash: 'COMPOSITE-HASH',
          trainingConfig: 'cfg', trainingWidth: 120, trainingHeight: 100, trainingOriginX: 59.5, trainingOriginY: 49.5,
          trainingOriginImageX: 60, trainingOriginImageY: 50, trainingFeaturePointCount: 42, trainingContourCount: 7, trainingWarnings: [] });
      }
      const isSecondSample = parameters.trainingImageData === 'SAMPLE-PNG-2';
      return response({ trainingSucceeded: true, trainingModelData: `MODEL-${String(parameters.trainingImageData)}`,
        trainingModelHash: `HASH-${String(parameters.trainingImageData)}`, trainingConfig: 'cfg', trainingWidth: 120, trainingHeight: 100,
        trainingOriginX: isSecondSample ? 58 : 60, trainingOriginY: isSecondSample ? 52 : 50,
        trainingOriginImageX: isSecondSample ? 68 : 60, trainingOriginImageY: isSecondSample ? 54 : 50 });
    });
    const onApply = vi.fn();
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview, onApply });

    fireEvent.click(dialog.getByRole('button', { name: '获取训练图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('SAMPLE-PNG-2'));
    await waitFor(() => expect((dialog.getByRole('button', { name: '训练模型' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(dialog.getByRole('button', { name: '加入当前图像为样本' }));
    fireEvent.click(dialog.getByRole('button', { name: '训练模型' }));

    await waitFor(() => expect(dialog.getByText('模型已训练，待应用到工作流')).toBeTruthy());
    const trainingCalls = onPreview.mock.calls.filter(([parameters]) => parameters.trainingMode && !parameters.trainingPreviewOnly);
    expect(trainingCalls).toHaveLength(3);
    expect(trainingCalls.slice(0, 2).map(([parameters]) => parameters.trainingImageData)).toEqual(['SAMPLE-PNG-1', 'SAMPLE-PNG-2']);
    expect(trainingCalls[1][0]).not.toHaveProperty('trainingOriginOverrideX');
    expect(trainingCalls[0][0]).not.toHaveProperty('trainingReferenceModelData');
    expect(trainingCalls[1][0]).toMatchObject({ trainingReferenceModelData: 'MODEL-SAMPLE-PNG-1',
      trainingReferenceOriginImageX: 60, trainingReferenceOriginImageY: 50 });
    expect(trainingCalls[2][0].trainingCombineModelData).toContain('"sourceOriginX":68');
    expect(trainingCalls[2][0].trainingCombineSourceImages).toBe(JSON.stringify(['SAMPLE-PNG-1', 'SAMPLE-PNG-2']));
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    expect(onApply).toHaveBeenCalledOnce();
    const applied = onApply.mock.calls[0][0];
    expect(applied.trainedModelData).toBe('COMPOSITE-MODEL');
    expect(applied.trainedSourceImages).toEqual([
      { data: 'SAMPLE-PNG-1', width: 120, height: 100 },
      { data: 'SAMPLE-PNG-2', width: 120, height: 100 }
    ]);
    expect(applied.trainedFeaturePointCount).toBe(42);
  });

  it('separates model training coverage from the runtime search angle window', () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: { modelMode: 'trained' } } };
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog });
    expect(dialog.getByRole('spinbutton', { name: '训练角度覆盖' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '金字塔层数' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '缩放容差' })).toBeTruthy();
    expect(dialog.queryByRole('spinbutton', { name: '缩放步长' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '原点 X' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '阈值' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '缩放容差' }), { target: { value: '10' } });
    expect(dialog.getByRole('spinbutton', { name: '缩放步长' })).toBeTruthy();
  });

  it('shows runtime angle controls on an existing trained model without invalidating it', () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const settings = { binarize: 'auto', threshold: 128, invert: false, trainingAngleRangeDeg: 180, angleStepDeg: 1,
      scaleTolerancePercent: 0, scaleStepPercent: 1, ignorePolarity: false, minContrast: 5,
      trainingOriginMode: 'centroid', trainingOriginX: 0, trainingOriginY: 0, roi: null, templatePath: '' };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: {
      modelMode: 'trained', trainedModelData: 'model', trainedModelSettings: settings
    } } };
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog });
    expect(dialog.getByRole('spinbutton', { name: '搜索起始角' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '搜索结束角' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '搜索最小尺度' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '搜索最大尺度' })).toBeTruthy();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '最大局部形变' }), { target: { value: '2' } });
    fireEvent.change(dialog.getByRole('spinbutton', { name: '搜索起始角' }), { target: { value: '-15' } });
    fireEvent.change(dialog.getByRole('spinbutton', { name: '搜索最小尺度' }), { target: { value: '90' } });
    expect(dialog.queryByText('训练参数已变化，需要重新训练')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行搜索' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('opens the last search image immediately and offers training when model settings change', async () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const settings = { binarize: 'auto', threshold: 128, invert: false, angleRangeDeg: 180, angleStepDeg: 1,
      scaleTolerancePercent: 0, scaleStepPercent: 1, ignorePolarity: false, minContrast: 5,
      trainingOriginMode: 'centroid', trainingOriginX: 0, trainingOriginY: 0, roi: null, templatePath: '' };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: {
      modelMode: 'trained', trainedModelData: 'model', trainedPreviewImage: 'saved-image', trainedModelSettings: settings } } };
    const matchingResult: RunResult = { ...report, nodeReports: [{ nodeId: 'shape', nodeType: 'match.shape', success: true, durationMs: 2, summary: { found: true, score: 0.95 } }] };
    const onPreview = vi.fn(async () => matchingResult);
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, result: matchingResult, onPreview });
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).not.toBe(''));
    expect(onPreview).not.toHaveBeenCalled();
    expect(dialog.queryByRole('spinbutton', { name: '训练角度覆盖' })).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '调整模型范围' }));
    fireEvent.change(dialog.getByRole('spinbutton', { name: '训练角度覆盖' }), { target: { value: '90' } });
    expect(dialog.getByText('训练参数已变化，需要重新训练')).toBeTruthy();
    expect((dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('tab', { name: '搜索推理' }));
    expect(dialog.getByRole('button', { name: '前往模型训练' })).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行搜索' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('button', { name: '前往模型训练' }));
    expect(dialog.getByRole('button', { name: '重新训练模型' })).toBeTruthy();
    expect(onPreview).not.toHaveBeenCalled();
  });

  it('migrates an older trained angle range and keeps old models usable', () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const settings = { binarize: 'auto', threshold: 128, invert: false, angleRangeDeg: 60, angleStepDeg: 1,
      scaleTolerancePercent: 0, scaleStepPercent: 1, ignorePolarity: false, minContrast: 5,
      trainingOriginMode: 'centroid', trainingOriginX: 0, trainingOriginY: 0, roi: null, templatePath: '' };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: {
      modelMode: 'trained', trainedModelData: 'model', trainedModelConfig: 'shape-v2|old', angleRangeDeg: 60,
      trainedModelSettings: settings
    } } };
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog });
    fireEvent.change(dialog.getByRole('spinbutton', { name: '搜索起始角' }), { target: { value: '-20' } });
    expect(dialog.queryByText('训练参数已变化，需要重新训练')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行搜索' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows compatibility mode after switching a trained node to legacy while retaining its model asset', async () => {
    const shapeCatalog: NodeCatalogItem = {
      type: 'match.shape', displayName: '形状匹配', category: '匹配', pluginId: 'builtin', description: '显式训练模型',
      inputs: [{ name: 'image', dataType: 'Image', required: true }, { name: 'template', dataType: 'Image' }],
      outputs: [{ name: 'image', dataType: 'Image' }, { name: 'found', dataType: 'Boolean' }],
      parameters: [{ name: 'modelMode', label: '模型生命周期', type: 'select', defaultValue: 'trained', options: [
        { label: '训练模型', value: 'trained' }, { label: '兼容模板匹配', value: 'legacy' }
      ] }]
    };
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', typeKey: 'match.shape', parameters: {
      modelMode: 'trained', trainedModelData: 'saved-model', trainedPreviewImage: 'saved-preview'
    } } };
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog });
    fireEvent.click(dialog.getByRole('tab', { name: '模型训练' }));
    expect(dialog.getByText('模型已保存在此工作流')).toBeTruthy();
    await waitFor(() => expect((dialog.getByRole('combobox', { name: '模型生命周期' }) as HTMLInputElement).disabled).toBe(false));
    fireEvent.mouseDown(dialog.getByRole('combobox', { name: '模型生命周期' }));
    fireEvent.click(screen.getByText('兼容模板匹配', { exact: true }));
    expect(dialog.getByText(/本次运行使用 template 输入或模板路径/)).toBeTruthy();
    expect(dialog.queryByText('模型已保存在此工作流')).toBeNull();
    expect(dialog.getByRole('button', { name: '训练并切换到训练模式' })).toBeTruthy();
  });

  it('keeps edits local and discards them when cancelled', () => {
    const { dialog, props, unmount } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'LOT-42' } });
    expect(props.onApply).not.toHaveBeenCalled();
    expect(node.data.parameters).toEqual({ expected: 'ABC' });
    fireEvent.click(dialog.getByRole('button', { name: /取\s*消/ }));
    expect(props.onClose).toHaveBeenCalledOnce();
    expect(props.onApply).not.toHaveBeenCalled();
    unmount();
    const fresh = mount();
    expect((fresh.dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('ABC');
  });

  it('commits the whole parameter snapshot once and can restore values from when it opened', () => {
    const { dialog, props } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'LOT-42' } });
    fireEvent.click(dialog.getByRole('button', { name: '还原为打开时' }));
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('ABC');
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'FINAL' } });
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    expect(props.onApply).toHaveBeenCalledExactlyOnceWith({ expected: 'FINAL' });
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('tests temporary parameters without applying them and marks the report stale after further edits', async () => {
    const { dialog, props } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'LOT-42' } });
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByText('执行成功')).toBeTruthy());
    expect(props.onPreview).toHaveBeenCalledExactlyOnceWith({ expected: 'LOT-42' });
    expect(props.onApply).not.toHaveBeenCalled();
    expect(node.data.parameters).toEqual({ expected: 'ABC' });
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'CHANGED' } });
    expect(dialog.getByText('已过期 · 当前参数待运行')).toBeTruthy();
    expect(within(dialog.getByRole('region', { name: '本节点运行结果' })).getByText('true')).toBeTruthy();
  });

  it('keeps the configuration target and draft fixed when viewing another node image', async () => {
    const imageCatalog: NodeCatalogItem = { ...catalog, inputs: [{ name: 'image', dataType: 'Image' }], capabilities: { supportsRoi: true } };
    const other: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '其他图像', outputs: [{ name: 'image', dataType: 'Image' }] } };
    const { dialog, props } = mount({ catalogItem: imageCatalog, nodes: [node, other], result: report });
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'KEEP-DRAFT' } });
    fireEvent.mouseDown(dialog.getByRole('combobox', { name: '预览节点' }));
    fireEvent.click(screen.getByText('其他图像', { exact: true }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/other/image'));
    expect(dialog.getByText('节点配置 · 批号检查')).toBeTruthy();
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('KEEP-DRAFT');
    expect(dialog.getByText('纯图像预览')).toBeTruthy();
    expect(props.onApply).not.toHaveBeenCalled();
  });

  it('includes ROI removal in the staged snapshot instead of changing the original node', async () => {
    const imageCatalog: NodeCatalogItem = { ...catalog, inputs: [{ name: 'image', dataType: 'Image' }], capabilities: { supportsRoi: true } };
    const withRoi: Node = { ...node, data: { ...node.data, inputs: imageCatalog.inputs,
      parameters: { expected: 'ABC', roi: { type: 'Rectangle', x: 1, y: 2, width: 3, height: 4 } } } };
    const imageNode: Node = { id: 'roi-image', position: { x: 0, y: 0 }, data: { outputs: [{ name: 'image', dataType: 'Image' }] } };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: withRoi.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog, props } = mount({ node: withRoi, nodes: [withRoi, imageNode],
      edges: [{ id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: withRoi.id, targetHandle: 'image' }],
      catalogItem: imageCatalog, result: report });
    fireEvent.click(await dialog.findByRole('button', { name: '在图像中编辑' }));
    await waitFor(() => expect(dialog.getByRole('button', { name: '清除测试 ROI' })).toBeTruthy());
    fireEvent.click(dialog.getByRole('button', { name: '清除测试 ROI' }));
    expect((withRoi.data.parameters as Record<string, unknown>).roi).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    expect(props.onApply).toHaveBeenCalledExactlyOnceWith({ expected: 'ABC' });
  });

  it('disables invalid, unsupported or busy actions and shows request failures', async () => {
    const onPreview = vi.fn(async () => { throw new Error('测试请求被拒绝'); });
    const { dialog } = mount({ onPreview });
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByText('测试请求被拒绝')).toBeTruthy());
    expect(dialog.getByRole('button', { name: '本节点运行结果' }).getAttribute('aria-expanded')).toBe('true');
  });

  it('blocks invalid parameter application and preview', () => {
    const numberCatalog: NodeCatalogItem = { ...catalog, parameters: [{ name: 'expected', label: '阈值', type: 'number', defaultValue: 10, min: 1, max: 20 }] };
    const numericNode: Node = { ...node, data: { ...node.data, parameters: { expected: 10 } } };
    const { dialog, props } = mount({ catalogItem: numberCatalog, node: numericNode, nodes: [numericNode] });
    fireEvent.change(dialog.getByRole('spinbutton', { name: '阈值' }), { target: { value: '' } });
    expect((dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
    expect(props.onApply).not.toHaveBeenCalled();
  });

  it('freezes changes during running and shows a single result area on the configuration tab', async () => {
    const { dialog } = mount({ disabled: true, initialTab: 'result', result: report });
    expect(dialog.getAllByRole('tab')).toHaveLength(2);
    expect(dialog.queryByRole('tab', { name: '运行结果' })).toBeNull();
    expect(dialog.queryByRole('button', { name: '完整结果' })).toBeNull();
    expect(dialog.getByRole('tab', { name: '配置' }).getAttribute('aria-selected')).toBe('true');
    expect(dialog.getAllByRole('region', { name: '本节点运行结果' })).toHaveLength(1);
    const results = dialog.getByRole('region', { name: '本节点运行结果' });
    expect(dialog.getByRole('button', { name: '本节点运行结果' }).getAttribute('aria-expanded')).toBe('true');
    expect(within(results).getByText('true')).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(results.parentElement));
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).disabled).toBe(true);
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('expands input snapshots and the full summary within the same result area', () => {
    const { dialog } = mount({ result: report });
    fireEvent.click(dialog.getByRole('button', { name: '本节点运行结果' }));
    const results = within(dialog.getByRole('region', { name: '本节点运行结果' }));
    const input = results.getByText('本次运行的输入');
    const summary = results.getByText('完整摘要');
    expect(input.parentElement?.hasAttribute('open')).toBe(false);
    expect(summary.parentElement?.hasAttribute('open')).toBe(false);
    fireEvent.click(input);
    fireEvent.click(summary);
    expect(input.parentElement?.hasAttribute('open')).toBe(true);
    expect(summary.parentElement?.hasAttribute('open')).toBe(true);
    expect(results.getByText('ABC')).toBeTruthy();
    expect(results.getByText(/"pass": true/)).toBeTruthy();
  });

  it('defaults to a collapsed status row and auto-expands the result on a new preview', async () => {
    const { dialog, props } = mount({ result: report, resultStale: true });
    const toggle = dialog.getByRole('button', { name: '本节点运行结果' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(within(toggle).getByText('执行成功')).toBeTruthy();
    expect(within(toggle).getByText('1.20 毫秒')).toBeTruthy();
    expect(within(toggle).getByText('已过期 · 当前参数待运行')).toBeTruthy();
    expect(dialog.queryByRole('table')).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(props.onPreview).toHaveBeenCalledOnce());
    // P1（评审）：新结果落地即自动展开（此前值表格被页脚裁在首屏外，需滚动整个弹窗体）
    await waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('true'));
    expect(dialog.getAllByRole('table').length).toBeGreaterThan(0);
    // 用户手动收起后保持收起（下一次运行前不再弹开）
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });

  it('automatically expands a failed run, allows collapsing it, and reopens on the next failure', async () => {
    const failure: RunResult = { ...report, success: false, nodeReports: [{ ...report.nodeReports[0], success: false, error: '输入图像无效' }] };
    const onPreview = vi.fn(async () => ({ ...failure, runId: 'next-failure' }));
    const { dialog } = mount({ result: failure, onPreview });
    const toggle = dialog.getByRole('button', { name: '本节点运行结果' });
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(dialog.getByRole('alert')).toBeTruthy();
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(toggle.getAttribute('aria-expanded')).toBe('true'));
    expect(dialog.getByText('输入图像无效')).toBeTruthy();
  });

  it('keeps staged parameters while maximizing and restoring the dialog', () => {
    const { dialog } = mount();
    const input = dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'KEEP-DRAFT' } });
    fireEvent.click(dialog.getByRole('button', { name: '最大化窗口' }));
    expect(screen.getByRole('dialog').classList.contains('is-maximized')).toBe(true);
    expect(input.value).toBe('KEEP-DRAFT');
    fireEvent.click(dialog.getByRole('button', { name: '还原窗口' }));
    expect(screen.getByRole('dialog').classList.contains('is-maximized')).toBe(false);
    expect(input.value).toBe('KEEP-DRAFT');
  });

  it('supports keyboard resizing and resetting the image/parameter split without changing the draft', () => {
    const { dialog, props } = mount({ initialTab: 'image' });
    const separator = dialog.getByRole('separator', { name: '调整图像与参数宽度' });
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'KEEP-DRAFT' } });
    fireEvent.keyDown(separator, { key: 'Home' });
    expect(separator.getAttribute('aria-valuenow')).toBe('35');
    fireEvent.keyDown(separator, { key: 'End' });
    expect(separator.getAttribute('aria-valuenow')).toBe('80');
    fireEvent.doubleClick(separator);
    expect(separator.getAttribute('aria-valuenow')).toBe('67');
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('KEEP-DRAFT');
    expect(props.onApply).not.toHaveBeenCalled();
  });

  it('keeps factory defaults one click away and stages the reset until application', () => {
    const customNode: Node = { ...node, data: { ...node.data, parameters: { expected: 'OPEN-VALUE' } } };
    const { dialog, props } = mount({ node: customNode, nodes: [customNode] });
    // 出厂默认按钮一次可达（不再是“更多参数操作 ···”二级菜单）；初始与打开时值不同 → 显示（1）
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'CHANGED' } });
    fireEvent.click(dialog.getByRole('button', { name: '还原为打开时' }));
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('OPEN-VALUE');
    fireEvent.click(dialog.getByRole('button', { name: '恢复出厂默认（1）' }));
    expect((dialog.getByRole('textbox', { name: '预期内容' }) as HTMLInputElement).value).toBe('ABC');
    expect(props.onApply).not.toHaveBeenCalled();
  });

  it('returns focus to the dialog and keeps the result inspectable after a preview run', async () => {
    const { dialog, props } = mount();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(props.onPreview).toHaveBeenCalledOnce());
    // P1（评审）：新结果落地自动展开（值表格不再被页脚裁在首屏外）
    await waitFor(() => expect(dialog.getByRole('button', { name: '本节点运行结果' }).getAttribute('aria-expanded')).toBe('true'));
    // P0（评审）：焦点回到弹窗内（真实浏览器里运行按钮会短暂 disabled 导致焦点落回 body，Esc 随之失效）
    await waitFor(() => expect(document.activeElement?.closest('.node-configuration-workspace')).toBeTruthy());
  });

  it('applies staged parameters with Ctrl+S', () => {
    const { dialog, props } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'FINAL' } });
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    expect(props.onApply).toHaveBeenCalledExactlyOnceWith({ expected: 'FINAL' });
  });

  it('runs the node preview with Ctrl+Enter', async () => {
    const { dialog, props } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'LATEST' } });
    fireEvent.keyDown(window, { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(props.onPreview).toHaveBeenCalledExactlyOnceWith({ expected: 'LATEST' }));
  });

  it('asks for confirmation before discarding staged changes closed via the ✕ button', async () => {
    const { dialog, props } = mount();
    fireEvent.change(dialog.getByRole('textbox', { name: '预期内容' }), { target: { value: 'DIRTY' } });
    const closeButton = (screen.getByRole('dialog').querySelector('.ant-modal-close')) as HTMLElement;
    fireEvent.click(closeButton);
    expect(props.onClose).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole('button', { name: '继续编辑' }));
    expect(props.onClose).not.toHaveBeenCalled();
    fireEvent.click(closeButton);
    fireEvent.click(await screen.findByRole('button', { name: '放弃修改' }));
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('closes without confirmation when nothing was changed', () => {
    const { dialog, props } = mount();
    const closeButton = (screen.getByRole('dialog').querySelector('.ant-modal-close')) as HTMLElement;
    fireEvent.click(closeButton);
    expect(props.onClose).toHaveBeenCalledOnce();
  });

  it('shows parameter guidance on demand without occupying the parameter row', async () => {
    const helpCatalog: NodeCatalogItem = { ...catalog, parameters: [{ ...catalog.parameters[0], description: '输入预期批号，用于逐字匹配。' }] };
    const { dialog } = mount({ catalogItem: helpCatalog });
    expect(dialog.queryByText('输入预期批号，用于逐字匹配。')).toBeNull();
    fireEvent.focus(dialog.getByRole('button', { name: '预期内容说明' }));
    expect((await screen.findByRole('tooltip')).textContent).toBe('输入预期批号，用于逐字匹配。');
  });
});
