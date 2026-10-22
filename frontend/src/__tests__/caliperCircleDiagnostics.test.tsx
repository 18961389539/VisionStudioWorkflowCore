import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useCaliperCircleDiagnostics } from '../caliperCircleDiagnostics';
import { deferredResponse, diagnosticManifest, diagnosticProfile, diagnosticReport, jsonResponse } from './fixtures/caliperDiagnostics';

const props = { enabled: true, runId: 'run-a', nodeId: 'circle', report: diagnosticReport() };
type Props = Parameters<typeof useCaliperCircleDiagnostics>[0];
const urlIndex = (url: string) => Number(new URL(url, 'http://local.test').searchParams.get('spokeIndex'));
function standardFetch(input: RequestInfo | URL) {
  const url = String(input);
  return Promise.resolve(jsonResponse(url.includes('?') ? diagnosticProfile(urlIndex(url)) : diagnosticManifest()));
}

afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('single-caliper diagnostic requests', () => {
  it.each([
    { ...props, enabled: false }, { ...props, runId: undefined }, { ...props, report: undefined },
    { ...props, report: { ...diagnosticReport(), success: false } },
    { ...props, report: { ...diagnosticReport(), summary: {} } }
  ])('does not read or execute anything without an enabled captured run (%j)', input => {
    const fetch = vi.fn(standardFetch); vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useCaliperCircleDiagnostics(input));
    expect(fetch).not.toHaveBeenCalled(); expect(result.current.loading).toBe(false);
    expect(result.current.profile).toBeUndefined();
  });

  it('switches spokes using GET only and reuses recently read profiles', async () => {
    const fetch = vi.fn(standardFetch); vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(result.current.profile?.index).toBe(0));
    act(() => result.current.select(1));
    await waitFor(() => expect(result.current.profile?.index).toBe(1));
    act(() => result.current.select(0));
    await waitFor(() => expect(result.current.profile?.index).toBe(0));
    expect(fetch.mock.calls.map(call => String(call[0]))).toEqual([
      '/api/runs/run-a/nodes/circle/caliper-diagnostics',
      '/api/runs/run-a/nodes/circle/caliper-diagnostics?spokeIndex=0',
      '/api/runs/run-a/nodes/circle/caliper-diagnostics?spokeIndex=1'
    ]);
    for (const call of vi.mocked(globalThis.fetch).mock.calls) expect(call[1]?.method ?? 'GET').toBe('GET');
  });

  it.each(['resolve', 'reject'] as const)('ignores a late profile %s after quickly selecting another spoke', async outcome => {
    const pending = deferredResponse(); let obsoleteSignal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, options?: RequestInit) => {
      if (String(input).endsWith('?spokeIndex=0')) { obsoleteSignal = options?.signal as AbortSignal; return pending.promise; }
      return standardFetch(input);
    }));
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(obsoleteSignal).toBeDefined());
    act(() => result.current.select(1));
    await waitFor(() => expect(result.current.profile?.index).toBe(1));
    expect(obsoleteSignal?.aborted).toBe(true);
    await act(async () => {
      if (outcome === 'resolve') pending.resolve(jsonResponse(diagnosticProfile(0)));
      else pending.reject(new Error('late old failure'));
    });
    expect(result.current.profile?.index).toBe(1);
    expect(result.current.error).toBeUndefined();
  });

  it('rejects an old manifest after a new run becomes current', async () => {
    const pending = deferredResponse();
    const fetch = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/run-a/')) return pending.promise;
      return Promise.resolve(jsonResponse(url.includes('?') ? diagnosticProfile(urlIndex(url), 'run-b', 8) : diagnosticManifest('run-b', 8)));
    });
    vi.stubGlobal('fetch', fetch);
    const { result, rerender } = renderHook((input: Props) => useCaliperCircleDiagnostics(input), { initialProps: props });
    rerender({ ...props, runId: 'run-b', report: diagnosticReport(8) });
    await waitFor(() => expect(result.current.profile?.runId).toBe('run-b'));
    await act(async () => pending.resolve(jsonResponse(diagnosticManifest())));
    expect(result.current.manifest?.runId).toBe('run-b');
    expect(fetch.mock.calls.filter(call => String(call[0]).includes('/run-a/') && String(call[0]).includes('?'))).toHaveLength(0);
  });

  it('clears the prior run immediately and ignores its in-flight profile', async () => {
    const pending = deferredResponse(); let started = false;
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/run-a/') && url.includes('?')) { started = true; return pending.promise; }
      const second = url.includes('/run-b/');
      return Promise.resolve(jsonResponse(url.includes('?') ? diagnosticProfile(0, 'run-b', 8) : diagnosticManifest(second ? 'run-b' : 'run-a', second ? 8 : 7)));
    }));
    const { result, rerender } = renderHook((input: Props) => useCaliperCircleDiagnostics(input), { initialProps: props });
    await waitFor(() => expect(started).toBe(true));
    rerender({ ...props, runId: 'run-b', report: diagnosticReport(8) });
    expect(result.current.profile).toBeUndefined();
    await waitFor(() => expect(result.current.profile?.runId).toBe('run-b'));
    await act(async () => pending.resolve(jsonResponse(diagnosticProfile())));
    expect(result.current.profile?.runId).toBe('run-b');
  });

  it.each([{ runId: 'wrong' }, { nodeId: 'other' }, { executionSequence: 6 }, { index: 1 }])('rejects a mismatched profile %j', async patch => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => jsonResponse(String(input).includes('?')
      ? { ...diagnosticProfile(), ...patch } : diagnosticManifest())));
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(result.current.error).toContain('不一致'));
    expect(result.current.profile).toBeUndefined(); expect(result.current.loading).toBe(false);
  });

  it('rejects metadata from a different execution of the same node/run', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(diagnosticManifest('run-a', 6))));
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(result.current.error).toContain('不一致'));
    expect(result.current.manifest).toBeUndefined();
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it('reports expired capture and can retry reads without executing a node', async () => {
    let expired = true;
    const fetch = vi.fn((input: RequestInfo | URL) => expired ? Promise.resolve(jsonResponse({}, 404)) : standardFetch(input));
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(result.current.error).toContain('已过期'));
    expect(result.current.loading).toBe(false); expect(result.current.profile).toBeUndefined();
    expired = false; act(() => result.current.retry());
    await waitFor(() => expect(result.current.profile?.index).toBe(0));
    expect(result.current.error).toBeUndefined();
    expect(fetch.mock.calls.every(call => String(call[0]).includes('/caliper-diagnostics'))).toBe(true);
  });

  it('expires a profile request without hiding the current run metadata', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, options?: RequestInit) => !String(input).includes('?')
      ? Promise.resolve(jsonResponse(diagnosticManifest()))
      : new Promise<Response>((_, reject) => options?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true }))));
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await act(async () => {});
    expect(result.current.manifest?.runId).toBe('run-a');
    await act(async () => { await vi.advanceTimersByTimeAsync(15_000); });
    expect(result.current.error).toContain('超时'); expect(result.current.loading).toBe(false);
    expect(result.current.manifest?.runId).toBe('run-a'); expect(result.current.profile).toBeUndefined();
  });

  it('bounds the recent-spoke cache and reloads evicted profiles', async () => {
    const fetch = vi.fn(async (input: RequestInfo | URL) => jsonResponse(String(input).includes('?')
      ? diagnosticProfile(urlIndex(String(input))) : diagnosticManifest('run-a', 7, 18)));
    vi.stubGlobal('fetch', fetch);
    const { result } = renderHook(() => useCaliperCircleDiagnostics(props));
    await waitFor(() => expect(result.current.profile?.index).toBe(0));
    for (let index = 1; index <= 17; index++) {
      await act(async () => result.current.select(index));
      await waitFor(() => expect(result.current.profile?.index).toBe(index));
    }
    act(() => result.current.select(0));
    await waitFor(() => expect(result.current.profile?.index).toBe(0));
    expect(fetch.mock.calls.filter(call => String(call[0]).endsWith('?spokeIndex=0'))).toHaveLength(2);
  });

  it('aborts pending reads when diagnostic mode closes and ignores their late errors', async () => {
    const pending = deferredResponse(); let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_: RequestInfo | URL, options?: RequestInit) => { signal = options?.signal as AbortSignal; return pending.promise; }));
    const { result, rerender } = renderHook((input: Props) => useCaliperCircleDiagnostics(input), { initialProps: props });
    rerender({ ...props, enabled: false });
    expect(signal?.aborted).toBe(true);
    await act(async () => pending.reject(new Error('obsolete')));
    expect(result.current.error).toBeUndefined(); expect(result.current.manifest).toBeUndefined();
    expect(result.current.profile).toBeUndefined(); expect(result.current.loading).toBe(false);
  });
});
