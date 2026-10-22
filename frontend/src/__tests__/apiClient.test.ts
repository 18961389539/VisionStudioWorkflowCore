import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiFetch, apiJson, isAbortError, isTimeoutError } from '../apiClient';

function abortableFetch() {
  return vi.fn((_input: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
    const signal = init?.signal;
    if (!signal) return;
    if (signal.aborted) reject(signal.reason);
    else signal.addEventListener('abort', () => reject(signal.reason), { once: true });
  }));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('apiClient', () => {
  it('aborts with TimeoutError after timeoutMs', async () => {
    vi.stubGlobal('fetch', abortableFetch());

    const error = await apiFetch('/api/slow', { timeoutMs: 10 }).catch((reason) => reason);

    expect(isTimeoutError(error)).toBe(true);
    expect(isAbortError(error)).toBe(false);
  });

  it('forwards caller cancellation', async () => {
    vi.stubGlobal('fetch', abortableFetch());
    const controller = new AbortController();

    const pending = apiFetch('/api/slow', { signal: controller.signal });
    controller.abort();
    const error = await pending.catch((reason) => reason);

    expect(isAbortError(error)).toBe(true);
  });

  it('passes the caller signal through unchanged when no timeout is requested', async () => {
    let captured: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      captured = init?.signal ?? undefined;
      return new Response('{}', { status: 200 });
    }));
    const controller = new AbortController();

    await apiFetch('/api/identity', { signal: controller.signal });

    expect(captured).toBe(controller.signal);
  });

  it('aborts the underlying request signal even when fetch ignores it', () => {
    let captured: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      captured = init?.signal ?? undefined;
      return new Promise<Response>(() => undefined);
    }));
    const controller = new AbortController();

    void apiFetch('/api/pending', { signal: controller.signal }).catch(() => undefined);
    controller.abort();

    expect(captured?.aborted).toBe(true);
  });

  it('keeps non-2xx responses for callers that handle status themselves', async () => {
    const response = new Response('{}', { status: 409 });
    vi.stubGlobal('fetch', vi.fn(async () => response));

    await expect(apiFetch('/api/workflows/a')).resolves.toBe(response);
  });

  it('surfaces ProblemDetails through ApiError for JSON helpers', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(
      JSON.stringify({ detail: '版本冲突', code: 'stale', correlationId: 'corr-1' }),
      { status: 409, headers: { 'Content-Type': 'application/json' } })));

    const error = await apiJson('/api/workflows/a', { errorLabel: '保存失败' }).catch((reason) => reason);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).message).toContain('版本冲突');
    expect((error as ApiError).message).toContain('关联号 corr-1');
  });

  it('parses successful JSON responses', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ value: 7 }), { status: 200 })));

    await expect(apiJson<{ value: number }>('/api/value')).resolves.toEqual({ value: 7 });
  });
});
