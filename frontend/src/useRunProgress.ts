import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';

export type RunProgressEvent = {
  runId: string;
  workflowId: string;
  nodeId: string;
  state: 'running' | 'ok' | 'error';
  completedCount: number;
  totalCount: number;
  success?: boolean | null;
  durationMs?: number | null;
};

let connection: HubConnection | undefined;
const listeners = new Set<(event: RunProgressEvent) => void>();

function ensureConnected() {
  if (listeners.size === 0) return;
  try {
    if (!connection) {
      connection = new HubConnectionBuilder()
        .withUrl('/hubs/diagnostics')
        .configureLogging(LogLevel.Warning)
        .build();
      connection.on('runNodeProgress', (event: RunProgressEvent) => {
        listeners.forEach((listener) => listener(event));
      });
    }
    if (connection.state === HubConnectionState.Disconnected) {
      void connection.start().catch(() => undefined);
    }
  } catch {
    // 浏览器无有效 base URL 或 SignalR 不可用时，静默降级为无实时进度
  }
}

export function subscribeRunProgress(listener: (event: RunProgressEvent) => void): () => void {
  listeners.add(listener);
  ensureConnected();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && connection) {
      const target = connection;
      connection = undefined;
      if (target.state !== HubConnectionState.Disconnected) {
        void target.stop().catch(() => undefined);
      }
    }
  };
}
