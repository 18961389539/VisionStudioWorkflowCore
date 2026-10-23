/**
 * ROI 参考/坐标帧/覆盖率域 —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

it('shows image catalog errors and correlation IDs directly in the node workspace', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 503,
    json: async () => ({ detail: 'Capture unavailable', correlationId: 'trace-images' }) })));
  const { dialog } = mount({ result: report, initialTab: 'image' });
  const error = await dialog.findByText(/Capture unavailable/);
  expect(error.textContent).toContain('trace-images');
  expect(error.getAttribute('role')).toBe('alert');
  expect(dialog.getByRole('button', { name: '重试加载' })).toBeTruthy();
});

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
    await waitFor(() => expect((view.dialog.getByRole('button', { name: '清除测试 ROI' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(view.dialog.getByRole('button', { name: '清除测试 ROI' }));
    await waitFor(() => expect((view.dialog.getByRole('radio', { name: '修正结果' }) as HTMLInputElement).disabled).toBe(false));
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
    await waitFor(() => expect((view.dialog.getByRole('button', { name: '重新设为基准' }) as HTMLButtonElement).disabled).toBe(false));
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

  it('does not draw a connected ROI over an image from a different coordinate frame', async () => {
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
    const runResult: RunResult = { ...report, runId: 'mismatched-crop-roi', nodeReports: [
      { nodeId: cropNode.id, nodeType: cropCatalog.type, success: false, durationMs: 1, summary: {}, inputs: {
        roi: { type: 'Roi', display: 'Circle', value: { type: 'Circle', x: 30, y: 24, radius: 12 } }
      } }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: 'input-image', portName: 'image', width: 100, height: 100 },
      { nodeId: 'other-image', portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: cropNode, catalogItem: cropCatalog, nodes: [cropNode, roiNode, inputImage, otherImage], edges, result: runResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/input-image/image'));
    expect(dialog.getByText('ROI 与目标图像坐标系不匹配')).toBeTruthy();
    expect(dialog.getByTestId('roi-preview-shape').getAttribute('data-type')).toBe('none');
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

