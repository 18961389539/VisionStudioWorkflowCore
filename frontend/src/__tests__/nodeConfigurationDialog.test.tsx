import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';

vi.mock('../components/ImageViewer', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../components/ImageViewer')>();
  return {
    ...actual,
    default: ({ previewUrl, showRoiTools, roiEnabled, roiTargetLabel, onRoiChange, overlays = [], measurementGeometry, measurementGeometryEditable, onMeasurementGeometryChange }: {
      previewUrl?: string; showRoiTools?: boolean; roiEnabled: boolean; onRoiChange: (roi?: VisionRoi) => void; overlays?: VisionOverlay[];
      roiTargetLabel?: string;
      measurementGeometry?: { type: 'line'; start: { x: number; y: number }; end: { x: number; y: number }; profileSpan?: number; profileCount?: number; profileWidth?: number }
        | { type: 'circle'; center: { x: number; y: number }; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number; profileWidth?: number; centerEditable?: boolean; radiusEditable?: boolean };
      measurementGeometryEditable?: boolean;
      onMeasurementGeometryChange?: (geometry: NonNullable<typeof measurementGeometry>) => void;
    }) =>
      <div><span data-testid="image-url">{previewUrl}</span><span data-testid="roi-enabled">{roiEnabled ? 'ROI 可编辑' : 'ROI 只读'}</span>
        <span data-testid="roi-target">{roiTargetLabel}</span><span>{showRoiTools ? 'ROI 工具' : '纯图像预览'}</span>
        <span data-testid="overlay-ids">{overlays.map(overlay => overlay.id).join(',')}</span>
        {measurementGeometry?.type === 'circle' && <span data-testid="circle-geometry" data-center-x={measurementGeometry.center.x}
          data-center-y={measurementGeometry.center.y} data-radius={measurementGeometry.radius} data-band={measurementGeometry.band}
          data-profile-width={measurementGeometry.profileWidth}
          data-center-editable={measurementGeometry.centerEditable !== false} data-radius-editable={measurementGeometry.radiusEditable !== false} />}
        {measurementGeometry?.type === 'line' && <span data-testid="line-geometry" data-start-x={measurementGeometry.start.x}
          data-start-y={measurementGeometry.start.y} data-end-x={measurementGeometry.end.x} data-end-y={measurementGeometry.end.y}
          data-profile-span={measurementGeometry.profileSpan} data-profile-width={measurementGeometry.profileWidth}
          data-editable={Boolean(measurementGeometryEditable)} />}
        {measurementGeometryEditable && measurementGeometry?.type === 'line' && <button onClick={() => onMeasurementGeometryChange?.({ type: 'line', start: { x: 23, y: 31 }, end: { x: 91, y: 47 } })}>模拟拖动搜索线</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'line' && measurementGeometry.profileSpan !== undefined
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, profileSpan: 45 })}>模拟调整采样跨度</button>}
        {measurementGeometryEditable && measurementGeometry && measurementGeometry.profileWidth !== undefined
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, profileWidth: 11 })}>模拟调整投影宽度</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'circle' && measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry,
            center: { x: 42, y: 51 }, radius: 17, band: 14, angleStartDeg: 350, angleEndDeg: 450 })}>模拟调整标称圆</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'circle' && (measurementGeometry.centerEditable === false || measurementGeometry.radiusEditable === false)
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, band: 14, angleStartDeg: 350, angleEndDeg: 450 })}>模拟调整圆弧</button>}
        {showRoiTools && roiEnabled && <button onClick={() => onRoiChange(undefined)}>清除测试 ROI</button>}</div>
  };
});

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

  it('keeps a saved ROI reference valid when equivalent imported input handles change casing', async () => {
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode], edges,
      onPreview: vi.fn(async () => capture) });
    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByRole('button', { name: '重新设为基准' })).toBeTruthy());

    const caseVariantEdges = edges.map(edge => ({ ...edge, targetHandle: edge.targetHandle?.toUpperCase(),
      sourceHandle: edge.sourceHandle?.toUpperCase() }));
    view.rerender(<NodeConfigurationDialog {...view.props} edges={caseVariantEdges} />);

    expect(view.dialog.queryByText(/图像或定位来源、模型原点已变化/)).toBeNull();
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('hides the previous corrected ROI overlay after the base ROI changes', async () => {
    const captureWithOverlay: RunResult = { ...capture, overlays: [
      { id: 'roi-transform-corrected-shape', nodeId: 'test', type: 'Circle', x: 132, y: 63, radius: 9 }
    ] };
    const view = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode], edges,
      onPreview: vi.fn(async () => captureWithOverlay) });

    fireEvent.click(view.dialog.getByRole('button', { name: '获取图像并设为基准' }));
    await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png'));
    fireEvent.click(view.dialog.getByRole('button', { name: '清除测试 ROI' }));
    fireEvent.click(view.dialog.getByRole('radio', { name: '修正结果' }));
    await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toContain('/nodes/test/image'));

    expect(view.dialog.getByText(/上次运行图像/)).toBeTruthy();
    expect(view.dialog.getByTestId('overlay-ids').textContent).not.toContain('roi-transform-corrected-shape');
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

  it('lets users capture a new reference and replace a malformed legacy ROI', async () => {
    const invalidNode = { ...roiNode, data: { ...roiNode.data,
      parameters: { ...roiNode.data.parameters as object, roi: { type: 'Polygon' } } } };
    const view = mount({ node: invalidNode, catalogItem: roiCatalog, nodes: [invalidNode, imageNode, locateNode], edges,
      onPreview: vi.fn(async () => capture) });
    const captureButton = view.dialog.getByRole('button', { name: '获取图像并设为基准' }) as HTMLButtonElement;

    expect(view.dialog.getByRole('button', { name: '清除无效区域' })).toBeTruthy();
    expect(captureButton.disabled).toBe(false);
    fireEvent.click(captureButton);
    await waitFor(() => expect(view.dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png'));
    expect(view.dialog.getByRole('button', { name: '清除无效区域' })).toBeTruthy();
    fireEvent.click(view.dialog.getByRole('button', { name: '清除无效区域' }));
    expect((view.dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(false);
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

  it('explains when an upstream corrected ROI will receive a second pose transform', () => {
    const upstream: Node = { id: 'upstream-roi', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '上游定位修正' } };
    const upstreamEdges = [...edges, { id: 'roi-override', source: upstream.id, sourceHandle: 'roi', target: roiNode.id, targetHandle: 'roi' }];
    const { dialog } = mount({ node: roiNode, catalogItem: roiCatalog, nodes: [roiNode, imageNode, locateNode, upstream], edges: upstreamEdges });

    expect(dialog.getByText(/上游定位修正 ROI 已经过一次位姿变换/)).toBeTruthy();
    expect(dialog.getByText(/只有需要叠加第二次修正时才保留此连接/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
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
    const grayscaleNode: Node = { id: 'gray', position: { x: 0, y: 0 }, data: { typeKey: 'image.grayscale', label: '灰度转换' } };
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
    const grayscaleNode: Node = { id: 'gray', position: { x: 0, y: 0 }, data: { typeKey: 'image.grayscale', label: '灰度转换' } };
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

describe('ROI input coordinate frame guidance', () => {
  const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
  const roiCatalog = fallbackCatalog.find(item => item.type === 'roi.transform')!;

  it('blocks a crop ROI from a different image frame before preview', () => {
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const roiNode: Node = { id: 'roi', position: { x: 0, y: 0 }, data: {
      typeKey: roiCatalog.type, label: '定位修正 ROI', inputs: roiCatalog.inputs, parameters: {}
    } };
    const inputImage: Node = { id: 'input-image', position: { x: 0, y: 0 }, data: { label: '裁剪输入图像' } };
    const otherImage: Node = { id: 'other-image', position: { x: 0, y: 0 }, data: { label: 'ROI 来源图像' } };
    const edges = [
      { id: 'crop-image', source: inputImage.id, sourceHandle: 'IMAGE', target: cropNode.id, targetHandle: 'IMAGE' },
      { id: 'roi-image', source: otherImage.id, sourceHandle: 'IMAGE', target: roiNode.id, targetHandle: 'DEBUGIMAGE' },
      { id: 'crop-roi', source: roiNode.id, sourceHandle: 'ROI', target: cropNode.id, targetHandle: 'ROI' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, inputImage, otherImage], edges });

    expect(dialog.getByText('ROI 与目标图像坐标系不匹配')).toBeTruthy();
    expect(dialog.getByText(/上游 ROI 与目标图像“裁剪输入图像”不在同一像素坐标系/)).toBeTruthy();
    expect(dialog.getByText(/裁剪区域来自上游：定位修正 ROI/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows a corrected ROI and crop input from the same image frame', () => {
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const roiNode: Node = { id: 'roi', position: { x: 0, y: 0 }, data: {
      typeKey: roiCatalog.type, label: '定位修正 ROI', inputs: roiCatalog.inputs, parameters: {}
    } };
    const inputImage: Node = { id: 'input-image', position: { x: 0, y: 0 }, data: { label: '源图像' } };
    const edges = [
      { id: 'crop-image', source: inputImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'roi-image', source: inputImage.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      { id: 'crop-roi', source: roiNode.id, sourceHandle: 'roi', target: cropNode.id, targetHandle: 'roi' }
    ];
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, inputImage], edges });

    expect(dialog.queryByText('ROI 与目标图像坐标系不匹配')).toBeNull();
    expect((dialog.getByRole('button', { name: '预览裁剪结果' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each(['measure.caliperLine', 'measure.caliperCircle'])('blocks a measurement ROI from another crop frame for %s', measurementType => {
    const measurementCatalog = fallbackCatalog.find(item => item.type === measurementType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: measurementType, label: '卡尺测量', inputs: measurementCatalog.inputs, parameters: {}
    } };
    const roiCrop: Node = { id: 'roi-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: 'ROI 裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const measurementCrop: Node = { id: 'measurement-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '测量图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const inputImage: Node = { id: 'input-image', position: { x: 0, y: 0 }, data: { label: '源图像' } };
    const edges = [
      { id: 'roi-crop-image', source: inputImage.id, sourceHandle: 'image', target: roiCrop.id, targetHandle: 'image' },
      { id: 'measurement-crop-image', source: inputImage.id, sourceHandle: 'IMAGE', target: measurementCrop.id, targetHandle: 'IMAGE' },
      { id: 'measurement-image', source: measurementCrop.id, sourceHandle: 'IMAGE', target: measurementNode.id, targetHandle: 'IMAGE' },
      { id: 'measurement-roi', source: roiCrop.id, sourceHandle: 'CROPPEDROI', target: measurementNode.id, targetHandle: 'ROI' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: measurementCatalog,
      nodes: [measurementNode, roiCrop, measurementCrop, inputImage], edges });

    expect(dialog.getByText('ROI 与目标图像坐标系不匹配')).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows a measurement mask from the crop that supplies its image', () => {
    const measurementCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: measurementCatalog.type, label: '卡尺找线', inputs: measurementCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const inputImage: Node = { id: 'input-image', position: { x: 0, y: 0 }, data: { label: '源图像' } };
    const edges = [
      { id: 'crop-image', source: inputImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'measurement-roi', source: cropNode.id, sourceHandle: 'croppedRoi', target: measurementNode.id, targetHandle: 'roi' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: measurementCatalog,
      nodes: [measurementNode, cropNode, inputImage], edges });

    expect(dialog.queryByText('ROI 与目标图像坐标系不匹配')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });
});

describe('measurement ROI image coverage preflight', () => {
  it.each(['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'])(
    'blocks %s when its local ROI cannot cover any input pixel', async type => {
      vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
        { nodeId: 'measurement', portName: 'image', width: 100, height: 80 },
        { nodeId: 'source', portName: 'image', width: 100, height: 80 }
      ] }) })));
      const measurementCatalog = fallbackCatalog.find(item => item.type === type)!;
      const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
        typeKey: type, label: '卡尺测量', inputs: measurementCatalog.inputs,
        parameters: { roi: { type: 'Rectangle', x: 120, y: 12, width: 20, height: 30 } }
      } };
      const imageNode: Node = { id: 'source', position: { x: 0, y: 0 }, data: { typeKey: 'image.synthetic', label: '场景图像' } };
      const edges = [{ id: 'measurement-image', source: imageNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' }];
      const onPreview = vi.fn(async () => report);
      const { dialog } = mount({ node: measurementNode, catalogItem: measurementCatalog,
        nodes: [measurementNode, imageNode], edges, result: report, onPreview });

      expect(await dialog.findByText(/测量 ROI 与当前输入图像（100×80）没有像素交集/)).toBeTruthy();
      expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
      expect(onPreview).not.toHaveBeenCalled();
    });
});

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

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected current-image %s from being transformed a second time', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: `当前图像${geometryLabel}`, inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'geometry-image', source: imageNode.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: imageNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: imageNode.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, transformNode, imageNode], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect(dialog.getByText(/断开 transform，或改接基准图像坐标/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows a connected search line measured in a separate reference image to follow the ROI transform', () => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === 'feature.line')!;
    const targetCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const geometryNode: Node = { id: 'reference-line', position: { x: 0, y: 0 }, data: {
      typeKey: sourceCatalog.type, label: '基准图像直线', inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetCatalog.type, label: '卡尺找线', inputs: targetCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const referenceImage: Node = { id: 'reference-image', position: { x: 0, y: 0 }, data: { label: '基准图像' } };
    const currentImage: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'geometry-image', source: referenceImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: currentImage.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: currentImage.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: 'line', target: measurementNode.id, targetHandle: 'searchLine' },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, transformNode, referenceImage, currentImage], edges });

    expect(dialog.queryByText('上游几何已处于变换目标坐标系')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('blocks a search line from the cropped image when the crop transform is connected too', () => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === 'feature.line')!;
    const targetCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceCatalog.type, label: '裁剪图像直线', inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetCatalog.type, label: '卡尺找线', inputs: targetCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '输入图像' } };
    const edges = [
      { id: 'crop-image', source: sourceImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'geometry-image', source: cropNode.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: 'line', target: measurementNode.id, targetHandle: 'searchLine' },
      { id: 'transform-input', source: cropNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, cropNode, sourceImage], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected %s from a different crop frame when no transform is connected', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropFrameMeasurement(sourceType, targetType, outputPort, inputPort, true);

    expect(dialog.getByText(`上游${geometryLabel === 'circle' ? '圆' : '搜索线'}与测量图像坐标不匹配`)).toBeTruthy();
    expect(dialog.getByText(/将.*和图像接到同一裁剪\/变换分支/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine', '上游搜索线与测量图像坐标不匹配'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle', '上游圆与测量图像坐标不匹配']
  ])('blocks connected %s geometry from before an affine image warp', (_geometryLabel, sourceType, targetType, outputPort, inputPort, mismatchTitle) => {
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const warpCatalog = fallbackCatalog.find(item => item.type === 'image.warpAffine')!;
    const transformCatalog = fallbackCatalog.find(item => item.type === 'coordinate.transformConstant')!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '变换前几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const warpNode: Node = { id: 'warp', position: { x: 0, y: 0 }, data: {
      typeKey: warpCatalog.type, label: '仿射变换图像', inputs: warpCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: transformCatalog.type, label: '像素仿射变换', inputs: transformCatalog.inputs,
      parameters: { sourceFrame: 'px', targetFrame: 'px', unit: 'px', x: 10, y: 0, angleDeg: 0 }
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '变换前图像' } };
    const edges = [
      { id: 'geometry-image', source: sourceImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'warp-image', source: sourceImage.id, sourceHandle: 'image', target: warpNode.id, targetHandle: 'image' },
      { id: 'warp-transform', source: transformNode.id, sourceHandle: 'transform', target: warpNode.id, targetHandle: 'transform' },
      { id: 'measurement-image', source: warpNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'measurement-geometry', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, warpNode, transformNode, sourceImage], edges });

    expect(dialog.getByText(mismatchTitle)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine', '上游搜索线与测量图像坐标不匹配'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle', '上游圆与测量图像坐标不匹配']
  ])('allows connected %s geometry and measurement images from the same crop frame without a transform', (_geometryLabel, sourceType, targetType, outputPort, inputPort, mismatchTitle) => {
    const { dialog } = mountCropFrameMeasurement(sourceType, targetType, outputPort, inputPort, false);

    expect(dialog.queryByText(mismatchTitle)).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected %s from the wrong source frame of a crop transform', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropTransformSourceMeasurement(sourceType, targetType, outputPort, inputPort, true);

    expect(dialog.getByText(`上游${geometryLabel === 'circle' ? '圆' : '搜索线'}与 transform 源坐标系不匹配`)).toBeTruthy();
    expect(dialog.getByText(/来自 transform 源图像/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('allows a connected %s from the crop transform source frame', (_geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropTransformSourceMeasurement(sourceType, targetType, outputPort, inputPort, false);

    expect(dialog.queryByText(/与 transform 源坐标系不匹配/)).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ['fitted line', 'measure.fitLine', 'measure.caliperLine', 'line', 'searchLine'],
    ['fitted circle', 'measure.fitCircle', 'measure.caliperCircle', 'circle', 'circle']
  ])('traces %s through connected Point2D measurements before allowing a transform', (geometryLabel, fitType, targetType, outputPort, inputPort) => {
    const pointCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const fitCatalog = fallbackCatalog.find(item => item.type === fitType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '最终卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const pointNodes: Node[] = [1, 2, 3].map(index => ({ id: `point-source-${index}`, position: { x: 0, y: 0 }, data: {
      typeKey: pointCatalog.type, label: `上游卡尺测点 ${index}`, inputs: pointCatalog.inputs, parameters: {}
    } }));
    const fitNode: Node = { id: 'fit-geometry', position: { x: 0, y: 0 }, data: {
      typeKey: fitType, label: `多点拟合${geometryLabel}`, inputs: fitCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      ...pointNodes.flatMap((pointNode, index) => [
        { id: `point-image-${index}`, source: imageNode.id, sourceHandle: 'image', target: pointNode.id, targetHandle: 'image' },
        { id: `fit-point-${index}`, source: pointNode.id, sourceHandle: 'point', target: fitNode.id, targetHandle: `point${index + 1}` }
      ]),
      { id: 'measurement-image', source: imageNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: imageNode.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'fit-geometry', source: fitNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, ...pointNodes, fitNode, transformNode, imageNode], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect(dialog.getByText(/断开 transform，或改接基准图像坐标/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

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

  it('shows a connected search line in its saved reference frame when transform is connected', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'search-line', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: {
        searchLine: { type: 'Line2D', display: '[(14, 22), (88, 46)]',
          value: { start: { x: 14, y: 22 }, end: { x: 88, y: 46 } } },
        transform: { type: 'Transform2D', display: 'translation', value: { sourceUnit: 'px', targetUnit: 'px',
          h00: 1, h01: 0, h02: 50, h10: 0, h11: 1, h12: 10, h20: 0, h21: 0, h22: 1 } }
      } }] };
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, lineSource, imageNode], edges, result: lineResult });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/上游基准搜索线/)).toBeTruthy();
    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('14');
    expect(geometry.getAttribute('data-end-y')).toBe('46');
    expect(geometry.getAttribute('data-editable')).toBe('false');
    expect(dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();

    fireEvent.click(dialog.getByRole('radio', { name: '当前测量图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/other/image'));
    const mappedGeometry = dialog.getByTestId('line-geometry');
    expect(mappedGeometry.getAttribute('data-start-x')).toBe('64');
    expect(mappedGeometry.getAttribute('data-start-y')).toBe('32');
    expect(mappedGeometry.getAttribute('data-end-x')).toBe('138');
    expect(mappedGeometry.getAttribute('data-end-y')).toBe('56');
    expect(mappedGeometry.getAttribute('data-editable')).toBe('false');
  });

  it('shows a connected search line on the crop input image when crop transform is connected', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 50, height: 50 } } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪前图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'line-image-input', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-search-input', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { searchLine: { type: 'Line2D', display: '[(24, 27), (86, 39)]',
        value: { start: { x: 24, y: 27 }, end: { x: 86, y: 39 } } } } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: lineNode.id, portName: 'image', width: 50, height: 50 }, { nodeId: cropNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode, lineSource, imageNode], edges, result: lineResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByText(/上游基准搜索线/)).toBeTruthy();
    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('24');
    expect(geometry.getAttribute('data-end-y')).toBe('39');
    expect(geometry.getAttribute('data-editable')).toBe('false');
  });

  it('uses the ROI reference image to edit measurement geometry through a composed ROI-and-crop transform', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线',
      inputs: lineCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正',
      parameters: { referenceImage: 'saved-png', referenceWidth: 100, referenceHeight: 100 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪',
      inputs: cropCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 10, y: 10, width: 70, height: 70 } } } };
    const composeNode: Node = { id: 'compose', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformCompose', label: '组合变换' } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '基准输入图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'roi-transform', source: roiNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformA' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformB' },
      { id: 'composed-transform', source: composeNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' },
      { id: 'cropped-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, cropNode, composeNode, imageNode], edges });

    expect(await dialog.findByTestId('line-geometry')).toBeTruthy();
    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-editable')).toBe('true');
  });

  it.each(['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'] as const)(
    'warns %s when a cropped image has no matching croppedRoi mask', async type => {
      const measurementCatalog = fallbackCatalog.find(item => item.type === type)!;
      const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
        typeKey: type, label: measurementCatalog.displayName, inputs: measurementCatalog.inputs, parameters: {}
      } };
      const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
      const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
        typeKey: cropCatalog.type, label: '预处理裁剪', inputs: cropCatalog.inputs,
        parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
      } };
      const edges = [
        { id: 'crop-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' }
      ];
      const { dialog, unmount } = mount({ node: measurementNode, catalogItem: measurementCatalog, nodes: [measurementNode, cropNode], edges });

      expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
      expect(dialog.getByText(/选区外像素会清零/)).toBeTruthy();
      unmount();
      cleanup();

      const maskedEdges = [...edges, {
        id: 'crop-roi', source: cropNode.id, sourceHandle: 'croppedRoi', target: measurementNode.id, targetHandle: 'roi'
      }];
      const masked = mount({ node: measurementNode, catalogItem: measurementCatalog, nodes: [measurementNode, cropNode], edges: maskedEdges });
      expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
    }
  );

  it('traces crop-mask guidance through pixel-coordinate-preserving image processing', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const grayscaleCatalog = fallbackCatalog.find(item => item.type === 'image.grayscale')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
    } };
    const grayscaleNode: Node = { id: 'grayscale', position: { x: 0, y: 0 }, data: {
      typeKey: grayscaleCatalog.type, label: '灰度转换', inputs: grayscaleCatalog.inputs, parameters: {}
    } };
    const nodes = [lineNode, cropNode, grayscaleNode];
    const edges = [
      { id: 'crop-gray', source: cropNode.id, sourceHandle: 'image', target: grayscaleNode.id, targetHandle: 'image' },
      { id: 'gray-line', source: grayscaleNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const view = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(view.dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(view.dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
    expect(view.dialog.getByText(/灰度转换等不改变坐标的处理可以放在两者之间/)).toBeTruthy();
    expect(view.dialog.getByText(/模糊等邻域滤波后请避开 ROI 边界测量/)).toBeTruthy();
    view.unmount();
    cleanup();

    const masked = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges: [...edges,
      { id: 'crop-mask', source: cropNode.id, sourceHandle: 'croppedRoi', target: lineNode.id, targetHandle: 'roi' }] });
    expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
  });

  it.each(['feature.line', 'feature.circle', 'measure.blobs', 'region.fromRoi'])(
    'keeps crop-mask guidance through %s image outputs', (imageNodeType) => {
      const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
      const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
      const imageNodeCatalog = fallbackCatalog.find(item => item.type === imageNodeType)!;
      const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
        typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
      } };
      const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
        typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
        parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
      } };
      const imageNode: Node = { id: 'image-pass', position: { x: 0, y: 0 }, data: {
        typeKey: imageNodeCatalog.type, label: imageNodeCatalog.displayName, inputs: imageNodeCatalog.inputs, parameters: {}
      } };
      const nodes = [lineNode, cropNode, imageNode];
      const edges = [
        { id: 'crop-pass', source: cropNode.id, sourceHandle: 'image', target: imageNode.id, targetHandle: 'image' },
        { id: 'pass-line', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
      ];
      const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

      expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
      expect(dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
    }
  );

  it('traces crop-mask guidance through the ROI transform debug-image input', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const roiCatalog = fallbackCatalog.find(item => item.type === 'roi.transform')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Circle', x: 40, y: 40, radius: 20 } }
    } };
    const roiNode: Node = { id: 'roi', position: { x: 0, y: 0 }, data: {
      typeKey: roiCatalog.type, label: '定位修正 ROI', inputs: roiCatalog.inputs, parameters: {}
    } };
    const edges = [
      { id: 'crop-roi', source: cropNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      { id: 'roi-line', source: roiNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode, roiNode], edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
  });

  it('keeps crop-mask guidance when a geometric image transform changes the pixel frame', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const resizeCatalog = fallbackCatalog.find(item => item.type === 'image.resize')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Circle', x: 30, y: 30, radius: 20 } }
    } };
    const resizeNode: Node = { id: 'resize', position: { x: 0, y: 0 }, data: {
      typeKey: resizeCatalog.type, label: '缩放图像', inputs: resizeCatalog.inputs, parameters: {}
    } };
    const nodes = [lineNode, cropNode, resizeNode];
    const edges = [
      { id: 'crop-resize', source: cropNode.id, sourceHandle: 'image', target: resizeNode.id, targetHandle: 'image' },
      { id: 'resize-line', source: resizeNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/多次裁剪或坐标变换后，单个 croppedRoi 可能无法直接覆盖当前图像/)).toBeTruthy();
  });

  it('does not warn about synthetic ROI edges for a rectangular crop', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 10, y: 10, width: 50, height: 40 } }
    } };
    const edges = [{ id: 'crop-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];

    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode], edges });

    expect(dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
  });

  it('warns when a pose-corrected rectangular ROI can rotate into a masked polygon during cropping', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '定位区域裁剪', inputs: cropCatalog.inputs,
      // This local rectangle is dormant because the connected corrected ROI takes precedence.
      parameters: { roi: { type: 'Rectangle', x: 0, y: 0, width: 80, height: 80 } }
    } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 30, height: 20 } }
    } };
    const edges = [
      { id: 'crop-image', source: 'image-source', sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'corrected-roi', source: roiNode.id, sourceHandle: 'roi', target: cropNode.id, targetHandle: 'roi' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const imageNode: Node = { id: 'image-source', position: { x: 0, y: 0 }, data: { label: '输入图像' } };
    const nodes = [lineNode, cropNode, roiNode, imageNode];
    const { dialog, unmount } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/选区外像素会清零/)).toBeTruthy();
    unmount();
    cleanup();

    const masked = mount({ node: lineNode, catalogItem: lineCatalog, nodes,
      edges: [...edges, { id: 'crop-mask', source: cropNode.id, sourceHandle: 'croppedRoi', target: lineNode.id, targetHandle: 'roi' }] });
    expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
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

  it('writes an interactively adjusted caliper profile span back into the staged edge length', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20, edgeLength: 0 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟调整采样跨度' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'edgeLength')!.label }) as HTMLInputElement).value).toBe('45');
    });
  });

  it('passes the backend-effective projection width into the circle-caliper preview', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, radialBand: 5, angleStartDeg: 0, angleEndDeg: 90,
        samplingMode: 'count', caliperCount: 3, sampleWidth: 4 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    expect((await dialog.findByTestId('circle-geometry')).getAttribute('data-profile-width')).toBe('5');
    fireEvent.click(dialog.getByRole('button', { name: '模拟调整投影宽度' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'sampleWidth')!.label }) as HTMLInputElement).value).toBe('11');
      expect(dialog.getByTestId('circle-geometry').getAttribute('data-profile-width')).toBe('11');
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

  it('keeps an upstream circle fixed while allowing image-based arc and radial-band edits', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 6, angleStartDeg: 0, angleEndDeg: 60 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'geometry.circle', label: '上游拟合圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'CIRCLE' }
    ];
    const circleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { CIRCLE: { type: 'Circle', display: '(68, 42, r=24)', value: { x: 68, y: 42, radius: 24 } } } }] };
    const onApply = vi.fn();
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges, result: circleResult, onApply });

    expect(dialog.getByText(/圆心和标称半径锁定为本次运行的上游输入/)).toBeTruthy();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerX')!.label })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerY')!.label })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radius')!.label })).toBeNull();
    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-center-x')).toBe('68');
    expect(geometry.getAttribute('data-center-y')).toBe('42');
    expect(geometry.getAttribute('data-radius')).toBe('24');
    expect(geometry.getAttribute('data-center-editable')).toBe('false');
    expect(geometry.getAttribute('data-radius-editable')).toBe('false');

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整圆弧' }));
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ centerX: 20, centerY: 30, radius: 10, radialBand: 14, angleStartDeg: -10, angleEndDeg: 90 });
  });

  it('uses the current workflow circle after a changed local preview fails', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { radialBand: 6 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'geometry.circle', label: '上游拟合圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' }
    ];
    const withCircle = (x: number, y: number, radius: number): RunResult => ({ ...report, nodeReports: [{ nodeId: circleNode.id,
      nodeType: circleCatalog.type, success: true, durationMs: 1, summary: {},
      inputs: { circle: { type: 'Circle', display: `(${x}, ${y}, r=${radius})`, value: { x, y, radius } } } }] });
    const onPreview = vi.fn().mockResolvedValueOnce(withCircle(22, 28, 12)).mockRejectedValueOnce(new Error('local preview failed'));
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges,
      result: withCircle(68, 42, 24), onPreview });

    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68'));
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('22'));
    fireEvent.change(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radialBand')!.label }),
      { target: { value: '12' } });
    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68'));

    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(dialog.getByText('本次节点测试未完成')).toBeTruthy());
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68');
  });

  it('allows arc and search-band edits for a connected circle in the saved transform reference frame', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 6, angleStartDeg: 0, angleEndDeg: 60 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitCircle', label: '上游拟合圆' } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-input', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const circleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { circle: { type: 'Circle', display: '(68, 42, r=24)', value: { x: 68, y: 42, radius: 24 } } } }] };
    const onApply = vi.fn();
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, roiNode, imageNode], edges, result: circleResult, onApply });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/图像预览显示对应基准图像/)).toBeTruthy();
    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-center-x')).toBe('68');
    expect(geometry.getAttribute('data-center-y')).toBe('42');
    expect(geometry.getAttribute('data-radius')).toBe('24');
    expect(geometry.getAttribute('data-center-editable')).toBe('false');

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整圆弧' }));
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ centerX: 20, centerY: 30, radius: 10, radialBand: 14, angleStartDeg: -10, angleEndDeg: 90 });
  });

  it('blocks a zero-span circle arc before running the measurement', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, angleStartDeg: 30, angleEndDeg: 30 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    expect(dialog.getByText(/单条径向卡尺，无法拟合圆/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('explains when a dense step-mode circle sampling is raised to the spoke limit', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'dense-circle', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 50, centerY: 50, radius: 20, samplingMode: 'step',
        angleStartDeg: -180, angleEndDeg: 180, angleStepDeg: 0.5 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges });

    expect(dialog.getByText(/将使用 360 条卡尺，实际步长约 1°/)).toBeTruthy();
    expect(dialog.queryByText('请修正参数后再运行或应用')).toBeNull();
  });

  it('warns when many circle calipers still cover too narrow an arc for a stable fit', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'narrow-circle', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 50, centerY: 50, radius: 20, samplingMode: 'count',
        angleStartDeg: 0, angleEndDeg: 10, caliperCount: 72, minValidCalipers: 3 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges });

    expect(dialog.getByText(/理论测点分布接近直线/)).toBeTruthy();
    expect(dialog.queryByText('请修正参数后再运行或应用')).toBeNull();
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

  it('uses the current workflow search line after a changed local preview fails', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: {} } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'feature.line', label: '上游搜索线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-override', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' }
    ];
    const withLine = (start: { x: number; y: number }, end: { x: number; y: number }): RunResult => ({ ...report, nodeReports: [{ nodeId: lineNode.id,
      nodeType: lineCatalog.type, success: true, durationMs: 1, summary: {},
      inputs: { searchLine: { type: 'Line2D', display: 'search line', value: { start, end } } } }] });
    const onPreview = vi.fn().mockResolvedValueOnce(withLine({ x: 20, y: 30 }, { x: 60, y: 30 }))
      .mockRejectedValueOnce(new Error('local preview failed'));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, lineSource, imageNode], edges,
      result: withLine({ x: 70, y: 42 }, { x: 90, y: 42 }), onPreview });

    await waitFor(() => expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70'));
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('20'));
    fireEvent.change(dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'sampleWidth')!.label }),
      { target: { value: '7' } });
    await waitFor(() => {
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-profile-width')).toBe('7');
    });

    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(dialog.getByText('本次节点测试未完成')).toBeTruthy());
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70');
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

    expect(await dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper/image');
  });

  it.each(['measure.caliper', 'measure.caliperLine', 'measure.caliperCircle', 'feature.edgeSubpixel'] as const)(
    'shows %s result overlays until its measurement geometry changes', async (type) => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === type)!;
    const parameters = type === 'measure.caliperCircle'
      ? { centerX: 50, centerY: 50, radius: 20 }
      : { startX: 10, startY: 20, endX: 80, endY: 20 };
    const labels: Record<string, string> = { 'measure.caliper': '卡尺测量', 'measure.caliperLine': '卡尺找线',
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
