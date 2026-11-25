import { useCallback, useEffect, useRef } from 'react';
import type { Node } from '@xyflow/react';
import { restoreCanvasAnnotations, saveCanvasAnnotations } from './canvasAnnotations';

type Params = { workflowId: string; nodes: Node[]; dragging: boolean; onFailure: () => void };

export function useCanvasAnnotationsPersistence({ workflowId, nodes, dragging, onFailure }: Params) {
  const latest = useRef({ workflowId, nodes });
  const pending = useRef(new Map<string, Node[]>());
  const timer = useRef<number | undefined>(undefined);
  const warned = useRef(false);
  const failure = useRef(onFailure);
  failure.current = onFailure;

  const flush = useCallback(() => {
    window.clearTimeout(timer.current);
    timer.current = undefined;
    for (const [id, snapshot] of pending.current) {
      if (saveCanvasAnnotations(id, snapshot)) pending.current.delete(id);
    }
    if (pending.current.size && !warned.current) failure.current();
    warned.current = pending.current.size > 0;
  }, []);

  useEffect(() => {
    // Flush the old owner before accepting a new document's graph, never under its new ID.
    if (latest.current.workflowId !== workflowId) flush();
    latest.current = { workflowId, nodes };
    pending.current.set(workflowId, nodes);
    if (!dragging) timer.current = window.setTimeout(flush, 600);
    return () => window.clearTimeout(timer.current);
  }, [workflowId, nodes, dragging, flush]);

  useEffect(() => {
    const visibilityChange = () => { if (document.visibilityState === 'hidden') flush(); };
    window.addEventListener('beforeunload', flush);
    window.addEventListener('pagehide', flush);
    document.addEventListener('visibilitychange', visibilityChange);
    return () => {
      window.removeEventListener('beforeunload', flush);
      window.removeEventListener('pagehide', flush);
      document.removeEventListener('visibilitychange', visibilityChange);
      flush();
    };
  }, [flush]);

  const restoreAnnotations = useCallback((id: string, workflowNodes: Node[]) => {
    // Same-document edits/reloads must see unsaved annotations, even when storage is unavailable.
    const liveNodes = pending.current.get(id) ?? (latest.current.workflowId === id ? latest.current.nodes : undefined);
    flush();
    return restoreCanvasAnnotations(id, workflowNodes, liveNodes);
  }, [flush]);

  return restoreAnnotations;
}
