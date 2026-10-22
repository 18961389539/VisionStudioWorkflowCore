import { useCallback, useEffect, useRef, useState } from 'react';
import type { AlarmRecord, ProductionRuntimeConfig, ProductionRuntimeStatus } from './types';
import { responseErrorMessage } from './apiErrors';
import { isProductionConfig, isProductionStatus } from './productionConfig';
import { apiFetch } from './apiClient';

export type ProductionJobItem = { id: string; name: string; publishedVersion?: number | null };
export type DeviceActionPendingState = {
  runId?: string | null; manifestHash?: string | null; deviceIds?: string[]; robotIds?: string[];
  deviceFingerprints?: Record<string, string>; phase?: string; setAt?: string | null;
  reason?: string | null; unreadable?: boolean;
  manualIntents?: { runId: string; action: string; deviceIds: string[]; robotIds: string[]; cameraIds: string[]; setAt: string; state: string; closedReason?: string | null }[];
};
type Snapshot = {
  status?: ProductionRuntimeStatus; config?: ProductionRuntimeConfig; jobs: ProductionJobItem[];
  alarms: AlarmRecord[]; pending?: DeviceActionPendingState;
};
type Resource = keyof Snapshot;
const endpoints: Record<Resource, { url: string; label: string }> = {
  status: { url: '/api/production/status', label: '生产状态' },
  config: { url: '/api/production/config', label: '生产配置' },
  jobs: { url: '/api/jobs', label: '配方列表' },
  alarms: { url: '/api/alarms?activeOnly=false', label: '告警记录' },
  pending: { url: '/api/production/device-actions/pending', label: '待核对动作清单' },
};
const resources = Object.keys(endpoints) as Resource[];
type Request = { controller: AbortController; timeout: ReturnType<typeof setTimeout> };
const isObject = (value: unknown): value is Record<string, unknown> => Boolean(value && typeof value === 'object' && !Array.isArray(value));
const stringList = (value: unknown): boolean => Array.isArray(value) && value.every((item) => typeof item === 'string');
function validPending(value: unknown): boolean {
  return isObject(value)
    && ['runId', 'manifestHash', 'phase', 'setAt', 'reason'].every((key) => value[key] == null || typeof value[key] === 'string')
    && ['deviceIds', 'robotIds'].every((key) => value[key] == null || stringList(value[key]))
    && (value.manualIntents == null || (Array.isArray(value.manualIntents) && value.manualIntents.every((intent) => isObject(intent)
      && ['runId', 'action', 'state'].every((key) => typeof intent[key] === 'string')
      && ['deviceIds', 'robotIds', 'cameraIds'].every((key) => stringList(intent[key])))));
}
function validList(value: unknown, resource: 'jobs' | 'alarms'): boolean {
  return Array.isArray(value) && value.every((item) => isObject(item) && typeof item.id === 'string'
    && (resource === 'jobs' ? typeof item.name === 'string'
      && (item.publishedVersion == null || (Number.isInteger(item.publishedVersion) && Number(item.publishedVersion) > 0))
      : typeof item.active === 'boolean' && ['severity', 'message', 'raisedAt'].every((key) => typeof item[key] === 'string')));
}

/** 每个资源独立限时、独立提交；慢配置与失败正文不能阻断现场清单和状态刷新。 */
export function useProductionMonitor(open: boolean) {
  const [snapshot, setSnapshot] = useState<Snapshot>({ jobs: [], alarms: [] });
  const [errors, setErrors] = useState<Partial<Record<Resource, string>>>({});
  const [confirmed, setConfirmed] = useState<Partial<Record<Resource, boolean>>>({});
  const [lastUpdatedAt, setLastUpdatedAt] = useState<Date>();
  const active = useRef(false);
  const requests = useRef(new Map<Resource, Request>());

  const accept = useCallback((resource: Resource, data: unknown) => {
    setSnapshot((previous) => ({ ...previous, [resource]: data }));
    setErrors((previous) => ({ ...previous, [resource]: undefined }));
    setConfirmed((previous) => ({ ...previous, [resource]: true }));
    if (resource === 'status') setLastUpdatedAt(new Date());
  }, []);

  const read = useCallback((resource: Resource) => {
    if (!active.current || requests.current.has(resource)) return;
    const { url, label } = endpoints[resource];
    const controller = new AbortController();
    const owns = () => active.current && requests.current.get(resource) === request;
    const request: Request = { controller, timeout: setTimeout(() => {
      if (!owns()) return;
      requests.current.delete(resource);
      controller.abort();
      setErrors((previous) => ({ ...previous, [resource]: `${label}读取超时（5 秒），正在重试。` }));
    }, 5000) };
    requests.current.set(resource, request);
    void (async () => {
      try {
        const response = await apiFetch(url, { signal: controller.signal });
        if (!owns() || controller.signal.aborted) return;
        if (resource === 'pending' && response.status === 204) { accept(resource, undefined); return; }
        if (!response.ok) throw new Error(await responseErrorMessage(response, `${label}读取失败（HTTP ${response.status}）`));
        const data: unknown = await response.json();
        if (!owns() || controller.signal.aborted) return;
        if ((resource === 'config' && !isProductionConfig(data)) || (resource === 'status' && !isProductionStatus(data))
          || ((resource === 'jobs' || resource === 'alarms') && !validList(data, resource))
          || (resource === 'pending' && !validPending(data))) {
          throw new Error(`${label}响应无效。`);
        }
        accept(resource, data);
      } catch (reason) {
        if (owns()) setErrors((previous) => ({ ...previous, [resource]: reason instanceof Error ? reason.message : `${label}读取失败` }));
      } finally {
        clearTimeout(request.timeout);
        // 关闭/重开和写入确认会让旧请求失效；旧 finally 不能释放新请求的所有权。
        if (requests.current.get(resource) === request) requests.current.delete(resource);
      }
    })();
  }, [accept]);

  const refresh = useCallback(() => { resources.forEach(read); }, [read]);
  useEffect(() => {
    active.current = open;
    setConfirmed({});
    if (open) refresh();
    const timer = open ? setInterval(refresh, 1000) : undefined;
    return () => {
      active.current = false;
      clearInterval(timer);
      for (const request of requests.current.values()) { clearTimeout(request.timeout); request.controller.abort(); }
      requests.current.clear();
    };
  }, [open, refresh]);

  const acceptWrite = useCallback((resource: 'config' | 'status', value: ProductionRuntimeConfig | ProductionRuntimeStatus) => {
    if (!active.current) return;
    const pending = requests.current.get(resource);
    if (pending) { requests.current.delete(resource); clearTimeout(pending.timeout); pending.controller.abort(); }
    accept(resource, value);
  }, [accept]);
  return { ...snapshot, errors, lastUpdatedAt, refresh,
    statusFresh: open && Boolean(confirmed.status) && !errors.status,
    configReady: open && Boolean(confirmed.config) && !errors.config,
    acceptConfig: (value: ProductionRuntimeConfig) => acceptWrite('config', value),
    acceptStatus: (value: ProductionRuntimeStatus) => acceptWrite('status', value),
  };
}
