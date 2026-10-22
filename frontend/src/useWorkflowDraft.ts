import { useEffect, useRef } from 'react';
import { clearDraft, writeDraft } from './draft';
import type { WorkflowPayload } from './types';

type Params = {
  snapshot: string;
  demoKey: string;
  dirty: boolean;
  recoveryPending: boolean;
  onFailure: () => void;
};

/** 草稿随文档内容防抖；离开页面或进入后台时立即落盘，恢复入口未决时保持原草稿。 */
export function useWorkflowDraft(params: Params) {
  const warned = useRef(false);
  const latest = useRef(params);
  latest.current = params;
  const persist = () => {
    const { dirty, recoveryPending, snapshot, demoKey, onFailure } = latest.current;
    if (!dirty || recoveryPending) return;
    const payload = JSON.parse(snapshot) as WorkflowPayload;
    const persisted = writeDraft({
      version: 1, savedAt: new Date().toISOString(), workflowId: payload.id,
      workflowName: payload.name, demoKey, payload,
    });
    if (!persisted && !warned.current) {
      warned.current = true;
      onFailure();
    } else if (persisted) warned.current = false;
  };
  const persistRef = useRef(persist);
  persistRef.current = persist;

  useEffect(() => {
    if (!params.dirty) {
      if (!params.recoveryPending) clearDraft();
      warned.current = false;
      return;
    }
    const flush = () => { window.clearTimeout(timer); persistRef.current(); };
    const timer = params.recoveryPending ? undefined : window.setTimeout(() => persistRef.current(), 600);
    const beforeUnload = (event: BeforeUnloadEvent) => {
      flush();
      event.preventDefault();
      event.returnValue = '';
    };
    const visibilityChange = () => { if (document.visibilityState === 'hidden') flush(); };
    window.addEventListener('beforeunload', beforeUnload);
    window.addEventListener('pagehide', flush);
    document.addEventListener('visibilitychange', visibilityChange);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('beforeunload', beforeUnload);
      window.removeEventListener('pagehide', flush);
      document.removeEventListener('visibilitychange', visibilityChange);
    };
  }, [params.snapshot, params.demoKey, params.dirty, params.recoveryPending]);

  // 普通重渲染不写草稿；真正卸载时补写最新未保存内容。
  useEffect(() => () => persistRef.current(), []);
}
