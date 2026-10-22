import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import ParameterTuningPanel from '../components/ParameterTuningPanel';
import { fallbackCatalog } from '../catalog';
import { deferred, jsonResponse, tuningDataset, tuningItem, tuningPreview, tuningRun, tuningWorkflow } from './fixtures/tuning';
import type { NodeCatalogItem, ValidationRunRecord } from '../types';

vi.mock('../components/ImageViewer', () => ({ default: (props: { previewUrl?: string; overlays: unknown[]; roiEnabled: boolean; onRoiChange: (roi: unknown) => void }) =>
  <div data-testid="tuning-viewer" data-preview={props.previewUrl ?? ''} data-overlays={props.overlays.length}>
    <button disabled={!props.roiEnabled} onClick={() => props.onRoiChange({ type: 'rectangle', x: 1, y: 1, width: 10, height: 10 })}>调整 ROI</button>
  </div> }));

type Fetcher = (url: string, init?: RequestInit) => Promise<Response>;
const flush = async () => { await act(async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); }); };
const tick = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
const width = () => screen.getByRole('spinbutton', { name: '宽度' });
const editWidth = async (value: number) => { fireEvent.change(width(), { target: { value: String(value) } }); await flush(); };
const runNode = () => screen.getByRole('button', { name: '运行当前节点' });
const validate = () => screen.getByRole('button', { name: '验证数据集' });
function mount(override?: (base: Fetcher) => Fetcher) {
  const records = new Map<string, ValidationRunRecord[]>([['A', [tuningRun()]], ['B', [tuningRun('B', 'B-history')]]]);
  let previewId = 0; let validationId = 0;
  const base: Fetcher = async (url, init) => {
    if (url === '/api/tuning/preview') {
      const body = JSON.parse(String(init!.body)); return jsonResponse(tuningPreview(body.fullWorkflow, tuningItem(body.datasetId, body.itemId), `preview-${++previewId}`));
    }
    if (url.endsWith('/runs')) {
      const id = decodeURIComponent(url.split('/')[4]);
      if (init?.method === 'POST') {
        const value = { ...tuningRun(id, `created-${++validationId}`, 'Running'), sourceWorkflowRunId: JSON.parse(String(init.body)).sourceWorkflowRunId };
        records.set(id, [value, ...(records.get(id) ?? [])]); return jsonResponse(value);
      }
      return jsonResponse(records.get(id) ?? []);
    }
    if (url.endsWith('/workflow')) return jsonResponse(tuningWorkflow);
    return jsonResponse(url === '/api/validation/datasets' ? [tuningDataset().dataset, tuningDataset('B').dataset]
      : tuningDataset(decodeURIComponent(url.split('/').at(-1)!)));
  };
  const fetchMock = vi.fn(override ? override(base) : base); vi.stubGlobal('fetch', fetchMock);
  const source = fallbackCatalog.find((node) => node.type === 'image.synthetic')!;
  const catalog: NodeCatalogItem[] = [{ ...source, capabilities: { ...source.capabilities, supportsRoi: true }, parameters: [
    { name: 'width', label: '宽度', type: 'number', defaultValue: 320, min: 64, max: 4096 },
  ] }];
  return { ...render(<ConfigProvider theme={{ token: { motion: false } }}><ParameterTuningPanel open onClose={vi.fn()} currentWorkflow={tuningWorkflow}
    catalog={catalog} prepareWorkflowChange={() => ({ apply: async () => true, cancel() {}, isCurrent: () => true, isSameDocument: () => true })} onApplied={vi.fn()} /></ConfigProvider>), fetchMock };
}
beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('参数调优工作区', () => {
  it('预览正文未结束也按期解除加载、显示原因，并允许手动重试', async () => {
    const body = deferred<unknown>(); let requests = 0;
    mount((base) => (url, init) => url === '/api/tuning/preview' && ++requests === 1
      ? Promise.resolve({ ...jsonResponse({}), json: () => body.promise }) : base(url, init));
    await flush();
    const runButton = runNode();
    fireEvent.click(runButton); await flush();
    expect(runButton).toHaveClass('ant-btn-loading');
    await tick(60_000);
    expect(screen.getByText('离线预览失败')).toBeInTheDocument();
    expect(screen.getByText(/离线预览超时（60 秒）/)).toBeInTheDocument();
    expect(runButton).not.toHaveClass('ant-btn-loading');
    expect(runButton).toBeEnabled();
    fireEvent.click(runNode()); await flush();
    expect(screen.getByText('当前节点离线预览')).toBeInTheDocument();
    await act(async () => { body.resolve(tuningPreview(false, tuningItem(), 'late-preview')); });
    expect(screen.getByTestId('tuning-viewer')).toHaveAttribute('data-preview', '/api/runs/preview-1/preview');
    expect(screen.queryByText('离线预览失败')).toBeNull();
  });

  it('验证请求悬挂超时后退出加载，但保持未确认提示与重复提交保护', async () => {
    const pending = deferred<Response>(); let savedBody!: string;
    const { fetchMock } = mount((base) => (url, init) => {
      if (init?.method === 'POST' && url.endsWith('/runs')) { savedBody = String(init.body); return pending.promise; }
      return base(url, init);
    });
    await flush();
    const validationButton = validate();
    fireEvent.click(validationButton); await flush();
    expect(validationButton).toHaveClass('ant-btn-loading');
    await tick(20_000);
    expect(validationButton).not.toHaveClass('ant-btn-loading');
    expect(validationButton).toBeDisabled();
    expect(screen.getByText('验证启动未完成或结果未确认')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '刷新验证记录' })).toBeEnabled();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST' && JSON.parse(String(init.body)).sourceWorkflowRunId)).toHaveLength(1);
    await act(async () => { pending.resolve(jsonResponse({ ...tuningRun('A', 'late-start', 'Running'),
      sourceWorkflowRunId: JSON.parse(savedBody).sourceWorkflowRunId })); });
    expect(validate()).toBeDisabled();
    expect(screen.getByText('验证启动未完成或结果未确认')).toBeInTheDocument();
  });

  it('参数或 ROI 编辑清除旧指标，并明确标出继续编辑用的旧图像背景', async () => {
    const { fetchMock } = mount(); await flush();
    fireEvent.click(runNode()); await flush();
    expect(screen.getByText('当前节点离线预览')).toBeInTheDocument();
    expect(screen.getByTestId('tuning-viewer')).toHaveAttribute('data-preview', '/api/runs/preview-1/preview');
    await editWidth(640);
    expect(screen.queryByText('当前节点离线预览')).toBeNull();
    expect(screen.getByText('背景为上次预览，当前参数的结果尚未更新')).toBeInTheDocument();
    expect(screen.getByTestId('tuning-viewer')).toHaveAttribute('data-overlays', '0');
    fireEvent.click(screen.getByRole('button', { name: '运行完整工作流' })); await flush();
    expect(screen.getByText('完整流程离线预览')).toBeInTheDocument();
    const writes = fetchMock.mock.calls.filter(([url]) => url === '/api/tuning/preview');
    expect(JSON.parse(String(writes[1][1]!.body))).toMatchObject({ fullWorkflow: true, workflow: { nodes: [{ parameters: { width: 640 } }] } });
    fireEvent.click(screen.getByRole('button', { name: '调整 ROI' })); await flush();
    expect(screen.queryByText('完整流程离线预览')).toBeNull();
    expect(screen.getByText('背景为上次预览，当前参数的结果尚未更新')).toBeInTheDocument();
  });

  it('切换数据集立即清除旧样本与图像，迟到 A 预览不能回到 B 界面', async () => {
    const oldPreview = deferred<Response>(); const detailB = deferred<Response>(); let signal!: AbortSignal;
    mount((base) => (url, init) => {
      if (url === '/api/tuning/preview') { signal = init!.signal as AbortSignal; return oldPreview.promise; }
      return url.endsWith('/datasets/B') ? detailB.promise : base(url, init);
    });
    await flush(); fireEvent.click(runNode()); await flush();
    fireEvent.mouseDown(screen.getByRole('combobox', { name: '调优数据集' })); await flush();
    fireEvent.click(screen.getByText('Dataset B (1)', { selector: '.ant-select-item-option-content' })); await flush();
    expect(signal.aborted).toBe(true);
    expect(screen.getByRole('combobox', { name: '调优样本' })).toBeDisabled();
    await act(async () => { oldPreview.resolve(jsonResponse(tuningPreview())); });
    expect(screen.getByTestId('tuning-viewer')).toHaveAttribute('data-preview', '');
    await act(async () => { detailB.resolve(jsonResponse(tuningDataset('B'))); });
    await flush();
    expect(screen.getByText('OK · B-sample · TRACE')).toBeInTheDocument();
    expect(runNode()).toBeEnabled();
  });

  it('验证绑定提交时的参数，期间继续编辑会显示尚未验证的快照差异', async () => {
    const pending = deferred<Response>(); let savedBody!: string;
    const { fetchMock } = mount((base) => (url, init) => {
      if (init?.method === 'POST' && url.endsWith('/runs')) { savedBody = String(init.body); return pending.promise; }
      return base(url, init);
    });
    await flush();
    act(() => { fireEvent.click(validate()); fireEvent.click(validate()); });
    await editWidth(640);
    await act(async () => { pending.resolve(jsonResponse({ ...tuningRun('A', 'created', 'Running'), sourceWorkflowRunId: JSON.parse(savedBody).sourceWorkflowRunId })); });
    await flush();
    expect(JSON.parse(savedBody).workflow.nodes[0].parameters.width).toBe(320);
    expect(screen.getByText('当前参数与已提交验证的快照不同')).toBeInTheDocument();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST' && JSON.parse(String(init.body)).sourceWorkflowRunId)).toHaveLength(1);
    expect(validate()).toBeEnabled();
  });

  it('启动断网时禁止直接重提，读取记录后允许人工核对，核对按钮不发写入', async () => {
    const { fetchMock } = mount((base) => async (url, init) => {
      if (init?.method === 'POST' && url.endsWith('/runs')) throw new TypeError('offline');
      return base(url, init);
    });
    await flush(); fireEvent.click(validate()); await flush();
    expect(screen.getByText('验证启动未完成或结果未确认')).toBeInTheDocument();
    expect(validate()).toBeDisabled();
    const acknowledge = screen.getByRole('button', { name: '已核对记录，允许重新提交' });
    expect(acknowledge).toBeEnabled();
    fireEvent.click(acknowledge); await flush();
    expect(validate()).toBeEnabled();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
  });

  it('候选读取期间继续编辑，响应不会覆盖输入或报载入成功', async () => {
    const pending = deferred<Response>();
    mount((base) => (url, init) => url.endsWith('/workflow') ? pending.promise : base(url, init));
    await flush(); fireEvent.click(screen.getByRole('button', { name: /^加\s*载$/ }));
    await editWidth(640);
    await act(async () => { pending.resolve(jsonResponse(tuningWorkflow)); });
    expect(width()).toHaveValue('640');
    expect(screen.queryByText('验证候选项已载入调优工作区')).toBeNull();
  });

  it('自动预览失败在界面保留具体原因，手动成功后清除错误', async () => {
    let broken = true;
    mount((base) => async (url, init) => url === '/api/tuning/preview' && broken
      ? jsonResponse({ detail: 'preview offline', correlationId: 'preview-trace' }, 503) : base(url, init));
    await flush(); await tick(550);
    expect(screen.getByText('离线预览失败')).toBeInTheDocument();
    expect(screen.getByText(/preview offline.*preview-trace/)).toBeInTheDocument();
    broken = false; fireEvent.click(runNode()); await flush();
    expect(screen.queryByText('离线预览失败')).toBeNull();
    expect(screen.getByText('当前节点离线预览')).toBeInTheDocument();
  });
});
