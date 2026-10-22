import type { ParameterTuningPreviewResult, ValidationDatasetDetail, ValidationDatasetItem, ValidationRunRecord, WorkflowPayload } from '../../types';

export const tuningWorkflow: WorkflowPayload = { id: 'workflow', name: 'Tuning',
  nodes: [{ id: 'source', name: 'Source', type: 'image.synthetic', parameters: { width: 320, height: 240 } }], edges: [] };
export const tuningItem = (datasetId = 'A', itemId = 'sample'): ValidationDatasetItem => ({ datasetId, itemId,
  sourceKind: 'TRACE', sourceRef: `trace-${datasetId}-${itemId}`, expectedDisposition: 'OK', displayName: `${datasetId}-${itemId}`,
  sourceDisposition: 'OK', sortOrder: 0, addedAt: '2026-10-20T10:00:00Z', replayReady: true });
export const tuningDataset = (id = 'A'): ValidationDatasetDetail => ({ dataset: { id, name: `Dataset ${id}`, description: '',
  itemCount: 1, topologyHash: 'topology', createdAt: '', updatedAt: '' }, items: [tuningItem(id)] });
export const tuningRun = (datasetId = 'A', runId = 'validation-1', status = 'Completed'): ValidationRunRecord => ({ datasetId, runId, status,
  sourceWorkflowRunId: 'source', workflowHash: 'hash', requestedCount: 1, completedCount: status === 'Completed' ? 1 : 0, startedAt: '', cancelRequested: false,
  summary: status !== 'Completed' ? undefined : { total: 1, completed: 1, trueOk: 1, trueNg: 0, falseOk: 0, falseNg: 0, errors: 0,
    accuracy: 1, falseOkRate: 0, falseNgRate: 0, p50DurationMs: 1, p95DurationMs: 2, p99DurationMs: 2, maxDurationMs: 2 } });
export function tuningPreview(fullWorkflow = false, item = tuningItem(), runId = 'preview-1'): ParameterTuningPreviewResult {
  const report = { nodeId: 'source', nodeType: 'image.synthetic', success: true, durationMs: 1, summary: { width: 320 } };
  return { datasetId: item.datasetId, itemId: item.itemId, sourceKind: item.sourceKind, sourceRef: item.sourceRef,
    expectedDisposition: item.expectedDisposition, fullWorkflow, classification: fullWorkflow ? 'TRUE_OK' : null,
    actualDisposition: fullWorkflow ? 'OK' : null, selectedNodeReport: report,
    replay: { runId, sourceRunId: item.sourceRef, success: true, totalDurationMs: 2, previewAvailable: true,
      previewWidth: 320, previewHeight: 240, overlays: [], nodeReports: [report], comparison: [], dispositionChanged: false, executionModel: 'offline' } };
}
export const jsonResponse = (data: unknown, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => data }) as Response;
export function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }
