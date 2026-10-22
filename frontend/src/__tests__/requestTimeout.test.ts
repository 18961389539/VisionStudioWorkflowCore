import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { withRequestTimeout } from '../requestTimeout';
import { deferred } from './fixtures/tuning';

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); });

describe('request deadlines', () => {
  it('starts immediately, returns the result and removes the deadline after success', async () => {
    const controller = new AbortController();
    const execute = vi.fn(async () => 42);
    const request = withRequestTimeout(controller, 15_000, execute);
    expect(execute).toHaveBeenCalledOnce();
    expect(await request).toBe(42);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(controller.signal.aborted).toBe(false);
  });

  it.each(['synchronous', 'asynchronous'])('preserves %s errors and clears the deadline', async (kind) => {
    const controller = new AbortController(); const error = new Error('request failed');
    const request = withRequestTimeout(controller, 15_000, () => {
      if (kind === 'synchronous') throw error;
      return Promise.reject(error);
    });
    await expect(request).rejects.toBe(error);
    expect(vi.getTimerCount()).toBe(0);
    expect(controller.signal.aborted).toBe(false);
  });

  it('settles at exactly the deadline without waiting for the underlying response', async () => {
    const controller = new AbortController(); const pending = deferred<number>();
    const completed = vi.fn(); const failed = vi.fn();
    const request = withRequestTimeout(controller, 15_000, () => pending.promise).then(completed, failed);
    await vi.advanceTimersByTimeAsync(14_999);
    expect(controller.signal.aborted).toBe(false);
    expect(failed).not.toHaveBeenCalled();
    expect(completed).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await request;
    expect(controller.signal.aborted).toBe(true);
    expect(failed).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ name: 'TimeoutError' }));
    expect(vi.getTimerCount()).toBe(0);
    pending.resolve(42);
    await Promise.resolve();
    expect(completed).not.toHaveBeenCalled();
    expect(failed).toHaveBeenCalledOnce();
  });

  it('settles manual cancellation immediately and ignores late success', async () => {
    const controller = new AbortController(); const pending = deferred<number>();
    const completed = vi.fn(); const failed = vi.fn(); const reason = new Error('scope changed');
    const request = withRequestTimeout(controller, 15_000, () => pending.promise).then(completed, failed);
    controller.abort(reason);
    await request;
    expect(failed).toHaveBeenCalledExactlyOnceWith(reason);
    expect(vi.getTimerCount()).toBe(0);
    pending.resolve(42);
    await Promise.resolve();
    expect(completed).not.toHaveBeenCalled();
  });

  it('does not execute an operation whose signal was already aborted', async () => {
    const controller = new AbortController(); const reason = new Error('already closed');
    controller.abort(reason);
    const execute = vi.fn(async () => 42);
    await expect(withRequestTimeout(controller, 15_000, execute)).rejects.toBe(reason);
    expect(execute).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['timeout', 'manual'])('handles late rejection after %s without resettling', async (kind) => {
    const controller = new AbortController(); let reject!: (reason: Error) => void;
    const pending = new Promise<number>((_resolve, fail) => { reject = fail; });
    const failed = vi.fn();
    const request = withRequestTimeout(controller, 15_000, () => pending).catch(failed);
    if (kind === 'timeout') await vi.advanceTimersByTimeAsync(15_000);
    else controller.abort();
    await request;
    reject(new Error('late failure'));
    await Promise.resolve();
    expect(failed).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
});
