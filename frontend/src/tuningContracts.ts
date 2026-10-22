import type { ParameterTuningPreviewResult, ValidationDatasetDetail, ValidationDatasetSummary, ValidationRunRecord } from './types';
import { isRunResult } from './runResult';

const object = (value: unknown): value is Record<string, unknown> => Boolean(value && typeof value === 'object' && !Array.isArray(value));
const finite = (value: unknown) => typeof value === 'number' && Number.isFinite(value);
export function isDatasetSummary(value: unknown): value is ValidationDatasetSummary {
  return object(value) && typeof value.id === 'string' && typeof value.name === 'string' && finite(value.itemCount);
}
export function isDatasetDetail(value: unknown, id: string): value is ValidationDatasetDetail {
  return object(value) && isDatasetSummary(value.dataset) && value.dataset.id === id && Array.isArray(value.items)
    && value.items.every((item) => object(item) && item.datasetId === id && typeof item.itemId === 'string'
      && ['TRACE', 'MEDIA'].includes(String(item.sourceKind)) && typeof item.sourceRef === 'string'
      && ['OK', 'NG'].includes(String(item.expectedDisposition)) && typeof item.replayReady === 'boolean' && typeof item.displayName === 'string');
}
export function isValidationRun(value: unknown, id: string): value is ValidationRunRecord {
  return object(value) && value.datasetId === id && typeof value.runId === 'string' && value.runId.length > 0
    && ['Running', 'Completed', 'Cancelled', 'Failed'].includes(String(value.status)) && typeof value.workflowHash === 'string'
    && finite(value.requestedCount) && finite(value.completedCount)
    && (value.summary == null || (object(value.summary) && ['total', 'completed', 'trueOk', 'trueNg', 'falseOk', 'falseNg', 'errors',
      'accuracy', 'falseOkRate', 'falseNgRate', 'p50DurationMs', 'p95DurationMs', 'p99DurationMs', 'maxDurationMs'].every((key) => finite(value.summary[key]))));
}
export function isTuningPreview(value: unknown, datasetId: string, itemId: string, nodeId: string, fullWorkflow: boolean): value is ParameterTuningPreviewResult {
  if (!object(value) || value.datasetId !== datasetId || value.itemId !== itemId || value.fullWorkflow !== fullWorkflow || !isRunResult(value.replay)) return false;
  const report = value.selectedNodeReport;
  return ['OK', 'NG'].includes(String(value.expectedDisposition)) && ['TRACE', 'MEDIA'].includes(String(value.sourceKind)) && typeof value.sourceRef === 'string'
    && (report == null || (object(report) && report.nodeId === nodeId && typeof report.success === 'boolean' && finite(report.durationMs) && object(report.summary)));
}
