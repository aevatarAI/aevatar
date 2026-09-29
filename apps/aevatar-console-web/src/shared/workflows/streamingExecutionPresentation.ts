import {
  extractStepRequest,
  type RuntimeEvent,
} from '@/shared/agui/runtimeEventSemantics';

export type WorkflowAnimationFrameScheduler = {
  readonly cancel: (handle: number) => void;
  readonly request: (callback: FrameRequestCallback) => number;
};

function resolveBrowserAnimationFrameScheduler(): WorkflowAnimationFrameScheduler | null {
  if (
    typeof window === 'undefined' ||
    typeof window.requestAnimationFrame !== 'function' ||
    (typeof document !== 'undefined' && document.visibilityState === 'hidden')
  ) {
    return null;
  }

  return {
    cancel: (handle) => window.cancelAnimationFrame?.(handle),
    request: (callback) => window.requestAnimationFrame(callback),
  };
}

export async function waitForWorkflowNodeStartPaint(
  event: RuntimeEvent,
  signal?: AbortSignal,
  scheduler: WorkflowAnimationFrameScheduler | null = resolveBrowserAnimationFrameScheduler(),
): Promise<void> {
  if (!extractStepRequest(event) || signal?.aborted || !scheduler) {
    return;
  }

  await new Promise<void>((resolve) => {
    let firstFrameHandle: number | null = null;
    let secondFrameHandle: number | null = null;
    let settled = false;

    const finish = () => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', handleAbort);
      resolve();
    };
    const handleAbort = () => {
      if (firstFrameHandle !== null) {
        scheduler.cancel(firstFrameHandle);
        firstFrameHandle = null;
      }
      if (secondFrameHandle !== null) {
        scheduler.cancel(secondFrameHandle);
        secondFrameHandle = null;
      }
      finish();
    };

    signal?.addEventListener('abort', handleAbort, { once: true });
    firstFrameHandle = scheduler.request(() => {
      firstFrameHandle = null;
      if (signal?.aborted) {
        finish();
        return;
      }
      secondFrameHandle = scheduler.request(() => {
        secondFrameHandle = null;
        finish();
      });
    });
  });
}
