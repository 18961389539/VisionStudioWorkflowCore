import { responseErrorMessage } from './apiErrors';

/** 默认请求期限；调用方需要更短/更长时显式传入 timeoutMs。 */
export const DEFAULT_TIMEOUT_MS = 15_000;

export type ApiFetchOptions = Omit<RequestInit, 'signal'> & {
  signal?: AbortSignal;
  /** 超过该毫秒数后中止请求（AbortSignal.timeout 的兼容实现）。 */
  timeoutMs?: number;
};

/** HTTP 非 2xx 且调用方要求抛错时使用（apiJson / apiText）。 */
export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = 'ApiError';
  }
}

function errorName(error: unknown): string {
  return typeof error === 'object' && error !== null && 'name' in error
    ? String((error as { name: unknown }).name)
    : '';
}

// DOMException 在部分环境（jsdom）不是 Error 子类，不能依赖 instanceof Error。
export function isAbortError(error: unknown): boolean {
  return errorName(error) === 'AbortError';
}

export function isTimeoutError(error: unknown): boolean {
  return errorName(error) === 'TimeoutError';
}

/**
 * 统一请求入口：
 * - 调用方 signal 与 timeoutMs 组合成一个信号，任一触发都会中止 fetch；
 * - 不在非 2xx 时抛错，保留调用方按状态码分别处理的既有逻辑；
 * - 超时以 TimeoutError 中止，调用方可用 isTimeoutError 区分“超时”与“主动取消”。
 *
 * 迁移约定：新代码与迁移中的面板统一使用本函数，不再直接调用全局 fetch。
 */
export async function apiFetch(input: RequestInfo | URL, options: ApiFetchOptions = {}): Promise<Response> {
  const { timeoutMs, signal, ...init } = options;
  if (timeoutMs === undefined) {
    // 未提供 signal 时保持与原调用完全一致的参数（不额外写入 signal: undefined）。
    return signal ? fetch(input, { ...init, signal }) : fetch(input, init);
  }

  // 只有需要超时才组合信号：否则原样透传调用方 signal，保持其身份与后续 abort 可见性。
  const controller = new AbortController();
  const forwardAbort = () => controller.abort(signal?.reason);
  if (signal) {
    if (signal.aborted) controller.abort(signal.reason);
    else signal.addEventListener('abort', forwardAbort, { once: true });
  }

  // 不依赖 AbortSignal.reason：部分 jsdom/浏览器实现会忽略自定义 reason，统一用标志位
  // 保证超时与主动取消可区分。
  let timedOut = false;
  const timer = timeoutMs === undefined
    ? undefined
    : window.setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);

  try {
    return await fetch(input, { ...init, signal: controller.signal });
  } catch (error) {
    if (timedOut) throw new DOMException('Request timed out.', 'TimeoutError');
    throw error;
  } finally {
    if (timer !== undefined) window.clearTimeout(timer);
    signal?.removeEventListener('abort', forwardAbort);
  }
}

/** 读取 JSON；非 2xx 抛出带 ProblemDetails 文案的 ApiError。 */
export async function apiJson<T>(
  input: RequestInfo | URL,
  options: ApiFetchOptions & { errorLabel?: string } = {}
): Promise<T> {
  const { errorLabel = 'Request failed', ...fetchOptions } = options;
  const response = await apiFetch(input, fetchOptions);
  if (!response.ok) {
    const message = await responseErrorMessage(response, `${errorLabel} (HTTP ${response.status})`);
    throw new ApiError(message, response.status);
  }
  return await response.json() as T;
}

/** 读取文本；非 2xx 抛出带 ProblemDetails 文案的 ApiError。 */
export async function apiText(
  input: RequestInfo | URL,
  options: ApiFetchOptions & { errorLabel?: string } = {}
): Promise<string> {
  const { errorLabel = 'Request failed', ...fetchOptions } = options;
  const response = await apiFetch(input, fetchOptions);
  if (!response.ok) {
    const message = await responseErrorMessage(response, `${errorLabel} (HTTP ${response.status})`);
    throw new ApiError(message, response.status);
  }
  return await response.text();
}

// ── 请求去重 ──────────────────────────────────────────────────────────────────

interface DedupeEntry {
  promise: Promise<Response>;
  controller: AbortController;
}

/** 进行中的去重请求；key 为 `GET url`。 */
const inFlightRequests = new Map<string, DedupeEntry>();

/** 取消指定去重请求：中止共享请求，所有共享方收到 AbortError（例如面板关闭时）。 */
export function cancelDedupedRequest(url: string, method = 'GET'): void {
  const key = `${method.toUpperCase()} ${url}`;
  const entry = inFlightRequests.get(key);
  if (!entry) return;
  inFlightRequests.delete(key);
  entry.controller.abort();
}

/** 清空全部去重请求记录（不中止底层请求）；仅测试用。 */
export function clearDedupedRequests(): void {
  inFlightRequests.clear();
}

/**
 * 相同 URL 的并发 GET 请求只发一次，后续调用共享同一请求。
 * - 仅对无 body 且 dedupe: true 的 GET 生效（默认 false，不影响现有调用）；
 * - 每个调用方拿到独立的 Response 克隆，body 可各自消费；
 * - 共享方共用一个请求生命周期：任一调用方的 signal 中止或 cancelDedupedRequest
 *   都会中止共享请求，所有共享方收到 AbortError。接入前须确认没有
 *   “变更后刷新同 URL”的调用顺序（合并会读到变更前的在途响应）。
 */
export async function apiFetchDeduped(
  input: RequestInfo | URL,
  options: ApiFetchOptions & { dedupe?: boolean } = {}
): Promise<Response> {
  const { dedupe = false, signal, ...rest } = options;
  if (!dedupe || (rest.method && rest.method.toUpperCase() !== 'GET') || rest.body != null) {
    return apiFetch(input, { ...rest, signal });
  }

  const url = String(input);
  const key = `GET ${url}`;
  const existing = inFlightRequests.get(key);
  if (existing) return existing.promise.then((response) => response.clone());

  const controller = new AbortController();
  const forwardAbort = () => controller.abort(signal?.reason);
  if (signal) {
    if (signal.aborted) controller.abort(signal.reason);
    else signal.addEventListener('abort', forwardAbort, { once: true });
  }

  const promise = apiFetch(url, { ...rest, signal: controller.signal })
    .finally(() => {
      inFlightRequests.delete(key);
      signal?.removeEventListener('abort', forwardAbort);
    });
  inFlightRequests.set(key, { promise, controller });
  return promise.then((response) => response.clone());
}

// ── 响应缓存 ──────────────────────────────────────────────────────────────────

interface CacheEntry { data: unknown; timestamp: number }

/** URL → 缓存条目；仅在 cachedGet 显式使用时生效。 */
const responseCache = new Map<string, CacheEntry>();

/** 缓存数据恒来自 response.json()（纯 JSON，无 Date/Map 等实例），JSON 往返即安全深拷贝。 */
function cloneCached<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

/**
 * 带缓存的 GET 请求：
 * - 同一 URL 在 ttlMs 内直接返回缓存结果（不发请求）；
 * - 返回值是缓存数据的深拷贝，调用方变更返回值不会污染缓存；
 * - `forceRefresh: true` 绕过缓存；
 * - `maxEntries` 超过时按插入顺序淘汰最早条目（FIFO，非 LRU）。
 */
export async function cachedGet<T>(
  url: string,
  options: {
    timeoutMs?: number;
    signal?: AbortSignal;
    ttlMs?: number;
    forceRefresh?: boolean;
    maxEntries?: number;
    errorLabel?: string;
  } = {}
): Promise<T> {
  const { ttlMs = 5_000, forceRefresh = false, maxEntries = 100, ...fetchOptions } = options;
  if (!forceRefresh) {
    const cached = responseCache.get(url);
    if (cached && Date.now() - cached.timestamp < ttlMs) return cloneCached(cached.data) as T;
  }

  const data = await apiJson<T>(url, { ...fetchOptions, errorLabel: options.errorLabel ?? 'Cached GET failed' });
  // 存入深拷贝：新调用方直接持有的 data 与缓存隔离，变更互不影响。
  responseCache.set(url, { data: cloneCached(data), timestamp: Date.now() });

  if (responseCache.size > maxEntries) {
    const firstKey = responseCache.keys().next().value;
    if (firstKey !== undefined) responseCache.delete(firstKey);
  }
  return data;
}

/** 清空指定 URL 的缓存；不传则清空全部。 */
export function invalidateCache(url?: string): void {
  if (url) responseCache.delete(url);
  else responseCache.clear();
}

// ── 取消令牌 ──────────────────────────────────────────────────────────────────

/**
 * 面板/组件作用域的取消令牌。
 * 组件挂载时创建、卸载时调用 `cancel()`，内部包装的请求随之中止。
 * 典型用法：
 * ```ts
 * const token = new RequestToken();
 * useEffect(() => () => token.cancel(), []);
 * apiFetch(url, { signal: token.signal })
 * ```
 */
export class RequestToken {
  private readonly controller = new AbortController();

  get signal(): AbortSignal { return this.controller.signal; }

  get aborted(): boolean { return this.controller.signal.aborted; }

  cancel(): void {
    if (!this.controller.signal.aborted) this.controller.abort();
  }
}
