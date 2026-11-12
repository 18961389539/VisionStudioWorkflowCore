import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CalibrationHandEyeWizard from '../components/CalibrationHandEyeWizard';

function stubBrowserApis() {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
  })));
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* jsdom 无布局 */ }
    unobserve() { /* no-op */ }
    disconnect() { /* no-op */ }
  });
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:mock'), revokeObjectURL: vi.fn() }));
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const solveResponse = {
  pose: { x: 1, y: 2, thetaDeg: 3 },
  translationRms: 0.01,
  angleRmsDeg: 0.02,
  maxTranslationError: 0.09,
  maxAngleErrorDeg: 0.5,
  worstSampleIndex: 1,
  outlierSampleIndex: 1,
  converged: true,
  iterations: 5,
  conditionNumber: 120,
  stdDevX: 0.001,
  stdDevY: 0.002,
  angleStdDevDeg: 0.01,
  crossCheckTranslationDelta: 0.0001,
  crossCheckAngleDeltaDeg: 0.001,
  unitWarning: null,
  sampleResidualsJson: JSON.stringify([0, 1, 2, 3].map((index) => ({
    index,
    translationError: index === 0 ? 0.09 : 0.01,
    angleErrorDeg: index === 1 ? 0.5 : 0.01,
    robot: { x: index, y: 0, thetaDeg: 0 },
    vision: { x: 0, y: 0, thetaDeg: 0 }
  }))),
  summary: { robustLoss: 'none' },
  samplesJson: '{}',
  mode: 'eyeInHand'
};

function stubApi(overrides: Record<string, Response | (() => Response)> = {}) {
  return vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    const url = String(input);
    const override = overrides[url];
    if (override) return typeof override === 'function' ? override() : override;
    if (url === '/api/calibration/wizard/handeye/sessions') return json([]);
    if (url === '/api/hand-eye-calibrations') return json([]);
    return json({});
  });
}

async function goToSamples() {
  fireEvent.click(await screen.findByRole('button', { name: /下一步：录入样本/ }));
}

async function solve() {
  fireEvent.click(await screen.findByRole('button', { name: /求解手眼/ }));
  await screen.findByRole('button', { name: /剔除最差样本/ });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CalibrationHandEyeWizard', () => {
  it('gates solve on live precheck and flags duplicate robot poses', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    expect(await screen.findByRole('button', { name: /求解手眼/ })).not.toBeDisabled();
    expect(screen.getByText(/样本预检/)).toBeTruthy();

    // 添加一个与样本 #0 完全相同的机器人位姿（0,0,-20）→ 预检应阻止求解。
    const draftInputs = screen.getAllByRole('spinbutton');
    fireEvent.change(draftInputs[2], { target: { value: '-20' } });
    fireEvent.click(screen.getByRole('button', { name: /添加位姿对/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: /求解手眼/ })).toBeDisabled());
    expect(screen.getByText(/机器人位姿重复/)).toBeTruthy();
  });

  it('sends solver settings and removes the node-reported worst sample (not max translation)', async () => {
    stubBrowserApis();
    const fetchMock = stubApi({ '/api/calibration/wizard/handeye/solve': json(solveResponse) });
    vi.stubGlobal('fetch', fetchMock);
    render(<CalibrationHandEyeWizard canApplyToSelectedNode onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    await solve();

    const solveCall = fetchMock.mock.calls.find(([url]) => String(url) === '/api/calibration/wizard/handeye/solve');
    expect(solveCall).toBeTruthy();
    const body = JSON.parse(String((solveCall![1] as RequestInit).body));
    expect(body.angularWeight).toBe(50);
    expect(body.visionScale).toBe(1);
    expect(body.robustLoss).toBe('none');
    expect(body.sourceUnit).toBe('mm');

    // 闭合散点与离群标记可见。
    expect(document.querySelector('svg[aria-label="闭合散点图"]')).toBeTruthy();
    expect(screen.getByText('离群')).toBeTruthy();

    // worstSampleIndex=1（角度残差主导），而平移残差最大的是 #0：必须按节点判定剔除 #1。
    fireEvent.click(screen.getByRole('button', { name: /剔除最差样本/ }));
    await screen.findByText(/已移除综合残差最大样本 #1/);
    expect(screen.queryByText('40, 10, -5')).toBeNull();
    expect(screen.getByText('0, 0, -20')).toBeTruthy();
  });

  it('warns when sourceUnit differs from unit without visionScale', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    const unitInputs = screen.getAllByDisplayValue('mm');
    fireEvent.change(unitInputs[unitInputs.length - 1], { target: { value: 'px' } });
    expect(await screen.findByText('单位不一致')).toBeTruthy();
  });

  it('imports CSV pose pairs and replaces the sample list', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    const { container } = render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    const file = new File(
      [['robotX,robotY,robotThetaDeg,visionX,visionY,visionThetaDeg', '1,2,3,4,5,6', '7,8,9,10,11,12'].join('\n')],
      'samples.csv',
      { type: 'text/csv' }
    );
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 2 对位姿/);
    expect(screen.getByText('1, 2, 3')).toBeTruthy();
    expect(screen.getByText('7, 8, 9')).toBeTruthy();
    expect(screen.getByRole('button', { name: /求解手眼/ })).toBeDisabled();
  });

  it('persists a session and lists it for reload', async () => {
    stubBrowserApis();
    let saved = false;
    const fetchMock = stubApi();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye/sessions' && init?.method === 'POST') {
        saved = true;
        return json({ sessionId: 's1', name: '测试会话', samplesJson: '{}' });
      }
      if (url === '/api/calibration/wizard/handeye/sessions') {
        return json(saved
          ? [{ sessionId: 's1', name: '测试会话', updatedAt: '2026-11-12T00:00:00Z', mode: 'eyeInHand', sampleCount: 4, hasSolve: false }]
          : []);
      }
      if (url === '/api/hand-eye-calibrations') return json([]);
      return json({});
    });
    render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    fireEvent.click(screen.getByRole('button', { name: /保存会话/ }));
    await screen.findByText(/会话已保存/);
    expect(await screen.findByText('测试会话')).toBeTruthy();
  });

  it('exports solver settings to the selected node', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi({ '/api/calibration/wizard/handeye/solve': json(solveResponse) }));
    const onApply = vi.fn();
    render(<CalibrationHandEyeWizard canApplyToSelectedNode selectedNodeType="calibration.handEye2D" onApplyToSelectedNode={onApply} />);
    await goToSamples();
    await solve();
    fireEvent.click(screen.getByRole('button', { name: /导出到节点/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledTimes(1));
    const parameters = onApply.mock.calls[0][0];
    expect(parameters.mode).toBe('eyeInHand');
    expect(parameters.angularWeight).toBe(50);
    expect(parameters.visionScale).toBe(1);
    expect(parameters.robustLoss).toBe('none');
    expect(parameters.sourceUnit).toBe('mm');
    expect(parameters.handEyeAssetId).toBeUndefined();
    expect(typeof parameters.samplesJson).toBe('string');
  });

  it('saves, publishes and reloads a versioned hand-eye asset', async () => {
    stubBrowserApis();
    const samplesJson = JSON.stringify({
      samples: [0, 1, 2, 3].map((i) => ({
        robot: { x: i * 10, y: i, thetaDeg: i * 10 - 20 },
        vision: { x: i * 2, y: -i, thetaDeg: i * 5 }
      }))
    });
    const sessionDocument = {
      formatVersion: 'handeye-wizard-v1', sessionId: 's1', name: '资产会话', updatedAt: '2026-11-12T00:00:00Z',
      samplesJson, mode: 'eyeToHand', angularWeight: 60, visionScale: 1, robustLoss: 'none',
      maxTranslationError: 0, maxAngleErrorDeg: 0, sourceFrame: 'Camera', targetFrame: '', unit: 'mm', sourceUnit: 'mm',
      lastSolveSummaryJson: null
    };
    const descriptor = {
      id: 'he1', name: '资产一', description: '', createdAt: '', updatedAt: '', latestVersion: 1, publishedVersion: null,
      versions: [{ version: 1, snapshotHash: '', modelHash: '', createdAt: '', note: '', published: false }],
      publicationHistory: []
    };
    let assetCreated = false;
    const fetchMock = stubApi({ '/api/calibration/wizard/handeye/solve': json(solveResponse) });
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye/solve') return json(solveResponse);
      if (url === '/api/calibration/wizard/handeye/sessions' && init?.method === 'POST') return json(sessionDocument);
      if (url === '/api/calibration/wizard/handeye/sessions') return json([]);
      if (url === '/api/hand-eye-calibrations' && init?.method === 'POST') { assetCreated = true; return json(descriptor); }
      if (url === '/api/hand-eye-calibrations') return json(assetCreated ? [descriptor] : []);
      if (url === '/api/hand-eye-calibrations/he1/versions/1/publish') return json({ ...descriptor, publishedVersion: 1 });
      if (url === '/api/hand-eye-calibrations/he1/versions/1') {
        return json({ assetId: 'he1', version: 1, snapshotHash: '', modelHash: '', createdAt: '', note: '', model: {}, sourceDocumentJson: JSON.stringify(sessionDocument), summaryJson: '{}' });
      }
      return json({});
    });
    render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    fireEvent.mouseDown(screen.getByText('眼在手上 eyeInHand'));
    fireEvent.click(await screen.findByText('眼在手外 eyeToHand'));
    await goToSamples();
    await solve();

    fireEvent.change(screen.getByPlaceholderText('如 handeye-cell1'), { target: { value: 'he1' } });
    fireEvent.change(screen.getByPlaceholderText('资产名称'), { target: { value: '资产一' } });
    fireEvent.click(screen.getByRole('button', { name: /保存为版本化资产/ }));
    await screen.findByText(/已保存手眼资产 he1 V1/);
    expect(await screen.findByText('已锁定 he1 V1')).toBeTruthy();

    fireEvent.click(await screen.findByRole('button', { name: /发\s*布/ }));
    await screen.findByText(/已发布手眼资产 he1 V1/);

    fireEvent.click(screen.getByRole('button', { name: /加\s*载/ }));
    await screen.findByText(/已加载手眼资产 he1 V1 的样本与设置/);
    expect(screen.getByText('0, 0, -20')).toBeTruthy();
  });

  it('loads and deletes a saved session', async () => {
    stubBrowserApis();
    const samplesJson = JSON.stringify({
      samples: [0, 1].map((i) => ({
        robot: { x: i, y: 0, thetaDeg: 0 },
        vision: { x: 0, y: i, thetaDeg: 0 }
      }))
    });
    const sessionDocument = {
      formatVersion: 'handeye-wizard-v1', sessionId: 's1', name: '待加载会话', updatedAt: '2026-11-12T00:00:00Z',
      samplesJson, mode: 'eyeInHand', angularWeight: 50, visionScale: 1, robustLoss: 'none',
      maxTranslationError: 0, maxAngleErrorDeg: 0, sourceFrame: 'Camera', targetFrame: '', unit: 'mm', sourceUnit: 'mm',
      lastSolveSummaryJson: null
    };
    const fetchMock = stubApi();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye/sessions' && init?.method === 'DELETE') return json({ deleted: true });
      if (url === '/api/calibration/wizard/handeye/sessions/s1') return json(sessionDocument);
      if (url === '/api/calibration/wizard/handeye/sessions') {
        return json([{ sessionId: 's1', name: '待加载会话', updatedAt: '2026-11-12T00:00:00Z', mode: 'eyeInHand', sampleCount: 2, hasSolve: false }]);
      }
      if (url === '/api/hand-eye-calibrations') return json([]);
      return json({});
    });
    render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }));
    await screen.findByText(/已加载会话「待加载会话」/);
    expect(screen.getAllByText('0, 0, 0').length).toBeGreaterThanOrEqual(2);
    const sessionRow = screen.getByText('待加载会话').closest('tr') as HTMLElement;
    fireEvent.click(within(sessionRow).getByRole('button', { name: /删\s*除/ }));
    await screen.findByText(/会话已删除/);
  });

  it('imports JSON pose pairs and exports CSV', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    const { container } = render(<CalibrationHandEyeWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    const samplesJson = JSON.stringify({
      samples: [0, 1].map((i) => ({
        robot: { x: i, y: 2, thetaDeg: 3 },
        vision: { x: 4, y: i, thetaDeg: 6 }
      }))
    });
    const file = new File([samplesJson], 'samples.json', { type: 'application/json' });
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 2 对位姿/);
    fireEvent.click(screen.getByRole('button', { name: /导出 CSV/ }));
    fireEvent.click(screen.getByRole('button', { name: /导出 JSON/ }));
    expect(URL.createObjectURL).toHaveBeenCalled();
  });
});