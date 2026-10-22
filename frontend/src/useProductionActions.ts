import { useEffect, useRef, useState } from 'react';
import { responseErrorMessage } from './apiErrors';
import { apiFetch } from './apiClient';

type Messages = { success: (text: string) => unknown; error: (text: string) => unknown };
type Action = { url: string; action: string; method?: string; body?: unknown; successMessage: string; onSuccess?: (data: unknown) => void };
type Result = { ok: true } | { ok: false; error: string; cancelled?: boolean };

/** 重复动作同步互斥；停止可在其它动作等待期间发出。所有写请求有期限且不自动重放。 */
export function useProductionActions(open: boolean, messages: Messages, refresh: () => void) {
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [error, setError] = useState<string>();
  const active = useRef(false);
  const requests = useRef(new Map<string, AbortController>());
  const runtimeGeneration = useRef(0);
  useEffect(() => {
    active.current = open;
    setBusy(new Set());
    setError(undefined);
    return () => {
      active.current = false;
      for (const controller of requests.current.values()) controller.abort();
      requests.current.clear();
    };
  }, [open]);

  const call = async ({ url, method = 'POST', body, successMessage, action, onSuccess }: Action): Promise<Result> => {
    if (!active.current || requests.current.has(action)
      || (action === 'start' && requests.current.has('save')) || (action === 'save' && requests.current.has('start'))) {
      return { ok: false, cancelled: true, error: '操作正在进行，请等待结果。' };
    }
    const controller = new AbortController();
    const runtimeAction = ['start', 'stop', 'recover', 'resolve'].includes(action);
    const generation = runtimeAction ? ++runtimeGeneration.current : runtimeGeneration.current;
    requests.current.set(action, controller);
    setBusy(new Set(requests.current.keys()));
    setError(undefined);
    const owns = () => active.current && requests.current.get(action) === controller;
    const superseded = () => runtimeAction && generation !== runtimeGeneration.current;
    const timeout = setTimeout(() => controller.abort(), 20_000);
    try {
      const response = await apiFetch(url, {
        method, signal: controller.signal,
        headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!owns() || superseded()) return { ok: false, cancelled: true, error: '操作已失效。' };
      if (!response.ok) {
        const detail = await responseErrorMessage(response, `操作失败（HTTP ${response.status}）`);
        if (!owns() || superseded()) return { ok: false, cancelled: true, error: '操作已失效。' };
        const text = response.status >= 500 ? `${detail}；结果未确认，请核对生产状态或配置后再操作。` : detail;
        setError(text);
        messages.error(text);
        refresh();
        return { ok: false, error: text };
      }
      const data: unknown = response.status === 204 ? undefined : await response.json();
      if (!owns() || superseded()) return { ok: false, cancelled: true, error: '操作已失效。' };
      if (controller.signal.aborted) throw new Error('请求超时');
      onSuccess?.(data);
      messages.success(successMessage);
      refresh();
      return { ok: true };
    } catch (reason) {
      if (!owns() || superseded()) return { ok: false, cancelled: true, error: '操作已失效。' };
      const text = `操作结果未确认，请核对生产状态或配置后再操作。${controller.signal.aborted ? '（请求超时）' : reason instanceof Error ? `（${reason.message}）` : ''}`;
      setError(text);
      messages.error(text);
      refresh();
      return { ok: false, error: text };
    } finally {
      clearTimeout(timeout);
      if (requests.current.get(action) === controller) {
        requests.current.delete(action);
        if (active.current) setBusy(new Set(requests.current.keys()));
      }
    }
  };
  return { busy, error, call };
}
