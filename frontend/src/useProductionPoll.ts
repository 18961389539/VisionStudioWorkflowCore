import { useCallback, useEffect, useRef, useState } from 'react';
import { isTimeoutError } from './apiClient';
import { runTimedAbortable } from './requestTimeout';

export function useProductionPoll<T>(read: (signal: AbortSignal) => Promise<T>, enabled = true, intervalMs = 5_000) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const [refreshing, setRefreshing] = useState(enabled);
  const [generation, setGeneration] = useState(0);
  const lifecycle = useRef<AbortController | undefined>(undefined);

  const refresh = useCallback(() => {
    if (!enabled) return;
    lifecycle.current?.abort();
    setRefreshing(true);
    setGeneration((value) => value + 1);
  }, [enabled]);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    lifecycle.current = controller;
    let next: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const latest = await runTimedAbortable(controller.signal, 5_000, read);
        if (controller.signal.aborted) return;
        setData(latest);
        setError(undefined);
      } catch (reason) {
        if (!controller.signal.aborted) {
          setError(isTimeoutError(reason) ? '读取超时' : reason instanceof Error ? reason.message : '读取失败');
        }
      } finally {
        if (!controller.signal.aborted) {
          setRefreshing(false);
          next = setTimeout(() => void poll(), intervalMs);
        }
      }
    };
    void poll();
    return () => {
      controller.abort();
      clearTimeout(next);
      if (lifecycle.current === controller) lifecycle.current = undefined;
    };
  }, [enabled, read, generation, intervalMs]);

  return { data, error, refresh, refreshing };
}
