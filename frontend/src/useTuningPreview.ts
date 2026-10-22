import { useEffect, useRef, useState } from 'react';
import type { ParameterTuningPreviewResult, ValidationDatasetItem, WorkflowPayload } from './types';
import { responseErrorMessage } from './apiErrors';
import { isTuningPreview } from './tuningContracts';
import { withRequestTimeout } from './requestTimeout';
import { apiFetch } from './apiClient';

type Params = { open: boolean; workflow: WorkflowPayload; nodeId?: string; sample?: ValidationDatasetItem; ready: boolean; autoPreview: boolean; revision: number };
type Operation = { controller: AbortController; key: string; fullWorkflow: boolean; automatic: boolean };

/** 编辑立即使旧预览失效；550 毫秒只用于延后下一次自动请求。 */
export function useTuningPreview(params: Params) {
  const snapshot = JSON.stringify(params.workflow);
  const { sample } = params;
  const imageKey = JSON.stringify([params.nodeId, sample?.datasetId, sample?.itemId, sample?.sourceKind, sample?.sourceRef]);
  const key = JSON.stringify([snapshot, params.nodeId, params.revision, params.ready,
    sample?.datasetId, sample?.itemId, sample?.sourceKind, sample?.sourceRef, sample?.expectedDisposition, sample?.replayReady]);
  const latest = useRef({ ...params, snapshot, key, imageKey }); latest.current = { ...params, snapshot, key, imageKey };
  const active = useRef(false);
  const operation = useRef<Operation | undefined>(undefined);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [state, setState] = useState<{ key: string; preview?: ParameterTuningPreviewResult; busy?: boolean; error?: string; automatic?: boolean; fullWorkflow?: boolean }>();
  const [background, setBackground] = useState<{ key: string; url: string; width?: number; height?: number }>();
  const invalidate = () => {
    clearTimeout(timer.current); timer.current = undefined;
    operation.current?.controller.abort(); operation.current = undefined;
  };
  useEffect(() => {
    active.current = params.open;
    setState(undefined);
    if (!params.open) setBackground(undefined);
    return () => { active.current = false; invalidate(); };
  }, [params.open, key]);

  const run = async (fullWorkflow: boolean, automatic = false): Promise<boolean> => {
    const context = latest.current;
    if (!active.current || !context.ready || !context.sample?.replayReady || !context.nodeId) return false;
    const pending = operation.current;
    if (pending?.key === context.key && pending.fullWorkflow === fullWorkflow && (!pending.automatic || automatic)) return false;
    invalidate();
    const controller = new AbortController();
    const value: Operation = { controller, key: context.key, fullWorkflow, automatic };
    operation.current = value;
    setState({ key: context.key, busy: true, automatic, fullWorkflow });
    const owns = () => active.current && operation.current === value && latest.current.key === value.key;
    try {
      const data: unknown = await withRequestTimeout(controller, 60_000, async () => {
        const response = await apiFetch('/api/tuning/preview', { method: 'POST', signal: controller.signal,
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ datasetId: context.sample.datasetId,
            itemId: context.sample.itemId, workflow: JSON.parse(context.snapshot), targetNodeId: context.nodeId, fullWorkflow }) });
        controller.signal.throwIfAborted();
        if (!response.ok) throw new Error(await responseErrorMessage(response, `离线预览失败（HTTP ${response.status}）`));
        return response.json();
      });
      if (!owns() || controller.signal.aborted) return false;
      if (!isTuningPreview(data, context.sample.datasetId, context.sample.itemId, context.nodeId, fullWorkflow)
        || data.sourceKind !== context.sample.sourceKind || data.sourceRef !== context.sample.sourceRef
        || data.expectedDisposition !== context.sample.expectedDisposition
        || (data.selectedNodeReport && data.selectedNodeReport.nodeType !== context.workflow.nodes.find((node) => node.id === context.nodeId)?.type)) {
        throw new Error('预览响应与当前样本、节点或运行模式不匹配');
      }
      setState({ key: context.key, preview: data });
      setBackground(data.replay.previewAvailable ? { key: context.imageKey, url: `/api/runs/${encodeURIComponent(data.replay.runId)}/preview`,
        width: data.replay.previewWidth, height: data.replay.previewHeight } : undefined);
      return true;
    } catch (reason) {
      if (owns()) setState({ key: context.key, error: controller.signal.aborted ? '离线预览超时（60 秒），请检查流程后重试。'
        : reason instanceof Error ? reason.message : '离线预览失败' });
      return false;
    } finally {
      if (operation.current === value) {
        operation.current = undefined;
        if (active.current && latest.current.key === context.key) setState((previous) => previous?.key === context.key ? { ...previous, busy: false } : previous);
      }
    }
  };

  useEffect(() => {
    if (!params.open || !params.autoPreview || !params.ready || !sample?.replayReady || !params.nodeId) return;
    timer.current = setTimeout(() => { timer.current = undefined; void run(false, true); }, 550);
    return () => {
      clearTimeout(timer.current); timer.current = undefined;
      if (operation.current?.automatic) {
        operation.current.controller.abort(); operation.current = undefined;
        if (active.current) setState((previous) => previous && { ...previous, busy: false });
      }
    };
  }, [params.open, params.autoPreview, key]);

  const current = params.open && state?.key === key ? state : undefined;
  const preview = current?.preview;
  const image = params.open && background?.key === imageKey ? background : undefined;
  return { preview, busy: Boolean(current?.busy), error: current?.error, run,
    automatic: Boolean(current?.automatic), fullWorkflow: preview?.fullWorkflow ?? current?.fullWorkflow,
    previewUrl: image?.url, imageWidth: image?.width, imageHeight: image?.height, backgroundStale: Boolean(image && !preview) };
}
