import { useCallback, useEffect, useRef, useState } from 'react';
import type { ValidationDatasetDetail, ValidationDatasetSummary, ValidationRunRecord } from './types';
import { responseErrorMessage } from './apiErrors';
import { isDatasetDetail, isDatasetSummary, isValidationRun } from './tuningContracts';
import { withRequestTimeout } from './requestTimeout';
import { apiFetch } from './apiClient';

type Resource = 'list' | 'detail' | 'runs';
type Request = { controller: AbortController; id?: string };
type Selection = { datasetId?: string; itemId?: string };
type Failure = { id?: string; text: string };

/** 数据集详情和验证记录独立读取；选择变化立即取消旧请求，正文只提交给所属数据集。 */
export function useTuningData(open: boolean) {
  const [datasets, setDatasets] = useState<ValidationDatasetSummary[]>([]);
  const [selection, setSelection] = useState<Selection>({});
  const [scopeRevision, setScopeRevision] = useState(0);
  const selected = useRef(selection); selected.current = selection;
  const [detail, setDetail] = useState<ValidationDatasetDetail>();
  const [records, setRecords] = useState<{ id: string; runs: ValidationRunRecord[] }>();
  const [runsRevision, setRunsRevision] = useState(0);
  const [failures, setFailures] = useState<Partial<Record<Resource, Failure>>>({});
  const [loading, setLoading] = useState<Partial<Record<Resource, boolean>>>({});
  const active = useRef(false);
  const requests = useRef(new Map<Resource, Request>());
  const acknowledged = useRef(new Map<string, ValidationRunRecord>());
  const stop = (resource: Resource) => { requests.current.get(resource)?.controller.abort(); requests.current.delete(resource); };
  const chooseDataset = useCallback((datasetId: string | undefined) => {
    if (selected.current.datasetId === datasetId) return;
    stop('detail'); stop('runs');
    acknowledged.current.clear();
    selected.current = { datasetId }; setSelection(selected.current);
    setScopeRevision((value) => value + 1);
    setDetail(undefined); setRecords(undefined); setLoading({});
  }, []);
  const read = useCallback(async (resource: Resource) => {
    const id = resource === 'list' ? undefined : selected.current.datasetId;
    if (!active.current || requests.current.has(resource) || (resource !== 'list' && !id)) return;
    const controller = new AbortController(); const request = { controller, id };
    requests.current.set(resource, request);
    setLoading((previous) => ({ ...previous, [resource]: true }));
    const owns = () => active.current && requests.current.get(resource) === request
      && (resource === 'list' || selected.current.datasetId === id);
    const label = resource === 'list' ? '数据集列表' : resource === 'detail' ? '数据集详情' : '验证记录';
    try {
      const root = '/api/validation/datasets';
      const url = resource === 'list' ? root : `${root}/${encodeURIComponent(id!)}${resource === 'runs' ? '/runs' : ''}`;
      const data: unknown = await withRequestTimeout(controller, 15_000, async () => {
        const response = await apiFetch(url, { signal: controller.signal });
        controller.signal.throwIfAborted();
        if (!response.ok) throw new Error(await responseErrorMessage(response, `${label}读取失败（HTTP ${response.status}）`));
        return response.json();
      });
      if (!owns() || controller.signal.aborted) return;
      if (resource === 'list') {
        if (!Array.isArray(data) || !data.every(isDatasetSummary)) throw new Error('数据集列表响应无效');
        setDatasets(data);
        if (!data.some((item) => item.id === selected.current.datasetId)) chooseDataset(data[0]?.id);
      } else if (resource === 'detail') {
        if (!isDatasetDetail(data, id!)) throw new Error('数据集详情与当前选择不匹配或不完整');
        setDetail(data);
      } else {
        if (!Array.isArray(data) || !data.every((run) => isValidationRun(run, id!))) throw new Error('验证记录与当前数据集不匹配或不完整');
        for (const run of data) acknowledged.current.delete(run.runId);
        setRecords({ id: id!, runs: [...acknowledged.current.values()].filter((run) => run.datasetId === id).concat(data) });
        setRunsRevision((value) => value + 1);
      }
      setFailures((previous) => ({ ...previous, [resource]: undefined }));
    } catch (reason) {
      if (owns()) setFailures((previous) => ({ ...previous, [resource]: { id, text: controller.signal.aborted ? `${label}读取超时（15 秒）`
        : reason instanceof Error ? reason.message : `${label}读取失败` } }));
    } finally {
      if (requests.current.get(resource) === request) {
        requests.current.delete(resource);
        if (active.current) setLoading((previous) => ({ ...previous, [resource]: false }));
      }
    }
  }, [chooseDataset]);
  useEffect(() => {
    active.current = open;
    if (open) { setDatasets([]); setDetail(undefined); setRecords(undefined); setFailures({}); setLoading({}); void read('list'); }
    return () => { active.current = false; for (const request of requests.current.values()) request.controller.abort(); requests.current.clear(); acknowledged.current.clear(); };
  }, [open, read]);
  useEffect(() => { if (open && selection.datasetId) { void read('detail'); void read('runs'); } }, [open, selection.datasetId, scopeRevision, read]);
  const datasetId = selection.datasetId;
  const dataset = detail?.dataset.id === datasetId ? detail : undefined;
  const runs = records && records.id === datasetId ? records.runs : [];
  const hasRunning = runs.some((run) => run.status === 'Running');
  useEffect(() => {
    if (!open || !datasetId || !hasRunning) return;
    const timer = setInterval(() => void read('runs'), 1000);
    return () => clearInterval(timer);
  }, [open, datasetId, hasRunning, read]);
  const sample = dataset?.items.find((item) => item.itemId === selection.itemId && item.replayReady) ?? dataset?.items.find((item) => item.replayReady);
  const errors = Object.fromEntries(Object.entries(failures).filter(([key, failure]) => failure && (key === 'list' || failure.id === datasetId))
    .map(([key, failure]) => [key, failure.text])) as Partial<Record<Resource, string>>;
  const acceptRun = (run: ValidationRunRecord) => {
    if (!active.current || run.datasetId !== selected.current.datasetId || !isValidationRun(run, run.datasetId)) return;
    stop('runs');
    acknowledged.current.set(run.runId, run);
    setRecords((previous) => ({ id: run.datasetId, runs: [run, ...(previous?.id === run.datasetId ? previous.runs.filter((item) => item.runId !== run.runId) : [])] }));
    void read('runs');
  };
  return { datasets, datasetId, dataset, runs, sample, itemId: sample?.itemId, errors, loading, runsRevision, scopeRevision,
    runsFresh: Boolean(records && records.id === datasetId && !errors.runs && !loading.runs),
    ready: Boolean(open && dataset && sample && !errors.detail && !loading.detail),
    chooseDataset, chooseItem: (itemId: string) => { selected.current = { ...selected.current, itemId }; setSelection(selected.current); },
    refresh: () => { void read('list'); void read('detail'); void read('runs'); },
    refreshRuns: () => { stop('runs'); void read('runs'); }, acceptRun };
}
