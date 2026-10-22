import { useEffect, useRef, useState } from 'react';
import type { WorkflowPayload } from './types';
import type { PrepareWorkflowChange, PreparedWorkflowChange, WorkflowApplyOptions } from './useWorkflowChanges';
import { responseErrorMessage } from './apiErrors';
import { isWorkflowDocument } from './workflowDocument';

type ImportRequest = {
  url?: string; workflow?: WorkflowPayload; label: string;
  select?: (data: unknown) => WorkflowPayload;
  options?: (response: Response) => WorkflowApplyOptions;
  onApplied?: (data: unknown) => void;
};
type Operation = { controller: AbortController; change: PreparedWorkflowChange };
type Params = { active: boolean; contextKey?: string; prepare: PrepareWorkflowChange; error: (text: string) => unknown };

/** 读取期间绑定源文档，读完再确认；关闭、切换来源或取消确认均不应用，也不报成功。 */
export function useWorkflowImport(params: Params) {
  const latest = useRef(params); latest.current = params;
  const active = useRef(false);
  const operation = useRef<Operation | undefined>(undefined);
  const [loading, setLoading] = useState(false);
  const cancel = () => {
    const value = operation.current;
    operation.current = undefined;
    value?.controller.abort();
    value?.change.cancel();
  };
  useEffect(() => {
    active.current = params.active;
    setLoading(false);
    return () => { active.current = false; cancel(); };
  }, [params.active, params.contextKey]);
  const load = async ({ url, workflow: preparedWorkflow, label, select, options, onApplied }: ImportRequest): Promise<boolean> => {
    if (!active.current || operation.current) return false;
    const controller = new AbortController();
    const value: Operation = { controller, change: latest.current.prepare(label) };
    operation.current = value;
    setLoading(true);
    const owns = () => active.current && operation.current === value && !controller.signal.aborted;
    const timeout = setTimeout(() => controller.abort(), 15_000);
    try {
      const response = url ? await fetch(url, { signal: controller.signal }) : undefined;
      if (!owns()) return false;
      if (response && !response.ok) throw new Error(await responseErrorMessage(response, `HTTP ${response.status}`));
      const data: unknown = response ? await response.json() : preparedWorkflow;
      if (!owns()) return false;
      const workflow = select ? select(data) : data;
      if (!isWorkflowDocument(workflow)) throw new Error('响应不是有效的工作流文档');
      clearTimeout(timeout); // 人工确认不属于网络读取期限。
      const applied = await value.change.apply(workflow, response ? options?.(response) : undefined);
      if (!owns() || !applied) return false;
      try { onApplied?.(data); }
      catch (reason) {
        latest.current.error(`流程已载入，但后续处理失败：${reason instanceof Error ? reason.message : '未知错误'}。请检查当前文档和来源状态，勿重复载入。`);
      }
      return true;
    } catch (reason) {
      if (active.current && operation.current === value) latest.current.error(`载入失败：${controller.signal.aborted ? '读取超时（15 秒）' : reason instanceof Error ? reason.message : '网络不可达'}。当前设计器保持不变。`);
      return false;
    } finally {
      clearTimeout(timeout);
      value.change.cancel();
      if (operation.current === value) { operation.current = undefined; if (active.current) setLoading(false); }
    }
  };
  return { load, loading };
}
