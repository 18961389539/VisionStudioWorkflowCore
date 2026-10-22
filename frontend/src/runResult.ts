import type { RunResult } from './types';

/**
 * 响应是否包含运行结果必需的身份、报告及耗时字段。
 * 执行完成但失败的结果同样带这些字段（哪怕节点报告为空：计划获取超时、执行前异常等），
 * 必须落地展示；请求被拒绝（租约冲突等 ProblemDetails）没有这些字段，不能进 applyResult。
 */
export function isRunResult(data: unknown): data is RunResult {
  if (!data || typeof data !== 'object') return false;
  const candidate = data as Partial<RunResult>;
  return typeof candidate.runId === 'string' && candidate.runId.length > 0
    && typeof candidate.success === 'boolean' && typeof candidate.previewAvailable === 'boolean' && Array.isArray(candidate.nodeReports)
    && candidate.nodeReports.every((report) => report && typeof report.nodeId === 'string'
      && typeof report.nodeType === 'string' && typeof report.success === 'boolean' && Number.isFinite(report.durationMs)
      && report.summary && typeof report.summary === 'object' && !Array.isArray(report.summary))
    && Number.isFinite(candidate.totalDurationMs)
    && (candidate.overlays == null || Array.isArray(candidate.overlays))
    && (candidate.controlFlowDecisions == null || (Array.isArray(candidate.controlFlowDecisions)
      && candidate.controlFlowDecisions.every((decision) => decision && typeof decision.nodeId === 'string'
        && Array.isArray(decision.activeBranches) && decision.activeBranches.every((branch) => typeof branch === 'string'))));
}
