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
