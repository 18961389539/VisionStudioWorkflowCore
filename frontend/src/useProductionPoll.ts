import { useCallback, useEffect, useRef, useState } from 'react';

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
    let next: number | undefined;
    const poll = async () => {
      const request = new AbortController();
      const abortRequest = () => request.abort();
      controller.signal.addEventListener('abort', abortRequest, { once: true });
      const timeout = window.setTimeout(abortRequest, 5_000);
      try {
        const latest = await read(request.signal);
        if (request.signal.aborted) throw new Error('读取超时');
        setData(latest);
        setError(undefined);
      } catch (reason) {
        if (!controller.signal.aborted) {
          setError(request.signal.aborted ? '读取超时' : reason instanceof Error ? reason.message : '读取失败');
        }
      } finally {
        window.clearTimeout(timeout);
        controller.signal.removeEventListener('abort', abortRequest);
        if (!controller.signal.aborted) {
          setRefreshing(false);
          next = window.setTimeout(() => void poll(), intervalMs);
        }
      }
    };
    void poll();
    return () => {
      controller.abort();
      window.clearTimeout(next);
      if (lifecycle.current === controller) lifecycle.current = undefined;
    };
  }, [enabled, read, generation, intervalMs]);

  return { data, error, refresh, refreshing };
}
