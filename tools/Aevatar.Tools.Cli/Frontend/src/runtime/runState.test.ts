import { describe, expect, it } from 'vitest';

import type { RuntimeEvent } from './sseUtils';
import { applyRuntimeEventToActiveRun } from './runState';

function applyAll(events: RuntimeEvent[]) {
  return events.reduce(
    (state, evt) => applyRuntimeEventToActiveRun(state, evt, { serviceId: 'workflow-service', serviceLabel: 'Workflow Service' }),
    null as ReturnType<typeof applyRuntimeEventToActiveRun>,
  );
}

describe('applyRuntimeEventToActiveRun', () => {
  it('tracks run context and active step from custom workflow events', () => {
    const state = applyAll([
      {
        type: 'CUSTOM',
        timestamp: 1000,
        name: 'aevatar.run.context',
        payload: { actorId: 'actor-1', workflowName: 'demo_workflow', commandId: 'cmd-1' },
      },
      {
        type: 'CUSTOM',
        timestamp: 1200,
        name: 'aevatar.step.request',
        payload: { stepId: 'collect_input', stepType: 'assign', targetRole: 'assistant' },
      },
    ]);

    expect(state).not.toBeNull();
    expect(state?.workflowName).toBe('demo_workflow');
    expect(state?.actorId).toBe('actor-1');
    expect(state?.commandId).toBe('cmd-1');
    expect(state?.currentStepId).toBe('collect_input');
    expect(state?.currentStepType).toBe('assign');
    expect(state?.status).toBe('running');
    expect(state?.steps).toHaveLength(1);
    expect(state?.steps[0]).toMatchObject({
      id: 'collect_input',
      stepType: 'assign',
      targetRole: 'assistant',
      status: 'active',
    });
  });

  it('moves into waiting state when a workflow is waiting for signal', () => {
    const state = applyAll([
      {
        type: 'RUN_STARTED',
        timestamp: 1000,
        threadId: 'actor-1',
        runId: 'run-1',
      },
      {
        type: 'CUSTOM',
        timestamp: 1200,
        name: 'aevatar.workflow.waiting_signal',
        payload: { stepId: 'wait_release', signalName: 'release_approved', prompt: 'Waiting for release approval' },
      },
    ]);

    expect(state).not.toBeNull();
    expect(state?.status).toBe('waiting');
    expect(state?.waitingKind).toBe('signal');
    expect(state?.waitingSignalName).toBe('release_approved');
    expect(state?.steps[0]).toMatchObject({
      id: 'wait_release',
      status: 'waiting',
    });
  });

  it('marks current step and run as failed when run error arrives', () => {
    const state = applyAll([
      {
        type: 'CUSTOM',
        timestamp: 1000,
        name: 'aevatar.step.request',
        payload: { stepId: 'call_tool', stepType: 'tool_call' },
      },
      {
        type: 'RUN_ERROR',
        timestamp: 1500,
        message: 'Connector timeout',
      },
    ]);

    expect(state).not.toBeNull();
    expect(state?.status).toBe('error');
    expect(state?.error).toBe('Connector timeout');
    expect(state?.steps[0]).toMatchObject({
      id: 'call_tool',
      status: 'error',
      error: 'Connector timeout',
    });
  });
});

