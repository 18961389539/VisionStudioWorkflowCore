/**
 * 草稿/参数可见性域 —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

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
    expect(dialog.queryByRole('spinbutton', { name: '训练缩放步长' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '原点 X' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '阈值' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '缩放容差' }), { target: { value: '10' } });
    expect(dialog.getByRole('spinbutton', { name: '训练缩放步长' })).toBeTruthy();
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

