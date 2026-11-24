import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { useMonitorSnapshot, type MonitorSnapshot } from '../useMonitorStream';
import type { ProductionRuntimeStatus } from '../types';

const { build } = vi.hoisted(() => ({ build: vi.fn() }));
vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl() { return this; }
    configureLogging() { return this; }
    build() { return build(); }
  },
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  LogLevel: { Warning: 3 },
}));

function makeConnection(failures = 0) {
  let onSnapshot: (snapshot: MonitorSnapshot) => void;
  let onClose: () => void;
  const connection = {
    state: 'Disconnected',
    start: vi.fn(async () => {
      if (failures > 0) {
        failures--;
        throw new Error('offline');
      }
      connection.state = 'Connected';
    }),
    stop: vi.fn(async () => { connection.drop(); }),
    on: (_event: string, listener: typeof onSnapshot) => { onSnapshot = listener; },
    onclose: (listener: typeof onClose) => { onClose = listener; },
    emit: (capturedAt: string) => onSnapshot({ capturedAt, cameras: [], devices: [], robots: [],
      production: {} as ProductionRuntimeStatus, alarmRevision: '0' }),
    drop: () => { connection.state = 'Disconnected'; onClose(); },
  };
  return connection;
}

const advance = async (ms: number) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };
beforeEach(() => { vi.useFakeTimers(); build.mockReset(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

it('retries an initial failure with backoff while the original panel stays open', async () => {
  const connection = makeConnection(2);
  build.mockReturnValue(connection);
  const panel = renderHook(() => useMonitorSnapshot(true));
  await advance(1000);
  expect(connection.start).toHaveBeenCalledTimes(2);
  await advance(2999);
  expect(connection.start).toHaveBeenCalledTimes(2);
  await advance(1);
  expect(connection.start).toHaveBeenCalledTimes(3);
  act(() => connection.emit('recovered'));
  expect(panel.result.current?.capturedAt).toBe('recovered');
});

it('reconnects once for shared subscribers and cancels retries when the last one closes', async () => {
  const connection = makeConnection();
  build.mockReturnValue(connection);
  const first = renderHook(() => useMonitorSnapshot(true));
  const second = renderHook(() => useMonitorSnapshot(true));
  act(() => connection.emit('first'));
  expect(first.result.current).toEqual(second.result.current);
  expect(build).toHaveBeenCalledOnce();

  act(() => connection.drop());
  expect(second.result.current).toBeUndefined();
  first.unmount();
  await advance(1000);
  expect(connection.start).toHaveBeenCalledTimes(2);
  act(() => connection.emit('second'));
  expect(second.result.current?.capturedAt).toBe('second');

  act(() => connection.drop());
  second.unmount();
  await advance(60_000);
  expect(connection.start).toHaveBeenCalledTimes(2);
});
