import { useEffect, useRef, useState } from 'react';
import type { NodeRunReport, OverlayPoint } from './types';
import { apiFetch } from './apiClient';
import { API_BASE } from './endpoints';

export type CaliperSpokeDiagnostic = {
  index: number; angleDeg: number; start: OverlayPoint; end: OverlayPoint; status: string;
  profileStart?: OverlayPoint | null; profileEnd?: OverlayPoint | null; clipped: boolean;
  edgePoint?: OverlayPoint | null; edgeRadius?: number | null; edgeStrength?: number | null;
  sampleCount: number; validSampleCount: number; candidateCount: number;
};
export type CaliperDiagnosticManifest = {
  runId: string; nodeId: string; executionSequence: number;
  imageWidth: number; imageHeight: number; coordinateFrame?: string;
  center: OverlayPoint; nominalRadius: number; radialBand: number;
  minContrast: number; sampleWidth: number; polarity: string; searchDirection: string;
  found: boolean; reason: string; spokes: CaliperSpokeDiagnostic[];
};
export type CaliperSpokeProfile = {
  runId: string; nodeId: string; executionSequence: number; index: number;
  startRadius: number; spacing: number; profile: (number | null)[];
  candidates: { position: number; amplitude: number; method: string }[];
  edgeRadius?: number | null; edgeStrength?: number | null;
};

export const caliperSpokeStatus: Record<string, string> = {
  used: '参与拟合', 'fit-outlier': '拟合离群点', 'fit-unavailable': '已有边缘，无法拟合',
  'radius-deviation': '超过半径偏差', 'outside-image': '卡尺位于图像外', 'clipped-too-short': '裁切后短于 2 px',
  'profile-has-no-finite-samples': 'ROI 内无有效采样',
  'profile-has-insufficient-contiguous-samples': '连续有效采样不足',
  'no-edge-above-threshold': '无符合阈值和极性的边缘'
};

async function readDiagnostic<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal });
  if (!response.ok) {
    if (response.status === 404) throw new Error('诊断已过期或未捕获，请重新运行此节点。');
    if (response.status === 403) throw new Error('当前用户无权查看卡尺诊断。');
    throw new Error(`诊断读取失败 (${response.status})`);
  }
  return await response.json() as T;
}

export function useCaliperCircleDiagnostics({ enabled, runId, nodeId, report }: {
  enabled: boolean; runId?: string; nodeId: string; report?: NodeRunReport;
}) {
  const available = report?.success === true && report.summary.caliperDiagnosticsAvailable === true;
  const key = enabled && available && runId ? `${runId}:${nodeId}:${report?.executionSequence ?? 0}` : undefined;
  const baseUrl = `${API_BASE}/runs/${encodeURIComponent(runId ?? '')}/nodes/${encodeURIComponent(nodeId)}/caliper-diagnostics`;
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [attempt, setAttempt] = useState(0);
  const [manifestState, setManifestState] = useState<{ key: string; data?: CaliperDiagnosticManifest; error?: string }>();
  const [profileState, setProfileState] = useState<{ key: string; index: number; data?: CaliperSpokeProfile; error?: string }>();
  const cache = useRef(new Map<string, CaliperSpokeProfile>());

  useEffect(() => {
    cache.current.clear();
    if (!key) return;
    const controller = new AbortController();
    let cancelled = false;
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    setManifestState({ key });
    void readDiagnostic<CaliperDiagnosticManifest>(baseUrl, controller.signal).then(data => {
      if (data.runId !== runId || data.nodeId.toLowerCase() !== nodeId.toLowerCase()
        || data.executionSequence !== (report?.executionSequence ?? 0) || !Array.isArray(data.spokes) || !data.spokes.length)
        throw new Error('诊断与当前运行报告不一致，请重新运行节点。');
      if (!cancelled) setManifestState({ key, data });
    }).catch(error => {
      if (!cancelled) setManifestState({ key, error: controller.signal.aborted ? '诊断读取超时，请重试。' : String(error.message ?? error) });
    }).finally(() => window.clearTimeout(timeout));
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timeout); };
  }, [key, attempt, baseUrl, nodeId, runId, report?.executionSequence]);

  const manifest = key && manifestState?.key === key ? manifestState.data : undefined;
  const index = manifest ? Math.min(manifest.spokes.length - 1, Math.max(0, selectedIndex)) : selectedIndex;
  useEffect(() => {
    if (!key || !manifest) return;
    const cacheKey = `${key}:${index}`;
    const cached = cache.current.get(cacheKey);
    if (cached) { setProfileState({ key, index, data: cached }); return; }
    const controller = new AbortController();
    let cancelled = false;
    const timeout = window.setTimeout(() => controller.abort(), 15_000);
    setProfileState({ key, index });
    void readDiagnostic<CaliperSpokeProfile>(`${baseUrl}?spokeIndex=${index}`, controller.signal).then(data => {
      if (data.runId !== manifest.runId || data.nodeId !== manifest.nodeId || data.index !== index
        || data.executionSequence !== manifest.executionSequence || !Array.isArray(data.profile) || !Array.isArray(data.candidates))
        throw new Error('卡尺剖面与当前选择不一致，请重试。');
      if (cancelled) return;
      // Keep only a few recent spokes; large search bands must not grow the editor cache indefinitely.
      if (cache.current.size >= 16) cache.current.delete(cache.current.keys().next().value!);
      cache.current.set(cacheKey, data);
      setProfileState({ key, index, data });
    }).catch(error => {
      if (!cancelled) setProfileState({ key, index, error: controller.signal.aborted ? '剖面读取超时，请重试。' : String(error.message ?? error) });
    }).finally(() => window.clearTimeout(timeout));
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timeout); };
  }, [baseUrl, key, manifest, index, attempt]);

  const currentProfile = profileState?.key === key && profileState?.index === index ? profileState : undefined;
  const error = key && manifestState?.key === key ? manifestState.error ?? currentProfile?.error : undefined;
  return { available, manifest, profile: currentProfile?.data, index,
    select: (next: number) => { if (Number.isFinite(next)) setSelectedIndex(Math.max(0, Math.round(next))); },
    loading: Boolean(key && !error && (!manifest || !currentProfile?.data)), error,
    retry: () => setAttempt(value => value + 1) };
}
