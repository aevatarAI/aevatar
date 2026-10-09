// The observation window includes slow reads, not only the backoff delays.
export const WORKFLOW_OBSERVATION_TIMEOUT_MS = 30_000;
export const WORKFLOW_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000, 3000, 5000, 8000, 10000,
] as const;

export function ensureWorkflowObservationActive(signal?: AbortSignal): void {
  if (signal?.aborted) {
    throw signal.reason ?? new DOMException('Request cancelled.', 'AbortError');
  }
}

export function waitForWorkflowObservation(
  delayMs: number,
  signal?: AbortSignal,
): Promise<void> {
  return new Promise((resolve, reject) => {
    ensureWorkflowObservationActive(signal);
    const onAbort = () => {
      window.clearTimeout(timer);
      reject(signal?.reason);
    };
    const timer = window.setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, delayMs);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}
