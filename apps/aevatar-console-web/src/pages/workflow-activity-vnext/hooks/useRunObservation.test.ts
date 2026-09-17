import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';
import {
  observeRunActivity,
  resolveRunObservationPhase,
} from './useRunObservation';

function createRun(runId: string): WorkflowActivityRunDetail {
  return {
    summary: {
      runId,
      workflowName: 'Order review',
      status: 'running',
      success: null,
      startedAtUtc: '2026-08-05T10:00:00Z',
      updatedAtUtc: '2026-08-05T10:00:01Z',
      stateVersion: 4,
      scopeId: 'scope-alpha',
      runOrigin: 'draft',
    },
    input: 'Review this order',
    finalOutput: '',
    finalError: '',
    diagnostics: [],
    steps: [],
    timeline: [],
    statistics: {
      totalSteps: 0,
      requestedSteps: 0,
      completedSteps: 0,
      roleReplyCount: 0,
      stepTypeCounts: {},
    },
    usageTotals: {
      promptTokens: 0,
      completionTokens: 0,
      totalTokens: 0,
      cost: 0,
    },
    recoveryCapability: {
      retryFailedStep: {
        eligibility: 0,
        unavailableReasonCode: 0,
        unavailableReason: '',
        recommendedActions: [],
        startingStepId: '',
        reusesPriorStepOutputs: false,
        mayIncurModelOrToolCost: false,
      },
      runAgain: {
        eligibility: 0,
        unavailableReasonCode: 0,
        unavailableReason: '',
        recommendedActions: [],
        startingStepId: '',
        reusesPriorStepOutputs: false,
        mayIncurModelOrToolCost: false,
      },
      workflowDefinitionRevisionId: 'revision-alpha',
      workflowDefinitionVersion: 3,
    },
    lineage: {
      availability: 0,
      retryFork: {
        availability: 0,
        sourceRunId: '',
        originalRunId: '',
        attempt: 0,
        startAtStepId: '',
        childRuns: [],
      },
      subWorkflow: {
        availability: 0,
        parentRunId: '',
        parentActorId: '',
        parentStepId: '',
        rootRunId: '',
        depth: 0,
        childRuns: [],
      },
      unavailableReason: '',
    },
  };
}

describe('observeRunActivity', () => {
  it('keeps observing the exact SSE run id through transient not-found responses', async () => {
    const pending = Object.assign(new Error('not ready'), { status: 404 });
    const read = jest
      .fn()
      .mockRejectedValueOnce(pending)
      .mockRejectedValueOnce(pending)
      .mockResolvedValueOnce(createRun('run-sse-alpha'));

    await expect(
      observeRunActivity({
        scopeId: 'scope-alpha',
        runId: 'run-sse-alpha',
        read,
        wait: async () => undefined,
        delaysMs: [0, 0, 0],
      }),
    ).resolves.toMatchObject({
      kind: 'observed',
      run: { summary: { runId: 'run-sse-alpha', stateVersion: 4 } },
    });
    expect(read.mock.calls).toEqual([
      ['scope-alpha', 'run-sse-alpha'],
      ['scope-alpha', 'run-sse-alpha'],
      ['scope-alpha', 'run-sse-alpha'],
    ]);
  });

  it('returns delayed instead of inventing an observed run after bounded not-found responses', async () => {
    const pending = Object.assign(new Error('not ready'), { status: 404 });
    const read = jest.fn().mockRejectedValue(pending);

    await expect(
      observeRunActivity({
        scopeId: 'scope-alpha',
        runId: 'run-sse-alpha',
        read,
        wait: async () => undefined,
        delaysMs: [0, 0],
      }),
    ).resolves.toEqual({ kind: 'delayed' });
    expect(read.mock.calls).toEqual([
      ['scope-alpha', 'run-sse-alpha'],
      ['scope-alpha', 'run-sse-alpha'],
    ]);
  });

  it('rejects a different observed run instead of treating it as the SSE run', async () => {
    const read = jest.fn().mockResolvedValue(createRun('run-other-beta'));

    await expect(
      observeRunActivity({
        scopeId: 'scope-alpha',
        runId: 'run-sse-alpha',
        read,
        wait: async () => undefined,
        delaysMs: [0],
      }),
    ).rejects.toThrow('does not match');
  });

  it('prioritizes later authorization errors over cached observation results', () => {
    expect(
      resolveRunObservationPhase({
        data: { kind: 'observed', run: createRun('run-sse-alpha') },
        enabled: true,
        error: Object.assign(new Error('expired'), { status: 401 }),
        isFetching: false,
        isPending: false,
      }),
    ).toBe('unauthorized');
    expect(
      resolveRunObservationPhase({
        data: { kind: 'delayed' },
        enabled: true,
        error: Object.assign(new Error('access changed'), { status: 403 }),
        isFetching: false,
        isPending: false,
      }),
    ).toBe('forbidden');
  });
});
