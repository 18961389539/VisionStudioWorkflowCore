import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CalibrationHandEye3DWizard from '../components/CalibrationHandEye3DWizard';

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

const rotX = (deg: number, t: number[]) => {
  const r = deg * Math.PI / 180; const c = Math.cos(r); const s = Math.sin(r);
  return [[1, 0, 0, t[0]], [0, c, -s, t[1]], [0, s, c, t[2]], [0, 0, 0, 1]];
};
const rotY = (deg: number, t: number[]) => {
  const r = deg * Math.PI / 180; const c = Math.cos(r); const s = Math.sin(r);
  return [[c, 0, s, t[0]], [0, 1, 0, t[1]], [-s, 0, c, t[2]], [0, 0, 0, 1]];
};
const rotZ = (deg: number, t: number[]) => {
  const r = deg * Math.PI / 180; const c = Math.cos(r); const s = Math.sin(r);
  return [[c, -s, 0, t[0]], [s, c, 0, t[1]], [0, 0, 1, t[2]], [0, 0, 0, 1]];
};

function makeSamples(count: number) {
  const builders = [rotX, rotY, rotZ];
  return Array.from({ length: count }, (_, i) => ({
    key: String(i),
    robotToBase: JSON.stringify(builders[i % 3](15 + i * 3, [i * 10, i * 2, i * 3])),
    targetToCamera: JSON.stringify(builders[(i + 1) % 3](10 + i * 2, [100 + i, 50, 200]))
  }));
}

const solveResponse = {
  transform: { sourceFrame: 'Camera', targetFrame: 'RobotTool', unit: 'mm', matrix: [1, 0, 0, 1, 0, 1, 0, 2, 0, 0, 1, 3, 0, 0, 0, 1] },
  calibrationJson: '{}',
  translationRms: 0.01,
  rotationRmsDeg: 0.02,
  robotTranslationSpan: 90,
  maxTranslationError: 0.03,
  maxRotationErrorDeg: 0.05,
  worstSampleIndex: 1,
  worstTranslationSampleIndex: 1,
  worstRotationSampleIndex: 2,
  outlierSampleIndex: 1,
  usedSampleCount: 5,
  rejectedSampleIndicesJson: '[3]',
  crossCheckTranslationDelta: 0.001,
  crossCheckAngleDeltaDeg: 0.002,
  sampleResidualsJson: JSON.stringify([0, 1, 2, 3, 4, 5].map((index) => ({
    index, translationError: 0.01, rotationErrorDeg: 0.02, rejected: index === 3,
    robotTranslation: { x: index, y: 0, z: 0 }, targetTranslation: { x: 0, y: 0, z: 0 }
  }))),
  summary: { method: 'DANIILIDIS' },
  samplesJson: '{}',
  mode: 'eyeInHand',
  method: 'DANIILIDIS'
};

function stubApi() {
  return vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    const url = String(input);
    if (url === '/api/calibration/wizard/handeye3d/sessions') return json([]);
    if (url === '/api/hand-eye3d-calibrations') return json([]);
    return json({});
  });
}

async function goToSamples() {
  fireEvent.click(await screen.findByRole('button', { name: /下一步：录入样本/ }));
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CalibrationHandEye3DWizard', () => {
  it('gates solve on sample count and motion diversity', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi());
    const { container } = render(<CalibrationHandEye3DWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    expect(screen.getByRole('button', { name: /求解三维手眼/ })).toBeDisabled();

    const file = new File([JSON.stringify({ samples: makeSamples(6).map((s) => ({ robotToBase: JSON.parse(s.robotToBase), targetToCamera: JSON.parse(s.targetToCamera) })) })], 's.json', { type: 'application/json' });
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 6 组位姿对/);
    await waitFor(() => expect(screen.getByRole('button', { name: /求解三维手眼/ })).not.toBeDisabled());
    expect(screen.getByText(/旋转轴覆盖/)).toBeTruthy();
  });

  it('sends solver settings and shows rejection/cross-check results', async () => {
    stubBrowserApis();
    const fetchMock = stubApi();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye3d/solve') return json(solveResponse);
      if (url === '/api/calibration/wizard/handeye3d/sessions') return json([]);
      if (url === '/api/hand-eye3d-calibrations') return json([]);
      return json({});
    });
    const { container } = render(<CalibrationHandEye3DWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    const file = new File([JSON.stringify({ samples: makeSamples(6).map((s) => ({ robotToBase: JSON.parse(s.robotToBase), targetToCamera: JSON.parse(s.targetToCamera) })) })], 's.json', { type: 'application/json' });
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 6 组位姿对/);
    fireEvent.click(screen.getByRole('button', { name: /求解三维手眼/ }));
    await screen.findByRole('button', { name: /剔除综合残差最大样本/ });

    const solveCall = fetchMock.mock.calls.find(([url]) => String(url) === '/api/calibration/wizard/handeye3d/solve');
    const body = JSON.parse(String((solveCall![1] as RequestInit).body));
    expect(body.outlierRejection).toBe('none');
    expect(body.crossCheckMethod).toBe('TSAI');
    expect(body.minTranslationSpan).toBe(0);
    expect(screen.getByText(/交叉验证 Δ平移/)).toBeTruthy();
    expect(screen.getByText(/已剔除 \[3\]/)).toBeTruthy();
    expect(screen.getByText('已剔除')).toBeTruthy();
  });

  it('persists a session and lists it for reload', async () => {
    stubBrowserApis();
    let saved = false;
    const fetchMock = stubApi();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye3d/sessions' && init?.method === 'POST') { saved = true; return json({ sessionId: 's1', name: '测试会话', samplesJson: '{}' }); }
      if (url === '/api/calibration/wizard/handeye3d/sessions') return json(saved ? [{ sessionId: 's1', name: '测试会话', updatedAt: '2026-11-12T00:00:00Z', mode: 'eyeInHand', method: 'DANIILIDIS', sampleCount: 6, hasSolve: false }] : []);
      if (url === '/api/hand-eye3d-calibrations') return json([]);
      return json({});
    });
    const { container } = render(<CalibrationHandEye3DWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    const file = new File([JSON.stringify({ samples: makeSamples(6).map((s) => ({ robotToBase: JSON.parse(s.robotToBase), targetToCamera: JSON.parse(s.targetToCamera) })) })], 's.json', { type: 'application/json' });
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 6 组位姿对/);
    fireEvent.click(screen.getByRole('button', { name: /保存会话/ }));
    await screen.findByText(/会话已保存/);
    expect(await screen.findByText('测试会话')).toBeTruthy();
  });

  it('saves, publishes and reloads a versioned 3D asset', async () => {
    stubBrowserApis();
    const samplesJson = JSON.stringify({ samples: makeSamples(6).map((s) => ({ robotToBase: JSON.parse(s.robotToBase), targetToCamera: JSON.parse(s.targetToCamera) })) });
    const sessionDocument = {
      formatVersion: 'handeye3d-wizard-v1', sessionId: 's1', name: '资产会话', updatedAt: '2026-11-12T00:00:00Z',
      samplesJson, mode: 'eyeInHand', method: 'DANIILIDIS', rotationWeight: 50, outlierRejection: 'none', crossCheckMethod: 'TSAI',
      maxTranslationError: 0, maxRotationErrorDeg: 0, minTranslationSpan: 0, sourceFrame: 'Camera', targetFrame: '', unit: 'mm', lastSolveSummaryJson: null
    };
    const descriptor = {
      id: 'he3d1', name: '资产一', description: '', createdAt: '', updatedAt: '', latestVersion: 1, publishedVersion: null,
      versions: [{ version: 1, snapshotHash: '', modelHash: '', createdAt: '', note: '', published: false }], publicationHistory: []
    };
    let assetCreated = false;
    const fetchMock = stubApi();
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/calibration/wizard/handeye3d/solve') return json(solveResponse);
      if (url === '/api/calibration/wizard/handeye3d/sessions' && init?.method === 'POST') return json(sessionDocument);
      if (url === '/api/calibration/wizard/handeye3d/sessions') return json([]);
      if (url === '/api/hand-eye3d-calibrations' && init?.method === 'POST') { assetCreated = true; return json(descriptor); }
      if (url === '/api/hand-eye3d-calibrations') return json(assetCreated ? [descriptor] : []);
      if (url === '/api/hand-eye3d-calibrations/he3d1/versions/1/publish') return json({ ...descriptor, publishedVersion: 1 });
      if (url === '/api/hand-eye3d-calibrations/he3d1/versions/1') return json({ assetId: 'he3d1', version: 1, snapshotHash: '', modelHash: '', createdAt: '', note: '', model: {}, sourceDocumentJson: JSON.stringify(sessionDocument), summaryJson: '{}' });
      return json({});
    });
    const { container } = render(<CalibrationHandEye3DWizard canApplyToSelectedNode={false} onApplyToSelectedNode={() => undefined} />);
    await goToSamples();
    const file = new File([samplesJson], 's.json', { type: 'application/json' });
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    fireEvent.change(input);
    await screen.findByText(/已导入 6 组位姿对/);
    fireEvent.click(screen.getByRole('button', { name: /求解三维手眼/ }));
    await screen.findByRole('button', { name: /剔除综合残差最大样本/ });

    fireEvent.change(screen.getByPlaceholderText('如 handeye3d-cell1'), { target: { value: 'he3d1' } });
    fireEvent.change(screen.getByPlaceholderText('资产名称'), { target: { value: '资产一' } });
    fireEvent.click(screen.getByRole('button', { name: /保存为版本化资产/ }));
    await screen.findByText(/已保存三维手眼资产 he3d1 V1/);
    expect(await screen.findByText('已锁定 he3d1 V1')).toBeTruthy();

    fireEvent.click(await screen.findByRole('button', { name: /发\s*布/ }));
    await screen.findByText(/已发布三维手眼资产 he3d1 V1/);

    fireEvent.click(screen.getByRole('button', { name: /加\s*载/ }));
    await screen.findByText(/已加载三维手眼资产 he3d1 V1 的样本与设置/);
  });
});