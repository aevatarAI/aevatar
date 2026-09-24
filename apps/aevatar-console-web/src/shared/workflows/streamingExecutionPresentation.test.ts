import { type AGUIEvent, AGUIEventType } from '@aevatar-react-sdk/types';
import { waitForWorkflowNodeStartPaint } from './streamingExecutionPresentation';

function createAnimationFrameScheduler() {
  const callbacks = new Map<number, FrameRequestCallback>();
  let nextHandle = 1;
  const scheduler = {
    cancel: jest.fn((handle: number) => {
      callbacks.delete(handle);
    }),
    request: jest.fn((callback: FrameRequestCallback) => {
      const handle = nextHandle;
      nextHandle += 1;
      callbacks.set(handle, callback);
      return handle;
    }),
  };

  return {
    callbackCount: () => callbacks.size,
    runNext(timestamp: number) {
      const next = callbacks.entries().next().value as
        | [number, FrameRequestCallback]
        | undefined;
      if (!next) {
        throw new Error('Expected a pending animation frame callback.');
      }
      callbacks.delete(next[0]);
      next[1](timestamp);
    },
    scheduler,
  };
}

function createStepRequestEvent(): AGUIEvent {
  const payload = {
    input: 'Alpha input',
    stepId: 'step-alpha',
    stepType: 'assign',
  };
  return {
    name: 'aevatar.step.request',
    payload,
    timestamp: Date.parse('2026-09-03T09:30:00Z'),
    type: AGUIEventType.CUSTOM,
    value: payload,
  } as AGUIEvent;
}

describe('waitForWorkflowNodeStartPaint', () => {
  it('does not schedule presentation work for non-node events', async () => {
    const { scheduler } = createAnimationFrameScheduler();

    await waitForWorkflowNodeStartPaint(
      {
        runId: 'run-alpha',
        timestamp: Date.parse('2026-09-03T09:30:00Z'),
        type: AGUIEventType.RUN_STARTED,
      } as AGUIEvent,
      undefined,
      scheduler,
    );

    expect(scheduler.request).not.toHaveBeenCalled();
  });

  it('resumes node event consumption only after a paint boundary', async () => {
    const { callbackCount, runNext, scheduler } =
      createAnimationFrameScheduler();
    let resolved = false;
    const waiting = waitForWorkflowNodeStartPaint(
      createStepRequestEvent(),
      undefined,
      scheduler,
    ).then(() => {
      resolved = true;
    });

    expect(callbackCount()).toBe(1);
    expect(resolved).toBe(false);
    runNext(0);
    expect(callbackCount()).toBe(1);
    expect(resolved).toBe(false);
    runNext(16);
    await waiting;
    expect(resolved).toBe(true);
  });

  it('cancels a pending paint boundary when the run is aborted', async () => {
    const { callbackCount, scheduler } = createAnimationFrameScheduler();
    const controller = new AbortController();
    const waiting = waitForWorkflowNodeStartPaint(
      createStepRequestEvent(),
      controller.signal,
      scheduler,
    );

    expect(callbackCount()).toBe(1);
    controller.abort();
    await waiting;

    expect(callbackCount()).toBe(0);
    expect(scheduler.cancel).toHaveBeenCalledWith(1);
  });

  it('cancels the second frame when the run is aborted after the first frame', async () => {
    const { callbackCount, runNext, scheduler } =
      createAnimationFrameScheduler();
    const controller = new AbortController();
    const waiting = waitForWorkflowNodeStartPaint(
      createStepRequestEvent(),
      controller.signal,
      scheduler,
    );

    runNext(0);
    expect(callbackCount()).toBe(1);
    controller.abort();
    await waiting;

    expect(callbackCount()).toBe(0);
    expect(scheduler.cancel).toHaveBeenCalledWith(2);
  });
});
