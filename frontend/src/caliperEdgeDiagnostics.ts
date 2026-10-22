import { useEffect, useState } from 'react';
import type { NodeRunReport, OverlayPoint } from './types';
import { apiFetch } from './apiClient';

export type CaliperEdgeDiagnostic = {
  runId: string; nodeId: string; executionSequence: number;
  imageWidth: number; imageHeight: number; coordinateFrame?: string;
  start: OverlayPoint; end: OverlayPoint; profileStart?: OverlayPoint | null; profileEnd?: OverlayPoint | null;
  clipped: boolean; startOffset: number; spacing: number; minContrast: number; sampleWidth: number;
  polarity: string; direction: string; refine: string; found: boolean; reason: string;
  profile: (number | null)[]; sampleCount: number; validSampleCount: number;
  candidates: { position: number; amplitude: number; method: string }[]; selectedCandidateIndex?: number | null;
  edgePosition?: number | null; edgePoint?: OverlayPoint | null; edgeStrength?: number | null; fitError?: number | null;
};

export const caliperEdgeReasons: Record<string, string> = {
  ok: '已找到边缘', 'search-line-outside-image': '搜索线完全位于图像外',
  'search-line-clipped-too-short': '搜索线裁切后短于 2 px',
  'profile-has-no-finite-samples': 'ROI 内无有效采样',
  'profile-has-insufficient-contiguous-samples': '连续有效采样不足',
  'no-edge-above-threshold': '无符合阈值和极性的边缘'
};

export function caliperProfilePoint(data: CaliperEdgeDiagnostic, position: number): OverlayPoint | undefined {
  if (!data.profileStart || !data.profileEnd || !Number.isFinite(position)) return undefined;
  const dx = data.profileEnd.x - data.profileStart.x, dy = data.profileEnd.y - data.profileStart.y;
  const length = Math.hypot(dx, dy);
  if (length <= 0) return undefined;
  return { x: data.profileStart.x + dx * position / length, y: data.profileStart.y + dy * position / length };
}

export function useCaliperEdgeDiagnostics({ enabled, runId, nodeId, report }: {
  enabled: boolean; runId?: string; nodeId: string; report?: NodeRunReport;
}) {
  const available = report?.success === true && report.summary.caliperEdgeDiagnosticsAvailable === true;
  const key = enabled && available && runId ? `${runId}:${nodeId}:${report?.executionSequence ?? 0}` : undefined;
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<{ key: string; data?: CaliperEdgeDiagnostic; error?: string }>();
  useEffect(() => {
    if (!key || !runId) return;
    const controller = new AbortController();
    let cancelled = false;
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    setState({ key });
    void (async () => {
      const response = await apiFetch(`/api/runs/${encodeURIComponent(runId)}/nodes/${encodeURIComponent(nodeId)}/caliper-edge-diagnostics`, { signal: controller.signal });
      if (!response.ok) {
        if (response.status === 404) throw new Error('诊断已过期或未捕获，请重新运行此节点。');
        if (response.status === 403) throw new Error('当前用户无权查看卡尺诊断。');
        throw new Error(`诊断读取失败 (${response.status})`);
      }
      const data = await response.json() as CaliperEdgeDiagnostic;
      const finitePoint = (point: OverlayPoint | null | undefined) => point && Number.isFinite(point.x) && Number.isFinite(point.y);
      if (data.runId !== runId || data.nodeId.toLowerCase() !== nodeId.toLowerCase()
        || data.executionSequence !== (report?.executionSequence ?? 0)
        || !finitePoint(data.start) || !finitePoint(data.end)
        || !Number.isFinite(data.imageWidth) || !Number.isFinite(data.imageHeight)
        || data.imageWidth <= 0 || data.imageHeight <= 0
        || !Number.isFinite(data.spacing) || data.spacing < 0 || !Number.isFinite(data.startOffset)
        || !Number.isFinite(data.minContrast) || data.minContrast < 0
        || !Array.isArray(data.profile) || !Array.isArray(data.candidates)
        || data.profile.some(value => value !== null && !Number.isFinite(value))
        || data.candidates.some(candidate => !Number.isFinite(candidate.position) || !Number.isFinite(candidate.amplitude))
        || data.profile.length > 0 && (!finitePoint(data.profileStart) || !finitePoint(data.profileEnd)))
        throw new Error('诊断与当前运行报告不一致，请重新运行节点。');
      if (!cancelled) setState({ key, data });
    })().catch(error => {
      if (!cancelled) setState({ key, error: controller.signal.aborted ? '诊断读取超时，请重试。' : String(error.message ?? error) });
    }).finally(() => window.clearTimeout(timeout));
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timeout); };
  }, [key, runId, nodeId, report?.executionSequence, attempt]);
  const current = key && state?.key === key ? state : undefined;
  return { available, data: current?.data, error: current?.error,
    loading: Boolean(key && !current?.data && !current?.error), retry: () => setAttempt(value => value + 1) };
}
