/**
 * 适用性/参数可见性域 —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

describe('numeric conditional parameters', () => {
  it('hides the HTTP retry delay until retries are enabled', () => {
    const httpCatalog = fallbackCatalog.find(item => item.type === 'output.httpRequest')!;
    const httpNode: Node = { id: 'http', position: { x: 0, y: 0 }, data: {
      typeKey: 'output.httpRequest', label: 'HTTP 请求', inputs: httpCatalog.inputs, parameters: {}
    } };
    const { dialog } = mount({ node: httpNode, nodes: [httpNode], catalogItem: httpCatalog });
    expect(dialog.queryByRole('spinbutton', { name: '重试间隔' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '重试次数' }), { target: { value: '2' } });
    expect(dialog.getByRole('spinbutton', { name: '重试间隔' })).toBeTruthy();
  });

  it('hides template scale step while the scale search is fixed', () => {
    const templateCatalog = fallbackCatalog.find(item => item.type === 'match.template')!;
    const templateNode: Node = { id: 'template', position: { x: 0, y: 0 }, data: {
      typeKey: 'match.template', label: '模板匹配', inputs: templateCatalog.inputs, parameters: {}
    } };
    const { dialog } = mount({ node: templateNode, nodes: [templateNode], catalogItem: templateCatalog });
    expect(dialog.queryByRole('spinbutton', { name: '搜索缩放步长' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '最大缩放（%）' }), { target: { value: '120' } });
    expect(dialog.getByRole('spinbutton', { name: '搜索缩放步长' })).toBeTruthy();
  });
});

describe('retry delay visibility', () => {
  it('hides try retry delay while retries are disabled', () => {
    const tryCatalog = fallbackCatalog.find(item => item.type === 'flow.try')!;
    const tryNode: Node = { id: 'try', position: { x: 0, y: 0 }, data: {
      typeKey: 'flow.try', label: '异常捕获', inputs: tryCatalog.inputs, parameters: {}
    } };
    const { dialog } = mount({ node: tryNode, nodes: [tryNode], catalogItem: tryCatalog });
    expect(dialog.queryByRole('spinbutton', { name: '重试间隔' })).toBeNull();
    fireEvent.change(dialog.getByRole('spinbutton', { name: '最大重试次数' }), { target: { value: '2' } });
    expect(dialog.getByRole('spinbutton', { name: '重试间隔' })).toBeTruthy();
  });
});

describe('switch cases editor integration', () => {
  it('shows editable branch rows instead of the raw JSON textarea', () => {
    const switchCatalog = fallbackCatalog.find(item => item.type === 'flow.switch')!;
    const switchNode: Node = { id: 'switch', position: { x: 0, y: 0 }, data: {
      typeKey: 'flow.switch', label: '多路分支', inputs: switchCatalog.inputs, parameters: { casesJson: '["OK","NG"]' }
    } };
    const { dialog } = mount({ node: switchNode, nodes: [switchNode], catalogItem: switchCatalog });
    expect((dialog.getByRole('textbox', { name: /分支 1 标签/ }) as HTMLInputElement).value).toBe('OK');
    expect((dialog.getByRole('textbox', { name: /分支 2 标签/ }) as HTMLInputElement).value).toBe('NG');
    expect(dialog.queryByRole('textbox', { name: /Cases \(JSON string array\)/ })).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: /添加分支/ }));
    expect(dialog.getAllByRole('textbox', { name: /分支 \d+ 标签/ })).toHaveLength(3);
    fireEvent.click(dialog.getByRole('button', { name: /删除分支 2/ }));
    expect(dialog.getAllByRole('textbox', { name: /分支 \d+ 标签/ })).toHaveLength(2);
  });
});

describe('inference crop applicability', () => {
  it.each(['inference.detection', 'inference.segmentation', 'inference.obb', 'inference.instanceSegmentation', 'inference.pose', 'inference.anomaly'])(
    'hides center crop on %s because output coordinates cannot be remapped', type => {
      const catalog = fallbackCatalog.find(item => item.type === type)!;
      const target: Node = { id: 'infer', position: { x: 0, y: 0 }, data: { typeKey: type, label: catalog.displayName, inputs: catalog.inputs, parameters: {} } };
      const { dialog } = mount({ node: target, nodes: [target], catalogItem: catalog });
      expect(dialog.queryByRole('switch', { name: '缩放后居中裁剪' })).toBeNull();
    });

  it('keeps center crop available on classification', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'inference.classification')!;
    const target: Node = { id: 'infer', position: { x: 0, y: 0 }, data: { typeKey: 'inference.classification', label: catalog.displayName, inputs: catalog.inputs, parameters: {} } };
    const { dialog } = mount({ node: target, nodes: [target], catalogItem: catalog });
    expect(dialog.getByRole('switch', { name: '缩放后居中裁剪' })).toBeTruthy();
  });
});

describe('mode-dependent parameter visibility', () => {
  it('hides the legacy template angle range once an explicit window is set', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'match.template')!;
    const explicit: Node = { id: 'template', position: { x: 0, y: 0 }, data: {
      typeKey: 'match.template', label: catalog.displayName, inputs: catalog.inputs, parameters: { searchAngleStartDeg: 10 }
    } };
    const explicitDialog = mount({ node: explicit, nodes: [explicit], catalogItem: catalog }).dialog;
    expect(explicitDialog.queryByRole('spinbutton', { name: '角度范围（度）' })).toBeNull();
    cleanup();

    const legacy: Node = { ...explicit, id: 'legacy', data: { ...explicit.data, parameters: {} } };
    const legacyDialog = mount({ node: legacy, nodes: [legacy], catalogItem: catalog }).dialog;
    expect(legacyDialog.getByRole('spinbutton', { name: '角度范围（度）' })).toBeTruthy();
  });

  it('shows nominal width only when closest-width selection is active', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'measure.caliperWidth')!;
    const closest: Node = { id: 'width', position: { x: 0, y: 0 }, data: {
      typeKey: 'measure.caliperWidth', label: catalog.displayName, inputs: catalog.inputs, parameters: { direction: 'closestwidth' }
    } };
    const closestDialog = mount({ node: closest, nodes: [closest], catalogItem: catalog }).dialog;
    expect(closestDialog.getByRole('spinbutton', { name: '标称宽度（最近宽度选择）' })).toBeTruthy();
    cleanup();

    const strongest: Node = { ...closest, id: 'strongest', data: { ...closest.data, parameters: {} } };
    const strongestDialog = mount({ node: strongest, nodes: [strongest], catalogItem: catalog }).dialog;
    expect(strongestDialog.queryByRole('spinbutton', { name: '标称宽度（最近宽度选择）' })).toBeNull();
  });
});

describe('flow.if hysteresis applicability', () => {
  it('hides hysteresis until the previous-condition input is connected', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'flow.if')!;
    const ifNode: Node = { id: 'if', position: { x: 0, y: 0 }, data: {
      typeKey: 'flow.if', label: catalog.displayName, inputs: catalog.inputs, parameters: {}
    } };
    const disconnected = mount({ node: ifNode, nodes: [ifNode], edges: [], catalogItem: catalog }).dialog;
    expect(disconnected.queryByRole('spinbutton', { name: '迟滞' })).toBeNull();
    cleanup();
    const source: Node = { id: 'source', position: { x: 0, y: 0 }, data: { label: '来源' } };
    const edge = { id: 'previous', source: 'source', sourceHandle: 'condition', target: 'if', targetHandle: 'previous' };
    const connected = mount({ node: ifNode, nodes: [ifNode, source], edges: [edge], catalogItem: catalog }).dialog;
    expect(connected.getByRole('spinbutton', { name: '迟滞' })).toBeTruthy();
  });
});

describe('robot action applicability', () => {
  it('shows only the controls used by the selected robot action', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'robot.executeTarget')!;
    const makeNode = (action: string): Node => ({ id: 'robot', position: { x: 0, y: 0 }, data: {
      typeKey: 'robot.executeTarget', label: catalog.displayName, inputs: catalog.inputs, parameters: { action }
    } });

    const sendTarget = mount({ node: makeNode('SendTarget'), nodes: [makeNode('SendTarget')], catalogItem: catalog }).dialog;
    expect(sendTarget.queryByRole('switch', { name: '等待执行完成' })).toBeNull();
    expect(sendTarget.queryByRole('spinbutton', { name: '超时时间' })).toBeNull();
    expect(sendTarget.queryByRole('spinbutton', { name: '重试间隔' })).toBeNull();
    expect(sendTarget.queryByRole('switch', { name: '自动确认' })).toBeNull();
    cleanup();

    const move = mount({ node: makeNode('Move'), nodes: [makeNode('Move')], catalogItem: catalog }).dialog;
    expect(move.getByRole('switch', { name: '等待执行完成' })).toBeTruthy();
    expect(move.getByRole('spinbutton', { name: '超时时间' })).toBeTruthy();
    expect(move.queryByRole('spinbutton', { name: '重试间隔' })).toBeNull();
    expect(move.queryByRole('switch', { name: '自动确认' })).toBeNull();
    cleanup();

    const handshake = mount({ node: makeNode('Handshake'), nodes: [makeNode('Handshake')], catalogItem: catalog }).dialog;
    expect(handshake.getByRole('spinbutton', { name: '重试间隔' })).toBeTruthy();
    expect(handshake.getByRole('switch', { name: '自动确认' })).toBeTruthy();
  });
});

describe('pose skeleton applicability', () => {
  it('shows skeleton drawing only for the 17-keypoint COCO topology', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'inference.pose')!;
    const makeNode = (numKeypoints: number): Node => ({ id: 'pose', position: { x: 0, y: 0 }, data: {
      typeKey: 'inference.pose', label: catalog.displayName, inputs: catalog.inputs, parameters: { numKeypoints }
    } });
    const check = (numKeypoints: number) => mount({ node: makeNode(numKeypoints), nodes: [makeNode(numKeypoints)], catalogItem: catalog }).dialog;

    let dialog = check(17);
    expect(dialog.getByRole('switch', { name: '绘制 COCO 骨架' })).toBeTruthy();
    cleanup();
    dialog = check(5);
    expect(dialog.queryByRole('switch', { name: '绘制 COCO 骨架' })).toBeNull();
  });
});

describe('metrology JSON authoring', () => {
  it('uses the JSON editor with a starter example for measurement objects', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'measure.metrology')!;
    const node: Node = { id: 'metrology', position: { x: 0, y: 0 }, data: {
      typeKey: 'measure.metrology', label: catalog.displayName, inputs: catalog.inputs, parameters: {}
    } };
    const dialog = mount({ node, nodes: [node], catalogItem: catalog }).dialog;

    fireEvent.click(dialog.getByText('高级：对象 JSON（保留旧配方和每对象扩展参数）'));
    const editor = dialog.getByRole('textbox', { name: '计量对象 JSON' }) as HTMLTextAreaElement;
    expect(dialog.getByRole('button', { name: '格式化 JSON' })).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: '插入示例' }));
    expect(editor.value).toContain('"type": "circle"');
    expect(editor.value).toContain('"type": "line"');
  });
});

describe('synchronized capture applicability', () => {
  it('shows the lead time only for scheduled capture', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'camera.syncCapture')!;
    const makeNode = (scheduled: boolean): Node => ({ id: 'sync', position: { x: 0, y: 0 }, data: {
      typeKey: 'camera.syncCapture', label: catalog.displayName, inputs: catalog.inputs, parameters: { scheduled }
    } });
    const check = (scheduled: boolean) => mount({ node: makeNode(scheduled), nodes: [makeNode(scheduled)], catalogItem: catalog }).dialog;

    let dialog = check(false);
    expect(dialog.queryByRole('spinbutton', { name: '定时触发提前量' })).toBeNull();
    cleanup();
    dialog = check(true);
    expect(dialog.getByRole('spinbutton', { name: '定时触发提前量' })).toBeTruthy();
  });
});

describe('dual-camera calibration input applicability', () => {
  it('shows image dimensions only for points + stereoCalibrate', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'calibration.dualCameraExtrinsics')!;
    const makeNode = (parameters: Record<string, unknown>): Node => ({ id: 'dual', position: { x: 0, y: 0 }, data: {
      typeKey: 'calibration.dualCameraExtrinsics', label: catalog.displayName, inputs: catalog.inputs, parameters
    } });
    const check = (parameters: Record<string, unknown>) => mount({ node: makeNode(parameters), nodes: [makeNode(parameters)], catalogItem: catalog }).dialog;
    let dialog = check({ inputMode: 'poses', method: 'average' });
    expect(dialog.queryByText('求解方法')).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '图像宽度（立体标定）' })).toBeNull();
    cleanup();
    dialog = check({ inputMode: 'poses', method: 'stereoCalibrate' });
    expect(dialog.queryByText('求解方法')).toBeNull();
    cleanup();
    dialog = check({ inputMode: 'points', method: 'average' });
    expect(dialog.getByText('求解方法')).toBeTruthy();
    expect(dialog.queryByRole('spinbutton', { name: '图像宽度（立体标定）' })).toBeNull();
    cleanup();
    dialog = check({ inputMode: 'points', method: 'stereoCalibrate' });
    expect(dialog.getByText('求解方法')).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '图像宽度（立体标定）' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '图像高度（立体标定）' })).toBeTruthy();
  });
});

describe('calibration board pattern applicability', () => {
  it('shows only the detection controls used by the selected board pattern', () => {
    const catalog = fallbackCatalog.find(item => item.type === 'calibration.detectBoard')!;
    const makeNode = (pattern: string): Node => ({ id: 'board', position: { x: 0, y: 0 }, data: {
      typeKey: 'calibration.detectBoard', label: catalog.displayName, inputs: catalog.inputs, parameters: { pattern }
    } });
    const check = (pattern: string) => mount({ node: makeNode(pattern), nodes: [makeNode(pattern)], catalogItem: catalog }).dialog;

    let dialog = check('chessboard');
    expect(dialog.getByRole('spinbutton', { name: '角点对比度' })).toBeTruthy();
    expect(dialog.queryByRole('switch', { name: '暗色目标' })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: '最大圆点面积' })).toBeNull();
    cleanup();

    dialog = check('circles');
    expect(dialog.queryByRole('spinbutton', { name: '角点对比度' })).toBeNull();
    expect(dialog.getByRole('switch', { name: '暗色目标' })).toBeTruthy();
    expect(dialog.getByRole('spinbutton', { name: '最大圆点面积' })).toBeTruthy();
  });
});
