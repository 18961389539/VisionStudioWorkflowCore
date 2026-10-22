import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import type { Node, Edge } from '@xyflow/react';
import type { NodeCatalogItem, WorkflowPayload } from './types';
import { serverIssueFromMessage, validateWorkflow, type WorkflowIssue } from './validation';
import { apiErrorMessage, responseErrorMessage } from './apiErrors';
import { withRequestTimeout } from './requestTimeout';

type ValidationState = { status: 'unvalidated' | 'ok' | 'issues'; summary?: string };
type Params = {
  workflow: WorkflowPayload; nodes: Node[]; edges: Edge[]; catalog: NodeCatalogItem[]; generationRef: RefObject<number>;
  onValidated: () => void;
  messages: { warning: (text: string) => unknown; success: (text: string) => unknown; error: (text: string) => unknown };
};
type Action = 'validate' | 'compile';
type Context = { snapshot: string; generation: number; catalog: NodeCatalogItem[]; workflowId: string };
type Operation = Context & { controller: AbortController };
type Validation = Context & { issues: WorkflowIssue[]; state: ValidationState };
const unvalidated: ValidationState = { status: 'unvalidated' };
const isRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
const isText = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0;
const isOptionalCount = (value: unknown) => value === undefined || (typeof value === 'number' && Number.isSafeInteger(value) && value >= 0);

function validationSummary(data: unknown): string {
  if (isRecord(data) && data.valid === false) throw new Error(apiErrorMessage(data, '校验失败（服务端未提供原因）'));
  if (!isRecord(data) || data.valid !== true || !Array.isArray(data.orderedNodeIds) || !data.orderedNodeIds.length
    || !data.orderedNodeIds.every(isText) || !isOptionalCount(data.pipelineSegmentCount) || !isOptionalCount(data.structuredRegionCount)) {
    throw new Error('校验响应无效：缺少有效的校验结果或编译统计');
  }
  return `流程校验通过 · ${data.orderedNodeIds.length} 个节点`
    + (data.pipelineSegmentCount === undefined ? '' : ` · ${data.pipelineSegmentCount} 个流水线`)
    + (data.structuredRegionCount === undefined ? '' : ` · ${data.structuredRegionCount} 个结构化区域`);
}

function formatDsl(data: unknown): string {
  if (!isRecord(data) || !isText(data.dslJson)) throw new Error('编译响应无效：缺少 DSL');
  let dsl: unknown;
  try { dsl = JSON.parse(data.dslJson); }
  catch { throw new Error('编译响应无效：DSL 不是有效 JSON'); }
  if (!isRecord(dsl) || !isText(dsl.Id) || typeof dsl.Version !== 'number' || !Number.isSafeInteger(dsl.Version) || dsl.Version < 1
    || !isText(dsl.DataType) || !Array.isArray(dsl.Steps)) throw new Error('编译响应无效：DSL 缺少有效的流程定义');
  return JSON.stringify(dsl, null, 2);
}

export function useWorkflowInspection(params: Params) {
  const snapshot = useMemo(() => JSON.stringify(params.workflow), [params.workflow]);
  const generation = params.generationRef.current;
  const latest = useRef({ ...params, snapshot }); latest.current = { ...params, snapshot };
  const active = useRef(false);
  const operations = useRef(new Map<Action, Operation>());
  const [busy, setBusy] = useState<Set<Action>>(new Set());
  const [validation, setValidation] = useState<Validation>();
  const [compiled, setCompiled] = useState<{ context: Context; value: string }>();
  const matches = (value: Context) => value.snapshot === latest.current.snapshot && value.generation === latest.current.generationRef.current
    && value.catalog === latest.current.catalog && value.workflowId === latest.current.workflow.id;
  const cancel = useCallback((action?: Action) => {
    for (const [key, value] of operations.current) {
      if (action && key !== action) continue;
      operations.current.delete(key);
      value.controller.abort();
    }
    if (active.current) setBusy(new Set(operations.current.keys()));
  }, []);
  useEffect(() => {
    active.current = true;
    return () => { active.current = false; cancel(); };
  }, [cancel]);
  useEffect(() => {
    setCompiled(undefined);
    setValidation(previous => previous?.generation === generation && previous.workflowId === params.workflow.id ? previous : undefined);
    return () => cancel();
  }, [snapshot, params.catalog, generation, params.workflow.id, cancel]);
  const resetInspection = useCallback(() => {
    cancel();
    if (active.current) { setValidation(undefined); setCompiled(undefined); }
  }, [cancel]);
  const closeDsl = useCallback(() => {
    cancel('compile');
    if (active.current) setCompiled(undefined);
  }, [cancel]);

  const run = async (action: Action): Promise<boolean> => {
    if (!active.current) return false;
    const context = latest.current;
    const pending = operations.current.get(action);
    if (pending && matches(pending)) return false;
    cancel(action);
    const controller = new AbortController();
    const operation: Operation = { controller, snapshot: context.snapshot, generation: context.generationRef.current,
      catalog: context.catalog, workflowId: context.workflow.id };
    operations.current.set(action, operation);
    setBusy(new Set(operations.current.keys()));
    if (action === 'compile') setCompiled(undefined);
    const owns = () => active.current && operations.current.get(action) === operation && matches(operation);
    const label = action === 'validate' ? '校验' : '编译';
    try {
      const issues = action === 'validate' ? validateWorkflow(context.nodes, context.catalog, context.edges) : [];
      let outcome: { ok: true; value: string } | { ok: false; error: string };
      try {
        const data: unknown = await withRequestTimeout(controller, 15_000, async () => {
          const response = await fetch(`/api/${action}`, { method: 'POST', signal: controller.signal,
            headers: { 'Content-Type': 'application/json' }, body: context.snapshot });
          controller.signal.throwIfAborted();
          if (!response.ok) throw new Error(await responseErrorMessage(response, `${label}失败（HTTP ${response.status}）`));
          try { return await response.json(); }
          catch { throw new Error(`${label}响应无效：无法解析 JSON`); }
        });
        if (!owns()) return false;
        outcome = { ok: true, value: action === 'validate' ? validationSummary(data) : formatDsl(data) };
      } catch (reason) {
        if (!owns()) return false;
        outcome = { ok: false, error: controller.signal.aborted ? `${label}请求超时（15 秒），请检查后重试。`
          : reason instanceof Error && reason.message.trim() ? reason.message : `${label}请求失败` };
      }
      if (action === 'compile') {
        if (outcome.ok === true) setCompiled({ context: operation, value: outcome.value });
        else latest.current.messages.error(outcome.error);
        return outcome.ok;
      }
      if (outcome.ok === false) issues.push(serverIssueFromMessage(outcome.error));
      setValidation({ ...operation, issues, state: issues.length ? { status: 'issues' } : { status: 'ok', summary: outcome.ok === true ? outcome.value : undefined } });
      latest.current.onValidated();
      if (issues.length) latest.current.messages.warning(`发现 ${issues.length} 个校验问题，点击列表项可定位`);
      else if (outcome.ok === true) latest.current.messages.success(outcome.value);
      return issues.length === 0;
    } finally {
      if (operations.current.get(action) === operation) {
        operations.current.delete(action);
        if (active.current) setBusy(new Set(operations.current.keys()));
      }
    }
  };
  const currentValidation = validation?.generation === generation && validation.workflowId === params.workflow.id ? validation : undefined;
  const isBusy = (action: Action) => {
    const operation = operations.current.get(action);
    return busy.has(action) && Boolean(operation && matches(operation));
  };
  return {
    validationIssues: currentValidation?.issues ?? [], validationState: currentValidation?.state ?? unvalidated,
    needsRevalidate: Boolean(currentValidation && !matches(currentValidation)), compiledDsl: compiled && matches(compiled.context) ? compiled.value : undefined,
    validationBusy: isBusy('validate'), compileBusy: isBusy('compile'),
    validate: () => run('validate'), inspectDsl: () => run('compile'), resetInspection, closeDsl,
  };
}
