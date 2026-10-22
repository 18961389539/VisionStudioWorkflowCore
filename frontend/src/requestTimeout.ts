/** Settle at the deadline even when the underlying request ignores cancellation. */
export function withRequestTimeout<T>(controller: AbortController, milliseconds: number, execute: () => Promise<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let settled = false;
    let timeout: ReturnType<typeof setTimeout> | undefined;
    const finish = (complete: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      controller.signal.removeEventListener('abort', cancel);
      complete();
    };
    const cancel = () => finish(() => reject(controller.signal.reason));
    if (controller.signal.aborted) { cancel(); return; }
    controller.signal.addEventListener('abort', cancel, { once: true });
    timeout = setTimeout(() => controller.abort(new DOMException('Request timed out', 'TimeoutError')), milliseconds);
    try {
      execute().then((value) => finish(() => resolve(value)), (reason) => finish(() => reject(reason)));
    } catch (reason) {
      finish(() => reject(reason));
    }
  });
}
