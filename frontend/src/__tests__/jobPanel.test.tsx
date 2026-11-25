import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import JobPanel from '../components/JobPanel';
import type { JobDescriptor, NodeCatalogItem, WorkflowPayload } from '../types';
import type { PrepareWorkflowChange } from '../useWorkflowChanges';

const workflow: WorkflowPayload = { id: 'designer', name: '当前检测流程', nodes: [{ id: 'source', type: 'image.synthetic', parameters: {} }], edges: [] };
const time = '2026-10-15T12:00:00Z';
const recipe = (id: string, name: string): JobDescriptor => ({
  id, name, recipeCode: 'MAIN', description: '', createdAt: time, updatedAt: time,
  latestVersion: 1, publishedVersion: null, publicationHistory: [],
  versions: [{ version: 1, workflowHash: id, createdAt: time, note: '', published: false }]
});

beforeEach(() => {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(initialJobs = [recipe('b-main', 'B 检测')], delayedRecipe?: Promise<unknown>,
  prepare: PrepareWorkflowChange = () => ({ apply: vi.fn(async () => true), cancel: vi.fn(), isCurrent: () => true, isSameDocument: () => true }),
  onOpenValidation?: (candidate?: WorkflowPayload, target?: { jobId: string; version: number }) => void,
  catalog: NodeCatalogItem[] = []) {
  let jobs = [...initialJobs];
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    let data: unknown = {};
    if (url === '/api/jobs') data = jobs;
    else if (url === '/api/recipes' && init?.method === 'POST') {
      const body = JSON.parse(String(init.body));
      data = recipe(body.id ?? 'generated-recipe', body.name);
      jobs = [...jobs, data as JobDescriptor];
    } else if (init?.method === 'POST' && url.includes('/publish?')) {
      const parts = url.split('/');
      jobs = jobs.map((job) => job.id === parts[3] ? { ...job, publishedVersion: Number(parts[5]),
        publishedDependencyManifestHash: 'manifest-1', updatedAt: '2026-10-20T12:00:00Z',
        versions: job.versions.map((version) => ({ ...version, published: version.version === Number(parts[5]) })) } : job);
      data = jobs.find((job) => job.id === parts[3]);
    } else if (url === '/api/jobs/b-main/versions/1/diff/2') data = {
      jobId: 'b-main', fromVersion: 1, toVersion: 2, fromHash: 'before', toHash: 'after',
      changes: [{ kind: 'Parameter', path: 'nodes/detect/parameters/threshold', before: '0.4', after: '0.7' }]
    };
    else if (url === '/api/jobs/b-main/versions/1' && jobs[0]?.latestVersion === 1)
      data = { jobId: 'b-main', version: 1, workflow: { ...workflow, id: 'b-main-effective' } };
    else if (/^\/api\/jobs\/b-main\/versions\/[12]$/.test(url)) {
      const version = Number(url.split('/').pop());
      data = { jobId: 'b-main', version, workflow: { ...workflow, nodes: [{
        id: 'detect', name: '检测节点', type: 'image.threshold', parameters: { threshold: version === 1 ? 0.4 : 0.7 }
      }] } };
    } else if (/^\/api\/jobs\/[^/]+$/.test(url)) data = jobs.find((job) => job.id === url.split('/')[3]);
    else if (url.endsWith('/parameterization')) data = { parameterBindings: {}, baseWorkflow: { ...workflow, id: 'recipe-base' } };
    else if (url === '/api/jobs/b-main/parameters' && delayedRecipe) data = await delayedRecipe;
    else if (url.endsWith('/parameters')) data = { values: { sourceRecipe: url.split('/')[3] } };
    else if (url.endsWith('/validation-candidates')) data = [];
    return { ok: true, json: async () => data } as Response;
  });
  vi.stubGlobal('fetch', fetchMock);
  render(<ConfigProvider theme={{ token: { motion: false } }}><JobPanel open onClose={vi.fn()} workflow={workflow} catalog={catalog} prepareWorkflowChange={prepare} onRunResult={vi.fn()} onOpenValidation={onOpenValidation} /></ConfigProvider>);
  return fetchMock;
}

describe('recipe workbench', () => {
  it('loads independent recipes without fetching products and keeps the validation gate', async () => {
    const fetchMock = mount();
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/jobs/b-main/parameters', expect.anything()));
    expect(fetchMock.mock.calls.some(([url]) => url.includes('/products'))).toBe(false);
    expect(screen.queryByRole('button', { name: '新建产品' })).toBeNull();
    expect(screen.queryByText('旧版作业')).toBeNull();
    expect(screen.queryByText('MAIN')).toBeNull();
    expect(screen.getByRole('button', { name: '去验证' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^发\s*布$/ })).toBeDisabled();
    expect(screen.getByRole('tab', { name: '版本' })).toHaveAttribute('aria-selected', 'true');
  });

  it('creates a recipe directly with the exact current workflow snapshot', async () => {
    const fetchMock = mount([]);
    await waitFor(() => expect(screen.getByRole('button', { name: '刷新列表' })).not.toHaveClass('ant-btn-loading'));
    fireEvent.click(screen.getByRole('button', { name: '新建配方' }));
    const createDialog = within((await screen.findByText('从当前工作流创建配方', { selector: '.ant-modal-title' })).closest('[role=dialog]') as HTMLElement);
    expect(createDialog.getByRole('button', { name: '创建配方' })).toBeEnabled();
    expect(createDialog.queryByRole('combobox', { name: '所属产品' })).toBeNull();
    expect(createDialog.queryByRole('textbox', { name: '配方 ID' })).toBeNull();
    expect(createDialog.queryByRole('textbox', { name: '配方编码' })).toBeNull();
    expect(createDialog.queryByText('高级设置（可选）')).toBeNull();
    fireEvent.change(createDialog.getByRole('textbox', { name: '配方名称' }), { target: { value: '   ' } });
    expect(createDialog.getByRole('button', { name: '创建配方' })).toBeDisabled();
    fireEvent.change(createDialog.getByRole('textbox', { name: '配方名称' }), { target: { value: '标准检测' } });
    fireEvent.click(createDialog.getByRole('button', { name: '创建配方' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url, init]) => url === '/api/recipes' && init?.method === 'POST')).toBe(true));
    const request = fetchMock.mock.calls.find(([url, init]) => url === '/api/recipes' && init?.method === 'POST')!;
    expect(JSON.parse(String(request[1]!.body))).toMatchObject({ name: '标准检测', workflow });
    expect(JSON.parse(String(request[1]!.body))).not.toHaveProperty('productId');
    expect(JSON.parse(String(request[1]!.body))).not.toHaveProperty('id');
    expect(JSON.parse(String(request[1]!.body))).not.toHaveProperty('recipeCode');
    await waitFor(() => expect(screen.getByRole('button', { name: '去验证' })).toBeInTheDocument());
  });

  it('没有匹配的验证记录时，空态给出「打开数据集验证」入口', async () => {
    const onOpenValidation = vi.fn();
    const fetchMock = mount(undefined, undefined, undefined, onOpenValidation);
    await screen.findByRole('button', { name: '去验证' });
    fireEvent.click(screen.getByRole('tab', { name: '验证' }));
    fireEvent.click(screen.getByRole('button', { name: '查询可用验证记录' }));
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/validation-candidates'))).toBe(true));
    // 空表不能只写“没有记录”：告诉用户下一步去哪跑出记录
    fireEvent.click(await screen.findByRole('button', { name: '打开数据集验证' }));
    expect(onOpenValidation).toHaveBeenCalledOnce();
  });

  it('starts validation from the selected immutable recipe workflow snapshot', async () => {
    const onOpenValidation = vi.fn();
    mount(undefined, undefined, undefined, onOpenValidation);
    await screen.findByRole('button', { name: '去验证' });
    fireEvent.click(screen.getByRole('tab', { name: '验证' }));
    fireEvent.click(screen.getByRole('button', { name: '对此版本运行数据集验证' }));

    await waitFor(() => expect(onOpenValidation).toHaveBeenCalledWith(
      { ...workflow, id: 'b-main-effective' }, { jobId: 'b-main', version: 1 }
    ));
  });

  it('renders recipe diffs as localized node and parameter rows', async () => {
    const job = {
      ...recipe('b-main', 'B 检测'),
      latestVersion: 2,
      versions: [
        { version: 1, workflowHash: 'before', createdAt: time, note: '', published: false },
        { version: 2, workflowHash: 'after', createdAt: time, note: '', published: false }
      ]
    };
    const catalog: NodeCatalogItem[] = [{
      type: 'image.threshold', displayName: '阈值节点', category: '图像', description: '',
      inputs: [], outputs: [],
      parameters: [{ name: 'threshold', label: '阈值', type: 'number', defaultValue: 0.5, internal: false }],
      declaresActions: false, declaresFrames: false, declaresTransformFrames: false,
      declaresOutputFrames: false, declaresRoiImageBindings: false
    }];
    mount([job], undefined, undefined, undefined, catalog);
    await screen.findAllByRole('button', { name: '更多 ▾' });
    fireEvent.click(screen.getAllByRole('button', { name: '更多 ▾' })[0]);
    fireEvent.click(await screen.findByText('对比最近两个版本'));

    const row = await screen.findByRole('row', { name: /参数变更.*检测节点.*阈值/ });
    expect(row).toHaveTextContent('0.4');
    expect(row).toHaveTextContent('0.7');
  });

  it('filters navigation without changing the selected recipe', async () => {
    mount();
    await screen.findByRole('button', { name: '去验证' });
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索配方' }), { target: { value: '不存在xyz' } });
    expect(screen.getByText('没有匹配的配方')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '去验证' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '清空筛选' }));
    expect(screen.queryByText('没有匹配的配方')).toBeNull();
  });

  it('recipe loading waits for confirmed application and does not claim success after cancellation', async () => {
    let confirm!: (applied: boolean) => void;
    const apply = vi.fn(() => new Promise<boolean>((resolve) => { confirm = resolve; }));
    const fetchMock = mount(undefined, undefined, () => ({ apply, cancel: vi.fn(), isCurrent: () => true, isSameDocument: () => true }));
    const base = fetchMock.getMockImplementation()!;
    const baseWorkflow = { ...workflow, id: 'b-main-base', name: '基础版本' };
    fetchMock.mockImplementation(async (url, init) => url === '/api/jobs/b-main/versions/1'
      ? { ok: true, status: 200, json: async () => ({ jobId: 'b-main', version: 1, workflow, baseWorkflow }) } as Response : base(url, init));
    fireEvent.click(await screen.findByRole('button', { name: '载入 V1 基础工作流' }, { timeout: 5_000 }));
    await waitFor(() => expect(apply).toHaveBeenCalledWith(baseWorkflow, undefined));
    expect(screen.queryByText(/已加载 b-main V1/)).toBeNull();
    await act(async () => { confirm(false); });
    expect(screen.queryByText(/已加载 b-main V1/)).toBeNull();
    expect(screen.getByRole('button', { name: '载入 V1 基础工作流' })).toBeEnabled();
  });

  it('rejects a mismatched recipe version before passing it to the designer', async () => {
    const apply = vi.fn(async () => true);
    const fetchMock = mount(undefined, undefined, () => ({ apply, cancel: vi.fn(), isCurrent: () => true, isSameDocument: () => true }));
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (url, init) => url === '/api/jobs/b-main/versions/1'
      ? { ok: true, status: 200, json: async () => ({ jobId: 'wrong-recipe', version: 1, workflow }) } as Response : base(url, init));
    fireEvent.click(await screen.findByRole('button', { name: '载入 V1 基础工作流' }, { timeout: 5_000 }));
    await screen.findByText(/配方版本响应与所选版本不匹配/);
    expect(apply).not.toHaveBeenCalled();
  });

  it('unmount cancels recipe loading and ignores its delayed JSON body', async () => {
    let release!: (data: unknown) => void;
    let signal!: AbortSignal;
    const apply = vi.fn(async () => true);
    const cancel = vi.fn();
    const fetchMock = mount(undefined, undefined, () => ({ apply, cancel, isCurrent: () => true, isSameDocument: () => true }));
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (url, init) => {
      if (url === '/api/jobs/b-main/versions/1') {
        signal = init!.signal as AbortSignal;
        return { ok: true, status: 200, json: () => new Promise((resolve) => { release = resolve; }) } as Response;
      }
      return base(url, init);
    });
    fireEvent.click(await screen.findByRole('button', { name: '载入 V1 基础工作流' }, { timeout: 5_000 }));
    await waitFor(() => expect(release).toBeDefined());
    cleanup();
    expect(signal.aborted).toBe(true);
    expect(cancel).toHaveBeenCalled();
    await act(async () => { release({ jobId: 'b-main', version: 1, workflow }); });
    expect(apply).not.toHaveBeenCalled();
  });

  it('does not apply a delayed recipe parameter response after switching recipes', async () => {
    let resolveRecipe!: (data: unknown) => void;
    const delayedRecipe = new Promise((resolve) => { resolveRecipe = resolve; });
    const fetchMock = mount([recipe('b-main', 'B 检测'), recipe('a-main', 'A 检测')], delayedRecipe);
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/jobs/b-main/parameters', expect.anything()));
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索配方' }), { target: { value: 'A 检测' } });
    fireEvent.click(await within(screen.getByRole('menu', { name: '配方列表' })).findByRole('menuitem', { name: /A 检测/ }));
    fireEvent.click(screen.getByRole('tab', { name: '参数' }));
    await screen.findByDisplayValue('a-main');
    expect(fetchMock.mock.calls.find(([url]) => url === '/api/jobs/b-main/parameters')?.[1]?.signal?.aborted).toBe(true);
    await act(async () => resolveRecipe({ values: { sourceRecipe: 'b-main' } }));
    expect(screen.queryByDisplayValue('b-main')).toBeNull();
    expect(screen.getByDisplayValue('a-main')).toBeInTheDocument();
  });

  it('publishes through the reviewed dialog and refreshes the active version only after confirmation', async () => {
    const job = recipe('b-main', 'B 检测');
    job.versions[0].validation = { validationRunId: 'validation', datasetId: 'dataset', accepted: true, linkedAt: time, reason: '' };
    const fetchMock = mount([job]);
    fireEvent.click(await screen.findByRole('button', { name: '发布 V1' }));
    await screen.findByText('首次发布：当前没有生效版本可比较。');
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: '确认发布' }));
    await screen.findByText('当前生效 V1');
    expect(screen.queryByText('发布 B 检测 V1', { selector: '.ant-modal-title' })).toBeNull();
    const write = fetchMock.mock.calls.find(([url, init]) => init?.method === 'POST' && url.includes('/publish?'))!;
    expect(new URL(write[0], 'http://localhost').searchParams.get('expectedUpdatedAt')).toBe(time);
  });

  it('a rejected publication keeps the confirmation visible instead of closing as if it succeeded', async () => {
    const job = recipe('b-main', 'B 检测');
    job.versions[0].validation = { validationRunId: 'validation', datasetId: 'dataset', accepted: true, linkedAt: time, reason: '' };
    const fetchMock = mount([job]);
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (url, init) => init?.method === 'POST' && url.includes('/publish?')
      ? { ok: false, status: 409, json: async () => ({ detail: 'changed after review' }) } as Response : base(url, init));
    fireEvent.click(await screen.findByRole('button', { name: '发布 V1' }));
    await screen.findByText('首次发布：当前没有生效版本可比较。');
    fireEvent.click(screen.getByRole('button', { name: '确认发布' }));
    await screen.findByText(/changed after review/);
    expect(screen.getByText('发布 B 检测 V1', { selector: '.ant-modal-title' }).closest('[role="dialog"]')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '确认发布' })).toBeDisabled();
  });

  it('a list body arriving after close and reopen cannot replace the new list', async () => {
    let releaseOld!: (jobs: JobDescriptor[]) => void;
    let listCalls = 0;
    let oldSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      if (url === '/api/jobs' && ++listCalls === 1) {
        oldSignal = init!.signal as AbortSignal;
        return { ok: true, json: () => new Promise<JobDescriptor[]>((resolve) => { releaseOld = resolve; }) } as Response;
      }
      const data = url === '/api/jobs' ? [recipe('new', '新列表配方')] : url.endsWith('/parameters') ? { values: {} } : { parameterBindings: {} };
      return { ok: true, json: async () => data } as Response;
    }));
    const element = (open: boolean) => <ConfigProvider theme={{ token: { motion: false } }}>
      <JobPanel open={open} onClose={vi.fn()} workflow={workflow} prepareWorkflowChange={() => ({ apply: vi.fn(async () => true), cancel: vi.fn(), isCurrent: () => true, isSameDocument: () => true })} onRunResult={vi.fn()} />
    </ConfigProvider>;
    const view = render(element(true));
    await act(async () => { await Promise.resolve(); });
    view.rerender(element(false));
    view.rerender(element(true));
    await within(screen.getByRole('menu', { name: '配方列表' })).findByRole('menuitem', { name: /新列表配方/ });
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { releaseOld([recipe('old', '旧列表配方')]); });
    expect(screen.queryByRole('menuitem', { name: /旧列表配方/ })).toBeNull();
    expect(screen.getByRole('menuitem', { name: /新列表配方/ })).toBeInTheDocument();
  });
});
