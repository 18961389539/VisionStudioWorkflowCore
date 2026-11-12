import '@testing-library/jest-dom/vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfigProvider } from 'antd';
import RecipePublicationDialog, { type PublicationTarget } from '../components/RecipePublicationDialog';
import type { JobDescriptor, JobVersionDiff } from '../types';

const time = '2026-10-20T10:00:00.1234567+00:00';
const newTime = '2026-10-20T10:01:00.1234567+00:00';
function recipe(publishedVersion: number | null = 3, id = 'A'): JobDescriptor {
  return {
    id, name: `配方 ${id}`, description: '', createdAt: time, updatedAt: time, latestVersion: 3, publishedVersion,
    versions: [1, 2, 3].map((version) => ({ version, workflowHash: `hash-${version}`, createdAt: time, note: '', published: version === publishedVersion,
      validation: { validationRunId: 'validation-1', datasetId: 'dataset-1', linkedAt: time, accepted: true, reason: '' } })),
    publicationHistory: [],
  };
}
function diff(id: string, fromVersion: number, toVersion: number): JobVersionDiff {
  return { jobId: id, fromVersion, toVersion, fromHash: 'old', toHash: 'new', changed: true,
    changes: [{ kind: 'Parameter', path: 'source.width', before: '320', after: '640' }] };
}
const target: PublicationTarget = { jobId: 'A', name: '旧名称', version: 1, rollback: true };
const response = (body: unknown, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => body }) as Response;
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}
function handler(job = recipe()) {
  return async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = String(input);
    if (init?.method === 'POST') return response({ ...job, publishedVersion: Number(url.match(/(?:versions|rollback)\/(\d+)/)?.[1]), updatedAt: newTime });
    if (url.includes('/diff/')) {
      const parts = url.split('/');
      return response(diff(job.id, Number(parts[5]), Number(parts[7])));
    }
    return response(job);
  };
}
function mount(nextTarget = target) {
  const onCancel = vi.fn();
  const onPublished = vi.fn();
  const element = (value: PublicationTarget) => <ConfigProvider theme={{ token: { motion: false } }}>
    <RecipePublicationDialog target={value} onCancel={onCancel} onPublished={onPublished} />
  </ConfigProvider>;
  const view = render(element(nextTarget));
  return { ...view, changeTarget: (value: PublicationTarget) => view.rerender(element(value)), onCancel, onPublished };
}
const confirmButton = (rollback = true) => screen.getByRole('button', { name: rollback ? '确认回滚' : '确认发布' });

beforeEach(() => {
  vi.stubGlobal('matchMedia', () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('fetch', vi.fn(handler()));
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('发布与回滚核对', () => {
  it('回滚比较当前生效 V3 与目标 V1，不能比较 V1 与自身', async () => {
    mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    expect(fetch).toHaveBeenCalledWith('/api/jobs/A/versions/3/diff/1', expect.anything());
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/versions/1/diff/1'))).toBe(false);
    expect(screen.getByRole('dialog', { name: '回滚 配方 A V1' })).toBeInTheDocument();
    expect(confirmButton()).toBeEnabled();
  });

  it('发布跳过中间草稿时仍比较实际生效版本', async () => {
    vi.stubGlobal('fetch', vi.fn(handler(recipe(1))));
    mount({ ...target, version: 3, rollback: false });
    await screen.findByText('当前生效 V1 → 目标 V3');
    expect(fetch).toHaveBeenCalledWith('/api/jobs/A/versions/1/diff/3', expect.anything());
    expect(confirmButton(false)).toBeEnabled();
  });

  it('首次发布不伪造与自身的差异', async () => {
    vi.stubGlobal('fetch', vi.fn(handler(recipe(null))));
    mount({ ...target, rollback: false });
    await screen.findByText('首次发布：当前没有生效版本可比较。');
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(confirmButton(false)).toBeEnabled();
  });

  it.each([
    { current: null, version: 1, message: '回滚只能选择比当前生效版本更早的版本。' },
    { current: 1, version: 3, message: '回滚只能选择比当前生效版本更早的版本。' },
    { current: 3, version: 3, message: '目标版本已生效，无需重复发布。' },
  ])('阻止不成立的回滚：%j', async ({ current, version, message }) => {
    vi.stubGlobal('fetch', vi.fn(handler(recipe(current))));
    mount({ ...target, version });
    await screen.findByText(message);
    expect(confirmButton()).toBeDisabled();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('重新核对后发现目标验证未通过，不允许提交', async () => {
    const job = recipe();
    job.versions[0].validation!.accepted = false;
    vi.stubGlobal('fetch', vi.fn(handler(job)));
    mount();
    await screen.findByText('目标版本尚未通过数据集验证，不能发布或回滚。');
    expect(confirmButton()).toBeDisabled();
  });

  it('差异读取失败保持对话框且禁止确认，重新核对成功后恢复', async () => {
    let failing = true;
    const base = handler();
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => failing && String(url).includes('/diff/')
      ? response({ detail: 'diff unavailable', correlationId: 'trace-diff' }, 503) : base(url, init)));
    mount();
    await screen.findByText(/diff unavailable.*trace-diff/);
    expect(confirmButton()).toBeDisabled();
    failing = false;
    fireEvent.click(screen.getByRole('button', { name: '重新核对' }));
    await screen.findByText('当前生效 V3 → 目标 V1');
    expect(confirmButton()).toBeEnabled();
  });

  it('不接受其它配方或其它版本的差异作为发布核对', async () => {
    const base = handler();
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => String(url).includes('/diff/')
      ? response(diff('A', 1, 1)) : base(url, init)));
    mount();
    await screen.findByText('版本差异与发布对象不匹配，请重新核对。');
    expect(confirmButton()).toBeDisabled();
  });
});

describe('发布提交结果', () => {
  it('同事件内重复确认只提交一次，保留原始修订精度且提交中禁止关闭', async () => {
    const pending = deferred<Response>();
    const base = handler();
    const fetchMock = vi.fn((url: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST' ? pending.promise : base(url, init));
    vi.stubGlobal('fetch', fetchMock);
    const { onCancel, onPublished } = mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    const button = confirmButton();
    act(() => { fireEvent.click(button); fireEvent.click(button); });
    const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');
    expect(writes).toHaveLength(1);
    expect(new URL(String(writes[0][0]), 'http://localhost').searchParams.get('expectedUpdatedAt')).toBe(time);
    expect(String(writes[0][0])).toContain('/rollback/1?');
    expect(screen.getByRole('button', { name: /^取\s*消$/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: '重新核对' })).toBeDisabled();
    expect(onCancel).not.toHaveBeenCalled();
    await act(async () => { pending.resolve(response({ ...recipe(), publishedVersion: 1, updatedAt: newTime })); });
    expect(onPublished).toHaveBeenCalledOnce();
  });

  it('后端拒绝发布时不关闭确认框，展示具体失败原因', async () => {
    const base = handler();
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST'
      ? response({ detail: 'runtime drift', code: 'dependency_drift', correlationId: 'trace-publish' }, 409) : base(url, init)));
    const { onCancel, onPublished } = mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton());
    await screen.findByText(/runtime drift.*trace-publish/);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(onPublished).not.toHaveBeenCalled();
    expect(onCancel).not.toHaveBeenCalled();
    expect(confirmButton()).toBeDisabled();
  });

  it('冲突后必须重新读取修订和当前生效版本才能再次确认', async () => {
    let job = recipe();
    const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') return response({ detail: 'publication review changed' }, 409);
      return handler(job)(url, init);
    });
    vi.stubGlobal('fetch', fetchMock);
    mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton());
    await screen.findByText(/publication review changed/);
    expect(confirmButton()).toBeDisabled();
    job = { ...recipe(2), updatedAt: newTime };
    fireEvent.click(screen.getByRole('button', { name: '重新核对' }));
    await screen.findByText('当前生效 V2 → 目标 V1');
    fireEvent.click(confirmButton());
    await waitFor(() => expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(2));
    const lastWrite = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')[1];
    expect(new URL(String(lastWrite[0]), 'http://localhost').searchParams.get('expectedUpdatedAt')).toBe(newTime);
  });

  it.each(['network', 'invalid-json'] as const)('%s 结果未确认时禁止直接重复提交', async (kind) => {
    const base = handler();
    const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method !== 'POST') return base(url, init);
      if (kind === 'network') throw new TypeError('offline');
      return { ...response({}), json: async () => { throw new SyntaxError('invalid JSON'); } } as Response;
    });
    vi.stubGlobal('fetch', fetchMock);
    const { onPublished } = mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton());
    await screen.findByText(/结果未确认，后端可能已经提交/);
    expect(confirmButton()).toBeDisabled();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
    expect(onPublished).not.toHaveBeenCalled();
  });
});

describe('发布核对生命周期', () => {
  it('目标切换后忽略旧正文并取消旧读取', async () => {
    const oldBody = deferred<JobDescriptor>();
    let oldSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      if (String(url) === '/api/jobs/A') { oldSignal = init!.signal as AbortSignal; return { ...response({}), json: () => oldBody.promise } as Response; }
      return handler(recipe(3, 'B'))(url, init);
    }));
    const { changeTarget } = mount();
    await act(async () => { await Promise.resolve(); });
    changeTarget({ ...target, jobId: 'B', name: 'B' });
    await screen.findByRole('dialog', { name: '回滚 配方 B V1' });
    expect(oldSignal.aborted).toBe(true);
    await act(async () => { oldBody.resolve(recipe()); });
    expect(screen.getByRole('dialog', { name: '回滚 配方 B V1' })).toBeInTheDocument();
  });

  it('卸载后到达的提交响应不会执行成功回调', async () => {
    const pending = deferred<Response>();
    const base = handler();
    vi.stubGlobal('fetch', vi.fn((url: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST' ? pending.promise : base(url, init)));
    const { unmount, onPublished } = mount();
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton());
    unmount();
    await act(async () => { pending.resolve(response({ ...recipe(), publishedVersion: 1, updatedAt: newTime })); });
    expect(onPublished).not.toHaveBeenCalled();
  });

  it('发布门禁：未成熟节点要求勾选确认，确认后带 acknowledge 重新提交', async () => {
    const finding = { nodeId: 'detect', nodeType: 'inference.obb', nodeName: 'OBB 检测', maturity: 'Experimental', reason: '无模型包可跑端到端' };
    const calls: string[] = [];
    const base = handler();
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        calls.push(String(url));
        // 第一次不带确认 → 门禁 409；带确认 → 正常发布。
        if (String(url).includes('acknowledgeMaturityNodes=true')) return response({ ...recipe(), publishedVersion: 1, updatedAt: newTime });
        return response({ code: 'NODE_MATURITY_ACK_REQUIRED', message: '该版本包含 1 个未达到 Stable 的节点', findings: [finding] }, 409);
      }
      return base(url, init);
    }));
    mount({ ...target, rollback: false });
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton(false));
    // 门禁不是"条件已变化"：必须列出节点、给出理由，而不是让用户去重新核对。
    expect(await screen.findByText('OBB 检测')).toBeInTheDocument();
    expect(screen.getByText(/无模型包可跑端到端/)).toBeInTheDocument();
    expect(confirmButton(false)).toBeDisabled();
    expect(screen.queryByText(/配方或发布条件已变化/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('checkbox'));
    expect(confirmButton(false)).toBeEnabled();
    fireEvent.click(confirmButton(false));
    await waitFor(() => expect(calls.at(-1)).toContain('acknowledgeMaturityNodes=true'));
  });

  it('发布门禁：确认过的版本再次提交时自动带上 acknowledge', async () => {
    // 反向守卫：普通 409（并发修订冲突）不能被误判成门禁，否则会跳过真正该拦的检查。
    const base = handler();
    vi.stubGlobal('fetch', vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') return response({ detail: '版本已被其他人更新', code: 'revision_mismatch' }, 409);
      return base(url, init);
    }));
    mount({ ...target, rollback: false });
    await screen.findByText('当前生效 V3 → 目标 V1');
    fireEvent.click(confirmButton(false));
    expect(await screen.findByText(/配方或发布条件已变化/)).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('读取超时给出重试入口，不留下无期限加载状态', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    mount();
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(screen.getByText('发布信息读取超时（20 秒），请重试。')).toBeInTheDocument();
    expect(confirmButton()).toBeDisabled();
    expect(screen.getByRole('button', { name: '重新核对' })).toBeEnabled();
  });
});
