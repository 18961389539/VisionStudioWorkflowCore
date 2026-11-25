import { useCallback, useEffect, useRef, useState } from 'react';
import type { AlarmRecord, ContractDto, ProductionRuntimeConfig, ProductionRuntimeStatus } from './types';
import { responseErrorMessage } from './apiErrors';
import { isProductionConfig, isProductionStatus } from './productionConfig';
import { apiFetch } from './apiClient';
import { alarms as alarmsEndpoint, apiUrl, production, type AlarmFilter } from './endpoints';
import { useMonitorSnapshot } from './useMonitorStream';

export type ProductionJobItem = { id: string; name: string; publishedVersion?: number | null };
export type DeviceActionPendingState = {
  runId?: string | null; manifestHash?: string | null; deviceIds?: string[]; robotIds?: string[];
  deviceFingerprints?: Record<string, string>; phase?: string; setAt?: string | null;
  reason?: string | null; unreadable?: boolean;
  manualIntents?: { runId: string; action: string; deviceIds: string[]; robotIds: string[]; cameraIds: string[]; setAt: string; state: string; closedReason?: string | null }[];
};
type AlarmSummary = ContractDto<'AlarmSummary'>;
type AlarmHistoryPage = { items: AlarmRecord[]; total: number };
type AlarmQuery = { page: number; filter: AlarmFilter };
const emptyAlarmHistory: AlarmHistoryPage = { items: [], total: 0 };
const defaultAlarmQuery: AlarmQuery = { page: 1, filter: 'all' };
export const ALARM_PAGE_SIZE = 8;

type Snapshot = {
  status?: ProductionRuntimeStatus; config?: ProductionRuntimeConfig; jobs: ProductionJobItem[];
  alarms: AlarmHistoryPage; alarmSummary?: AlarmSummary; pending?: DeviceActionPendingState;
};
type Resource = keyof Snapshot;
const endpoints: Record<Resource, { url: string; label: string }> = {
  status: { url: apiUrl('production/status'), label: '生产状态' },
  config: { url: apiUrl('production/config'), label: '生产配置' },
  jobs: { url: apiUrl('jobs'), label: '配方列表' },
  alarms: { url: alarmsEndpoint.list, label: '告警记录' },
  alarmSummary: { url: alarmsEndpoint.summary(), label: '活动告警统计' },
  pending: { url: apiUrl('production/device-actions/pending'), label: '待核对动作清单' },
};
const resources = Object.keys(endpoints) as Resource[];
type Request = { controller: AbortController; timeout: ReturnType<typeof setTimeout>; refreshPending: boolean };
function endpointUrl(resource: Resource, scopedStationId?: string): string {
  if (!scopedStationId) return endpoints[resource].url;
  switch (resource) {
    case 'status': return production.runtimeStatus(scopedStationId);
    case 'config': return production.runtimeConfig(scopedStationId);
    case 'alarms': return alarmsEndpoint.page(0, ALARM_PAGE_SIZE, 'all', scopedStationId);
    case 'alarmSummary': return alarmsEndpoint.summary(scopedStationId);
    case 'pending': return production.runtimePendingActions(scopedStationId);
    case 'jobs': return endpoints.jobs.url;
  }
}
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

const isCount = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
function validAlarmSummary(value: unknown): boolean {
  return isObject(value) && isCount(value.activeCount) && isCount(value.criticalCount)
    && isCount(value.pendingCount) && value.criticalCount <= value.activeCount && value.pendingCount <= value.activeCount
    && (value.latestActiveMessage === null || typeof value.latestActiveMessage === 'string');
}

/** 每个资源独立限时、独立提交；慢配置与失败正文不能阻断现场清单和状态刷新。 */
export function useProductionMonitor(open: boolean, canReadPending = true, stationId?: string) {
  const scopedStationId = stationId && stationId.toLowerCase() !== 'default' ? stationId : undefined;
  const [snapshot, setSnapshot] = useState<Snapshot>({ jobs: [], alarms: emptyAlarmHistory });
  const [errors, setErrors] = useState<Partial<Record<Resource, string>>>({});
  const [confirmed, setConfirmed] = useState<Partial<Record<Resource, boolean>>>({});
  const [lastUpdatedAt, setLastUpdatedAt] = useState<Date>();
  const [configRevision, setConfigRevision] = useState<string>();
  const [alarmQuery, setAlarmQuery] = useState(defaultAlarmQuery);
  const alarmQueryRef = useRef(defaultAlarmQuery);
  const [alarmsLoading, setAlarmsLoading] = useState(false);
  const active = useRef(false);
  const requests = useRef(new Map<Resource, Request>());
  const statusClock = useRef<{ epoch: string; version: number } | undefined>(undefined);
  const retiredStatusEpochs = useRef(new Set<string>());

  const cancelRead = useCallback((resource: Resource) => {
    const pending = requests.current.get(resource);
    if (!pending) return;
    requests.current.delete(resource);
    clearTimeout(pending.timeout);
    pending.controller.abort();
  }, []);

  const accept = useCallback((resource: Resource, data: unknown, revision?: string) => {
    if (resource === 'status') {
      const { statusEpoch: epoch, statusVersion: version } = data as ProductionRuntimeStatus;
      const current = statusClock.current;
      if (epoch && typeof version === 'number') {
        if (retiredStatusEpochs.current.has(epoch) || (current?.epoch === epoch && current.version > version)) return false;
        if (current && current.epoch !== epoch) retiredStatusEpochs.current.add(current.epoch);
        statusClock.current = { epoch, version };
      } else if (current) {
        return false;
      }
      setLastUpdatedAt(new Date());
    }
    if (resource === 'config') setConfigRevision(revision);
    setSnapshot((previous) => ({ ...previous, [resource]: data }));
    setErrors((previous) => ({ ...previous, [resource]: undefined }));
    setConfirmed((previous) => ({ ...previous, [resource]: true }));
    return true;
  }, []);

  const read = useCallback(function readResource(resource: Resource, queueIfBusy = false) {
    if (!active.current) return;
    const inFlight = requests.current.get(resource);
    if (inFlight) {
      if (queueIfBusy) inFlight.refreshPending = true;
      return;
    }
    const { label } = endpoints[resource];
    const url = endpointUrl(resource, scopedStationId);
    const { page, filter } = alarmQueryRef.current;
    const skip = (page - 1) * ALARM_PAGE_SIZE;
    const statusAtStart = statusClock.current;
    const mayReportError = () => resource !== 'status' || statusClock.current === statusAtStart;
    const controller = new AbortController();
    const owns = () => active.current && requests.current.get(resource) === request;
    const finish = () => {
      if (!owns()) return;
      requests.current.delete(resource);
      if (request.refreshPending) readResource(resource);
      else if (resource === 'alarms') setAlarmsLoading(false);
    };
    const request: Request = {
      controller,
      refreshPending: false,
      timeout: setTimeout(() => {
        if (!owns()) return;
        controller.abort();
        if (mayReportError()) setErrors((previous) => ({ ...previous, [resource]: `${label}读取超时（5 秒），正在重试。` }));
        finish();
      }, 5000),
    };
    requests.current.set(resource, request);
    void (async () => {
      try {
        const response = await apiFetch(resource === 'alarms'
          ? alarmsEndpoint.page(skip, ALARM_PAGE_SIZE, filter, scopedStationId)
          : url, { signal: controller.signal });
        if (!owns() || controller.signal.aborted) return;
        if (resource === 'pending' && response.status === 204) { accept(resource, undefined); return; }
        if (!response.ok) throw new Error(await responseErrorMessage(response, `${label}读取失败（HTTP ${response.status}）`));
        let data: unknown = await response.json();
        if (!owns() || controller.signal.aborted) return;
        if ((resource === 'config' && !isProductionConfig(data)) || (resource === 'status' && !isProductionStatus(data))
          || ((resource === 'jobs' || resource === 'alarms') && !validList(data, resource))
          || (resource === 'alarmSummary' && !validAlarmSummary(data))
          || (resource === 'pending' && !validPending(data))) {
          throw new Error(`${label}响应无效。`);
        }
        if (resource === 'alarms') {
          const rawTotal = response.headers.get('X-Total-Count');
          const total = rawTotal && /^\d+$/.test(rawTotal) ? Number(rawTotal) : NaN;
          if (!isCount(total) || total < (data as AlarmRecord[]).length) throw new Error(`${label}分页总数无效。`);
          const lastPage = Math.max(1, Math.ceil(total / ALARM_PAGE_SIZE));
          if (page > lastPage) {
            alarmQueryRef.current = { page: lastPage, filter };
            setAlarmQuery(alarmQueryRef.current);
            setAlarmsLoading(true);
            request.refreshPending = true;
          }
          data = { items: data, total };
        }
        const revision = resource === 'config' ? response.headers.get('ETag') : undefined;
        if (resource === 'config' && !revision) throw new Error('生产配置响应缺少修订号。');
        accept(resource, data, revision ?? undefined);
      } catch (reason) {
        if (owns() && mayReportError()) setErrors((previous) => ({ ...previous, [resource]: reason instanceof Error ? reason.message : `${label}读取失败` }));
      } finally {
        clearTimeout(request.timeout);
        // 关闭/重开和写入确认会让旧请求失效；旧 finally 不能释放新请求的所有权。
        finish();
      }
    })();
  }, [accept, scopedStationId]);

  const refresh = useCallback(() => {
    resources.forEach((resource) => {
      if (resource !== 'pending' || canReadPending) read(resource, resource === 'alarms' || resource === 'alarmSummary');
    });
  }, [read, canReadPending]);
  const updateAlarmQuery = useCallback((query: AlarmQuery) => {
    const previousQuery = alarmQueryRef.current;
    const filterChanged = query.filter !== previousQuery.filter;
    if (!active.current || (!filterChanged && query.page === previousQuery.page)) return;
    alarmQueryRef.current = query;
    setAlarmQuery(query);
    cancelRead('alarms');
    setAlarmsLoading(true);
    setErrors((previous) => ({ ...previous, alarms: undefined }));
    setSnapshot((previous) => ({
      ...previous,
      alarms: { items: [], total: filterChanged ? 0 : previous.alarms.total },
    }));
    read('alarms');
  }, [read, cancelRead]);
  // 推送（2026-11-13）：生产状态直接来自 /hubs/monitor 快照，报警按版本戳补拉；
  // 其余资源（配置/配方/待核对动作）保留 10 s 兜底——它们的变化频率远低于状态与报警。
  const monitorSnapshot = useMonitorSnapshot(open);
  const alarmRevisionRef = useRef<string | undefined>(undefined);
  useEffect(() => {
    // 面板重新打开时，监视钩子可能仍留有上一次订阅的快照。
    if (!open || !monitorSnapshot || !active.current) return;
    if (!scopedStationId && isProductionStatus(monitorSnapshot.production)) accept('status', monitorSnapshot.production);
    if (monitorSnapshot.alarmRevision !== alarmRevisionRef.current) {
      alarmRevisionRef.current = monitorSnapshot.alarmRevision;
      read('alarms', true);
      read('alarmSummary', true);
    }
  }, [monitorSnapshot, open, accept, read, scopedStationId]);

  useEffect(() => {
    active.current = open;
    setConfirmed({});
    if (open) {
      setAlarmsLoading(true);
      refresh();
    }
    const timer = open ? setInterval(refresh, 10_000) : undefined;
    return () => {
      active.current = false;
      clearInterval(timer);
      for (const request of requests.current.values()) { clearTimeout(request.timeout); request.controller.abort(); }
      requests.current.clear();
    };
  }, [open, refresh]);

  const acceptWrite = useCallback((resource: 'config' | 'status', value: ProductionRuntimeConfig | ProductionRuntimeStatus, revision?: string) => {
    if (!active.current || !accept(resource, value, revision)) return;
    cancelRead(resource);
  }, [accept, cancelRead]);
  return { ...snapshot, alarms: snapshot.alarms.items, alarmTotal: snapshot.alarms.total, alarmsLoading,
    alarmPage: alarmQuery.page, alarmFilter: alarmQuery.filter,
    setAlarmPage: (page: number) => updateAlarmQuery({ page, filter: alarmQueryRef.current.filter }),
    setAlarmFilter: (filter: AlarmFilter) => updateAlarmQuery({ page: 1, filter }),
    invalidateAlarmReads: () => { cancelRead('alarms'); cancelRead('alarmSummary'); },
    errors, lastUpdatedAt, refresh,
    statusFresh: open && Boolean(confirmed.status) && !errors.status,
    configReady: open && Boolean(configRevision && confirmed.config) && !errors.config,
    configRevision,
    acceptConfig: (value: ProductionRuntimeConfig, revision: string) => acceptWrite('config', value, revision),
    acceptStatus: (value: ProductionRuntimeStatus) => acceptWrite('status', value),
  };
}
