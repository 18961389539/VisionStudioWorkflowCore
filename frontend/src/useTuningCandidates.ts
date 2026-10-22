import { useEffect, useRef, useState } from 'react';
import type { ValidationRunRecord, WorkflowPayload } from './types';
import { responseErrorMessage } from './apiErrors';
import { isValidationRun } from './tuningContracts';
import { isWorkflowDocument } from './workflowDocument';
import { withRequestTimeout } from './requestTimeout';
import { apiFetch } from './apiClient';
import { validation } from './endpoints';

type Params = {
  open: boolean; datasetId?: string; scopeRevision: number; workflow: WorkflowPayload; revision: number; runs: ValidationRunRecord[];
  runsRevision: number; runsFresh: boolean; refreshRuns: () => void;
  onStarted: (run: ValidationRunRecord) => void; onLoad: (workflow: WorkflowPayload) => void; success: (text: string) => unknown;
};
type Action = 'validation' | 'candidate';
type Submission = { datasetId: string; runId: string; snapshot: string; running: boolean };
type Uncertain = { marker: string; snapshot: string; observedRevision: number };

/** 验证绑定提交快照，候选读取绑定当前编辑；两者有各自的同步锁与关闭取消。 */
export function useTuningCandidates(params: Params) {
  const snapshot = JSON.stringify(params.workflow);
  const latest = useRef({ ...params, snapshot }); latest.current = { ...params, snapshot };
  const active = useRef(false);
  const requests = useRef(new Map<Action, AbortController>());
  const submittedRef = useRef<Submission | undefined>(undefined);
  const [submitted, setSubmitted] = useState<Submission>();
  const [busy, setBusy] = useState<ReadonlySet<Action>>(new Set());
  const [errors, setErrors] = useState<Partial<Record<Action, string>>>({});
  const uncertainRef = useRef<Uncertain | undefined>(undefined);
  const [uncertain, setUncertain] = useState<Uncertain>();
  const stop = (action: Action) => { requests.current.get(action)?.abort(); requests.current.delete(action); };
  useEffect(() => {
    active.current = params.open; submittedRef.current = undefined;
    uncertainRef.current = undefined; setUncertain(undefined);
    setSubmitted(undefined); setBusy(new Set()); setErrors({});
    return () => { active.current = false; for (const controller of requests.current.values()) controller.abort(); requests.current.clear(); };
  }, [params.open, params.datasetId, params.scopeRevision]);
  useEffect(() => {
    stop('candidate');
    setBusy(new Set(requests.current.keys()));
    setErrors((previous) => ({ ...previous, candidate: undefined }));
  }, [snapshot, params.revision]);
  useEffect(() => {
    const pending = uncertainRef.current;
    if (!pending || !params.open || !params.datasetId) return;
    const found = params.runs.find((run) => run.datasetId === params.datasetId && run.sourceWorkflowRunId === pending.marker);
    if (!found) return;
    const value = { datasetId: found.datasetId, runId: found.runId, snapshot: pending.snapshot, running: found.status === 'Running' };
    submittedRef.current = value; setSubmitted(value);
    uncertainRef.current = undefined; setUncertain(undefined);
    setErrors((previous) => ({ ...previous, validation: undefined }));
    latest.current.success('已在验证记录中确认先前提交的任务');
  }, [params.runs, params.open, params.datasetId, uncertain?.marker]);

  const validatingCurrent = () => {
    const context = latest.current; const previous = submittedRef.current;
    if (!previous || previous.datasetId !== context.datasetId || previous.snapshot !== context.snapshot) return false;
    const run = context.runs.find((item) => item.runId === previous.runId);
    return run ? run.status === 'Running' : previous.running;
  };
  const execute = async (action: Action, runId?: string): Promise<boolean> => {
    const context = latest.current;
    if (!active.current || !context.datasetId || requests.current.has(action)) return false;
    if (action === 'validation' && (validatingCurrent() || uncertainRef.current)) return false;
    if (action === 'candidate' && !context.runs.some((run) => run.runId === runId && run.datasetId === context.datasetId && run.status !== 'Running')) return false;
    const controller = new AbortController(); requests.current.set(action, controller);
    setBusy(new Set(requests.current.keys())); setErrors((previous) => ({ ...previous, [action]: undefined }));
    const owns = () => active.current && requests.current.get(action) === controller && latest.current.datasetId === context.datasetId;
    let knownFailure = false;
    const marker = action === 'validation' ? `parameter-tuning:${crypto.randomUUID()}` : undefined;
    try {
      const url = action === 'validation' ? validation.datasetRuns(context.datasetId)
        : validation.runWorkflow(runId!);
      const data: unknown = await withRequestTimeout(controller, action === 'validation' ? 20_000 : 15_000, async () => {
        const response = await apiFetch(url, action === 'validation' ? { method: 'POST', signal: controller.signal,
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceWorkflowRunId: marker, workflow: JSON.parse(context.snapshot) }) }
          : { signal: controller.signal });
        controller.signal.throwIfAborted();
        if (!response.ok) { knownFailure = response.status < 500; throw new Error(await responseErrorMessage(response, `HTTP ${response.status}`)); }
        return response.json();
      });
      if (!owns() || controller.signal.aborted) return false;
      if (action === 'validation') {
        if (!isValidationRun(data, context.datasetId) || data.sourceWorkflowRunId !== marker) throw new Error('验证启动响应与当前数据集或提交对象不匹配');
        const value = { datasetId: context.datasetId, runId: data.runId, snapshot: context.snapshot, running: data.status === 'Running' };
        submittedRef.current = value; setSubmitted(value);
        latest.current.onStarted(data);
        latest.current.success('提交时的候选工作流已创建数据集验证任务');
      } else {
        if (context.snapshot !== latest.current.snapshot || context.revision !== latest.current.revision) return false;
        if (!isWorkflowDocument(data)) throw new Error('验证候选项不是有效的工作流文档');
        latest.current.onLoad(data);
        latest.current.success('验证候选项已载入调优工作区');
      }
      return true;
    } catch (reason) {
      if (owns()) {
        if (action === 'validation' && !knownFailure) {
          const value = { marker: marker!, snapshot: context.snapshot, observedRevision: latest.current.runsRevision };
          uncertainRef.current = value; setUncertain(value);
          latest.current.refreshRuns();
        }
        const reasonText = controller.signal.aborted ? '请求超时' : reason instanceof Error ? reason.message : '网络不可达';
        setErrors((previous) => ({ ...previous, [action]: action === 'validation' && !knownFailure
          ? `验证启动结果未确认，后端可能已创建任务。请先刷新验证记录核对，勿直接重复提交。${reasonText}`
          : `${action === 'validation' ? '验证启动' : '候选载入'}失败：${reasonText}` }));
      }
      return false;
    } finally {
      if (requests.current.get(action) === controller) { requests.current.delete(action); if (active.current) setBusy(new Set(requests.current.keys())); }
    }
  };
  return { validationBusy: busy.has('validation'), candidateBusy: busy.has('candidate'), errors,
    submitted: submitted?.datasetId === params.datasetId ? submitted : undefined,
    submittedStale: Boolean(submitted && submitted.datasetId === params.datasetId && submitted.snapshot !== snapshot),
    uncertain: Boolean(uncertain), canAcknowledge: Boolean(uncertain && params.runsFresh && params.runsRevision > uncertain.observedRevision),
    acknowledge: () => {
      if (uncertainRef.current && latest.current.runsFresh && latest.current.runsRevision > uncertainRef.current.observedRevision) {
        uncertainRef.current = undefined; setUncertain(undefined); setErrors((previous) => ({ ...previous, validation: undefined }));
      }
    },
    validatingCurrent: validatingCurrent(), startValidation: () => execute('validation'), loadCandidate: (runId: string) => execute('candidate', runId) };
}
