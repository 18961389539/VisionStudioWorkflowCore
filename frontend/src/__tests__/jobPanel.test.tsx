import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import JobPanel from '../components/JobPanel';
import type { JobDescriptor, WorkflowPayload } from '../types';

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

function mount(initialJobs = [recipe('b-main', 'B 检测')], delayedRecipe?: Promise<unknown>) {
  let jobs = [...initialJobs];
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    let data: unknown = {};
    if (url === '/api/jobs') data = jobs;
    else if (url === '/api/recipes' && init?.method === 'POST') {
      const body = JSON.parse(String(init.body));
      data = recipe(body.id ?? 'generated-recipe', body.name);
      jobs = [...jobs, data as JobDescriptor];
    } else if (url.endsWith('/parameterization')) data = { parameterBindings: {} };
    else if (url === '/api/jobs/b-main/parameters' && delayedRecipe) data = await delayedRecipe;
    else if (url.endsWith('/parameters')) data = { values: { sourceRecipe: url.split('/')[3] } };
    else if (url.endsWith('/validation-candidates')) data = [];
    return { ok: true, json: async () => data } as Response;
  });
  vi.stubGlobal('fetch', fetchMock);
  render(<ConfigProvider theme={{ token: { motion: false } }}><JobPanel open onClose={vi.fn()} workflow={workflow} onLoadWorkflow={vi.fn()} onRunResult={vi.fn()} /></ConfigProvider>);
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

  it('filters navigation without changing the selected recipe', async () => {
    mount();
    await screen.findByRole('button', { name: '去验证' });
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索配方' }), { target: { value: '不存在xyz' } });
    expect(screen.getByText('没有匹配的配方')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '去验证' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '清空筛选' }));
    expect(screen.queryByText('没有匹配的配方')).toBeNull();
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
});
