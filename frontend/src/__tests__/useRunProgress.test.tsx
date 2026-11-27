import { describe, expect, it, vi, beforeEach } from 'vitest';
import { subscribeRunProgress, type RunProgressEvent } from '../useRunProgress';

const mocks = vi.hoisted(() => {
  const handlers: Record<string, (event: RunProgressEvent) => void> = {};
  const start = vi.fn().mockResolvedValue(undefined);
  const stop = vi.fn().mockResolvedValue(undefined);
  const withUrl = vi.fn();
  const connection = {
    state: 'Disconnected',
    on: vi.fn((eventName: string, handler: (event: RunProgressEvent) => void) => {
      handlers[eventName] = handler;
    }),
    start: vi.fn(() => {
      connection.state = 'Connected';
      return start();
    }),
    stop: vi.fn(() => {
      connection.state = 'Disconnected';
      return stop();
    }),
  };
  return { handlers, start, stop, withUrl, connection };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl(url: string) {
      mocks.withUrl(url);
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return mocks.connection;
    }
  },
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  LogLevel: { Warning: 3 },
}));

describe('useRunProgress', () => {
  beforeEach(() => {
    mocks.connection.state = 'Disconnected';
    mocks.withUrl.mockClear();
    mocks.start.mockClear();
    mocks.stop.mockClear();
  });

  it('connects to /hubs/runs and forwards progress and canceled events', () => {
    const received: RunProgressEvent[] = [];
    const unsubscribe = subscribeRunProgress((evt) => received.push(evt));

    expect(mocks.withUrl).toHaveBeenCalledWith('/hubs/runs');
    expect(mocks.start).toHaveBeenCalledTimes(1);

    mocks.handlers.runNodeProgress?.({
      runId: 'r1',
      workflowId: 'wf1',
      nodeId: 'n1',
      state: 'running',
      completedCount: 0,
      totalCount: 2,
    });
    mocks.handlers.runNodeProgress?.({
      runId: 'r1',
      workflowId: 'wf1',
      nodeId: '',
      state: 'canceled',
      completedCount: 1,
      totalCount: 2,
      success: false,
    });

    expect(received.map((x) => x.state)).toEqual(['running', 'canceled']);

    unsubscribe();
    expect(mocks.stop).toHaveBeenCalledTimes(1);
  });
});
