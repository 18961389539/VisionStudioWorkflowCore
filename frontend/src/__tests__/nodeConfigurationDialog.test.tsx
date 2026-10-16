import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';

vi.mock('../components/ImageViewer', () => ({
  default: ({ previewUrl, showRoiTools, roiEnabled, onRoiChange }: { previewUrl?: string; showRoiTools?: boolean; roiEnabled: boolean; onRoiChange: (roi?: VisionRoi) => void }) =>
    <div><span data-testid="image-url">{previewUrl}</span><span>{showRoiTools ? 'ROI 工具' : '纯图像预览'}</span>
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
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', typeKey: 'match.shape', parameters: { modelMode: 'trained', minScore: 0.6 } } };
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
    expect(onPreview).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ trainingMode: true, modelMode: 'trained' }));
    expect(onApply).not.toHaveBeenCalled();
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    const applied = onApply.mock.calls[0][0];
    expect(applied.trainedModelData).toBe('model-bytes');
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
      ? response({ trainingPreviewReady: true })
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
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog, onPreview, onApply });
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
  });

  it('groups model coverage with training and hides inactive controls', () => {
    const shapeCatalog = fallbackCatalog.find(item => item.type === 'match.shape')!;
    const shapeNode: Node = { id: 'shape', position: { x: 0, y: 0 }, data: { label: '形状匹配', parameters: { modelMode: 'trained' } } };
    const { dialog } = mount({ node: shapeNode, nodes: [shapeNode], catalogItem: shapeCatalog });
    expect(dialog.getByRole('spinbutton', { name: '角度搜索范围' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '缩放容差' })).toBeTruthy();
    expect(dialog.queryByRole('spinbutton', { name: '缩放步长' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '原点 X' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '阈值' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '缩放容差' }), { target: { value: '10' } });
    expect(dialog.getByRole('spinbutton', { name: '缩放步长' })).toBeTruthy();
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
    expect(dialog.queryByRole('spinbutton', { name: '角度搜索范围' })).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '调整模型范围' }));
    fireEvent.change(dialog.getByRole('spinbutton', { name: '角度搜索范围' }), { target: { value: '90' } });
    expect(dialog.getByText('训练参数已变化，需要重新训练')).toBeTruthy();
    expect((dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('tab', { name: '搜索推理' }));
    expect(dialog.getByRole('button', { name: '前往模型训练' })).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行搜索' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('button', { name: '前往模型训练' }));
    expect(dialog.getByRole('button', { name: '重新训练模型' })).toBeTruthy();
    expect(onPreview).not.toHaveBeenCalled();
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
    const withRoi: Node = { ...node, data: { ...node.data, parameters: { expected: 'ABC', roi: { type: 'Rectangle', x: 1, y: 2, width: 3, height: 4 } } } };
    const imageCatalog: NodeCatalogItem = { ...catalog, inputs: [{ name: 'image', dataType: 'Image' }], capabilities: { supportsRoi: true } };
    const { dialog, props } = mount({ node: withRoi, nodes: [withRoi], catalogItem: imageCatalog, result: report });
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
