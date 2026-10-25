import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useRef } from 'react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import WorkflowModulePanel from '../components/WorkflowModulePanel';
import ParameterTuningPanel from '../components/ParameterTuningPanel';
import { useWorkflowChanges } from '../useWorkflowChanges';
import { fallbackCatalog } from '../catalog';
import type { WorkflowModuleDescriptor, WorkflowPayload } from '../types';

const workflow: WorkflowPayload = { id: 'designer', name: 'Designer', nodes: [{ id: 'source', name: 'Source', type: 'image.synthetic', parameters: { width: 320, height: 240 } }], edges: [] };
const internal: WorkflowPayload = { ...workflow, id: 'module-workflow', name: 'Module Internal' };
const time = '2026-10-20T10:00:00Z';
const version = { moduleId: 'locate-part', version: 1, moduleHash: 'hash', workflow: internal, inputs: [], outputs: [], parameters: [], createdAt: time, note: '' };
const module: WorkflowModuleDescriptor = { id: 'locate-part', name: '定位模块', description: '', latestVersion: 1, versions: [version], createdAt: time, updatedAt: time };
const response = (data: unknown) => ({ ok: true, status: 200, json: async () => data }) as Response;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
const flush = async () => { await act(async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); }); };

beforeEach(() => {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('fetch', vi.fn(async (url: string) => response(url === '/api/modules' ? [module] : url.startsWith('/api/modules/') ? module : [])));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mount(kind: 'module' | 'tuning' = 'module', dirty = true) {
  const apply = vi.fn(); const warning = vi.fn(); const onApplied = vi.fn();
  function Harness({ current, generation = 1, open = true }: { current: WorkflowPayload; generation?: number; open?: boolean }) {
    const generationRef = useRef(generation); generationRef.current = generation;
    const changes = useWorkflowChanges({ snapshot: JSON.stringify(current), dirty, generationRef, save: async () => true, apply, warning });
    return <ConfigProvider theme={{ token: { motion: false } }}>
      {kind === 'module' ? <WorkflowModulePanel open={open} onClose={vi.fn()} currentWorkflow={current} selectedNodeId="source" selectedNodeType="image.synthetic"
        prepareWorkflowChange={changes.prepare} onInsertModule={vi.fn()} onUpgradeSelected={vi.fn()} />
        : <ParameterTuningPanel open={open} onClose={vi.fn()} currentWorkflow={current} catalog={fallbackCatalog.filter((node) => node.type === 'image.synthetic')}
          prepareWorkflowChange={changes.prepare} onApplied={onApplied} />}
      {changes.pending && <div><button onClick={changes.cancel}>取消流程切换</button><button onClick={changes.discard}>确认丢弃并载入</button></div>}
    </ConfigProvider>;
  }
  const view = render(<Harness current={workflow} />);
  return { ...view, apply, warning, onApplied,
    change: (current = workflow, generation = 1, open = true) => view.rerender(<Harness current={current} generation={generation} open={open} />) };
}

describe('模块编辑与载入', () => {
  it('取消模块载入不设置编辑来源，实际载入之后才允许另存版本', async () => {
    const { apply } = mount();
    const openButton = await screen.findByRole('button', { name: '在设计器中打开模块内部' });
    const saveButton = screen.getByRole('button', { name: '将设计器内容另存为新版本' });
    expect(saveButton).toBeDisabled();
    fireEvent.click(openButton);
    await screen.findByRole('button', { name: '取消流程切换' });
    fireEvent.click(screen.getByRole('button', { name: '取消流程切换' }));
    await flush();
    expect(apply).not.toHaveBeenCalled();
    expect(saveButton).toBeDisabled();
    fireEvent.click(openButton);
    fireEvent.click(await screen.findByRole('button', { name: '确认丢弃并载入' }));
    await waitFor(() => expect(saveButton).toBeEnabled());
    expect(apply).toHaveBeenCalledWith(internal, expect.objectContaining({ mode: 'replace', markClean: undefined }));
  });

  it('模块编辑归属按文档代次绑定，切换其它文档后不能误存为模块版本', async () => {
    const { change } = mount('module', false);
    fireEvent.click(await screen.findByRole('button', { name: '在设计器中打开模块内部' }));
    await waitFor(() => expect(screen.getByRole('button', { name: '将设计器内容另存为新版本' })).toBeEnabled());
    change({ ...workflow, id: 'another-document' }, 2);
    expect(screen.getByRole('button', { name: '将设计器内容另存为新版本' })).toBeDisabled();
  });

  it('提取期间修改源流程时保留新编辑，模块创建与画布替换分别反馈', async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.mocked(fetch);
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((url, init) => String(url) === '/api/modules/extract' ? pending.promise : base(url, init));
    const { apply, change, warning } = mount();
    const extract = await screen.findByRole('button', { name: '提取模块并替换所选节点' });
    await flush();
    // 2026-10-25：替换画布节点属破坏性操作，先经 Popconfirm 确认才发请求。
    act(() => { fireEvent.click(extract); });
    fireEvent.click(await screen.findByRole('button', { name: /^替\s*换$/ }));
    expect(fetchMock.mock.calls.filter(([url]) => url === '/api/modules/extract')).toHaveLength(1);
    change({ ...workflow, name: 'Edited during extraction' });
    await act(async () => { pending.resolve(response({ module, version, replacementWorkflow: workflow })); });
    await screen.findByText(/模块已创建，但设计器未应用替换/);
    expect(apply).not.toHaveBeenCalled();
    expect(warning).toHaveBeenCalledWith(expect.stringContaining('设计器已改变'));
  });

  it('提取成功按可撤销编辑应用，不把父流程标记为已保存', async () => {
    const fetchMock = vi.mocked(fetch);
    const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation(async (url, init) => String(url) === '/api/modules/extract'
      ? response({ module, version, replacementWorkflow: { ...workflow, name: 'Extracted' } }) : base(url, init));
    const { apply } = mount();
    await flush();
    fireEvent.click(await screen.findByRole('button', { name: '提取模块并替换所选节点' }));
    fireEvent.click(await screen.findByRole('button', { name: /^替\s*换$/ }));
    await screen.findByText('已创建 定位模块 V1 并替换所选节点');
    expect(apply).toHaveBeenCalledWith(expect.objectContaining({ id: workflow.id, name: 'Extracted' }), expect.objectContaining({ mode: 'edit', markClean: false, revision: undefined }));
    expect(screen.queryByRole('button', { name: '确认丢弃并载入' })).toBeNull();
  });

  it('关闭模块面板取消在途提取，迟到响应不再替换画布', async () => {
    const pending = deferred<Response>(); let signal!: AbortSignal;
    const fetchMock = vi.mocked(fetch); const base = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((url, init) => {
      if (url === '/api/modules/extract') { signal = init!.signal as AbortSignal; return pending.promise; }
      return base(url, init);
    });
    const { apply, change } = mount();
    await flush();
    fireEvent.click(await screen.findByRole('button', { name: '提取模块并替换所选节点' }));
    fireEvent.click(await screen.findByRole('button', { name: /^替\s*换$/ }));
    change(workflow, 1, false);
    expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(response({ module, version, replacementWorkflow: workflow })); });
    expect(apply).not.toHaveBeenCalled();
  });
});

describe('调优候选归属', () => {
  it('源流程修改后拒绝旧候选，重新从设计器还原后才可应用', async () => {
    const { apply, warning, onApplied, change } = mount('tuning');
    await screen.findByRole('button', { name: '应用到设计器' });
    change({ ...workflow, name: 'New designer edit' });
    fireEvent.click(screen.getByRole('button', { name: '应用到设计器' }));
    await flush();
    expect(apply).not.toHaveBeenCalled();
    expect(onApplied).not.toHaveBeenCalled();
    expect(warning).toHaveBeenCalledWith(expect.stringContaining('设计器已改变'));
    fireEvent.click(screen.getByRole('button', { name: '还原为设计器版本' }));
    fireEvent.click(screen.getByRole('button', { name: '应用到设计器' }));
    await waitFor(() => expect(onApplied).toHaveBeenCalledOnce());
    expect(apply).toHaveBeenCalledWith(expect.objectContaining({ name: 'New designer edit' }), expect.objectContaining({ mode: 'edit', markClean: false }));
  });
});
