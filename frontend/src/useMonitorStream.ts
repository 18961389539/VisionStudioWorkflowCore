import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { useEffect, useState } from 'react';
import type { CameraDescriptor, DeviceDescriptor, ProductionRuntimeStatus, RobotDescriptor } from './types';

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
  /** 报警文件版本戳；变化时生产面板只补拉一次报警列表。 */
  alarmRevision: string;
};

let shared: HubConnection | undefined;
const listeners = new Set<(snapshot: MonitorSnapshot) => void>();

async function ensureStarted(): Promise<void> {
  if (shared) {
    // 之前被最后一个订阅者停掉、或重连失败停在 Disconnected：重新拉起来。
    if (shared.state === HubConnectionState.Disconnected) {
      await shared.start().catch(() => undefined);
    }
    return;
  }
  const builder = new HubConnectionBuilder()
    .withUrl('/hubs/monitor')
    .withAutomaticReconnect([0, 1000, 3000, 10000])
    .configureLogging(LogLevel.Warning);
  // build() 会立刻解析相对地址；宿主没给出可解析的 base（测试环境、被嵌进 about:blank 的窗口）
  // 时它是**同步抛出**的，不走 start() 的 promise 链——那样就成了未捕获拒绝，会把面板一起带崩。
  // 推送只是"更快看到变化"，拿不到就退回定时兜底，不影响任何功能。
  let connection: HubConnection;
  try {
    connection = builder.build();
  } catch {
    return;
  }
  connection.on('monitorSnapshot', (snapshot: MonitorSnapshot) => {
    listeners.forEach((listener) => listener(snapshot));
  });
  shared = connection;
  await connection.start().catch(() => undefined);
}

/**
 * 订阅监视快照。`enabled=false` 时不建连接（面板关闭）。
 * 返回值只在收到推送后变化；首帧为 undefined，面板应把它当作"还没有新数据"。
 */
export function useMonitorSnapshot(enabled: boolean): MonitorSnapshot | undefined {
  const [snapshot, setSnapshot] = useState<MonitorSnapshot>();

  useEffect(() => {
    if (!enabled) return;
    const listener = (next: MonitorSnapshot) => setSnapshot(next);
    listeners.add(listener);
    void ensureStarted().catch(() => undefined);
    return () => {
      listeners.delete(listener);
      if (listeners.size === 0 && shared) {
        const connection = shared;
        shared = undefined;
        if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
      }
    };
  }, [enabled]);

  return snapshot;
}
