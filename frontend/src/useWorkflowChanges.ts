import { useEffect, useRef, useState, type RefObject } from 'react';
import type { WorkflowPayload } from './types';
import { isWorkflowDocument } from './workflowDocument';

export type WorkflowApplyOptions = { markClean?: boolean; revision?: string };
export type PreparedWorkflowChange = {
  apply: (workflow: WorkflowPayload, options?: WorkflowApplyOptions) => Promise<boolean>;
  cancel: () => void;
  isCurrent: () => boolean;
  isSameDocument: () => boolean;
};
export type PrepareWorkflowChange = (label: string, mode?: 'replace' | 'edit') => PreparedWorkflowChange;
type Params = {
  snapshot: string; dirty: boolean; generationRef: RefObject<number>; save: () => Promise<boolean>;
  apply: (workflow: WorkflowPayload, options: WorkflowApplyOptions & { mode: 'replace' | 'edit'; label: string }) => void;
  warning: (text: string) => unknown;
};
type Pending = { label: string; valid: () => boolean; run: () => void; resolve: (applied: boolean) => void };

/** 替换文档必须经确认；基于当前文档的编辑保持源快照、撤销记录和保存基线。 */
export function useWorkflowChanges(params: Params) {
  const latest = useRef(params);
  latest.current = params;
  const mounted = useRef(false);
  const pendingRef = useRef<Pending | undefined>(undefined);
  const savingRef = useRef(false);
  const [pending, setPending] = useState<Pending>();
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; pendingRef.current?.resolve(false); pendingRef.current = undefined; };
  }, []);
  const finish = (value: Pending, result: boolean) => {
    if (pendingRef.current !== value) return;
    pendingRef.current = undefined;
    if (mounted.current) setPending(undefined);
    value.resolve(result);
  };
  const execute = (value: Pending): boolean => {
    if (!mounted.current || !value.valid()) {
      if (mounted.current) latest.current.warning('操作已取消：源流程已变化或载入入口已关闭，请重新发起。');
      return false;
    }
    try { value.run(); return true; }
    catch (reason) {
      latest.current.warning(`未应用流程：${reason instanceof Error ? reason.message : '文档应用失败'}`);
      return false;
    }
  };
  const request = (label: string, run: () => void, stillValid: () => boolean = () => true): Promise<boolean> => {
    if (!mounted.current || savingRef.current) return Promise.resolve(false);
    const { snapshot, generationRef } = latest.current;
    const generation = generationRef.current;
    const valid = () => generation === latest.current.generationRef.current && snapshot === latest.current.snapshot && stillValid();
    if (!valid()) return Promise.resolve(false);
    pendingRef.current && finish(pendingRef.current, false);
    return new Promise((resolve) => {
      const value: Pending = { label, run, valid, resolve };
      if (!latest.current.dirty) { resolve(execute(value)); return; }
      pendingRef.current = value;
      setPending(value);
    });
  };
  const prepare: PrepareWorkflowChange = (label, mode = 'replace') => {
    const snapshot = latest.current.snapshot;
    const generation = latest.current.generationRef.current;
    let active = true;
    let consumed = false;
    let ownPending: Pending | undefined;
    const isSameDocument = () => mounted.current && active && generation === latest.current.generationRef.current;
    const isCurrent = () => isSameDocument() && snapshot === latest.current.snapshot;
    return {
      isCurrent, isSameDocument,
      cancel: () => { active = false; if (ownPending) finish(ownPending, false); },
      apply: async (workflow, options = {}) => {
        if (consumed || !isCurrent()) {
          if (mounted.current && active) latest.current.warning('未应用流程：准备期间设计器已改变，请重新载入或重新生成候选方案。');
          return false;
        }
        if (!isWorkflowDocument(workflow) || (mode === 'edit' && workflow.id !== JSON.parse(snapshot).id)) {
          latest.current.warning('未应用流程：响应不是有效的工作流文档，或不属于当前编辑文档。');
          return false;
        }
        consumed = true;
        const candidate = JSON.parse(JSON.stringify(workflow)) as WorkflowPayload;
        const run = () => latest.current.apply(candidate, { ...options, mode, label,
          markClean: mode === 'edit' ? false : options.markClean,
          revision: mode === 'edit' ? undefined : options.revision });
        if (mode === 'edit') {
          if (savingRef.current || pendingRef.current) return false;
          return execute({ label, valid: isCurrent, run, resolve: () => {} });
        }
        const result = request(label, run, isCurrent);
        ownPending = pendingRef.current?.run === run ? pendingRef.current : undefined;
        return result;
      },
    };
  };
  const cancel = () => { if (!savingRef.current && pendingRef.current) finish(pendingRef.current, false); };
  const discard = () => {
    const value = pendingRef.current;
    if (savingRef.current || !value) return;
    finish(value, execute(value));
  };
  const saveAndContinue = async () => {
    const value = pendingRef.current;
    if (!value || savingRef.current) return;
    if (!value.valid()) { finish(value, execute(value)); return; }
    savingRef.current = true;
    setBusy(true);
    try {
      const saved = await latest.current.save();
      if (!mounted.current || pendingRef.current !== value) return;
      if (!value.valid()) { finish(value, execute(value)); return; }
      if (saved) finish(value, execute(value));
    } catch (reason) {
      if (mounted.current) latest.current.warning(`保存未完成，当前修改仍保留。${reason instanceof Error ? reason.message : ''}`);
    } finally {
      savingRef.current = false;
      if (mounted.current) setBusy(false);
    }
  };
  return { pending, busy, prepare, request, cancel, discard, saveAndContinue };
}
