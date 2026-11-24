import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { useEffect, useState } from 'react';
import type { CameraDescriptor, ContractDto, DeviceDescriptor, ProductionRuntimeStatus, RobotDescriptor } from './types';

/**
 * 设备监视推送（2026-11-12）。
 *
 * 背景：三个面板此前各自 `setInterval` 拉列表——RobotPanel 300 ms、CameraPanel 1 s、
 * DevicePanel 1 s。单客户端十几个请求/秒，而返回内容绝大多数没变化。
 *
 * 现在：服务端每秒采集一次描述符，**内容变了才广播** `monitorSnapshot`；面板收到通知后
 * 再拉自己需要的详情，定时器降为 10 s 兜底（负责断线后不永远停在旧状态）。
 *
 * 连接是模块级单例：三个面板同时打开只建一条 WebSocket。最后一个订阅者卸载时断开，
 * 避免"面板关了连接还挂着"。
 */

export type MonitorSnapshot = {
  capturedAt: string;
  cameras: CameraDescriptor[];
  devices: DeviceDescriptor[];
  robots: RobotDescriptor[];
  /** 生产状态随快照直达（2026-11-13）：生产面板不再为状态单独轮询。 */
  production: ProductionRuntimeStatus;
  /** 告警数据版本戳。生产面板据此刷新活动告警统计和当前历史页。 */
  alarmRevision: string;
  /** 本机多工位运行时状态列表。 */
  runtimes?: ContractDto<'ProductionStationRuntimeStatus'>[] | null;
};

const retryDelays = [1000, 3000, 10_000, 30_000];
let shared: HubConnection | undefined;
let currentSnapshot: MonitorSnapshot | undefined;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
let retryAttempt = 0;
const listeners = new Set<(snapshot: MonitorSnapshot | undefined) => void>();

function publish(snapshot: MonitorSnapshot | undefined) {
  currentSnapshot = snapshot;
  listeners.forEach((listener) => listener(snapshot));
}

function scheduleRetry() {
  if (listeners.size === 0 || retryTimer !== undefined) return;
  const delay = retryDelays[retryAttempt];
  retryAttempt = Math.min(retryAttempt + 1, retryDelays.length - 1);
  retryTimer = setTimeout(() => {
    retryTimer = undefined;
    ensureStarted();
  }, delay);
}

function createConnection(): HubConnection {
  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/monitor')
    .configureLogging(LogLevel.Warning)
    .build();
  connection.on('monitorSnapshot', (snapshot: MonitorSnapshot) => {
    if (shared !== connection) return;
    const previous = currentSnapshot?.production;
    const next = snapshot.production;
    if (previous?.statusEpoch && previous.statusEpoch === next.statusEpoch
      && (previous.statusVersion ?? 0) > (next.statusVersion ?? 0)) return;
    publish(snapshot);
  });
  connection.onclose(() => {
    if (shared !== connection) return;
    publish(undefined);
    scheduleRetry();
  });
  return connection;
}

function ensureStarted() {
  if (listeners.size === 0 || retryTimer !== undefined) return;
  try {
    const connection = shared ??= createConnection();
    if (connection.state !== HubConnectionState.Disconnected) return;
    void connection.start().then(() => {
      if (shared === connection && connection.state === HubConnectionState.Connected) retryAttempt = 0;
    }, () => { if (shared === connection) scheduleRetry(); });
  } catch {
    // Resolving a relative endpoint can throw synchronously when the document has no usable base URL.
    scheduleRetry();
  }
}

/**
 * 订阅监视快照。`enabled=false` 时不建连接（面板关闭）。
 * 返回值只在收到推送后变化；首帧为 undefined，面板应把它当作"还没有新数据"。
 */
export function useMonitorSnapshot(enabled: boolean): MonitorSnapshot | undefined {
  const [snapshot, setSnapshot] = useState<MonitorSnapshot>();

  useEffect(() => {
    if (!enabled) return;
    listeners.add(setSnapshot);
    setSnapshot(currentSnapshot);
    ensureStarted();
    return () => {
      listeners.delete(setSnapshot);
      if (listeners.size !== 0) return;
      clearTimeout(retryTimer);
      retryTimer = undefined;
      retryAttempt = 0;
      const connection = shared;
      shared = undefined;
      currentSnapshot = undefined;
      if (connection && connection.state !== HubConnectionState.Disconnected) void connection.stop().catch(() => undefined);
    };
  }, [enabled]);

  return enabled ? snapshot : undefined;
}
