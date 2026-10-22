import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useTuningCandidates } from '../useTuningCandidates';
import { deferred, jsonResponse, tuningRun, tuningWorkflow } from './fixtures/tuning';

function mount() {
  const props = { open: true, datasetId: 'A', scopeRevision: 0, workflow: tuningWorkflow, revision: 0, runs: [tuningRun()], runsRevision: 1, runsFresh: true,
    refreshRuns: vi.fn(), onStarted: vi.fn(), onLoad: vi.fn(), success: vi.fn() };
  return { ...renderHook((value) => useTuningCandidates(value), { initialProps: props }), props };
}
const started = (body: BodyInit | null | undefined, runId = 'new') => ({ ...tuningRun('A', runId, 'Running'), sourceWorkflowRunId: JSON.parse(String(body)).sourceWorkflowRunId });
beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => jsonResponse(init?.method === 'POST' ? started(init.body) : tuningWorkflow)));
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('调优验证与候选操作', () => {
  it('验证提交同步防重并固定请求快照，编辑后可以发起下一份候选验证', async () => {
    const pending = deferred<Response>(); let body!: BodyInit;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => { body = init!.body!; return pending.promise; }));
    const { result, props, rerender } = mount();
    act(() => { void result.current.startValidation(); void result.current.startValidation(); });
    expect(fetch).toHaveBeenCalledOnce();
    const changed = { ...tuningWorkflow, name: 'new edit' };
    rerender({ ...props, workflow: changed, revision: 1 });
    await act(async () => { pending.resolve(jsonResponse(started(body))); });
    expect(JSON.parse(String(body)).workflow.name).toBe('Tuning');
    expect(props.onStarted).toHaveBeenCalledOnce();
    expect(result.current.submittedStale).toBe(true);
    expect(result.current.validatingCurrent).toBe(false);
    rerender(props);
    expect(result.current.validatingCurrent).toBe(true);
    expect(await result.current.startValidation()).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();
  });

  it('旧回调读取当前候选，但候选读取不能覆盖等待期间的新参数', async () => {
    const pending = deferred<unknown>(); let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => { signal = init!.signal as AbortSignal; return { ...jsonResponse({}), json: () => pending.promise }; }));
    const { result, props, rerender } = mount();
    await act(async () => { void result.current.loadCandidate('validation-1'); });
    rerender({ ...props, revision: 1, workflow: { ...tuningWorkflow, name: 'new local edit' } });
    expect(signal.aborted).toBe(true);
    await act(async () => { pending.resolve(tuningWorkflow); });
    expect(props.onLoad).not.toHaveBeenCalled();
    expect(result.current.candidateBusy).toBe(false);
  });

  it('候选只从当前数据集的非运行记录载入，并检查文档结构', async () => {
    const { result, props } = mount();
    expect(await result.current.loadCandidate('not-in-current-dataset')).toBe(false);
    expect(fetch).not.toHaveBeenCalled();
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ id: 'broken', nodes: [null], edges: [] })));
    await act(async () => { expect(await result.current.loadCandidate('validation-1')).toBe(false); });
    expect(result.current.errors.candidate).toContain('有效的工作流文档');
    expect(props.onLoad).not.toHaveBeenCalled();
  });

  it('HTTP 明确拒绝保留错误码和关联号，允许修正后再次提交', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ detail: 'topology differs', code: 'topology', correlationId: 'validation-trace' }, 409)));
    const { result } = mount();
    await act(async () => { await result.current.startValidation(); });
    expect(result.current.errors.validation).toMatch(/topology differs.*topology.*validation-trace/);
    expect(result.current.uncertain).toBe(false);
    expect(result.current.validationBusy).toBe(false);
  });

  it.each(['network', 'server-error', 'invalid-response'])('%s 结果未确认时禁止直接重提且不自动重复写入', async (kind) => {
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (kind === 'network') throw new TypeError('offline');
      return jsonResponse(kind === 'server-error' ? { detail: 'server failure' } : tuningRun('B'), kind === 'server-error' ? 503 : 200);
    }));
    const { result, props } = mount();
    await act(async () => { await result.current.startValidation(); });
    expect(result.current.errors.validation).toContain('未确认');
    expect(result.current.uncertain).toBe(true);
    expect(props.refreshRuns).toHaveBeenCalledOnce();
    expect(await result.current.startValidation()).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();
    expect(props.onStarted).not.toHaveBeenCalled();
  });

  it('通过唯一提交标识在记录中找到断网前的任务，可确认成功而不重复创建', async () => {
    let marker!: string;
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => { marker = JSON.parse(String(init!.body)).sourceWorkflowRunId; throw new TypeError('offline'); }));
    const { result, props, rerender } = mount();
    await act(async () => { await result.current.startValidation(); });
    rerender({ ...props, runsRevision: 2, runs: [{ ...tuningRun('A', 'recovered', 'Running'), sourceWorkflowRunId: marker }] });
    expect(result.current.uncertain).toBe(false);
    expect(result.current.submitted?.runId).toBe('recovered');
    expect(result.current.validatingCurrent).toBe(true);
    expect(props.success).toHaveBeenCalledWith(expect.stringContaining('确认先前提交'));
    expect(fetch).toHaveBeenCalledOnce();
  });

  it('没找到任务时必须先成功刷新记录再人工确认，确认本身不会发出写入', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('offline'); }));
    const { result, props, rerender } = mount();
    await act(async () => { await result.current.startValidation(); });
    act(() => result.current.acknowledge());
    expect(result.current.uncertain).toBe(true);
    rerender({ ...props, runsRevision: 2, runsFresh: false });
    expect(result.current.canAcknowledge).toBe(false);
    rerender({ ...props, runsRevision: 2 });
    expect(result.current.canAcknowledge).toBe(true);
    act(() => result.current.acknowledge());
    expect(result.current.uncertain).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();
  });

  it('关闭重开和 A → B → A 都使旧启动响应失效，旧 finally 不能解锁新请求', async () => {
    const old = deferred<Response>(); const next = deferred<Response>(); let count = 0; const bodies: BodyInit[] = []; const signals: AbortSignal[] = [];
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => { bodies.push(init!.body!); signals.push(init!.signal as AbortSignal); return ++count === 1 ? old.promise : next.promise; }));
    const { result, props, rerender } = mount();
    act(() => { void result.current.startValidation(); });
    rerender({ ...props, datasetId: 'B', scopeRevision: 1 }); rerender({ ...props, scopeRevision: 2 });
    act(() => { void result.current.startValidation(); });
    expect(signals[0].aborted).toBe(true);
    await act(async () => { old.resolve(jsonResponse(started(bodies[0], 'old'))); });
    expect(result.current.validationBusy).toBe(true);
    expect(props.onStarted).not.toHaveBeenCalled();
    await act(async () => { next.resolve(jsonResponse(started(bodies[1]))); });
    expect(props.onStarted).toHaveBeenCalledOnce();
  });

  it('验证与候选读取各自占用状态，取消候选不会取消已提交快照的验证', async () => {
    const candidate = deferred<Response>(); const validation = deferred<Response>(); let body!: BodyInit; let validationSignal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      if (init?.method === 'POST') { body = init.body!; validationSignal = init.signal as AbortSignal; return validation.promise; }
      return candidate.promise;
    }));
    const { result, props, rerender } = mount();
    act(() => { void result.current.loadCandidate('validation-1'); void result.current.startValidation(); });
    rerender({ ...props, revision: 1 });
    expect(result.current.candidateBusy).toBe(false);
    expect(result.current.validationBusy).toBe(true);
    expect(validationSignal.aborted).toBe(false);
    await act(async () => { validation.resolve(jsonResponse(started(body))); candidate.resolve(jsonResponse(tuningWorkflow)); });
    expect(props.onLoad).not.toHaveBeenCalled();
    expect(props.onStarted).toHaveBeenCalledOnce();
  });

  it('20 秒启动超时保留未确认状态，卸载后不再接受候选响应', async () => {
    let signal!: AbortSignal;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      signal = init!.signal as AbortSignal; signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    })));
    const { result, unmount } = mount();
    act(() => { void result.current.startValidation(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(result.current.validationBusy).toBe(false);
    expect(result.current.uncertain).toBe(true);
    expect(result.current.errors.validation).toContain('请求超时');
    act(() => { void result.current.loadCandidate('validation-1'); });
    unmount(); expect(signal.aborted).toBe(true);
  });

  it.each(['response', 'body', 'error-body'])('候选 %s 不响应取消时仍解除锁，旧内容不能覆盖重试', async (stage) => {
    const response = deferred<Response>(); const body = deferred<unknown>(); const retry = deferred<Response>();
    let calls = 0; let settled: boolean | undefined;
    vi.stubGlobal('fetch', vi.fn(() => ++calls > 1 ? retry.promise : stage === 'response' ? response.promise
      : Promise.resolve({ ...jsonResponse({}, stage === 'error-body' ? 503 : 200), json: () => body.promise })));
    const { result, props } = mount();
    await act(async () => { void result.current.loadCandidate('validation-1').then((value) => { settled = value; }); });
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(result.current.candidateBusy).toBe(false);
    expect(result.current.errors.candidate).toContain('请求超时');
    expect(settled).toBe(false);

    act(() => { void result.current.loadCandidate('validation-1'); });
    await act(async () => {
      response.resolve(jsonResponse(tuningWorkflow)); body.resolve(stage === 'error-body' ? { detail: 'late error' } : tuningWorkflow);
    });
    expect(result.current.candidateBusy).toBe(true);
    expect(props.onLoad).not.toHaveBeenCalled();
    await act(async () => { retry.resolve(jsonResponse(tuningWorkflow)); });
    expect(result.current.candidateBusy).toBe(false);
    expect(props.onLoad).toHaveBeenCalledExactlyOnceWith(tuningWorkflow);
  });

  it.each(['response', 'body', 'error-body'])('验证 %s 不响应取消时及时标为未确认，不重复提交且能从记录找回', async (stage) => {
    const response = deferred<Response>(); const body = deferred<unknown>();
    let requestBody!: BodyInit; let settled: boolean | undefined;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      requestBody = init!.body!;
      return stage === 'response' ? response.promise
        : Promise.resolve({ ...jsonResponse({}, stage === 'error-body' ? 503 : 200), json: () => body.promise });
    }));
    const { result, props, rerender } = mount();
    await act(async () => { void result.current.startValidation().then((value) => { settled = value; }); });
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(result.current.validationBusy).toBe(false);
    expect(result.current.uncertain).toBe(true);
    expect(result.current.errors.validation).toContain('请求超时');
    expect(props.refreshRuns).toHaveBeenCalledOnce();
    expect(settled).toBe(false);
    expect(await result.current.startValidation()).toBe(false);
    expect(fetch).toHaveBeenCalledOnce();

    const run = started(requestBody, 'recovered-timeout');
    await act(async () => {
      response.resolve(jsonResponse(run)); body.resolve(stage === 'error-body' ? { detail: 'late error' } : run);
    });
    expect(result.current.uncertain).toBe(true);
    expect(props.onStarted).not.toHaveBeenCalled();
    rerender({ ...props, runsRevision: 2, runs: [run] });
    expect(result.current.uncertain).toBe(false);
    expect(result.current.submitted?.runId).toBe('recovered-timeout');
    expect(result.current.validatingCurrent).toBe(true);
    expect(fetch).toHaveBeenCalledOnce();
  });

  it('已收到明确拒绝时，错误正文超时不误报可能创建验证任务', async () => {
    const body = deferred<unknown>();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ...jsonResponse({}, 409), json: () => body.promise })));
    const { result, props } = mount();
    await act(async () => { void result.current.startValidation(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(result.current.validationBusy).toBe(false);
    expect(result.current.errors.validation).toContain('请求超时');
    expect(result.current.uncertain).toBe(false);
    expect(props.refreshRuns).not.toHaveBeenCalled();
    expect(fetch).toHaveBeenCalledOnce();
    await act(async () => { body.resolve({ detail: 'late rejection' }); });
    expect(result.current.errors.validation).toContain('请求超时');
  });
});
