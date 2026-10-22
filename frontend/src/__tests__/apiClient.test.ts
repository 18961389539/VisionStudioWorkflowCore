import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError, apiFetch, apiJson, isAbortError, isTimeoutError,
  apiFetchDeduped, cachedGet, invalidateCache, RequestToken, clearDedupedRequests
} from '../apiClient';

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
  vi.restoreAllMocks();
  invalidateCache();
  clearDedupedRequests();
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
});

describe('apiFetchDeduped', () => {
  it('merges concurrent GET requests to the same URL', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ ok: 1 }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    const [a, b, c] = await Promise.all([
      apiFetchDeduped('/api/merge', { dedupe: true }),
      apiFetchDeduped('/api/merge', { dedupe: true }),
      apiFetchDeduped('/api/merge', { dedupe: true })
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(a.status).toBe(200);
    expect(b.status).toBe(200);
    expect(c.status).toBe(200);
  });

  it('does not dedupe when dedupe is false (default)', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await Promise.all([
      apiFetchDeduped('/api/no-dedupe'),
      apiFetchDeduped('/api/no-dedupe')
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('dedupes by URL: different URLs get separate requests', async () => {
    const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await Promise.all([
      apiFetchDeduped('/api/a', { dedupe: true }),
      apiFetchDeduped('/api/b', { dedupe: true })
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('cachedGet', () => {
  it('serves from cache within TTL and refetches with forceRefresh', async () => {
    let counter = 0;
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ value: ++counter }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    const first = await cachedGet<{ value: number }>('/api/data', { ttlMs: 5_000 });
    const second = await cachedGet<{ value: number }>('/api/data', { ttlMs: 5_000 });
    expect(first.value).toBe(1);
    expect(second.value).toBe(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    const forced = await cachedGet<{ value: number }>('/api/data', { ttlMs: 5_000, forceRefresh: true });
    expect(forced.value).toBe(2);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('refetches after TTL expires', async () => {
    let counter = 0;
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ value: ++counter }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await cachedGet('/api/ttl', { ttlMs: 10 });
    await new Promise((resolve) => setTimeout(resolve, 20));
    await cachedGet('/api/ttl', { ttlMs: 10 });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('invalidateCache clears the cache for a specific URL', async () => {
    let counter = 0;
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ value: ++counter }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await cachedGet('/api/inv', { ttlMs: 60_000 });
    invalidateCache('/api/inv');
    await cachedGet('/api/inv', { ttlMs: 60_000 });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('RequestToken', () => {
  it('starts active and can be cancelled', () => {
    const token = new RequestToken();
    expect(token.aborted).toBe(false);
    expect(token.signal.aborted).toBe(false);

    token.cancel();
    expect(token.aborted).toBe(true);
    expect(token.signal.aborted).toBe(true);
  });

  it('cancel is idempotent', () => {
    const token = new RequestToken();
    token.cancel();
    expect(() => token.cancel()).not.toThrow();
    expect(token.aborted).toBe(true);
  });

  it('cancels in-flight requests', async () => {
    vi.stubGlobal('fetch', abortableFetch());
    const token = new RequestToken();

    const pending = apiFetch('/api/cancel-test', { signal: token.signal });
    token.cancel();
    const error = await pending.catch((reason) => reason);

    expect(isAbortError(error)).toBe(true);
  });
});
